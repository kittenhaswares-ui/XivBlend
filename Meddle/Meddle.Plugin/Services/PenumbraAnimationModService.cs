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
    private const int RegistrySchemaVersion = 2;
    private const int ExpectedPenumbraBreakingVersion = 5;
    private const long MaximumPapBytes = 32L * 1024L * 1024L;
    private const long MaximumImportBytes = 512L * 1024L * 1024L;
    private const int MaximumMetadataFiles = 512;
    private const long MaximumMetadataFileBytes = 16L * 1024L * 1024L;
    private const long MaximumMetadataBytes = 64L * 1024L * 1024L;
    private const int MaximumDiscoveredPapPaths = 4_096;
    private const int MaximumRegistryTextLength = 4_096;
    private const int MaximumSelectedOptions = 4_096;
    private const int MaximumSelectedOptionCharacters = 1_048_576;
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
    private readonly ICallGateSubscriber<string> getModDirectory;
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
        getModDirectory = pluginInterface.GetIpcSubscriber<string>("Penumbra.GetModDirectory");
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
            if (displayName.Length > 1_024
                || collection.Name is null
                || collection.Name.Length > MaximumRegistryTextLength)
            {
                throw new InvalidDataException("Penumbra returned animation-mod metadata that exceeds XivBlend's safety limits.");
            }

            var modRoot = ResolveModRoot(modIdentifier);
            var canonicalModRoot = ResolveFinalDirectoryPath(modRoot);
            var discoveredPaths = DiscoverManifestBodyPapPaths(
                modRoot,
                canonicalModRoot,
                targetRace);
            var candidates = BuildCandidates(vanillaEntries, targetRace, discoveredPaths);
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException("No compatible player body PAP paths were available to check.");
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

                var identity = $"{modIdentifier}\n{targetRace}\n{candidate.BaseEntry?.EntryId ?? candidate.AnimationKey}\n{candidate.CanonicalGamePath}\n{fileIdentity.ContentSha256}";
                var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20]
                    .ToLowerInvariant();
                var binding = new CustomAnimationBinding(
                    EntryId: $"custom:{digest}",
                    VariantId: $"custom-{digest}",
                    BaseEntryId: candidate.BaseEntry?.EntryId,
                    BaseEmoteId: candidate.BaseEntry?.EmoteId,
                    TargetRaceCode: targetRace,
                    SourceRaceCode: candidate.SourceRaceCode,
                    CanonicalGamePath: candidate.CanonicalGamePath,
                    ModRelativePath: relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                    ContentSha256: fileIdentity.ContentSha256,
                    ContentLength: fileIdentity.ContentLength);

                // Candidates are ordered race-specific first, common fallback
                // second, so the authored target-race PAP wins when both exist.
                winners.TryAdd(candidate.WinnerKey, binding);
            }

            if (winners.Count == 0)
            {
                throw new InvalidOperationException(
                    "Penumbra found no active winning player-animation PAP from that mod for your current character. " +
                    "Enable the desired option, resolve any conflicts, then try again.");
            }

            var selectedOptions = FormatSelectedOptions(settings.Options);
            var source = new CustomAnimationSource(
                ModIdentifier: modIdentifier,
                DisplayName: displayName,
                ImportedAtUtc: DateTimeOffset.UtcNow,
                CollectionId: collection.Id,
                CollectionName: collection.Name,
                SelectedOptions: selectedOptions,
                Bindings: winners.Values
                    .OrderBy(item => item.BaseEmoteId ?? uint.MaxValue)
                    .ThenBy(item => item.CanonicalGamePath, StringComparer.Ordinal)
                    .ToArray());
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
                            .ThenBy(item => item.BaseEmoteId ?? uint.MaxValue)
                            .ThenBy(item => item.CanonicalGamePath, StringComparer.Ordinal)
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
        var genericPoseIcon = vanillaEntries.FirstOrDefault(item => item.Command == "/pose")
                              ?? vanillaEntries.FirstOrDefault(item => item.Command == "/groundsit")
                              ?? vanillaEntries.FirstOrDefault(item => item.Command == "/lounge")
                              ?? vanillaEntries.FirstOrDefault();
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
                        StringComparison.Ordinal))
                {
                    logger.LogWarning(
                        "Skipping malformed custom animation registry binding {EntryId}",
                        binding.EntryId);
                    continue;
                }

                if (!PenumbraAnimationManifestDiscovery.TryParseCanonicalBodyPapPath(
                        binding.CanonicalGamePath,
                        out var papReference))
                {
                    logger.LogWarning(
                        "Skipping custom animation binding {EntryId} with invalid body PAP path {Path}",
                        binding.EntryId,
                        binding.CanonicalGamePath);
                    continue;
                }

                AnimationCatalogEntry? baseEntry = null;
                AnimationCatalogVariant? baseVariant = null;
                if (!string.IsNullOrWhiteSpace(binding.BaseEntryId)
                    && vanillaById.TryGetValue(binding.BaseEntryId, out baseEntry))
                {
                    baseVariant = baseEntry.Variants.FirstOrDefault(
                        item => item.VariantId == baseEntry.DefaultVariantId && item.Kind == "Body")
                        ?? baseEntry.Variants.FirstOrDefault(item => item.Kind == "Body");
                }

                var animationKey = papReference.AnimationKey;
                var isStandalone = baseEntry is null || baseVariant is null;
                var iconEntry = isStandalone
                    ? SelectStandaloneIcon(vanillaEntries, animationKey) ?? genericPoseIcon
                    : baseEntry;
                var variant = new AnimationCatalogVariant(
                    VariantId: binding.VariantId,
                    ActionTimelineId: baseVariant?.ActionTimelineId ?? 0,
                    Slot: baseVariant?.Slot ?? 0,
                    Label: isStandalone ? "Active standalone Penumbra animation" : "Active Penumbra override",
                    Kind: "Body",
                    Key: baseVariant?.Key ?? animationKey,
                    TimelineKey: baseVariant?.TimelineKey ?? animationKey,
                    LoadType: baseVariant?.LoadType ?? 0,
                    IsDefault: true,
                    IsLoop: baseVariant?.IsLoop
                            ?? animationKey.EndsWith("_loop", StringComparison.OrdinalIgnoreCase),
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
                    EmoteId: isStandalone ? null : binding.BaseEmoteId,
                    Name: isStandalone
                        ? StandaloneDisplayName(source, animationKey)
                        : $"{source.DisplayName} — {baseEntry!.Name}",
                    Command: isStandalone ? string.Empty : baseEntry!.Command,
                    IconId: iconEntry?.IconId ?? 0,
                    ResolvedIconId: iconEntry?.ResolvedIconId ?? 0,
                    IconRelativePath: iconEntry?.IconRelativePath ?? string.Empty,
                    Category: "Custom",
                    SourceKind: "PenumbraMod",
                    SourceDisplayName: source.DisplayName,
                    DefaultVariantId: binding.VariantId,
                    Variants: [variant]));
            }
        }

        return entries;
    }

    private static AnimationCatalogEntry? SelectStandaloneIcon(
        IReadOnlyList<AnimationCatalogEntry> vanillaEntries,
        string animationKey)
    {
        if (animationKey.Contains("sit", StringComparison.OrdinalIgnoreCase)
            || animationKey.Contains("pose", StringComparison.OrdinalIgnoreCase))
        {
            return vanillaEntries.FirstOrDefault(item => item.Command == "/groundsit")
                   ?? vanillaEntries.FirstOrDefault(item => item.Command == "/lounge")
                   ?? vanillaEntries.FirstOrDefault(item => item.Command == "/pose");
        }

        return vanillaEntries.FirstOrDefault(item => item.Command == "/pose");
    }

    private static IReadOnlyList<string> FormatSelectedOptions(
        IReadOnlyDictionary<string, List<string>> options)
    {
        if (options.Count > MaximumSelectedOptions)
        {
            throw new InvalidDataException(
                $"Penumbra returned more than {MaximumSelectedOptions:N0} selected animation-mod option groups.");
        }

        var result = new List<string>(options.Count);
        var totalCharacters = 0;
        foreach (var item in options.OrderBy(
                     value => value.Key,
                     StringComparer.CurrentCultureIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(item.Key)
                || item.Key.Length > MaximumRegistryTextLength
                || item.Value is null
                || item.Value.Count > MaximumSelectedOptions
                || item.Value.Any(value => value is null || value.Length > MaximumRegistryTextLength))
            {
                throw new InvalidDataException("Penumbra returned invalid selected animation-mod option metadata.");
            }

            var text = $"{item.Key}: {string.Join(", ", item.Value)}";
            if (text.Length > MaximumRegistryTextLength)
            {
                throw new InvalidDataException("A selected animation-mod option description exceeds XivBlend's safety limit.");
            }

            totalCharacters = checked(totalCharacters + text.Length);
            if (totalCharacters > MaximumSelectedOptionCharacters)
            {
                throw new InvalidDataException("Selected animation-mod option metadata exceeds XivBlend's total safety limit.");
            }

            result.Add(text);
        }

        return result;
    }

    private static string StandaloneDisplayName(CustomAnimationSource source, string animationKey)
    {
        if (source.Bindings.Count == 1)
        {
            return source.DisplayName;
        }

        var leaf = animationKey.Split('/').LastOrDefault() ?? animationKey;
        var friendly = string.Join(' ', leaf.Split('_', StringSplitOptions.RemoveEmptyEntries));
        return $"{source.DisplayName} — {friendly}";
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
        if (!IsSingleDirectoryName(modIdentifier))
        {
            throw new InvalidDataException("Penumbra returned an unsafe mod directory identifier.");
        }

        var baseValue = getModDirectory.InvokeFunc();
        if (string.IsNullOrWhiteSpace(baseValue) || !Path.IsPathRooted(baseValue))
        {
            throw new DirectoryNotFoundException("Penumbra did not return a valid physical mod directory.");
        }

        var baseRoot = Path.GetFullPath(baseValue);
        if (!Directory.Exists(baseRoot))
        {
            throw new DirectoryNotFoundException($"Penumbra's physical mod directory does not exist: {baseRoot}");
        }

        var root = Path.GetFullPath(Path.Combine(baseRoot, modIdentifier));
        if (!IsInside(root, baseRoot))
        {
            throw new InvalidDataException("The selected Penumbra mod path leaves its physical mod directory.");
        }

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Penumbra's mod directory no longer exists: {root}");
        }

        return root;
    }

    private static List<AnimationCandidate> BuildCandidates(
        IReadOnlyList<AnimationCatalogEntry> vanillaEntries,
        ushort targetRace,
        IReadOnlyList<PenumbraBodyPapReference> discoveredPaths)
    {
        var result = new List<AnimationCandidate>();
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            AddCatalogCandidate(result, knownPaths, entry, variant, targetRace);
            if (targetRace != 101)
            {
                AddCatalogCandidate(result, knownPaths, entry, variant, 101);
            }
        }

        foreach (var discovered in discoveredPaths)
        {
            if (knownPaths.Contains(discovered.CanonicalGamePath))
            {
                continue;
            }

            result.Add(new AnimationCandidate(
                BaseEntry: null,
                SourceRaceCode: discovered.SourceRaceCode,
                CanonicalGamePath: discovered.CanonicalGamePath,
                AnimationKey: discovered.AnimationKey,
                WinnerKey: $"pap:{discovered.AnimationKey}"));
        }

        return result;
    }

    private static void AddCatalogCandidate(
        ICollection<AnimationCandidate> result,
        ISet<string> knownPaths,
        AnimationCatalogEntry entry,
        AnimationCatalogVariant variant,
        ushort sourceRaceCode)
    {
        var gamePath = $"chara/human/c{sourceRaceCode:D4}/animation/a0001/bt_common/{variant.Key}.pap";
        if (!PenumbraAnimationManifestDiscovery.TryParseCanonicalBodyPapPath(gamePath, out var parsed))
        {
            return;
        }

        result.Add(new AnimationCandidate(
            BaseEntry: entry,
            SourceRaceCode: sourceRaceCode,
            CanonicalGamePath: parsed.CanonicalGamePath,
            AnimationKey: parsed.AnimationKey,
            WinnerKey: $"base:{entry.EntryId}"));
        knownPaths.Add(parsed.CanonicalGamePath);
    }

    private static IReadOnlyList<PenumbraBodyPapReference> DiscoverManifestBodyPapPaths(
        string modRoot,
        string canonicalModRoot,
        ushort targetRaceCode)
    {
        var metadataFiles = new List<string>();
        var defaultMetadata = Path.Combine(modRoot, "default_mod.json");
        if (File.Exists(defaultMetadata))
        {
            metadataFiles.Add(defaultMetadata);
        }

        foreach (var path in Directory.EnumerateFiles(modRoot, "group_*.json", SearchOption.TopDirectoryOnly))
        {
            metadataFiles.Add(path);
            if (metadataFiles.Count > MaximumMetadataFiles)
            {
                throw new InvalidDataException(
                    $"The selected mod contains more than {MaximumMetadataFiles:N0} Penumbra metadata files.");
            }
        }

        metadataFiles.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(
            Path.GetFileName(left),
            Path.GetFileName(right)));

        long totalBytes = 0;
        var discovered = new Dictionary<string, PenumbraBodyPapReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var metadataPath in metadataFiles)
        {
            using var stream = OpenContainedMetadata(metadataPath, modRoot, canonicalModRoot);
            if (stream.Length is <= 0 or > MaximumMetadataFileBytes)
            {
                throw new InvalidDataException(
                    $"Penumbra metadata '{Path.GetFileName(metadataPath)}' has invalid size {stream.Length:N0} bytes.");
            }

            totalBytes = checked(totalBytes + stream.Length);
            if (totalBytes > MaximumMetadataBytes)
            {
                throw new InvalidDataException(
                    $"The selected mod contains more than {MaximumMetadataBytes / 1024 / 1024} MiB of Penumbra metadata.");
            }

            IReadOnlyList<PenumbraBodyPapReference> fileReferences;
            try
            {
                fileReferences = PenumbraAnimationManifestDiscovery.ReadBodyPapReferences(
                    stream,
                    targetRaceCode,
                    MaximumDiscoveredPapPaths);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Penumbra metadata '{Path.GetFileName(metadataPath)}' is invalid JSON.",
                    exception);
            }

            foreach (var reference in fileReferences)
            {
                discovered.TryAdd(reference.CanonicalGamePath, reference);
                if (discovered.Count > MaximumDiscoveredPapPaths)
                {
                    throw new InvalidDataException(
                        $"The selected mod exceeds the {MaximumDiscoveredPapPaths:N0}-path animation safety limit.");
                }
            }
        }

        return discovered.Values
            .OrderBy(item => item.SourceRaceCode == targetRaceCode ? 0 : 1)
            .ThenBy(item => item.CanonicalGamePath, StringComparer.Ordinal)
            .ToArray();
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

    private static FileStream OpenContainedMetadata(
        string path,
        string modRoot,
        string canonicalModRoot)
    {
        var lexicalPath = Path.GetFullPath(path);
        if (!File.Exists(lexicalPath) || !IsInside(lexicalPath, modRoot))
        {
            throw new InvalidDataException("Penumbra metadata leaves the selected mod directory.");
        }

        var stream = new FileStream(
            lexicalPath,
            FileMode.Open,
            FileAccess.Read,
            // Keep the bounded length check stable for the entire JSON parse.
            // If Penumbra is replacing this file concurrently, the user can
            // retry after that short update instead of parsing a growing file.
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        try
        {
            var finalPath = ResolveFinalPath(stream.SafeFileHandle);
            if (!IsInside(finalPath, canonicalModRoot))
            {
                throw new InvalidDataException(
                    "Penumbra metadata leaves the selected mod directory through a link or junction.");
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

    private static bool IsSingleDirectoryName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value is "." or ".."
            || Path.IsPathRooted(value)
            || value.IndexOf(Path.DirectorySeparatorChar) >= 0
            || value.IndexOf(Path.AltDirectorySeparatorChar) >= 0
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        return string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal);
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
                || loaded.SchemaVersion is not 1 and not RegistrySchemaVersion
                || loaded.Sources is null
                || loaded.Sources.Count > 1_024
                || loaded.Sources.Any(source => !ValidRegistrySource(source, loaded.SchemaVersion == 1)))
            {
                throw new InvalidDataException("Custom animation registry uses an unsupported schema.");
            }

            return loaded.SchemaVersion == RegistrySchemaVersion
                ? loaded
                : loaded with { SchemaVersion = RegistrySchemaVersion };
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or InvalidDataException
                or ArgumentException
                or OverflowException)
        {
            logger.LogWarning(exception, "Ignoring invalid XivBlend custom animation registry {Path}", RegistryPath);
            LastError = "The saved custom animation list was invalid and has been ignored.";
            return new CustomAnimationRegistry(RegistrySchemaVersion, []);
        }
    }

    private static bool ValidRegistrySource(CustomAnimationSource? source, bool legacySchema)
    {
        if (source is null
            || !IsSingleDirectoryName(source.ModIdentifier)
            || source.ModIdentifier.Length > 1_024
            || string.IsNullOrWhiteSpace(source.DisplayName)
            || source.DisplayName.Length > 1_024
            || source.CollectionName is null
            || source.CollectionName.Length > 4_096
            || source.SelectedOptions is null
            || source.SelectedOptions.Count > MaximumSelectedOptions
            || source.SelectedOptions.Any(item => item is null || item.Length > MaximumRegistryTextLength)
            || source.SelectedOptions.Sum(item => (long)item.Length) > MaximumSelectedOptionCharacters
            || source.Bindings is null
            || source.Bindings.Count > 16_384
            || source.Bindings.Any(binding => !ValidRegistryBinding(binding, legacySchema)))
        {
            return false;
        }

        return source.Bindings.Select(item => item.EntryId).Distinct(StringComparer.Ordinal).Count()
               == source.Bindings.Count;
    }

    private static bool ValidRegistryBinding(CustomAnimationBinding? binding, bool legacySchema)
    {
        if (binding is null) return false;
        var entryId = binding.EntryId;
        var variantId = binding.VariantId;
        if (string.IsNullOrWhiteSpace(entryId)
            || !entryId.StartsWith("custom:", StringComparison.Ordinal)
            || entryId.Length != "custom:".Length + 20
            || entryId["custom:".Length..].Any(character => !Uri.IsHexDigit(character))
            || string.IsNullOrWhiteSpace(variantId)
            || !variantId.Equals(
                $"custom-{entryId["custom:".Length..]}",
                StringComparison.Ordinal)
            || !PlayerRaceCode(binding.TargetRaceCode)
            || !PlayerRaceCode(binding.SourceRaceCode)
            || string.IsNullOrWhiteSpace(binding.CanonicalGamePath)
            || binding.CanonicalGamePath.Length > 4_096
            || !PenumbraAnimationManifestDiscovery.TryParseCanonicalBodyPapPath(
                binding.CanonicalGamePath,
                out var papReference)
            || papReference.SourceRaceCode != binding.SourceRaceCode
            || string.IsNullOrWhiteSpace(binding.ModRelativePath)
            || binding.ModRelativePath.Length > 32_768
            || !IsSafeRelativeFilePath(binding.ModRelativePath)
            || binding.ContentSha256 is null
            || binding.ContentSha256.Length != 64
            || binding.ContentSha256.Any(character => !Uri.IsHexDigit(character))
            || binding.ContentLength is < 32 or > MaximumPapBytes)
        {
            return false;
        }

        var hasBaseEntry = !string.IsNullOrWhiteSpace(binding.BaseEntryId);
        var hasBaseEmote = binding.BaseEmoteId is > 0;
        return legacySchema
            ? hasBaseEntry && hasBaseEmote
            : hasBaseEntry == hasBaseEmote;
    }

    private static bool IsSafeRelativeFilePath(string value)
    {
        if (Path.IsPathRooted(value) || value.IndexOf('\0') >= 0)
        {
            return false;
        }

        var segments = value.Replace('\\', '/').Split('/');
        return segments.Length > 0 && segments.All(segment => segment is not "" and not "." and not "..");
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
        AnimationCatalogEntry? BaseEntry,
        ushort SourceRaceCode,
        string CanonicalGamePath,
        string AnimationKey,
        string WinnerKey);

    private sealed record PapFileIdentity(string ContentSha256, long ContentLength);
}
