using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Meddle.Plugin.Models;
using Meddle.Utils.Files;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Logging;

namespace Meddle.Plugin.Services;

/// <summary>
/// Imports only the active PAP overrides that Penumbra says are currently
/// winning for the local player. Penumbra remains the option/conflict resolver;
/// XivBlend persists a content-hashed, path-contained snapshot for Blender.
/// </summary>
public sealed class PenumbraAnimationModService : IService
{
    private const int RegistrySchemaVersion = 1;
    private const int ExpectedPenumbraBreakingVersion = 5;
    private const long MaximumPapBytes = 32L * 1024L * 1024L;
    private const long MaximumImportBytes = 512L * 1024L * 1024L;
    private const uint FileFlagBackupSemantics = 0x02000000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ILogger<PenumbraAnimationModService> logger;
    private readonly IObjectTable objectTable;
    private readonly ResolverService resolverService;
    private readonly ICallGateSubscriber<(int Breaking, int Features)> apiVersion;
    private readonly ICallGateSubscriber<Dictionary<string, string>> getModList;
    private readonly ICallGateSubscriber<string, string, (int ErrorCode, string FullPath, bool FullDefault, bool NameDefault)> getModPath;
    private readonly ICallGateSubscriber<int, (bool ObjectValid, bool IndividualSet, (Guid Id, string Name) EffectiveCollection)> getCollectionForObject;
    private readonly ICallGateSubscriber<
        Guid,
        string,
        string,
        bool,
        bool,
        int,
        (int ErrorCode,
            (bool Enabled,
             int Priority,
             Dictionary<string, List<string>> Options,
             bool Inherited,
             bool Temporary)? Settings)> getCurrentModSettingsWithTemp;
    private readonly ICallGateSubscriber<
        Guid,
        string[],
        string[],
        (int ErrorCode, string[] ForwardResolved, string[][] ReverseResolved)> resolvePaths;
    private readonly object registryLock = new();

    private IReadOnlyList<PenumbraAnimationModChoice> installedMods = [];
    private CustomAnimationRegistry registry;

    public PenumbraAnimationModService(
        ILogger<PenumbraAnimationModService> logger,
        IObjectTable objectTable,
        ResolverService resolverService,
        IDalamudPluginInterface pluginInterface)
    {
        this.logger = logger;
        this.objectTable = objectTable;
        this.resolverService = resolverService;
        apiVersion = pluginInterface.GetIpcSubscriber<(int, int)>("Penumbra.ApiVersion.V5");
        getModList = pluginInterface.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");
        getModPath = pluginInterface.GetIpcSubscriber<string, string, (int, string, bool, bool)>("Penumbra.GetModPath.V5");
        getCollectionForObject = pluginInterface.GetIpcSubscriber<int, (bool, bool, (Guid, string))>(
            "Penumbra.GetCollectionForObject.V5");
        getCurrentModSettingsWithTemp = pluginInterface.GetIpcSubscriber<
            Guid,
            string,
            string,
            bool,
            bool,
            int,
            (int, (bool, int, Dictionary<string, List<string>>, bool, bool)?)>(
            "Penumbra.GetCurrentModSettingsWithTemp");
        resolvePaths = pluginInterface.GetIpcSubscriber<
            Guid,
            string[],
            string[],
            (int, string[], string[][])>("Penumbra.ResolvePaths");
        registry = LoadRegistry();
    }

    public IReadOnlyList<PenumbraAnimationModChoice> InstalledMods => installedMods;
    public IReadOnlyList<PenumbraAnimationModChoice> ImportedSources
    {
        get
        {
            lock (registryLock)
            {
                return registry.Sources
                    .Select(item => new PenumbraAnimationModChoice(item.ModIdentifier, item.DisplayName))
                    .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
            }
        }
    }
    public bool IsAvailable { get; private set; }
    public string Status { get; private set; } = "Choose Refresh to read installed Penumbra animation mods.";
    public string? LastError { get; private set; }
    public int ImportedSourceCount
    {
        get
        {
            lock (registryLock)
            {
                return registry.Sources.Count;
            }
        }
    }

    private static string RegistryPath => Path.Combine(
        AnimationLibraryService.LibraryRoot,
        "custom-sources.json");

    public bool RefreshInstalledMods()
    {
        LastError = null;
        try
        {
            var version = apiVersion.InvokeFunc();
            if (version.Breaking != ExpectedPenumbraBreakingVersion)
            {
                throw new InvalidOperationException(
                    $"Penumbra IPC {version.Breaking}.{version.Features} is incompatible; XivBlend expects API 5.x.");
            }

            installedMods = getModList.InvokeFunc()
                .Select(item => new PenumbraAnimationModChoice(item.Key, item.Value))
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.ModIdentifier, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            IsAvailable = true;
            Status = installedMods.Count == 0
                ? "Penumbra is connected, but it reported no installed mods."
                : $"Found {installedMods.Count} installed Penumbra mods.";
            return true;
        }
        catch (Exception exception)
        {
            IsAvailable = false;
            installedMods = [];
            LastError = "Penumbra IPC is unavailable. Install/enable Penumbra, then Refresh.";
            Status = "Could not read Penumbra's installed mods.";
            logger.LogDebug(exception, "Could not enumerate Penumbra animation mods");
            return false;
        }
    }

    /// <summary>
    /// Must be called from Dalamud's framework/UI thread because it reads the
    /// local player and invokes Penumbra's object/collection IPC.
    /// </summary>
    public unsafe bool ImportActiveOverrides(
        string modIdentifier,
        IReadOnlyList<AnimationCatalogEntry> vanillaEntries)
    {
        LastError = null;
        try
        {
            if (string.IsNullOrWhiteSpace(modIdentifier))
            {
                throw new InvalidOperationException("Choose a Penumbra mod first.");
            }

            var version = apiVersion.InvokeFunc();
            if (version.Breaking != ExpectedPenumbraBreakingVersion)
            {
                throw new InvalidOperationException(
                    $"Penumbra IPC {version.Breaking}.{version.Features} is incompatible; XivBlend expects API 5.x.");
            }

            var localPlayer = objectTable.LocalPlayer
                ?? throw new InvalidOperationException("Log in and wait until your own character is visible.");
            var character = (Character*)localPlayer.Address;
            if (character == null || character->DrawObject == null)
            {
                throw new InvalidOperationException("Your character is not fully drawn yet. Wait a moment and retry.");
            }

            var parsed = resolverService.ParseCharacter(character)
                ?? throw new InvalidOperationException("XivBlend could not identify your current character rig.");
            var targetRace = checked((ushort)parsed.GenderRace);

            var collectionResult = getCollectionForObject.InvokeFunc(localPlayer.ObjectIndex);
            if (!collectionResult.ObjectValid || collectionResult.EffectiveCollection.Id == Guid.Empty)
            {
                throw new InvalidOperationException("Penumbra did not return an effective collection for your character.");
            }

            var collection = collectionResult.EffectiveCollection;
            var settingsResult = getCurrentModSettingsWithTemp.InvokeFunc(
                collection.Id,
                modIdentifier,
                string.Empty,
                false,
                false,
                0);
            if (settingsResult.ErrorCode != 0 || settingsResult.Settings is not { Enabled: true } settings)
            {
                throw new InvalidOperationException(
                    "That mod is not enabled in your character's effective Penumbra collection.");
            }

            var displayName = installedMods
                .FirstOrDefault(item => string.Equals(
                    item.ModIdentifier,
                    modIdentifier,
                    StringComparison.Ordinal))
                ?.DisplayName
                ?? getModList.InvokeFunc().GetValueOrDefault(modIdentifier)
                ?? modIdentifier;
            var modRoot = ResolveModRoot(modIdentifier);
            var canonicalModRoot = ResolveFinalDirectoryPath(modRoot);
            var candidates = BuildCandidates(vanillaEntries, targetRace);
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException("The vanilla emote catalog contains no compatible body PAP paths.");
            }

            Status = $"Asking Penumbra which {displayName} animation files are active...";
            var resolved = resolvePaths.InvokeFunc(
                collection.Id,
                candidates.Select(item => item.CanonicalGamePath).ToArray(),
                []);
            if (resolved.ErrorCode != 0 || resolved.ForwardResolved.Length != candidates.Count)
            {
                throw new InvalidOperationException(
                    $"Penumbra could not resolve the animation paths (error {resolved.ErrorCode}).");
            }

            var winners = new Dictionary<string, CustomAnimationBinding>(StringComparer.Ordinal);
            var fileIdentities = new Dictionary<string, PapFileIdentity>(StringComparer.OrdinalIgnoreCase);
            long totalImportedBytes = 0;
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                var resolvedPath = resolved.ForwardResolved[index];
                if (!TryResolveContainedFile(modRoot, resolvedPath, out var physicalPath, out var relativePath))
                {
                    continue;
                }

                if (!fileIdentities.TryGetValue(physicalPath, out var fileIdentity))
                {
                    fileIdentity = ReadContainedPapIdentity(physicalPath, canonicalModRoot);
                    totalImportedBytes = checked(totalImportedBytes + fileIdentity.ContentLength);
                    if (totalImportedBytes > MaximumImportBytes)
                    {
                        throw new InvalidDataException(
                            $"The selected mod resolves to more than {MaximumImportBytes / 1024 / 1024} MiB of unique PAP data.");
                    }

                    fileIdentities.Add(physicalPath, fileIdentity);
                }

                var identity = $"{modIdentifier}\n{targetRace}\n{candidate.BaseEntry.EntryId}\n{candidate.CanonicalGamePath}\n{fileIdentity.ContentSha256}";
                var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20]
                    .ToLowerInvariant();
                var binding = new CustomAnimationBinding(
                    EntryId: $"custom:{digest}",
                    VariantId: $"custom-{digest}",
                    BaseEntryId: candidate.BaseEntry.EntryId,
                    BaseEmoteId: candidate.BaseEntry.EmoteId ?? 0,
                    TargetRaceCode: targetRace,
                    SourceRaceCode: candidate.SourceRaceCode,
                    CanonicalGamePath: candidate.CanonicalGamePath,
                    ModRelativePath: relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                    ContentSha256: fileIdentity.ContentSha256,
                    ContentLength: fileIdentity.ContentLength);

                // Candidates are ordered race-specific first, common fallback
                // second, so the authored target-race PAP wins when both exist.
                winners.TryAdd(candidate.BaseEntry.EntryId, binding);
            }

            if (winners.Count == 0)
            {
                throw new InvalidOperationException(
                    "Penumbra found no active winning emote PAP from that mod for your current character. " +
                    "Enable the desired option, resolve any conflicts, then try again.");
            }

            var selectedOptions = settings.Options
                .OrderBy(item => item.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => $"{item.Key}: {string.Join(", ", item.Value)}")
                .ToArray();
            var source = new CustomAnimationSource(
                ModIdentifier: modIdentifier,
                DisplayName: displayName,
                ImportedAtUtc: DateTimeOffset.UtcNow,
                CollectionId: collection.Id,
                CollectionName: collection.Name,
                SelectedOptions: selectedOptions,
                Bindings: winners.Values.OrderBy(item => item.BaseEmoteId).ToArray());
            lock (registryLock)
            {
                var existingSource = registry.Sources.FirstOrDefault(item => string.Equals(
                    item.ModIdentifier,
                    modIdentifier,
                    StringComparison.Ordinal));
                if (existingSource is not null)
                {
                    source = source with
                    {
                        Bindings = existingSource.Bindings
                            .Where(item => item.TargetRaceCode != targetRace)
                            .Concat(source.Bindings)
                            .OrderBy(item => item.TargetRaceCode)
                            .ThenBy(item => item.BaseEmoteId)
                            .ToArray(),
                    };
                }

                var nextRegistry = registry with
                {
                    Sources = registry.Sources
                        .Where(item => !string.Equals(
                            item.ModIdentifier,
                            modIdentifier,
                            StringComparison.Ordinal))
                        .Append(source)
                        .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                        .ToArray(),
                };
                SaveRegistry(nextRegistry);
                registry = nextRegistry;
            }

            IsAvailable = true;
            Status = $"Added {winners.Count} active animation override(s) from {displayName}. Refreshing the Blender catalog makes them available under Custom.";
            return true;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            Status = "Custom animation import failed.";
            logger.LogError(exception, "Could not import active Penumbra animation overrides from {Mod}", modIdentifier);
            return false;
        }
    }

    public bool RemoveSource(string modIdentifier)
    {
        LastError = null;
        try
        {
            lock (registryLock)
            {
                var remaining = registry.Sources
                    .Where(item => !string.Equals(item.ModIdentifier, modIdentifier, StringComparison.Ordinal))
                    .ToArray();
                if (remaining.Length == registry.Sources.Count)
                {
                    return false;
                }

                var nextRegistry = registry with { Sources = remaining };
                SaveRegistry(nextRegistry);
                registry = nextRegistry;
            }

            Status = "Removed the custom animation source from XivBlend's catalog. The Penumbra mod itself was not changed.";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LastError = exception.Message;
            Status = "Could not update the saved custom animation list.";
            logger.LogError(exception, "Could not remove custom animation source {Mod}", modIdentifier);
            return false;
        }
    }

    public IReadOnlyList<AnimationCatalogEntry> BuildCatalogEntries(
        IReadOnlyList<AnimationCatalogEntry> vanillaEntries)
    {
        CustomAnimationRegistry snapshot;
        lock (registryLock)
        {
            snapshot = registry;
        }

        var vanillaById = vanillaEntries
            .Where(item => string.Equals(item.SourceKind, "Vanilla", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.EntryId, StringComparer.Ordinal);
        var entries = new List<AnimationCatalogEntry>();
        foreach (var source in snapshot.Sources)
        {
            foreach (var binding in source.Bindings)
            {
                if (!binding.EntryId.StartsWith("custom:", StringComparison.Ordinal)
                    || binding.EntryId.Length != "custom:".Length + 20
                    || binding.EntryId["custom:".Length..].Any(character => !Uri.IsHexDigit(character))
                    || !binding.VariantId.Equals(
                        $"custom-{binding.EntryId["custom:".Length..]}",
                        StringComparison.Ordinal)
                    || binding.BaseEmoteId == 0)
                {
                    logger.LogWarning(
                        "Skipping malformed custom animation registry binding {EntryId}",
                        binding.EntryId);
                    continue;
                }

                if (!vanillaById.TryGetValue(binding.BaseEntryId, out var baseEntry))
                {
                    continue;
                }

                var baseVariant = baseEntry.Variants.FirstOrDefault(
                    item => item.VariantId == baseEntry.DefaultVariantId && item.Kind == "Body")
                    ?? baseEntry.Variants.FirstOrDefault(item => item.Kind == "Body");
                if (baseVariant is null)
                {
                    continue;
                }

                var variant = new AnimationCatalogVariant(
                    VariantId: binding.VariantId,
                    ActionTimelineId: baseVariant.ActionTimelineId,
                    Slot: baseVariant.Slot,
                    Label: "Active Penumbra override",
                    Kind: "Body",
                    Key: baseVariant.Key,
                    TimelineKey: baseVariant.TimelineKey,
                    LoadType: baseVariant.LoadType,
                    IsDefault: true,
                    IsLoop: baseVariant.IsLoop,
                    CacheRelativePathTemplate:
                        $"clips/custom/{binding.EntryId["custom:".Length..]}/{{race}}/{{faceKey}}/{binding.VariantId}.glb",
                    BundleRelativePathTemplate:
                        $"bundles/custom/{binding.EntryId["custom:".Length..]}/{{race}}/{{faceKey}}/{binding.VariantId}.json",
                    ModIdentifier: source.ModIdentifier,
                    CanonicalGamePath: binding.CanonicalGamePath,
                    ModRelativePath: binding.ModRelativePath,
                    ContentSha256: binding.ContentSha256,
                    SourceRaceCode: binding.SourceRaceCode,
                    CompatibleRaceCodes: [binding.TargetRaceCode]);
                entries.Add(new AnimationCatalogEntry(
                    EntryId: binding.EntryId,
                    EmoteId: binding.BaseEmoteId,
                    Name: $"{source.DisplayName} — {baseEntry.Name}",
                    Command: baseEntry.Command,
                    IconId: baseEntry.IconId,
                    ResolvedIconId: baseEntry.ResolvedIconId,
                    IconRelativePath: baseEntry.IconRelativePath,
                    Category: "Custom",
                    SourceKind: "PenumbraMod",
                    SourceDisplayName: source.DisplayName,
                    DefaultVariantId: binding.VariantId,
                    Variants: [variant]));
            }
        }

        return entries;
    }

    public TrustedCustomPap ReadTrustedPap(AnimationCatalogVariant variant, ushort requestedRaceCode)
    {
        if (variant.ModIdentifier is null
            || variant.ModRelativePath is null
            || variant.ContentSha256 is null
            || variant.SourceRaceCode is null
            || variant.CompatibleRaceCodes is null
            || !variant.CompatibleRaceCodes.Contains(requestedRaceCode))
        {
            throw new InvalidDataException("The custom animation catalog binding is incomplete or incompatible with this rig.");
        }

        CustomAnimationBinding binding;
        lock (registryLock)
        {
            binding = registry.Sources
                .Where(item => string.Equals(item.ModIdentifier, variant.ModIdentifier, StringComparison.Ordinal))
                .SelectMany(item => item.Bindings)
                .FirstOrDefault(item => string.Equals(item.VariantId, variant.VariantId, StringComparison.Ordinal))
                ?? throw new InvalidDataException("The custom animation binding is no longer trusted. Refresh that mod.");
        }

        var modRoot = ResolveModRoot(variant.ModIdentifier);
        var canonicalModRoot = ResolveFinalDirectoryPath(modRoot);
        var candidate = Path.GetFullPath(Path.Combine(
            modRoot,
            binding.ModRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(candidate, modRoot) || !File.Exists(candidate))
        {
            throw new FileNotFoundException(
                "The imported animation file moved or left its Penumbra mod directory. Refresh the custom source.",
                candidate);
        }

        var bytes = ReadContainedAndValidatePap(candidate, canonicalModRoot);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (bytes.LongLength != binding.ContentLength
            || !string.Equals(hash, binding.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The imported animation changed since it was trusted. Refresh the custom source before playing it.");
        }

        return new TrustedCustomPap(
            $"Penumbra/{variant.ModIdentifier}/{binding.ModRelativePath}",
            bytes,
            binding.SourceRaceCode);
    }

    private string ResolveModRoot(string modIdentifier)
    {
        var result = getModPath.InvokeFunc(modIdentifier, string.Empty);
        if (result.ErrorCode != 0 || string.IsNullOrWhiteSpace(result.FullPath))
        {
            throw new DirectoryNotFoundException(
                $"Penumbra could not locate mod '{modIdentifier}' (error {result.ErrorCode}).");
        }

        var root = Path.GetFullPath(result.FullPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Penumbra's mod directory no longer exists: {root}");
        }

        return root;
    }

    private static List<AnimationCandidate> BuildCandidates(
        IReadOnlyList<AnimationCatalogEntry> vanillaEntries,
        ushort targetRace)
    {
        var result = new List<AnimationCandidate>();
        foreach (var entry in vanillaEntries
                     .Where(item => string.Equals(item.SourceKind, "Vanilla", StringComparison.OrdinalIgnoreCase)))
        {
            var variant = entry.Variants.FirstOrDefault(
                item => item.VariantId == entry.DefaultVariantId && item.Kind == "Body")
                ?? entry.Variants.FirstOrDefault(item => item.Kind == "Body");
            if (variant is null)
            {
                continue;
            }

            result.Add(new AnimationCandidate(
                entry,
                targetRace,
                $"chara/human/c{targetRace:D4}/animation/a0001/bt_common/{variant.Key}.pap"));
            if (targetRace != 101)
            {
                result.Add(new AnimationCandidate(
                    entry,
                    101,
                    $"chara/human/c0101/animation/a0001/bt_common/{variant.Key}.pap"));
            }
        }

        return result;
    }

    private static bool TryResolveContainedFile(
        string modRoot,
        string resolvedValue,
        out string physicalPath,
        out string relativePath)
    {
        physicalPath = string.Empty;
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(resolvedValue)
            || !Path.IsPathRooted(resolvedValue)
            || !resolvedValue.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var candidate = Path.GetFullPath(resolvedValue);
            if (!File.Exists(candidate) || !IsInside(candidate, modRoot))
            {
                return false;
            }

            var relative = Path.GetRelativePath(modRoot, candidate);
            if (Path.IsPathRooted(relative)
                || relative.Equals("..", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return false;
            }

            physicalPath = candidate;
            relativePath = relative;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static PapFileIdentity ReadContainedPapIdentity(string path, string canonicalModRoot)
    {
        using var stream = OpenContainedPap(path, canonicalModRoot);
        ValidatePapHeader(stream, path);
        stream.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new PapFileIdentity(hash, stream.Length);
    }

    private static byte[] ReadContainedAndValidatePap(string path, string canonicalModRoot)
    {
        using var stream = OpenContainedPap(path, canonicalModRoot);
        ValidatePapHeader(stream, path);
        stream.Position = 0;
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        _ = new PapFile(bytes);
        return bytes;
    }

    private static FileStream OpenContainedPap(string path, string canonicalModRoot)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        try
        {
            var finalPath = ResolveFinalPath(stream.SafeFileHandle);
            if (!IsInside(finalPath, canonicalModRoot))
            {
                throw new InvalidDataException(
                    "The resolved PAP leaves the selected Penumbra mod directory through a link or junction.");
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void ValidatePapHeader(FileStream stream, string path)
    {
        if (stream.Length is < 32 or > MaximumPapBytes)
        {
            throw new InvalidDataException(
                $"Custom PAP '{Path.GetFileName(path)}' has invalid size {stream.Length} bytes; " +
                $"the safety limit is {MaximumPapBytes / 1024 / 1024} MiB.");
        }

        Span<byte> header = stackalloc byte[26];
        stream.Position = 0;
        stream.ReadExactly(header);
        var animationCount = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        var havokOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[18..]);
        var footerOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[22..]);
        var animationTableEnd = checked(26L + animationCount * 40L);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != PapFile.PapMagic
            || animationCount is 0 or > 4096
            || havokOffset < animationTableEnd
            || footerOffset <= havokOffset
            || footerOffset > stream.Length)
        {
            throw new InvalidDataException("The resolved custom animation has an invalid PAP header.");
        }
    }

    private static string ResolveFinalDirectoryPath(string path)
    {
        using var handle = CreateFileW(
            path,
            0,
            (uint)(FileShare.ReadWrite | FileShare.Delete),
            IntPtr.Zero,
            (uint)FileMode.Open,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not securely open mod directory '{path}'.");
        }

        return ResolveFinalPath(handle);
    }

    private static string ResolveFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= 32_768)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)capacity, 0);
            if (length == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve a trusted final filesystem path.");
            }

            if (length < capacity)
            {
                var result = buffer.ToString();
                if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    return @"\\" + result[8..];
                }

                return result.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
                    ? result[4..]
                    : result;
            }

            capacity = checked((int)length + 1);
        }

        throw new PathTooLongException("The resolved custom PAP path is too long.");
    }

    private static bool IsInside(string path, string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private CustomAnimationRegistry LoadRegistry()
    {
        try
        {
            var info = new FileInfo(RegistryPath);
            if (!info.Exists)
            {
                return new CustomAnimationRegistry(RegistrySchemaVersion, []);
            }

            if (info.Length is <= 0 or > 16_777_216)
            {
                throw new InvalidDataException("Custom animation registry has an invalid size.");
            }

            var loaded = JsonSerializer.Deserialize<CustomAnimationRegistry>(
                File.ReadAllText(RegistryPath),
                JsonOptions);
            if (loaded is null
                || loaded.SchemaVersion != RegistrySchemaVersion
                || loaded.Sources is null
                || loaded.Sources.Count > 1_024
                || loaded.Sources.Any(source =>
                    source is null
                    || string.IsNullOrWhiteSpace(source.ModIdentifier)
                    || source.ModIdentifier.Length > 1_024
                    || string.IsNullOrWhiteSpace(source.DisplayName)
                    || source.DisplayName.Length > 1_024
                    || source.SelectedOptions is null
                    || source.SelectedOptions.Count > 4_096
                    || source.Bindings is null
                    || source.Bindings.Count > 16_384
                    || source.Bindings.Any(binding =>
                        binding is null
                        || string.IsNullOrWhiteSpace(binding.EntryId)
                        || string.IsNullOrWhiteSpace(binding.VariantId)
                        || string.IsNullOrWhiteSpace(binding.BaseEntryId)
                        || binding.BaseEmoteId == 0
                        || !PlayerRaceCode(binding.TargetRaceCode)
                        || !PlayerRaceCode(binding.SourceRaceCode)
                        || string.IsNullOrWhiteSpace(binding.CanonicalGamePath)
                        || binding.CanonicalGamePath.Length > 4_096
                        || string.IsNullOrWhiteSpace(binding.ModRelativePath)
                        || binding.ModRelativePath.Length > 32_768
                        || Path.IsPathRooted(binding.ModRelativePath)
                        || binding.ContentSha256 is null
                        || binding.ContentSha256.Length != 64
                        || binding.ContentSha256.Any(character => !Uri.IsHexDigit(character))
                        || binding.ContentLength is < 32 or > MaximumPapBytes)))
            {
                throw new InvalidDataException("Custom animation registry uses an unsupported schema.");
            }

            return loaded;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            logger.LogWarning(exception, "Ignoring invalid XivBlend custom animation registry {Path}", RegistryPath);
            LastError = "The saved custom animation list was invalid and has been ignored.";
            return new CustomAnimationRegistry(RegistrySchemaVersion, []);
        }
    }

    private static void SaveRegistry(CustomAnimationRegistry value)
    {
        var parent = Path.GetDirectoryName(RegistryPath)!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".custom-sources.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(true);
            }

            File.Move(temporary, RegistryPath, true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // Private temporary cleanup is best effort.
            }
        }
    }

    private static bool PlayerRaceCode(ushort value) =>
        value is >= 101 and <= 1801 && value % 100 == 1;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    private sealed record AnimationCandidate(
        AnimationCatalogEntry BaseEntry,
        ushort SourceRaceCode,
        string CanonicalGamePath);

    private sealed record PapFileIdentity(string ContentSha256, long ContentLength);
}
