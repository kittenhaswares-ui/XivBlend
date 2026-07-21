using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Meddle.Utils.Files;
using Meddle.Utils.Files.SqPack;
using Microsoft.Extensions.Logging;

namespace Meddle.Plugin.Services;

public enum AnimationVfxAssetStatus
{
    SyncControl,
    StaticEmbeddedMeshPreview,
    UnsupportedApricot,
    MetadataOnly,
    MissingAsset,
    AnalysisFailed,
    ExportFailed,
}

public sealed record AnimationVfxParticleSummary(int TypeId, string TypeName, int Count);

/// <summary>
/// The durable result of extracting one AVFX source.  <see cref="StaticPreviewPath"/>
/// is only embedded reference geometry; it is never a claim that Apricot VFX
/// playback, shaders, binders or materials were converted.
/// </summary>
public sealed record AnimationVfxAssetResult(
    AnimationVfxAssetStatus Status,
    string GamePath,
    string? ContentSha256,
    string? AssetDirectory,
    string? AvfxAssetPath,
    string? StaticPreviewPath,
    string? StaticPreviewSha256,
    bool RequiresApricotRuntime,
    int EmbeddedModelCount,
    int RenderableModelCount,
    int EmbeddedVertexCount,
    int EmbeddedTriangleCount,
    IReadOnlyList<AnimationVfxParticleSummary> ParticleTypes,
    IReadOnlyList<string> TextureReferences,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reads an AVFX from the user's SqPack and publishes a content-addressed,
/// immutable local copy.  When the AVFX contains validated VDrw/VIdx buffers,
/// a separate static reference-mesh GLB is emitted.  This service deliberately
/// does not evaluate the proprietary Apricot runtime.
/// </summary>
public sealed class AnimationVfxAssetExporter : IService
{
    private const int MaximumGamePathLength = 512;
    private const int ShortHashCharacters = 20;
    private const string RawAssetFileName = "source.avfx";
    private const string StaticPreviewFileName = "static-preview-v1.glb";
    private const int MaximumResultWarnings = 1_024;

    private static readonly ConcurrentDictionary<string, object> AssetLocks = new(StringComparer.Ordinal);

    private readonly ILogger<AnimationVfxAssetExporter> logger;
    private readonly SqPack sqPack;

    public AnimationVfxAssetExporter(
        ILogger<AnimationVfxAssetExporter> logger,
        SqPack sqPack)
    {
        this.logger = logger;
        this.sqPack = sqPack;
    }

    public AnimationVfxAssetResult Export(
        string gamePath,
        string buildRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryNormalizeGamePath(gamePath, out var normalizedPath, out var pathError))
        {
            return EmptyResult(
                AnimationVfxAssetStatus.AnalysisFailed,
                gamePath?.Trim() ?? string.Empty,
                pathError);
        }

        if (AvfxAnalyzer.IsSyncControlPath(normalizedPath))
        {
            return EmptyResult(
                AnimationVfxAssetStatus.SyncControl,
                normalizedPath,
                "The shared sync-action AVFX is timeline control metadata and was intentionally filtered.");
        }

        byte[] bytes;
        try
        {
            var descriptor = sqPack.GetFile(normalizedPath);
            if (descriptor is null)
            {
                return EmptyResult(
                    AnimationVfxAssetStatus.MissingAsset,
                    normalizedPath,
                    $"The AVFX is missing from SqPack: {normalizedPath}");
            }

            var rawData = descriptor.File.RawData;
            if (rawData.Length is < 8 || rawData.Length > AvfxAnalysisOptions.Default.MaximumFileBytes)
            {
                return EmptyResult(
                    AnimationVfxAssetStatus.AnalysisFailed,
                    normalizedPath,
                    $"The AVFX size {rawData.Length:N0} is outside XivBlend's bounded analysis range.");
            }

            bytes = rawData.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(exception, "Could not read animation VFX {GamePath} from SqPack", normalizedPath);
            return EmptyResult(
                AnimationVfxAssetStatus.MissingAsset,
                normalizedPath,
                $"The AVFX could not be read from SqPack: {exception.Message}");
        }

        return ExportBytes(normalizedPath, bytes, buildRoot, cancellationToken);
    }

    /// <summary>
    /// Publishes already-resolved bytes using the same bounded path.  Keeping
    /// this separate makes the cache logic testable and permits a future caller
    /// to pass a Penumbra-resolved AVFX without weakening path validation.
    /// </summary>
    public AnimationVfxAssetResult ExportBytes(
        string gamePath,
        ReadOnlySpan<byte> bytes,
        string buildRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryNormalizeGamePath(gamePath, out var normalizedPath, out var pathError))
        {
            return EmptyResult(
                AnimationVfxAssetStatus.AnalysisFailed,
                gamePath?.Trim() ?? string.Empty,
                pathError);
        }

        if (AvfxAnalyzer.IsSyncControlPath(normalizedPath))
        {
            return EmptyResult(
                AnimationVfxAssetStatus.SyncControl,
                normalizedPath,
                "The shared sync-action AVFX is timeline control metadata and was intentionally filtered.");
        }

        AvfxAnalysis analysis;
        try
        {
            analysis = AvfxAnalyzer.Analyze(bytes, normalizedPath);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or ArgumentException or OverflowException)
        {
            logger.LogWarning(exception, "Animation VFX analysis failed for {GamePath}", normalizedPath);
            return EmptyResult(
                AnimationVfxAssetStatus.AnalysisFailed,
                normalizedPath,
                $"The AVFX failed bounded structural analysis: {exception.Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var contentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var shortHash = contentHash[..ShortHashCharacters];
        string assetDirectory;
        try
        {
            assetDirectory = ResolveAssetDirectory(buildRoot, shortHash);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return BuildResult(
                AnimationVfxAssetStatus.ExportFailed,
                normalizedPath,
                contentHash,
                null,
                null,
                null,
                analysis,
                $"The AVFX cache location is invalid or unavailable: {exception.Message}");
        }

        var assetLock = AssetLocks.GetOrAdd(assetDirectory, static _ => new object());
        lock (assetLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return PublishLocked(
                normalizedPath,
                bytes,
                contentHash,
                assetDirectory,
                analysis,
                cancellationToken);
        }
    }

    private AnimationVfxAssetResult PublishLocked(
        string gamePath,
        ReadOnlySpan<byte> bytes,
        string contentHash,
        string assetDirectory,
        AvfxAnalysis analysis,
        CancellationToken cancellationToken)
    {
        var rawPath = Path.Combine(assetDirectory, RawAssetFileName);
        var previewPath = Path.Combine(assetDirectory, StaticPreviewFileName);
        try
        {
            Directory.CreateDirectory(assetDirectory);
            PublishRawAsset(rawPath, bytes, contentHash, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(exception, "Could not publish raw animation VFX {GamePath}", gamePath);
            return BuildResult(
                AnimationVfxAssetStatus.ExportFailed,
                gamePath,
                contentHash,
                assetDirectory,
                null,
                null,
                analysis,
                $"The exact AVFX bytes could not be preserved: {exception.Message}");
        }

        if (analysis.PreviewStatus != AvfxPreviewStatus.StaticEmbeddedMeshPreview)
        {
            return BuildResult(
                analysis.PreviewStatus == AvfxPreviewStatus.UnsupportedApricot
                    ? AnimationVfxAssetStatus.UnsupportedApricot
                    : AnimationVfxAssetStatus.MetadataOnly,
                gamePath,
                contentHash,
                assetDirectory,
                rawPath,
                null,
                analysis,
                null);
        }

        var tempPreview = Path.Combine(assetDirectory, $".{StaticPreviewFileName}.{Guid.NewGuid():N}.tmp.glb");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            AvfxStaticMeshPreviewExporter.WriteGlb(analysis, tempPreview);
            if (!IsValidGlb(tempPreview))
            {
                throw new InvalidDataException("The static embedded-mesh writer did not produce a valid GLB.");
            }

            File.Move(tempPreview, previewPath, true);
            logger.LogInformation(
                "Published animation VFX {GamePath} as {RawPath} with static mesh preview {PreviewPath}",
                gamePath,
                rawPath,
                previewPath);
            return BuildResult(
                AnimationVfxAssetStatus.StaticEmbeddedMeshPreview,
                gamePath,
                contentHash,
                assetDirectory,
                rawPath,
                previewPath,
                analysis,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(exception, "Could not publish static AVFX mesh preview for {GamePath}", gamePath);
            return BuildResult(
                AnimationVfxAssetStatus.ExportFailed,
                gamePath,
                contentHash,
                assetDirectory,
                rawPath,
                null,
                analysis,
                $"The exact AVFX was preserved, but its static embedded-mesh preview failed: {exception.Message}");
        }
        finally
        {
            TryDelete(tempPreview);
        }
    }

    private static void PublishRawAsset(
        string outputPath,
        ReadOnlySpan<byte> bytes,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        if (FileHasHash(outputPath, bytes.Length, expectedHash)) return;

        var parent = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("The AVFX asset output has no parent directory.");
        var tempPath = Path.Combine(parent, $".{RawAssetFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.SequentialScan))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // This is a private content-addressed cache. If the existing
                // file was truncated or altered, atomically restore the exact
                // SqPack bytes so a cache fault can heal on the next click.
                File.Move(tempPath, outputPath, true);
            }
            catch (IOException) when (FileHasHash(outputPath, bytes.Length, expectedHash))
            {
                // Another exporter won the atomic publication race with the
                // same content, so this is a successful deduplication.
            }

            if (!FileHasHash(outputPath, bytes.Length, expectedHash))
            {
                throw new InvalidDataException("The published AVFX did not retain the source SHA-256 digest.");
            }
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static bool FileHasHash(string path, int expectedLength, string expectedHash)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != expectedLength) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return string.Equals(actualHash, expectedHash, StringComparison.Ordinal);
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

    private static string ResolveAssetDirectory(string buildRoot, string shortHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildRoot);
        if (shortHash.Length != ShortHashCharacters
            || shortHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("The AVFX short content hash is invalid.", nameof(shortHash));
        }

        var root = Path.GetFullPath(buildRoot);
        var assetsRoot = Path.GetFullPath(Path.Combine(root, "assets", "vfx"));
        var directory = Path.GetFullPath(Path.Combine(assetsRoot, shortHash));
        var prefix = assetsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The AVFX asset directory escaped the supplied build root.");
        }

        return directory;
    }

    private static bool TryNormalizeGamePath(
        string? gamePath,
        out string normalizedPath,
        out string? error)
    {
        normalizedPath = gamePath?.Trim().Replace('\\', '/').TrimStart('/') ?? string.Empty;
        if (normalizedPath.Length is < 1 or > MaximumGamePathLength
            || !normalizedPath.StartsWith("vfx/", StringComparison.OrdinalIgnoreCase)
            || !normalizedPath.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.Contains(':')
            || normalizedPath.Contains('\0')
            || normalizedPath.Split('/').Any(segment => segment is "" or "." or "..")
            || normalizedPath.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '-' or '.')))
        {
            error = "The AVFX source is not a safe canonical vfx/.../*.avfx game path.";
            return false;
        }

        normalizedPath = normalizedPath.ToLowerInvariant();
        error = null;
        return true;
    }

    private static AnimationVfxAssetResult BuildResult(
        AnimationVfxAssetStatus status,
        string gamePath,
        string? contentHash,
        string? assetDirectory,
        string? rawPath,
        string? previewPath,
        AvfxAnalysis analysis,
        string? serviceWarning)
    {
        var warnings = analysis.Warnings
            .Append(serviceWarning)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumResultWarnings)
            .ToArray();
        var particleTypes = analysis.ParticleTypeHistogram
            .OrderBy(item => item.Key)
            .Select(item => new AnimationVfxParticleSummary(
                item.Key,
                Enum.IsDefined(typeof(AvfxParticleType), item.Key)
                    ? ((AvfxParticleType)item.Key).ToString()
                    : $"Unknown({item.Key})",
                item.Value))
            .ToArray();

        return new AnimationVfxAssetResult(
            status,
            gamePath,
            contentHash,
            assetDirectory,
            rawPath,
            previewPath,
            previewPath is null ? null : ComputeFileSha256(previewPath),
            analysis.RequiresApricotRuntime,
            analysis.EmbeddedModels.Count,
            analysis.RenderableModelCount,
            analysis.EmbeddedModels.Sum(model => model.Vertices.Count),
            analysis.EmbeddedModels.Sum(model => model.Triangles.Count),
            particleTypes,
            analysis.ReferencedTexturePaths.ToArray(),
            warnings);
    }

    private static AnimationVfxAssetResult EmptyResult(
        AnimationVfxAssetStatus status,
        string gamePath,
        string? warning)
    {
        var warnings = string.IsNullOrWhiteSpace(warning) ? Array.Empty<string>() : new[] { warning };
        return new AnimationVfxAssetResult(
            status,
            gamePath,
            null,
            null,
            null,
            null,
            null,
            false,
            0,
            0,
            0,
            0,
            Array.Empty<AnimationVfxParticleSummary>(),
            Array.Empty<string>(),
            warnings);
    }

    private static bool IsValidGlb(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < 20 or > 536_870_912) return false;
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

    private static string ComputeFileSha256(string path)
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
            // Private content-addressed staging cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Private content-addressed staging cleanup is best effort.
        }
    }
}
