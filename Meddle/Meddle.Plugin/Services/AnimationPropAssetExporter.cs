using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Meddle.Plugin.Models;
using Meddle.Plugin.Models.Composer;
using Meddle.Utils;
using Meddle.Utils.Files;
using Meddle.Utils.Files.SqPack;
using Meddle.Utils.Helpers;
using Microsoft.Extensions.Logging;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace Meddle.Plugin.Services;

/// <summary>
/// Converts the static weapon-model references used by C198 emote events into
/// local, on-demand GLBs. Game models and textures remain in the user's cache;
/// none are shipped with XivBlend or packed into a character .blend.
/// </summary>
public sealed class AnimationPropAssetExporter : IService
{
    private const string ConsumableAttachmentType = "fdr";
    private const int LeftHandFlag = 0x1;
    private const ushort ConsumableWeaponModelId = 9901;
    private const string IntegrityManifestFileName = "prop-cache-v1.json";
    private const int IntegritySchemaVersion = 1;
    private const int MaximumIntegrityManifestBytes = 8 * 1024 * 1024;
    private const int MaximumIntegrityFiles = 16_384;
    private const long MaximumIntegrityFileBytes = 512L * 1024 * 1024;
    private const long MaximumIntegrityTotalBytes = 8L * 1024 * 1024 * 1024;

    private readonly ILogger<AnimationPropAssetExporter> logger;
    private readonly SqPack sqPack;
    private readonly ConcurrentDictionary<ushort, AttachmentLookup> attachmentCache = new();

    public AnimationPropAssetExporter(
        ILogger<AnimationPropAssetExporter> logger,
        SqPack sqPack)
    {
        this.logger = logger;
        this.sqPack = sqPack;
    }

    public AnimationPropAssetResult Export(
        string kind,
        ushort modelId,
        ushort bodyId,
        int variant,
        int flags,
        ushort raceCode,
        string assetDirectory,
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attachment = ResolveAttachment(raceCode, modelId, flags);
        var modelPath = BuildModelGamePath(modelId, bodyId);
        if (!string.Equals(kind, "Model", StringComparison.Ordinal))
        {
            return BuildResult(
                AnimationPropAssetStatuses.UnsupportedKind,
                null,
                null,
                modelPath,
                attachment,
                $"Prop event kind '{kind}' is not supported by the static model exporter.");
        }

        if (variant is < 0 or > 9999)
        {
            return BuildResult(
                AnimationPropAssetStatuses.ExportFailed,
                null,
                null,
                modelPath,
                attachment,
                $"Prop model {modelId:D4}/{bodyId:D4} has invalid material variant {variant}.");
        }

        try
        {
            if (sqPack.GetFile(modelPath) is null)
            {
                return BuildResult(
                    AnimationPropAssetStatuses.MissingModel,
                    null,
                    null,
                    modelPath,
                    attachment,
                    $"The emote prop model is missing from SqPack: {modelPath}");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(exception, "Could not resolve animation prop model {ModelPath}", modelPath);
            return BuildResult(
                AnimationPropAssetStatuses.ExportFailed,
                null,
                null,
                modelPath,
                attachment,
                $"The emote prop model could not be read from SqPack: {exception.Message}");
        }

        var outputPath = Path.Combine(assetDirectory, "prop.glb");
        var integrityPath = Path.Combine(assetDirectory, IntegrityManifestFileName);
        if (IsValidPublishedAsset(outputPath, cacheDirectory))
        {
            return BuildResult(
                AnimationPropAssetStatuses.Ready,
                outputPath,
                cacheDirectory,
                modelPath,
                attachment,
                attachment.Warning);
        }

        var tempOutput = outputPath + $".{Guid.NewGuid():N}.tmp";
        var tempIntegrity = integrityPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(assetDirectory);
            Directory.CreateDirectory(cacheDirectory);

            var descriptor = sqPack.GetFile(modelPath)
                ?? throw new FileNotFoundException("The emote prop model disappeared from SqPack.", modelPath);
            var mdlFile = new MdlFile(descriptor.File.RawData.ToArray());
            var materialNames = mdlFile.GetMaterialNames()
                .OrderBy(item => item.Key)
                .Select(item => item.Value)
                .ToArray();
            if (materialNames.Length == 0)
            {
                throw new InvalidDataException($"Prop model {modelPath} does not declare any materials.");
            }

            var exportConfig = new Configuration.ExportConfiguration
            {
                CacheFileTypes = CacheFileType.Mtrl,
                ApplyVisibilityFlags = false,
                UseDeformer = false,
            };
            var composerCache = new ComposerCache(
                sqPack,
                cacheDirectory,
                exportConfig,
                refreshExistingFiles: true);
            var materials = new MaterialBuilder?[materialNames.Length];
            var requiresCharacterArrays = false;
            for (var index = 0; index < materialNames.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var materialPath = ResolveWeaponMaterialPath(
                    modelId,
                    bodyId,
                    variant,
                    materialNames[index]);
                if (sqPack.GetFile(materialPath) is null)
                {
                    throw new FileNotFoundException(
                        $"Prop model material {index} is missing from SqPack.",
                        materialPath);
                }

                var mtrlFile = composerCache.GetMtrlFile(materialPath, out _);
                requiresCharacterArrays |= UsesCharacterArrayTextures(
                    mtrlFile.GetShaderPackageName());
                materials[index] = composerCache.ComposeMaterial(materialPath);
            }

            if (requiresCharacterArrays)
            {
                composerCache.SaveCharacterArrayTextures();
            }

            var model = new Meddle.Utils.Export.Model(modelPath, mdlFile, null);
            var meshExports = ModelBuilder.BuildMeshes(
                    model,
                    materials,
                    null,
                    null,
                    exportConfig.CreateMeshBuilderOptions())
                .Where(item => item.Mesh.Primitives.Any(primitive => primitive.Triangles.Count > 0))
                .ToArray();
            if (meshExports.Length == 0)
            {
                throw new InvalidDataException($"Prop model {modelPath} produced no visible triangles.");
            }

            var scene = new SceneBuilder();
            var root = new NodeBuilder($"XivBlend Prop w{modelId:D4}b{bodyId:D4}v{variant:D4}");
            scene.AddNode(root);
            foreach (var (meshExport, index) in meshExports.Select((value, index) => (value, index)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = new NodeBuilder($"Prop Mesh {index:D2}");
                root.AddNode(node);
                scene.AddRigidMesh(meshExport.Mesh, node);
            }

            scene.ToGltf2().SaveGLB(tempOutput);
            if (!IsValidGlb(tempOutput))
            {
                throw new InvalidDataException("The prop exporter did not produce a valid GLB.");
            }

            WriteIntegrityManifest(
                tempIntegrity,
                tempOutput,
                cacheDirectory,
                composerCache.GetTrackedCacheFiles());
            File.Move(tempOutput, outputPath, true);
            File.Move(tempIntegrity, integrityPath, true);
            if (!IsValidPublishedAsset(outputPath, cacheDirectory))
            {
                throw new InvalidDataException(
                    "The prop asset or one of its mapped material cache files failed integrity validation.");
            }
            logger.LogInformation(
                "Exported animation prop {ModelPath} variant {Variant} to {OutputPath}",
                modelPath,
                variant,
                outputPath);
            return BuildResult(
                AnimationPropAssetStatuses.Ready,
                outputPath,
                cacheDirectory,
                modelPath,
                attachment,
                attachment.Warning);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(
                exception,
                "Could not export animation prop {ModelPath} variant {Variant}",
                modelPath,
                variant);
            var message = $"The emote prop {modelId:D4}/{bodyId:D4}/v{variant:D4} could not be exported: {exception.Message}";
            if (!string.IsNullOrWhiteSpace(attachment.Warning))
            {
                message += $" {attachment.Warning}";
            }

            return BuildResult(
                AnimationPropAssetStatuses.ExportFailed,
                null,
                null,
                modelPath,
                attachment,
                message);
        }
        finally
        {
            TryDelete(tempOutput);
            TryDelete(tempIntegrity);
        }
    }

    private AttachmentLookup ResolveAttachment(ushort raceCode, ushort modelId, int flags)
    {
        var bone = (flags & LeftHandFlag) != 0 ? "n_buki_l" : "n_buki_r";
        if (modelId != ConsumableWeaponModelId)
        {
            return new AttachmentLookup(
                new AnimationPropAttachment(
                    bone,
                    1.0f,
                    Vector3.Zero,
                    Vector3.Zero),
                $"Prop model w{modelId:D4} has no verified ATCH category mapping; direct hand-bone identity was used.");
        }

        var authored = attachmentCache.GetOrAdd(raceCode, LoadConsumableAttachment);
        return authored with { Transform = authored.Transform with { Bone = bone } };
    }

    private AttachmentLookup LoadConsumableAttachment(ushort raceCode)
    {
        var path = $"chara/xls/attachoffset/c{raceCode:D4}.atch";
        try
        {
            var descriptor = sqPack.GetFile(path)
                ?? throw new FileNotFoundException("The race attachment file is missing.", path);
            var file = new AtchFile(descriptor.File.RawData.ToArray());
            var entry = file.GetPoint(ConsumableAttachmentType)?.Entries.FirstOrDefault()
                ?? throw new InvalidDataException(
                    $"Race c{raceCode:D4} has no '{ConsumableAttachmentType}' attachment entry.");
            if (entry.Scale <= 0.0f)
            {
                throw new InvalidDataException(
                    $"Race c{raceCode:D4} has an invalid consumable attachment scale {entry.Scale}.");
            }

            return new AttachmentLookup(
                new AnimationPropAttachment(
                    entry.Bone,
                    entry.Scale,
                    entry.Offset,
                    entry.Rotation),
                null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(
                exception,
                "Could not read consumable attachment transform for c{RaceCode:D4}",
                raceCode);
            return new AttachmentLookup(
                new AnimationPropAttachment(
                    "n_buki_r",
                    1.0f,
                    Vector3.Zero,
                    Vector3.Zero),
                $"The c{raceCode:D4} consumable attachment transform was unavailable; identity scale was used.");
        }
    }

    private static AnimationPropAssetResult BuildResult(
        string status,
        string? assetPath,
        string? cacheDirectory,
        string modelPath,
        AttachmentLookup attachment,
        string? warning)
    {
        return new AnimationPropAssetResult(
            status,
            assetPath,
            cacheDirectory,
            modelPath,
            attachment.Transform,
            warning);
    }

    private static string BuildModelGamePath(ushort modelId, ushort bodyId)
    {
        return $"chara/weapon/w{modelId:D4}/obj/body/b{bodyId:D4}/model/w{modelId:D4}b{bodyId:D4}.mdl";
    }

    private static string ResolveWeaponMaterialPath(
        ushort modelId,
        ushort bodyId,
        int variant,
        string materialName)
    {
        var normalized = materialName.Replace('\\', '/').Trim();
        string resolved;
        if (normalized.StartsWith('/'))
        {
            resolved = $"chara/weapon/w{modelId:D4}/obj/body/b{bodyId:D4}/material/v{variant:D4}/{normalized.TrimStart('/')}";
        }
        else if (normalized.StartsWith("chara/", StringComparison.OrdinalIgnoreCase))
        {
            resolved = normalized;
        }
        else if (!normalized.Contains('/'))
        {
            resolved = $"chara/weapon/w{modelId:D4}/obj/body/b{bodyId:D4}/material/v{variant:D4}/{normalized}";
        }
        else
        {
            throw new InvalidDataException($"Prop material path is not a canonical game path: {materialName}");
        }

        if (resolved.Contains("..", StringComparison.Ordinal)
            || resolved.Contains(':')
            || !resolved.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Prop material path is unsafe or invalid: {materialName}");
        }

        return resolved;
    }

    private static bool UsesCharacterArrayTextures(string shaderPackage)
    {
        return shaderPackage is "character.shpk"
            or "characterlegacy.shpk"
            or "characterstockings.shpk"
            or "characterinc.shpk"
            or "characterglass.shpk"
            or "characterscroll.shpk"
            or "charactertransparency.shpk";
    }

    private static bool IsValidGlb(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < 20 or > 536_870_912)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[12];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            stream.ReadExactly(header);
            return BinaryPrimitives.ReadUInt32LittleEndian(header) == 0x46546C67
                   && BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) == 2
                   && BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) == info.Length;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsValidPublishedAsset(string assetPath, string cacheDirectory)
    {
        try
        {
            if (!IsValidGlb(assetPath) || !Directory.Exists(cacheDirectory)) return false;
            var assetDirectory = Path.GetDirectoryName(Path.GetFullPath(assetPath));
            if (assetDirectory is null) return false;
            var manifestPath = Path.Combine(assetDirectory, IntegrityManifestFileName);
            var manifestInfo = new FileInfo(manifestPath);
            if (!manifestInfo.Exists
                || manifestInfo.Length is <= 0 or > MaximumIntegrityManifestBytes)
            {
                return false;
            }

            var manifest = JsonSerializer.Deserialize<PropCacheIntegrityManifest>(
                File.ReadAllText(manifestPath));
            if (manifest is null
                || manifest.SchemaVersion != IntegritySchemaVersion
                || !IsLowerHexSha256(manifest.AssetSha256)
                || manifest.Files is not { Count: > 0 and <= MaximumIntegrityFiles }
                || !string.Equals(
                    ComputeSha256(assetPath),
                    manifest.AssetSha256,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var cacheRoot = Path.GetFullPath(cacheDirectory);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            foreach (var file in manifest.Files)
            {
                if (file is null
                    || !IsSafeRelativeCachePath(file.RelativePath)
                    || file.Length is <= 0 or > MaximumIntegrityFileBytes
                    || !IsLowerHexSha256(file.Sha256)
                    || !seen.Add(file.RelativePath))
                {
                    return false;
                }

                totalBytes = checked(totalBytes + file.Length);
                if (totalBytes > MaximumIntegrityTotalBytes) return false;
                var resolved = ResolveInside(cacheRoot, file.RelativePath);
                var info = new FileInfo(resolved);
                if (!info.Exists
                    || info.Length != file.Length
                    || !string.Equals(
                        ComputeSha256(resolved),
                        file.Sha256,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or InvalidDataException
                or ArgumentException
                or OverflowException)
        {
            return false;
        }
    }

    private static void WriteIntegrityManifest(
        string outputPath,
        string assetPath,
        string cacheDirectory,
        IReadOnlyList<string> trackedFiles)
    {
        if (trackedFiles.Count is < 1 or > MaximumIntegrityFiles)
        {
            throw new InvalidDataException(
                $"The prop material cache referenced {trackedFiles.Count:N0} files; expected 1–{MaximumIntegrityFiles:N0}.");
        }

        var cacheRoot = Path.GetFullPath(cacheDirectory);
        var entries = new List<PropCacheIntegrityFile>(trackedFiles.Count);
        long totalBytes = 0;
        foreach (var file in trackedFiles
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(cacheRoot, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!IsSafeRelativeCachePath(relative)
                || !string.Equals(ResolveInside(cacheRoot, relative), file, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The prop material cache file escaped its shared cache root: {file}");
            }

            var info = new FileInfo(file);
            if (!info.Exists || info.Length is <= 0 or > MaximumIntegrityFileBytes)
            {
                throw new InvalidDataException(
                    $"The prop material cache file has an invalid size: {relative}");
            }

            totalBytes = checked(totalBytes + info.Length);
            if (totalBytes > MaximumIntegrityTotalBytes)
            {
                throw new InvalidDataException(
                    "The prop material cache exceeds the bounded integrity-manifest size.");
            }

            entries.Add(new PropCacheIntegrityFile(
                relative,
                info.Length,
                ComputeSha256(file)));
        }

        var manifest = new PropCacheIntegrityManifest(
            IntegritySchemaVersion,
            ComputeSha256(assetPath),
            entries);
        using (var stream = new FileStream(
                   outputPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   64 * 1024,
                   FileOptions.SequentialScan))
        {
            JsonSerializer.Serialize(stream, manifest);
            stream.Flush(true);
        }

        if (new FileInfo(outputPath).Length > MaximumIntegrityManifestBytes)
        {
            throw new InvalidDataException("The prop cache integrity manifest exceeded its size limit.");
        }
    }

    private static string ResolveInside(string root, string relativePath)
    {
        var resolvedRoot = Path.GetFullPath(root);
        var resolved = Path.GetFullPath(Path.Combine(resolvedRoot, relativePath));
        var prefix = resolvedRoot.TrimEnd(
                         Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The prop cache integrity path escaped its cache root.");
        }

        return resolved;
    }

    private static bool IsSafeRelativeCachePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 32_768
            || Path.IsPathRooted(value)
            || value.Contains('\\')
            || value.Contains(':')
            || value.Contains('\0'))
        {
            return false;
        }

        return value.Split('/').All(segment => segment is not "" and not "." and not "..");
    }

    private static bool IsLowerHexSha256(string? value)
    {
        return value is { Length: 64 }
               && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Private cache staging cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Private cache staging cleanup is best effort.
        }
    }

    private sealed record AttachmentLookup(
        AnimationPropAttachment Transform,
        string? Warning);

    private sealed record PropCacheIntegrityManifest(
        int SchemaVersion,
        string AssetSha256,
        IReadOnlyList<PropCacheIntegrityFile> Files);

    private sealed record PropCacheIntegrityFile(
        string RelativePath,
        long Length,
        string Sha256);
}

public sealed record AnimationPropAttachment(
    string Bone,
    float Scale,
    Vector3 Offset,
    Vector3 Rotation);

public sealed record AnimationPropAssetResult(
    string Status,
    string? AssetPath,
    string? CacheDirectory,
    string ModelGamePath,
    AnimationPropAttachment Attachment,
    string? Warning);
