using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Meddle.Plugin.Models;
using Meddle.Utils;
using Meddle.Utils.Files;
using Meddle.Utils.Files.SqPack;
using Meddle.Utils.Helpers;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Meddle.Plugin.Services;

/// <summary>
/// Builds and serves a tightly scoped, local-only library of player emote
/// bundles read from the live SqPack. Body and facial PAPs share their original
/// TMB timing; visible emote event metadata is preserved for Blender-side
/// playback. Combat actions, weapon timelines, mounts, movement sets and NPC
/// animations are never queried. Current Dalamud builds cannot prove that a live SqPack index
/// has not previously been modified by TexTools; that boundary is surfaced in
/// the UI and documentation rather than presented as a cryptographic guarantee.
/// </summary>
public sealed partial class AnimationLibraryService : IService, IDisposable
{
    public const int CatalogSchemaVersion = 2;
    public const int QueueSchemaVersion = 2;
    public const int BundleSchemaVersion = 2;

    private const long MaximumBundleJsonBytes = 4_194_304;
    private const int MaximumBundleLayers = 1_024;
    private const int MaximumBundleVisualEffects = 4_096;
    private const int MaximumBundleVfxParticleTypes = 256;
    private const int MaximumBundleVfxTextureReferences = 4_096;
    private const int MaximumBundleVfxTypeNameLength = 128;
    private const int MaximumBundleVfxGamePathLength = 512;
    private const int MaximumBundleProps = 1_024;
    private const int MaximumBundleWarnings = 256;
    private const int MaximumBundleWarningLength = 4_096;
    private const int MaximumBundleFrame = 10_000_000;
    private const int MaximumUniqueFacialTracks = 128;
    private const long MaximumBundleSampledTransforms = 4_000_000;

    private const string ScopeDescription =
        "Vanilla player emotes and facial expressions: primary icon-click body motion, synchronized facial PAP layers, " +
        "visible prop/VFX timeline metadata, and explicitly imported active Penumbra body-PAP overrides. " +
        "Combat, job/weapon timelines, mounts, movement and NPC animation are excluded.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly HashSet<ushort> PlayerRaceCodes =
    [
        101, 201, 301, 401, 501, 601, 701, 801, 901,
        1001, 1101, 1201, 1301, 1401, 1501, 1601, 1701, 1801,
    ];

    private readonly ILogger<AnimationLibraryService> logger;
    private readonly IDataManager dataManager;
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly SqPack sqPack;
    private readonly VanillaPapAnimationExporter papExporter;
    private readonly AnimationPropAssetExporter propAssetExporter;
    private readonly AnimationVfxAssetExporter vfxAssetExporter;
    private readonly PenumbraAnimationModService customMods;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly CancellationTokenSource disposeToken = new();
    private readonly object stateLock = new();

    private AnimationCatalog? catalog;
    private Task prepareTask = Task.CompletedTask;
    private Task requestTask = Task.CompletedTask;
    private DateTime nextQueueScanUtc = DateTime.MinValue;
    private int iconProgress;
    private int iconTotal;
    private bool disposed;

    public AnimationLibraryService(
        ILogger<AnimationLibraryService> logger,
        IDataManager dataManager,
        IClientState clientState,
        IFramework framework,
        SqPack sqPack,
        VanillaPapAnimationExporter papExporter,
        AnimationPropAssetExporter propAssetExporter,
        AnimationVfxAssetExporter vfxAssetExporter,
        PenumbraAnimationModService customMods)
    {
        this.logger = logger;
        this.dataManager = dataManager;
        this.clientState = clientState;
        this.framework = framework;
        this.sqPack = sqPack;
        this.papExporter = papExporter;
        this.propAssetExporter = propAssetExporter;
        this.vfxAssetExporter = vfxAssetExporter;
        this.customMods = customMods;

        Directory.CreateDirectory(LibraryRoot);
        Directory.CreateDirectory(RequestsDirectory);
        Directory.CreateDirectory(ResponsesDirectory);
        Directory.CreateDirectory(ProcessingDirectory);
        framework.Update += OnFrameworkUpdate;
    }

    public static string LibraryRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XivBlend",
        "AnimationLibrary");

    public static string RequestsDirectory => Path.Combine(LibraryRoot, "requests");
    public static string ResponsesDirectory => Path.Combine(LibraryRoot, "responses");
    private static string ProcessingDirectory => Path.Combine(LibraryRoot, "processing");

    public bool IsPreparing => !prepareTask.IsCompleted;
    public bool IsServingRequest => !requestTask.IsCompleted;
    public bool HasModifiedGameData => dataManager.HasModifiedGameDataFiles;
    public string Status { get; private set; } = "Animation library has not been prepared yet.";
    public string? LastError { get; private set; }
    public int CatalogEntryCount => catalog?.Entries.Count ?? 0;
    public int IconProgress => Volatile.Read(ref iconProgress);
    public int IconTotal => Volatile.Read(ref iconTotal);

    public IReadOnlyList<AnimationCatalogEntry> GetVanillaEntriesSnapshot()
    {
        lock (stateLock)
        {
            if (catalog is not null)
            {
                return catalog.Entries
                    .Where(item => string.Equals(item.SourceKind, "Vanilla", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
        }

        return BuildCatalog().Entries
            .Where(item => string.Equals(item.SourceKind, "Vanilla", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public bool StartPrepareLibrary()
    {
        if (IsPreparing)
        {
            Status = "The animation catalog is already being prepared.";
            return false;
        }

        LastError = null;
        if (HasModifiedGameData)
        {
            LastError =
                "Dalamud reports modified live SqPack files. Restore TexTools index modifications before " +
                "building this strictly vanilla animation library; Penumbra itself is bypassed.";
            Status = "Vanilla animation library preparation was refused.";
            return false;
        }

        AnimationCatalog nextCatalog;
        try
        {
            Status = "Reading the vanilla player emote catalog...";
            nextCatalog = BuildCatalog();
            lock (stateLock)
            {
                catalog = nextCatalog;
            }
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            Status = "Animation catalog could not be read.";
            logger.LogError(exception, "Could not build the XivBlend animation catalog");
            return false;
        }

        prepareTask = Task.Run(
            () => PrepareLibraryFilesAsync(nextCatalog, disposeToken.Token),
            disposeToken.Token);
        return true;
    }

    public string GetLibrarySummary()
    {
        var source = HasModifiedGameData
            ? "Dalamud reports modified live SqPacks, so extraction is blocked."
            : "Penumbra is bypassed; current Dalamud cannot independently verify TexTools index integrity.";
        return $"{CatalogEntryCount} animation cards (vanilla plus explicitly imported custom sources). {source}";
    }

    private AnimationCatalog BuildCatalog()
    {
        var entries = new List<AnimationCatalogEntry>();
        var emotes = dataManager.GetExcelSheet<Emote>();

        foreach (var emote in emotes)
        {
            var name = emote.Name.ToString().Trim();
            if (emote.RowId == 0
                || string.IsNullOrWhiteSpace(name)
                || emote.Icon == 0
                || emote.DrawsWeapon
                || emote.TextCommand.RowId == 0
                || emote.EmoteCategory.RowId is < 1 or > 3
                || emote.ActionTimeline.Count == 0)
            {
                continue;
            }

            var command = emote.TextCommand.ValueNullable?.Command.ToString().Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(command)
                || command.Equals("/draw", StringComparison.OrdinalIgnoreCase)
                || command.Equals("/sheathe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Slot zero is the animation the game associates with the normal
            // icon click.  Intro/ground/chair/upper-body slots are deliberately
            // deferred so v1 has one predictable action per icon.
            var timelineReference = emote.ActionTimeline[0];
            var timeline = timelineReference.ValueNullable;
            if (timeline is null)
            {
                continue;
            }

            var timelineKey = timeline.Value.Key.ToString().Trim().Replace('\\', '/');
            var key = timelineKey;
            var kind = ResolveKind(timeline.Value.LoadType, key);
            if (kind is null)
            {
                continue;
            }

            if (kind == "Face")
            {
                key = key["facial/pose/".Length..];
            }

            if (!SafeAnimationKey().IsMatch(key))
            {
                logger.LogWarning(
                    "Skipping emote {EmoteId} because its timeline key is not a safe game path: {Key}",
                    emote.RowId,
                    key);
                continue;
            }

            var variantId = $"emote-{emote.RowId:D4}-timeline-{timeline.Value.RowId:D5}";
            var cacheTemplate = kind == "Face"
                ? $"clips/{{race}}/{{face}}/face/{variantId}.glb"
                : $"clips/{{race}}/{{faceKey}}/body/{variantId}.glb";
            var bundleTemplate = kind == "Face"
                ? $"bundles/{{race}}/{{face}}/face/{variantId}.json"
                : $"bundles/{{race}}/{{faceKey}}/body/{variantId}.json";
            var variant = new AnimationCatalogVariant(
                VariantId: variantId,
                ActionTimelineId: timeline.Value.RowId,
                Slot: 0,
                Label: "Default / Loop",
                Kind: kind,
                Key: key,
                TimelineKey: timelineKey,
                LoadType: timeline.Value.LoadType,
                IsDefault: true,
                IsLoop: timeline.Value.IsLoop,
                CacheRelativePathTemplate: cacheTemplate,
                BundleRelativePathTemplate: bundleTemplate);

            var resolvedIconId = emote.Icon;
            var category = emote.EmoteCategory.ValueNullable?.Name.ToString().Trim();
            if (string.IsNullOrWhiteSpace(category))
            {
                category = emote.EmoteCategory.RowId switch
                {
                    1 => "General",
                    2 => "Special",
                    3 => "Expressions",
                    _ => "Other",
                };
            }

            entries.Add(new AnimationCatalogEntry(
                EntryId: $"vanilla:emote:{emote.RowId}",
                EmoteId: emote.RowId,
                Name: name,
                Command: command,
                IconId: emote.Icon,
                ResolvedIconId: resolvedIconId,
                IconRelativePath: $"icons/{resolvedIconId:D6}.png",
                Category: category,
                SourceKind: "Vanilla",
                SourceDisplayName: "FINAL FANTASY XIV",
                DefaultVariantId: variantId,
                Variants: [variant]));
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException("The live Emote sheet produced no scoped player animations.");
        }

        var vanillaEntries = entries.ToArray();
        entries.AddRange(customMods.BuildCatalogEntries(vanillaEntries));

        return new AnimationCatalog(
            SchemaVersion: CatalogSchemaVersion,
            ConverterVersion: VanillaPapAnimationExporter.ConverterVersion,
            GameVersion: ReadGameVersion(),
            Language: clientState.ClientLanguage.ToString(),
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Scope: ScopeDescription,
            Entries: entries.OrderBy(entry => entry.EmoteId ?? uint.MaxValue).ToArray());
    }

    private async Task PrepareLibraryFilesAsync(AnimationCatalog preparedCatalog, CancellationToken cancellationToken)
    {
        try
        {
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var buildRoot = GetBuildRoot(preparedCatalog);
                var iconDirectory = Path.Combine(buildRoot, "icons");
                Directory.CreateDirectory(buildRoot);
                Directory.CreateDirectory(iconDirectory);

                var catalogPath = Path.Combine(buildRoot, "catalog.json");
                AtomicWriteJson(catalogPath, preparedCatalog);

                var uniqueIcons = preparedCatalog.Entries
                    .Select(entry => entry.ResolvedIconId)
                    .Distinct()
                    .Order()
                    .ToArray();
                Volatile.Write(ref iconProgress, 0);
                Volatile.Write(ref iconTotal, uniqueIcons.Length);

                foreach (var iconId in uniqueIcons)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Status = $"Extracting vanilla game icons ({IconProgress}/{IconTotal})...";
                    var outputPath = Path.Combine(iconDirectory, $"{iconId:D6}.png");
                    if (!File.Exists(outputPath))
                    {
                        ExportVanillaIcon(iconId, outputPath);
                    }

                    Interlocked.Increment(ref iconProgress);
                }

                var relativeCatalogPath = Path.GetRelativePath(LibraryRoot, catalogPath)
                    .Replace(Path.DirectorySeparatorChar, '/');
                var pointer = new AnimationLibraryPointer(
                    SchemaVersion: CatalogSchemaVersion,
                    GameVersion: preparedCatalog.GameVersion,
                    Language: preparedCatalog.Language,
                    CatalogRelativePath: relativeCatalogPath);
                AtomicWriteJson(Path.Combine(LibraryRoot, "current.json"), pointer);

                var customCount = preparedCatalog.Entries.Count(item =>
                    !string.Equals(item.SourceKind, "Vanilla", StringComparison.OrdinalIgnoreCase));
                Status = customCount == 0
                    ? $"Animation browser ready: {preparedCatalog.Entries.Count} vanilla player emotes."
                    : $"Animation browser ready: {preparedCatalog.Entries.Count - customCount} vanilla emotes and {customCount} custom animation(s).";
                logger.LogInformation(
                    "Prepared XivBlend animation catalog {GameVersion}/{Language}: {Count} entries",
                    preparedCatalog.GameVersion,
                    preparedCatalog.Language,
                    preparedCatalog.Entries.Count);
            }
            finally
            {
                operationGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Animation library preparation cancelled.";
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            Status = "Animation library preparation failed.";
            logger.LogError(exception, "Could not prepare XivBlend animation library files");
        }
    }

    private void ExportVanillaIcon(uint iconId, string outputPath)
    {
        var group = iconId / 1000 * 1000;
        var iconPath = $"ui/icon/{group:D6}/{iconId:D6}_hr1.tex";
        var descriptor = sqPack.GetFile(iconPath)
            ?? throw new FileNotFoundException($"Vanilla emote icon {iconId} is missing.", iconPath);

        var tex = new TexFile(descriptor.File.RawData);
        var texture = tex.ToResource().ToTexture();
        using var bitmap = texture.Bitmap;
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException($"Could not encode vanilla icon {iconId} as PNG.");

        var parent = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("Icon output has no parent directory.");
        Directory.CreateDirectory(parent);
        var tempPath = Path.Combine(parent, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                data.SaveTo(stream);
                stream.Flush(true);
            }

            File.Move(tempPath, outputPath, true);
        }
        finally
        {
            TryDeletePrivateFile(tempPath);
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (disposed || IsServingRequest || DateTime.UtcNow < nextQueueScanUtc)
        {
            return;
        }

        nextQueueScanUtc = DateTime.UtcNow.AddMilliseconds(750);
        string? requestPath;
        try
        {
            requestPath = Directory.EnumerateFiles(RequestsDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(File.GetCreationTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException exception)
        {
            logger.LogDebug(exception, "Could not scan XivBlend animation request queue");
            return;
        }

        if (requestPath is null)
        {
            return;
        }

        var processingPath = Path.Combine(
            ProcessingDirectory,
            $"{Path.GetFileNameWithoutExtension(requestPath)}-{Guid.NewGuid():N}.json");
        try
        {
            File.Move(requestPath, processingPath);
        }
        catch (IOException)
        {
            return;
        }

        requestTask = Task.Run(
            () => ProcessRequestAsync(processingPath, disposeToken.Token),
            disposeToken.Token);
    }

    private async Task ProcessRequestAsync(string processingPath, CancellationToken cancellationToken)
    {
        AnimationLibraryRequest? request = null;
        try
        {
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var requestLength = new FileInfo(processingPath).Length;
                if (requestLength is <= 0 or > 65_536)
                {
                    throw new InvalidDataException("Animation request JSON has an invalid size.");
                }

                request = JsonSerializer.Deserialize<AnimationLibraryRequest>(
                    await File.ReadAllTextAsync(processingPath, cancellationToken).ConfigureAwait(false),
                    JsonOptions)
                    ?? throw new InvalidDataException("Animation request JSON is empty.");
                ValidateRequest(request);

                var activeCatalog = await GetOrBuildCatalogForQueueAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(request.GameVersion, activeCatalog.GameVersion, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"The Blender file requested game build {request.GameVersion}, but XivBlend is on " +
                        $"{activeCatalog.GameVersion}. Refresh the animation browser catalog.");
                }

                var entry = FindRequestedEntry(activeCatalog, request)
                    ?? throw new KeyNotFoundException("The requested animation entry is not in the current player catalog.");
                var variant = entry.Variants.FirstOrDefault(item => item.VariantId == request.VariantId)
                    ?? throw new KeyNotFoundException($"Animation variant '{request.VariantId}' was not found.");
                ValidateVariantRequest(request, variant);

                if (entry.SourceKind == "Vanilla" && HasModifiedGameData)
                {
                    throw new InvalidOperationException(
                        "Dalamud reports modified live SqPack files. XivBlend refused this vanilla request; " +
                        "disable or restore TexTools index modifications first.");
                }

                var buildRoot = GetBuildRoot(activeCatalog);
                var relativeClipPath = ResolveClipRelativePath(variant, request);
                var outputPath = ResolveInside(buildRoot, relativeClipPath);
                var relativeBundlePath = ResolveBundleRelativePath(variant, request);
                var bundlePath = ResolveInside(buildRoot, relativeBundlePath);
                var cachedBundle = ReadValidBundle(bundlePath, entry, variant, request);
                if (cachedBundle is not null)
                {
                    WriteResponse(new AnimationLibraryResponse(
                        SchemaVersion: QueueSchemaVersion,
                        RequestId: request.RequestId,
                        Status: "ready",
                        ClipRelativePath: ToLibraryRelative(outputPath),
                        BundleRelativePath: ToLibraryRelative(bundlePath),
                        Error: null,
                        FrameCount: cachedBundle.FrameEnd - cachedBundle.FrameStart + 1,
                        DurationSeconds: (cachedBundle.FrameEnd - cachedBundle.FrameStart)
                            / (float)cachedBundle.FramesPerSecond,
                        AnimatedBoneCount: null,
                        SourcePap: cachedBundle.Layers.FirstOrDefault()?.SourcePap,
                        Warnings: cachedBundle.Warnings));
                    Status = $"Animation cache hit: {entry.Name}.";
                    return;
                }

                Status = $"Decoding synchronized animation bundle: {entry.Name}...";
                var built = await BuildAnimationBundleAsync(
                        activeCatalog,
                        entry,
                        variant,
                        request,
                        outputPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(bundlePath)!);
                AtomicWriteJson(bundlePath, built.Manifest, MaximumBundleJsonBytes);

                WriteResponse(new AnimationLibraryResponse(
                    SchemaVersion: QueueSchemaVersion,
                    RequestId: request.RequestId,
                    Status: "ready",
                    ClipRelativePath: ToLibraryRelative(outputPath),
                    BundleRelativePath: ToLibraryRelative(bundlePath),
                    Error: null,
                    FrameCount: built.Manifest.FrameEnd - built.Manifest.FrameStart + 1,
                    DurationSeconds: (built.Manifest.FrameEnd - built.Manifest.FrameStart)
                        / (float)built.Manifest.FramesPerSecond,
                    AnimatedBoneCount: null,
                    SourcePap: built.Manifest.Layers.FirstOrDefault()?.SourcePap,
                    Warnings: built.Manifest.Warnings));
                Status = $"Animation ready in Blender: {entry.Name}.";
            }
            finally
            {
                operationGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Animation request cancelled.";
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            Status = "Animation request failed.";
            logger.LogError(exception, "Could not serve Blender animation request {Path}", processingPath);
            if (request is not null && Guid.TryParseExact(request.RequestId, "D", out _))
            {
                WriteResponse(new AnimationLibraryResponse(
                    SchemaVersion: QueueSchemaVersion,
                    RequestId: request.RequestId,
                    Status: "error",
                    ClipRelativePath: null,
                    BundleRelativePath: null,
                    Error: exception.Message,
                    FrameCount: null,
                    DurationSeconds: null,
                    AnimatedBoneCount: null,
                    SourcePap: null));
            }
        }
        finally
        {
            TryDeletePrivateFile(processingPath);
        }
    }

    private async Task<BuiltAnimationBundle> BuildAnimationBundleAsync(
        AnimationCatalog activeCatalog,
        AnimationCatalogEntry entry,
        AnimationCatalogVariant variant,
        AnimationLibraryRequest request,
        string primaryOutputPath,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var layers = new List<AnimationBundleLayer>();
        var visualEffects = new List<AnimationBundleVisualEvent>();
        var props = new List<AnimationBundlePropEvent>();
        var source = string.Equals(entry.SourceKind, "Vanilla", StringComparison.OrdinalIgnoreCase)
            ? ResolveVanillaSource(request, variant)
            : string.Equals(entry.SourceKind, "PenumbraMod", StringComparison.OrdinalIgnoreCase)
                ? ResolveCustomSource(request, variant)
                : throw new InvalidOperationException($"Unsupported animation source '{entry.SourceKind}'.");
        TmbTimelineFile? actionTimeline = null;
        TmbTimelineFile? embeddedTimeline = null;
        string requestedTrack = variant.Key;
        var exactTrack = false;

        if (variant.Kind == "Body")
        {
            // Standalone custom PAPs discovered from Penumbra metadata have no
            // corresponding ActionTimeline sheet row. Their embedded PAP
            // timeline remains authoritative, so do not invent a vanilla TMB
            // lookup (or a misleading missing-TMB warning) for timeline ID 0.
            if (variant.ActionTimelineId != 0)
            {
                actionTimeline = TryReadVanillaTimeline(
                    $"chara/action/{variant.TimelineKey}.tmb",
                    warnings,
                    "The emote action timeline could not be read");
            }

            var bodyEvent = actionTimeline?.Animations
                .Where(item => item.Magic == "C010")
                .OrderBy(item => item.Time)
                .ThenBy(item => item.TrackOrder ?? int.MaxValue)
                .FirstOrDefault(item => item.Path.StartsWith("cbem_", StringComparison.OrdinalIgnoreCase))
                ?? actionTimeline?.Animations
                    .Where(item => item.Magic == "C010")
                    .OrderBy(item => item.Time)
                    .FirstOrDefault();
            if (bodyEvent is not null)
            {
                requestedTrack = bodyEvent.Path;
                exactTrack = true;
            }
            else if (variant.ActionTimelineId != 0)
            {
                warnings.Add("The TMB did not name a primary body track; XivBlend used its deterministic PAP fallback.");
            }
        }

        var actionName = $"XIV Emote - {entry.Name}";
        SampledAnimation primary;
        try
        {
            primary = await SampleOnFrameworkThreadAsync(
                    source,
                    actionName,
                    requestedTrack,
                    variant.Kind == "Face",
                    exactTrack,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException) when (
            exactTrack
            && string.Equals(entry.SourceKind, "PenumbraMod", StringComparison.OrdinalIgnoreCase))
        {
            // Animation replacers frequently preserve the canonical PAP path
            // but rename the single internal Havok track. For an explicitly
            // trusted mod source, deterministic type-zero fallback is the
            // useful behavior; facial event tracks remain strict.
            warnings.Add(
                $"The mod renamed body track '{requestedTrack}'; XivBlend selected its primary type-zero PAP track instead.");
            primary = await SampleOnFrameworkThreadAsync(
                    source,
                    actionName,
                    variant.Key,
                    false,
                    false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        await WriteAnimationGlbAsync(primary, primaryOutputPath, cancellationToken).ConfigureAwait(false);

        var primaryFrames = Math.Max(1, primary.FrameCount - 1);
        layers.Add(new AnimationBundleLayer(
            Kind: variant.Kind,
            ClipRelativePath: ToLibraryRelative(primaryOutputPath),
            StartFrame: 0,
            DurationFrames: primaryFrames,
            SourceStartFrame: 0.0f,
            SourceEndFrame: primaryFrames,
            TrackOrder: -1,
            ItemOrder: -1,
            SourcePap: source.PapPath,
            SourceAnimation: primary.SourceAnimationName));

        if (variant.Kind == "Body")
        {
            try
            {
                var pap = new PapFile(source.PapBytes);
                embeddedTimeline = new TmbTimelineFile(pap.GetTimelineData(primary.SourceAnimationIndex));
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentOutOfRangeException)
            {
                warnings.Add($"The body PAP timeline could not be read, so facial and visible events were skipped: {exception.Message}");
            }

            if (embeddedTimeline is not null)
            {
                AddVisibleTimelineEvents(
                    embeddedTimeline,
                    visualEffects,
                    props,
                    warnings,
                    request.RaceCode,
                    GetBuildRoot(activeCatalog),
                    cancellationToken);
                await AddFacialLayersAsync(
                        activeCatalog,
                        entry,
                        request,
                        actionTimeline,
                        embeddedTimeline,
                        layers,
                        warnings,
                        checked((long)primary.FrameCount * primary.AnimatedBoneCount),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var frameEnd = Math.Max(primaryFrames, embeddedTimeline?.TimelineLengthFrames ?? 0);
        foreach (var layer in layers)
        {
            frameEnd = Math.Max(frameEnd, checked(layer.StartFrame + layer.DurationFrames));
        }

        foreach (var item in visualEffects)
        {
            frameEnd = Math.Max(frameEnd, checked(item.StartFrame + Math.Max(0, item.DurationFrames)));
        }

        foreach (var item in props)
        {
            frameEnd = Math.Max(frameEnd, checked(item.StartFrame + Math.Max(0, item.DurationFrames)));
        }

        var manifest = new AnimationBundleManifest(
            SchemaVersion: BundleSchemaVersion,
            ConverterVersion: VanillaPapAnimationExporter.ConverterVersion,
            GameVersion: activeCatalog.GameVersion,
            EntryId: entry.EntryId,
            VariantId: variant.VariantId,
            RaceCode: request.RaceCode,
            FaceSkeleton: request.FaceSkeleton ?? string.Empty,
            DisplayName: entry.Name,
            SourceKind: entry.SourceKind,
            FramesPerSecond: VanillaPapAnimationExporter.FramesPerSecond,
            FrameStart: 0,
            FrameEnd: frameEnd,
            IsLoop: variant.IsLoop,
            Layers: layers,
            VisualEffects: visualEffects,
            Props: props,
            Warnings: NormalizeBundleWarnings(warnings));
        if (!HasValidBundleShape(manifest, entry, variant, request))
        {
            throw new InvalidDataException("The generated animation bundle failed XivBlend's safety validation.");
        }

        foreach (var layer in manifest.Layers)
        {
            if (!IsValidAnimationGlb(ResolveInside(LibraryRoot, layer.ClipRelativePath)))
            {
                throw new InvalidDataException(
                    $"The generated animation layer '{layer.ClipRelativePath}' is missing or invalid.");
            }
        }

        foreach (var prop in manifest.Props.Where(item =>
                     item.AssetStatus == AnimationPropAssetStatuses.Ready))
        {
            if (!AnimationPropAssetExporter.IsValidPublishedAsset(
                    ResolveInside(LibraryRoot, prop.AssetRelativePath!),
                    ResolveInside(LibraryRoot, prop.AssetCacheRelativePath!)))
            {
                throw new InvalidDataException(
                    $"The generated prop asset '{prop.AssetRelativePath}' is missing or invalid.");
            }
        }

        foreach (var visualEffect in manifest.VisualEffects)
        {
            if (!IsValidBundleVfxAsset(visualEffect))
            {
                throw new InvalidDataException(
                    $"The generated VFX metadata or asset '{visualEffect.GamePath}' is missing or invalid.");
            }
        }

        return new BuiltAnimationBundle(manifest);
    }

    private async Task AddFacialLayersAsync(
        AnimationCatalog activeCatalog,
        AnimationCatalogEntry entry,
        AnimationLibraryRequest request,
        TmbTimelineFile? actionTimeline,
        TmbTimelineFile embeddedTimeline,
        ICollection<AnimationBundleLayer> layers,
        ICollection<string> warnings,
        long retainedSampledTransforms,
        CancellationToken cancellationToken)
    {
        var allFaceEvents = embeddedTimeline.Animations
            .Where(item => item.Path.StartsWith("cfxf_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Time)
            .ThenBy(item => item.TrackOrder ?? int.MaxValue)
            .ThenBy(item => item.ItemOrder)
            .ToArray();
        if (allFaceEvents.Length == 0)
        {
            return;
        }

        var availableLayerSlots = Math.Max(0, MaximumBundleLayers - layers.Count);
        var faceEvents = allFaceEvents.Take(availableLayerSlots).ToArray();
        if (faceEvents.Length < allFaceEvents.Length)
        {
            warnings.Add(
                $"{allFaceEvents.Length - faceEvents.Length:N0} facial timeline event(s) exceeded the per-emote safety limit and were skipped.");
        }

        if (string.IsNullOrWhiteSpace(request.FaceSkeleton))
        {
            warnings.Add("This exported rig has no face skeleton tag, so synchronized facial motion was skipped.");
            return;
        }

        var faceLibrary = actionTimeline?.FaceLibrary;
        var sampledByTrack = new Dictionary<
            string,
            (SampledAnimation Sampled, string Path, ResolvedAnimationSource Source)>(
            StringComparer.OrdinalIgnoreCase);
        var attemptedTracks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rejectedTracks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueTrackLimitReported = false;
        var transformLimitReported = false;
        foreach (var faceEvent in faceEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rejectedTracks.Contains(faceEvent.Path))
            {
                continue;
            }

            if (!sampledByTrack.TryGetValue(faceEvent.Path, out var cached))
            {
                if (retainedSampledTransforms >= MaximumBundleSampledTransforms)
                {
                    rejectedTracks.Add(faceEvent.Path);
                    if (!transformLimitReported)
                    {
                        warnings.Add(
                            $"Additional facial motion was skipped after the {MaximumBundleSampledTransforms:N0}-transform per-emote safety budget.");
                        transformLimitReported = true;
                    }

                    continue;
                }

                if (attemptedTracks.Count >= MaximumUniqueFacialTracks)
                {
                    rejectedTracks.Add(faceEvent.Path);
                    if (!uniqueTrackLimitReported)
                    {
                        warnings.Add(
                            $"Additional facial tracks were skipped after the {MaximumUniqueFacialTracks:N0}-track per-emote safety limit.");
                        uniqueTrackLimitReported = true;
                    }

                    continue;
                }

                attemptedTracks.Add(faceEvent.Path);

                try
                {
                    var faceSource = ResolveFaceTrackSource(request, faceLibrary, faceEvent.Path);
                    var sampled = await SampleOnFrameworkThreadAsync(
                            faceSource,
                            $"XIV Face - {entry.Name} - {faceEvent.Path}",
                            faceEvent.Path,
                            true,
                            true,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var sampledTransforms = checked((long)sampled.FrameCount * sampled.AnimatedBoneCount);
                    if (sampledTransforms > MaximumBundleSampledTransforms - retainedSampledTransforms)
                    {
                        rejectedTracks.Add(faceEvent.Path);
                        if (!transformLimitReported)
                        {
                            warnings.Add(
                                $"Additional facial motion was skipped after the {MaximumBundleSampledTransforms:N0}-transform per-emote safety budget.");
                            transformLimitReported = true;
                        }

                        continue;
                    }

                    retainedSampledTransforms += sampledTransforms;
                    var clipPath = ResolveFaceLayerPath(
                        GetBuildRoot(activeCatalog),
                        request,
                        faceSource.PapPath,
                        faceEvent.Path);
                    await WriteAnimationGlbAsync(sampled, clipPath, cancellationToken).ConfigureAwait(false);
                    cached = (sampled, clipPath, faceSource);
                    sampledByTrack.Add(faceEvent.Path, cached);
                }
                catch (Exception exception) when (exception is InvalidDataException or FileNotFoundException)
                {
                    rejectedTracks.Add(faceEvent.Path);
                    warnings.Add($"Facial track '{faceEvent.Path}' was skipped: {exception.Message}");
                    continue;
                }
            }

            var sampledFrames = Math.Max(1, cached.Sampled.FrameCount - 1);
            var controlled = (faceEvent.Flags & 0x01) != 0
                             && float.IsFinite(faceEvent.AnimationStart)
                             && float.IsFinite(faceEvent.AnimationEnd)
                             && faceEvent.AnimationEnd > faceEvent.AnimationStart;
            var sourceStart = Math.Clamp(controlled ? faceEvent.AnimationStart : 0.0f, 0.0f, sampledFrames);
            var sourceEnd = Math.Clamp(controlled ? faceEvent.AnimationEnd : sampledFrames, sourceStart, sampledFrames);
            if (sourceEnd <= sourceStart)
            {
                sourceStart = 0.0f;
                sourceEnd = sampledFrames;
            }

            var startFrame = Math.Clamp(faceEvent.Time, 0, MaximumBundleFrame - 1);
            var requestedDuration = faceEvent.Duration > 0
                ? (long)faceEvent.Duration
                : Math.Max(1L, (long)MathF.Ceiling(sourceEnd - sourceStart));
            var duration = (int)Math.Clamp(requestedDuration, 1L, MaximumBundleFrame - (long)startFrame);
            layers.Add(new AnimationBundleLayer(
                Kind: "Face",
                ClipRelativePath: ToLibraryRelative(cached.Path),
                StartFrame: startFrame,
                DurationFrames: duration,
                SourceStartFrame: sourceStart,
                SourceEndFrame: sourceEnd,
                TrackOrder: faceEvent.TrackOrder ?? int.MaxValue,
                ItemOrder: faceEvent.ItemOrder,
                SourcePap: cached.Source.PapPath,
                SourceAnimation: cached.Sampled.SourceAnimationName));
        }
    }

    private void AddVisibleTimelineEvents(
        TmbTimelineFile timeline,
        ICollection<AnimationBundleVisualEvent> visualEffects,
        ICollection<AnimationBundlePropEvent> props,
        ICollection<string> warnings,
        ushort raceCode,
        string buildRoot,
        CancellationToken cancellationToken)
    {
        var usableVisualEffects = timeline.VisualEffects
            .Where(item => IsSafeVfxGamePath(item.Path))
            .Take(MaximumBundleVisualEffects)
            .ToArray();
        if (usableVisualEffects.Length < timeline.VisualEffects.Count)
        {
            warnings.Add(
                $"{timeline.VisualEffects.Count - usableVisualEffects.Length:N0} invalid or excessive VFX event(s) were skipped.");
        }

        foreach (var item in usableVisualEffects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startFrame = Math.Clamp(item.Time, 0, MaximumBundleFrame);
            var asset = vfxAssetExporter.Export(item.Path, buildRoot, cancellationToken);
            if (asset.Status != AnimationVfxAssetStatus.SyncControl)
            {
                foreach (var warning in asset.Warnings)
                {
                    warnings.Add(warning);
                }
            }

            visualEffects.Add(new AnimationBundleVisualEvent(
                Kind: item.Magic == "C173" ? "AsyncVfx" : "Vfx",
                StartFrame: startFrame,
                DurationFrames: (int)Math.Clamp(
                    Math.Max(0L, item.Duration),
                    0L,
                    MaximumBundleFrame - (long)startFrame),
                GamePath: asset.GamePath,
                BindPoint1: item.BindPoint1,
                BindPoint2: item.BindPoint2,
                BindPoint3: item.BindPoint3,
                BindPoint4: item.BindPoint4,
                ScaleX: FiniteOr(item.Scale.X, 1.0f),
                ScaleY: FiniteOr(item.Scale.Y, 1.0f),
                ScaleZ: FiniteOr(item.Scale.Z, 1.0f),
                RotationX: FiniteOr(item.Rotation.X, 0.0f),
                RotationY: FiniteOr(item.Rotation.Y, 0.0f),
                RotationZ: FiniteOr(item.Rotation.Z, 0.0f),
                PositionX: FiniteOr(item.Position.X, 0.0f),
                PositionY: FiniteOr(item.Position.Y, 0.0f),
                PositionZ: FiniteOr(item.Position.Z, 0.0f),
                ColorR: FiniteOr(item.Color.X, 1.0f),
                ColorG: FiniteOr(item.Color.Y, 1.0f),
                ColorB: FiniteOr(item.Color.Z, 1.0f),
                ColorA: FiniteOr(item.Color.W, 1.0f),
                Visibility: item.Visibility,
                TrackOrder: item.TrackOrder ?? int.MaxValue,
                ItemOrder: item.ItemOrder,
                AssetStatus: asset.Status.ToString(),
                SourceRelativePath: asset.AvfxAssetPath is null
                    ? null
                    : ToLibraryRelative(asset.AvfxAssetPath),
                StaticPreviewRelativePath: asset.StaticPreviewPath is null
                    ? null
                    : ToLibraryRelative(asset.StaticPreviewPath),
                StaticPreviewSha256: asset.StaticPreviewSha256,
                ContentSha256: asset.ContentSha256,
                RequiresApricotRuntime: asset.RequiresApricotRuntime,
                EmbeddedModelCount: asset.EmbeddedModelCount,
                RenderableModelCount: asset.RenderableModelCount,
                EmbeddedVertexCount: asset.EmbeddedVertexCount,
                EmbeddedTriangleCount: asset.EmbeddedTriangleCount,
                ParticleTypes: asset.ParticleTypes
                    .Select(value => new AnimationBundleVfxParticleType(
                        value.TypeId,
                        value.TypeName,
                        value.Count))
                    .ToArray(),
                TextureReferences: asset.TextureReferences.ToArray()));
        }

        var summonVisibleFrame = timeline.Visibility
            .Where(item => item.EndVisibility > 0.001f
                           && (!item.EnableFilter || (item.Filter & 0x08) != 0))
            .OrderBy(item => item.Time)
            .Select(item => (int?)Math.Max(0, item.Time))
            .FirstOrDefault();
        var usableProps = timeline.Props.Take(MaximumBundleProps).ToArray();
        if (usableProps.Length < timeline.Props.Count)
        {
            warnings.Add(
                $"{timeline.Props.Count - usableProps.Length:N0} prop event(s) exceeded the per-emote safety limit and were skipped.");
        }

        foreach (var item in usableProps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authoredStart = Math.Clamp(item.Time, 0, MaximumBundleFrame);
            var visibleStart = summonVisibleFrame is { } frame
                ? Math.Max(authoredStart, frame)
                : authoredStart;
            visibleStart = Math.Clamp(visibleStart, 0, MaximumBundleFrame);
            var authoredEnd = (int)Math.Clamp(
                authoredStart + Math.Max(0L, item.Duration),
                authoredStart,
                MaximumBundleFrame);
            var assetDirectory = ResolveInside(
                buildRoot,
                Path.Combine(
                    "assets",
                    "props",
                    $"w{item.ModelId:D4}",
                    $"b{item.BodyId:D4}",
                    $"v{item.Variant:D4}"));
            var asset = propAssetExporter.Export(
                "Model",
                item.ModelId,
                item.BodyId,
                item.Variant,
                item.Flags,
                raceCode,
                assetDirectory,
                ResolveInside(buildRoot, Path.Combine("assets", "props", "cache")),
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(asset.Warning))
            {
                warnings.Add(asset.Warning);
            }

            props.Add(new AnimationBundlePropEvent(
                Kind: "Model",
                StartFrame: visibleStart,
                DurationFrames: Math.Max(0, authoredEnd - visibleStart),
                Flags: item.Flags,
                ModelId: item.ModelId,
                BodyId: item.BodyId,
                Variant: item.Variant,
                TrackOrder: item.TrackOrder ?? int.MaxValue,
                ItemOrder: item.ItemOrder,
                AssetStatus: asset.Status,
                AssetRelativePath: asset.AssetPath is null ? null : ToLibraryRelative(asset.AssetPath),
                AssetCacheRelativePath: asset.CacheDirectory is null ? null : ToLibraryRelative(asset.CacheDirectory),
                ModelGamePath: asset.ModelGamePath,
                AttachmentBone: asset.Attachment.Bone,
                AttachmentScale: FiniteOr(asset.Attachment.Scale, 1.0f),
                AttachmentOffsetX: FiniteOr(asset.Attachment.Offset.X, 0.0f),
                AttachmentOffsetY: FiniteOr(asset.Attachment.Offset.Y, 0.0f),
                AttachmentOffsetZ: FiniteOr(asset.Attachment.Offset.Z, 0.0f),
                AttachmentRotationX: FiniteOr(asset.Attachment.Rotation.X, 0.0f),
                AttachmentRotationY: FiniteOr(asset.Attachment.Rotation.Y, 0.0f),
                AttachmentRotationZ: FiniteOr(asset.Attachment.Rotation.Z, 0.0f)));
        }
    }

    private static float FiniteOr(float value, float fallback) => float.IsFinite(value) ? value : fallback;

    private async Task<SampledAnimation> SampleOnFrameworkThreadAsync(
        ResolvedAnimationSource source,
        string actionName,
        string animationKey,
        bool faceAnimation,
        bool exactName,
        CancellationToken cancellationToken)
    {
        return await framework.RunOnFrameworkThread(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return papExporter.Sample(
                        source.PapBytes,
                        source.SkeletonBytes,
                        actionName,
                        animationKey,
                        faceAnimation,
                        exactName);
                })
            .ConfigureAwait(false);
    }

    private async Task WriteAnimationGlbAsync(
        SampledAnimation sampled,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (IsValidAnimationGlb(outputPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var tempOutput = outputPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await Task.Run(() => papExporter.WriteGlb(sampled, tempOutput), cancellationToken)
                .ConfigureAwait(false);
            File.Move(tempOutput, outputPath, true);
        }
        finally
        {
            TryDeletePrivateFile(tempOutput);
        }
    }

    private TmbTimelineFile? TryReadVanillaTimeline(
        string gamePath,
        ICollection<string> warnings,
        string failurePrefix)
    {
        try
        {
            var descriptor = sqPack.GetFile(gamePath);
            if (descriptor is null)
            {
                warnings.Add($"{failurePrefix}: {gamePath} is missing.");
                return null;
            }

            return new TmbTimelineFile(descriptor.File.RawData);
        }
        catch (InvalidDataException exception)
        {
            warnings.Add($"{failurePrefix}: {exception.Message}");
            return null;
        }
    }

    private ResolvedAnimationSource ResolveFaceTrackSource(
        AnimationLibraryRequest request,
        string? faceLibrary,
        string animationName)
    {
        var race = $"c{request.RaceCode:D4}";
        var face = request.FaceSkeleton
            ?? throw new InvalidDataException("A facial PAP library requires a captured face skeleton.");
        if (!string.IsNullOrWhiteSpace(faceLibrary) && !SafeAnimationKey().IsMatch(faceLibrary))
        {
            throw new InvalidDataException("The TMB facial library key is not a safe game path.");
        }

        if (!SafeAnimationKey().IsMatch(animationName))
        {
            throw new InvalidDataException("The scheduled facial animation name is not safe.");
        }

        var skeletonPath = $"chara/human/{race}/skeleton/face/{face}/skl_{race}{face}.sklb";
        var skeletonDescriptor = sqPack.GetFile(skeletonPath)
            ?? throw new FileNotFoundException("The captured face skeleton is missing.", skeletonPath);
        var derivedLibrary = animationName.StartsWith("cfxf_", StringComparison.OrdinalIgnoreCase)
            ? animationName["cfxf_".Length..]
            : animationName;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(faceLibrary))
        {
            candidates.Add($"chara/human/{race}/animation/{face}/nonresident/{faceLibrary}.pap");
        }

        // A few vanilla TMBs omit TMPP entirely, while others schedule a
        // resident expression alongside their declared nonresident pack.
        // The exact cfxf_* name gives a deterministic second candidate.
        if (!string.IsNullOrWhiteSpace(derivedLibrary) && SafeAnimationKey().IsMatch(derivedLibrary))
        {
            candidates.Add($"chara/human/{race}/animation/{face}/nonresident/{derivedLibrary}.pap");
        }

        candidates.Add($"chara/human/{race}/animation/{face}/resident/face.pap");
        foreach (var papPath in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var papDescriptor = sqPack.GetFile(papPath);
            if (papDescriptor is null)
            {
                continue;
            }

            var papBytes = papDescriptor.File.RawData.ToArray();
            var pap = new PapFile(papBytes);
            if (!pap.Animations.Any(item =>
                    item.HavokIndex >= 0
                    && string.Equals(item.GetName, animationName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return new ResolvedAnimationSource(
                papPath,
                papBytes,
                skeletonPath,
                skeletonDescriptor.File.RawData.ToArray());
        }

        throw new FileNotFoundException(
            $"No vanilla facial PAP candidate contains exact track '{animationName}'.");
    }

    private static string ResolveFaceLayerPath(
        string buildRoot,
        AnimationLibraryRequest request,
        string facePackIdentity,
        string animationName)
    {
        var identity = $"{facePackIdentity}\n{animationName}";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16]
            .ToLowerInvariant();
        var safeName = SafeDirectorySegment().Replace(animationName, "_").Trim('_');
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "face";
        }

        var relative = Path.Combine(
            "clips",
            $"c{request.RaceCode:D4}",
            request.FaceSkeleton!,
            "face-layers",
            $"{safeName}-{digest}.glb");
        return ResolveInside(buildRoot, relative);
    }

    private static IReadOnlyList<string> NormalizeBundleWarnings(IEnumerable<string> warnings)
    {
        var unique = warnings
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().Length > MaximumBundleWarningLength
                ? item.Trim()[..MaximumBundleWarningLength]
                : item.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumBundleWarnings + 1)
            .ToArray();
        if (unique.Length <= MaximumBundleWarnings)
        {
            return unique;
        }

        return unique
            .Take(MaximumBundleWarnings - 1)
            .Append("Additional animation warnings were omitted by the per-emote safety limit.")
            .ToArray();
    }

    private static bool HasValidBundleShape(
        AnimationBundleManifest? manifest,
        AnimationCatalogEntry entry,
        AnimationCatalogVariant variant,
        AnimationLibraryRequest request)
    {
        return manifest is not null
               && manifest.SchemaVersion == BundleSchemaVersion
               && manifest.ConverterVersion == VanillaPapAnimationExporter.ConverterVersion
               && string.Equals(manifest.EntryId, entry.EntryId, StringComparison.Ordinal)
               && string.Equals(manifest.VariantId, variant.VariantId, StringComparison.Ordinal)
               && manifest.RaceCode == request.RaceCode
               && string.Equals(manifest.FaceSkeleton, request.FaceSkeleton ?? string.Empty, StringComparison.Ordinal)
               && manifest.FrameStart >= 0
               && manifest.FrameEnd > manifest.FrameStart
               && manifest.FrameEnd <= MaximumBundleFrame
               && manifest.FramesPerSecond == VanillaPapAnimationExporter.FramesPerSecond
               && manifest.Layers is { Count: > 0 and <= MaximumBundleLayers }
               && manifest.VisualEffects is { Count: <= MaximumBundleVisualEffects }
               && manifest.Props is { Count: <= MaximumBundleProps }
               && manifest.Warnings is { Count: <= MaximumBundleWarnings }
               && !manifest.Layers.Any(layer =>
                   layer is null
                   || string.IsNullOrWhiteSpace(layer.Kind)
                   || string.IsNullOrWhiteSpace(layer.ClipRelativePath)
                   || layer.ClipRelativePath.Length > 32_768
                   || layer.StartFrame < manifest.FrameStart
                   || layer.DurationFrames <= 0
                   || layer.StartFrame > manifest.FrameEnd - layer.DurationFrames
                   || !float.IsFinite(layer.SourceStartFrame)
                   || !float.IsFinite(layer.SourceEndFrame)
                   || layer.SourceStartFrame < 0
                   || layer.SourceEndFrame < layer.SourceStartFrame)
               && !manifest.VisualEffects.Any(item =>
                   item is null
                   || string.IsNullOrWhiteSpace(item.Kind)
                   || item.Kind.Length > 128
                   || string.IsNullOrWhiteSpace(item.GamePath)
                   || item.GamePath.Length > 4_096
                   || !IsSafeVfxGamePath(item.GamePath)
                   || item.StartFrame < manifest.FrameStart
                   || item.DurationFrames < 0
                   || item.StartFrame > manifest.FrameEnd - item.DurationFrames
                   || !float.IsFinite(item.ScaleX)
                   || !float.IsFinite(item.ScaleY)
                   || !float.IsFinite(item.ScaleZ)
                   || !float.IsFinite(item.RotationX)
                   || !float.IsFinite(item.RotationY)
                   || !float.IsFinite(item.RotationZ)
                   || !float.IsFinite(item.PositionX)
                   || !float.IsFinite(item.PositionY)
                   || !float.IsFinite(item.PositionZ)
                   || !float.IsFinite(item.ColorR)
                   || !float.IsFinite(item.ColorG)
                   || !float.IsFinite(item.ColorB)
                   || !float.IsFinite(item.ColorA)
                   || !HasValidVfxMetadataShape(item))
               && !manifest.Props.Any(item =>
                   item is null
                   || string.IsNullOrWhiteSpace(item.Kind)
                   || item.StartFrame < manifest.FrameStart
                   || item.DurationFrames < 0
                   || item.StartFrame > manifest.FrameEnd - item.DurationFrames
                   || !AnimationPropAssetStatuses.IsKnown(item.AssetStatus)
                   || string.IsNullOrWhiteSpace(item.AttachmentBone)
                   || item.AttachmentBone.Length > 256
                   || !float.IsFinite(item.AttachmentScale)
                   || item.AttachmentScale <= 0.0f
                   || !float.IsFinite(item.AttachmentOffsetX)
                   || !float.IsFinite(item.AttachmentOffsetY)
                   || !float.IsFinite(item.AttachmentOffsetZ)
                   || !float.IsFinite(item.AttachmentRotationX)
                   || !float.IsFinite(item.AttachmentRotationY)
                   || !float.IsFinite(item.AttachmentRotationZ)
                   || (item.ModelGamePath is not null
                       && (!IsSafeGameAssetPath(item.ModelGamePath, ".mdl")
                           || item.ModelGamePath.Length > 4_096))
                   || (item.AssetStatus == AnimationPropAssetStatuses.Ready
                       && (string.IsNullOrWhiteSpace(item.AssetRelativePath)
                           || string.IsNullOrWhiteSpace(item.AssetCacheRelativePath)))
                   || (item.AssetStatus != AnimationPropAssetStatuses.Ready
                       && (item.AssetRelativePath is not null
                           || item.AssetCacheRelativePath is not null)))
               && !manifest.Warnings.Any(item =>
                   item is null || item.Length > MaximumBundleWarningLength);
    }

    private static bool HasValidVfxMetadataShape(AnimationBundleVisualEvent item)
    {
        if (!AnimationVfxAssetStatuses.IsKnown(item.AssetStatus)
            || item.EmbeddedModelCount < 0
            || item.EmbeddedModelCount > AvfxAnalysisOptions.Default.MaximumModels
            || item.RenderableModelCount is < 0
            || item.RenderableModelCount > item.EmbeddedModelCount
            || item.EmbeddedVertexCount < 0
            || item.EmbeddedVertexCount > AvfxAnalysisOptions.Default.MaximumVertices
            || item.EmbeddedTriangleCount < 0
            || item.EmbeddedTriangleCount > AvfxAnalysisOptions.Default.MaximumTriangles
            || item.ParticleTypes is null
                or { Count: > MaximumBundleVfxParticleTypes }
            || item.TextureReferences is null
                or { Count: > MaximumBundleVfxTextureReferences })
        {
            return false;
        }

        var previousTypeId = int.MinValue;
        long totalParticleNodes = 0;
        foreach (var particle in item.ParticleTypes)
        {
            if (particle is null
                || particle.TypeId <= previousTypeId
                || string.IsNullOrWhiteSpace(particle.TypeName)
                || particle.TypeName.Length > MaximumBundleVfxTypeNameLength
                || particle.Count <= 0
                || particle.Count > AvfxAnalysisOptions.Default.MaximumChunkCount)
            {
                return false;
            }

            var expectedName = Enum.IsDefined(typeof(AvfxParticleType), particle.TypeId)
                ? ((AvfxParticleType)particle.TypeId).ToString()
                : $"Unknown({particle.TypeId})";
            if (!string.Equals(particle.TypeName, expectedName, StringComparison.Ordinal))
            {
                return false;
            }

            previousTypeId = particle.TypeId;
            totalParticleNodes += particle.Count;
        }

        if (totalParticleNodes > AvfxAnalysisOptions.Default.MaximumChunkCount
            || item.TextureReferences.Any(path =>
                !IsSafeVfxTexturePath(path) || path.Length > 4_096)
            || item.TextureReferences.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != item.TextureReferences.Count)
        {
            return false;
        }

        var sourcePresent = item.SourceRelativePath is not null;
        var previewPresent = item.StaticPreviewRelativePath is not null;
        var previewHashPresent = item.StaticPreviewSha256 is not null;
        var hashPresent = item.ContentSha256 is not null;
        if (sourcePresent && !IsSafeLibraryAssetPath(item.SourceRelativePath!, ".avfx")
            || previewPresent && !IsSafeLibraryAssetPath(item.StaticPreviewRelativePath!, ".glb")
            || previewHashPresent && !IsLowerHexSha256(item.StaticPreviewSha256!)
            || hashPresent && !IsLowerHexSha256(item.ContentSha256!))
        {
            return false;
        }

        if (previewPresent != previewHashPresent) return false;

        if (hashPresent)
        {
            var shortHash = item.ContentSha256![..20];
            if (sourcePresent
                && !item.SourceRelativePath!.Replace('\\', '/').EndsWith(
                    $"/assets/vfx/{shortHash}/source.avfx",
                    StringComparison.Ordinal)
                || previewPresent
                && !item.StaticPreviewRelativePath!.Replace('\\', '/').EndsWith(
                    $"/assets/vfx/{shortHash}/static-preview-v1.glb",
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        var hasNoExtractedMetadata = item.EmbeddedModelCount == 0
                                     && item.RenderableModelCount == 0
                                     && item.EmbeddedVertexCount == 0
                                     && item.EmbeddedTriangleCount == 0
                                     && item.ParticleTypes.Count == 0
                                     && item.TextureReferences.Count == 0;
        return item.AssetStatus switch
        {
            AnimationVfxAssetStatuses.SyncControl =>
                AvfxAnalyzer.IsSyncControlPath(item.GamePath)
                && !sourcePresent
                && !previewPresent
                && !hashPresent
                && !item.RequiresApricotRuntime
                && hasNoExtractedMetadata,
            AnimationVfxAssetStatuses.StaticEmbeddedMeshPreview =>
                sourcePresent
                && previewPresent
                && previewHashPresent
                && hashPresent
                && item.RenderableModelCount > 0
                && item.EmbeddedVertexCount > 0
                && item.EmbeddedTriangleCount > 0,
            AnimationVfxAssetStatuses.UnsupportedApricot =>
                sourcePresent
                && !previewPresent
                && !previewHashPresent
                && hashPresent
                && item.RequiresApricotRuntime
                && item.RenderableModelCount == 0,
            AnimationVfxAssetStatuses.MetadataOnly =>
                sourcePresent
                && !previewPresent
                && !previewHashPresent
                && hashPresent
                && !item.RequiresApricotRuntime
                && item.RenderableModelCount == 0,
            AnimationVfxAssetStatuses.MissingAsset or AnimationVfxAssetStatuses.AnalysisFailed =>
                !sourcePresent
                && !previewPresent
                && !previewHashPresent
                && !hashPresent
                && !item.RequiresApricotRuntime
                && hasNoExtractedMetadata,
            AnimationVfxAssetStatuses.ExportFailed =>
                !previewPresent
                && !previewHashPresent
                && hashPresent,
            _ => false,
        };
    }

    private AnimationBundleManifest? ReadValidBundle(
        string bundlePath,
        AnimationCatalogEntry entry,
        AnimationCatalogVariant variant,
        AnimationLibraryRequest request)
    {
        try
        {
            var info = new FileInfo(bundlePath);
            if (!info.Exists || info.Length is <= 0 or > MaximumBundleJsonBytes)
            {
                return null;
            }

            var manifest = JsonSerializer.Deserialize<AnimationBundleManifest>(
                File.ReadAllText(bundlePath),
                JsonOptions);
            if (!HasValidBundleShape(manifest, entry, variant, request))
            {
                return null;
            }

            foreach (var layer in manifest!.Layers)
            {
                if (!IsValidAnimationGlb(ResolveInside(LibraryRoot, layer.ClipRelativePath)))
                {
                    return null;
                }
            }

            foreach (var prop in manifest.Props.Where(item =>
                         item.AssetStatus == AnimationPropAssetStatuses.Ready))
            {
                if (!AnimationPropAssetExporter.IsValidPublishedAsset(
                        ResolveInside(LibraryRoot, prop.AssetRelativePath!),
                        ResolveInside(LibraryRoot, prop.AssetCacheRelativePath!)))
                {
                    return null;
                }
            }

            // Export failures can be transient (locked cache, interrupted
            // publication, permissions). Do not make them permanent merely
            // because a bundle manifest was written successfully.
            if (manifest.Props.Any(item =>
                    item.AssetStatus == AnimationPropAssetStatuses.ExportFailed)
                || manifest.VisualEffects.Any(item =>
                    item.AssetStatus == AnimationVfxAssetStatuses.ExportFailed))
            {
                return null;
            }

            foreach (var visualEffect in manifest.VisualEffects)
            {
                if (!IsValidBundleVfxAsset(visualEffect))
                {
                    return null;
                }
            }

            return manifest;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or InvalidDataException
                or ArgumentException
                or OverflowException)
        {
            logger.LogDebug(exception, "Ignoring invalid XivBlend animation bundle {Path}", bundlePath);
            return null;
        }
    }

    private static AnimationCatalogEntry? FindRequestedEntry(
        AnimationCatalog activeCatalog,
        AnimationLibraryRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.EntryId))
        {
            return activeCatalog.Entries.FirstOrDefault(
                item => string.Equals(item.EntryId, request.EntryId, StringComparison.Ordinal));
        }

        return request.EmoteId is { } emoteId
            ? activeCatalog.Entries.FirstOrDefault(item => item.EmoteId == emoteId)
            : null;
    }

    private static string ToLibraryRelative(string path)
    {
        var resolved = ResolveInside(LibraryRoot, Path.GetRelativePath(LibraryRoot, path));
        return Path.GetRelativePath(LibraryRoot, resolved).Replace(Path.DirectorySeparatorChar, '/');
    }

    private async Task<AnimationCatalog> GetOrBuildCatalogForQueueAsync(CancellationToken cancellationToken)
    {
        lock (stateLock)
        {
            if (catalog is not null)
            {
                return catalog;
            }
        }

        var built = await framework.RunOnFrameworkThread(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return BuildCatalog();
                })
            .ConfigureAwait(false);
        lock (stateLock)
        {
            catalog ??= built;
            return catalog;
        }
    }

    private ResolvedAnimationSource ResolveVanillaSource(
        AnimationLibraryRequest request,
        AnimationCatalogVariant variant)
    {
        var race = $"c{request.RaceCode:D4}";
        string papPath;
        string skeletonPath;
        byte[] papBytes;

        if (variant.Kind == "Body")
        {
            var racePap = $"chara/human/{race}/animation/a0001/bt_common/{variant.Key}.pap";
            var fallbackPap = $"chara/human/c0101/animation/a0001/bt_common/{variant.Key}.pap";
            var raceDescriptor = sqPack.GetFile(racePap);
            if (raceDescriptor is not null)
            {
                papPath = racePap;
                papBytes = raceDescriptor.File.RawData.ToArray();
                skeletonPath = $"chara/human/{race}/skeleton/base/b0001/skl_{race}b0001.sklb";
            }
            else
            {
                papPath = fallbackPap;
                papBytes = sqPack.GetFile(fallbackPap)?.File.RawData.ToArray()
                    ?? throw new FileNotFoundException(
                        "The common vanilla PAP fallback is unavailable for this emote.",
                        fallbackPap);
                // The fallback binding was authored against c0101. Sample it
                // on that source skeleton, then let Blender apply the named
                // bone Action to the exported target rig. Pairing its track
                // indices directly with another race's skeleton is unsafe.
                skeletonPath = "chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb";
            }
        }
        else if (variant.Kind == "Face")
        {
            var face = request.FaceSkeleton!;
            var individual = $"chara/human/{race}/animation/{face}/nonresident/{variant.Key}.pap";
            var resident = $"chara/human/{race}/animation/{face}/resident/face.pap";
            var individualDescriptor = sqPack.GetFile(individual);
            if (individualDescriptor is not null)
            {
                papPath = individual;
                papBytes = individualDescriptor.File.RawData.ToArray();
            }
            else
            {
                papPath = resident;
                papBytes = sqPack.GetFile(resident)?.File.RawData.ToArray()
                    ?? throw new FileNotFoundException(
                        "The vanilla facial PAP is unavailable for this face rig.",
                        resident);
            }

            skeletonPath = $"chara/human/{race}/skeleton/face/{face}/skl_{race}{face}.sklb";
        }
        else
        {
            throw new InvalidOperationException($"Unsupported animation kind '{variant.Kind}'.");
        }

        var skeletonDescriptor = sqPack.GetFile(skeletonPath)
            ?? throw new FileNotFoundException("The vanilla target skeleton is unavailable.", skeletonPath);

        return new ResolvedAnimationSource(
            papPath,
            papBytes,
            skeletonPath,
            skeletonDescriptor.File.RawData.ToArray());
    }

    private ResolvedAnimationSource ResolveCustomSource(
        AnimationLibraryRequest request,
        AnimationCatalogVariant variant)
    {
        if (variant.Kind != "Body")
        {
            throw new InvalidOperationException("The first custom importer supports body emote PAP overrides only.");
        }

        var trusted = customMods.ReadTrustedPap(variant, request.RaceCode);
        var sourceRace = $"c{trusted.SourceRaceCode:D4}";
        var skeletonPath = $"chara/human/{sourceRace}/skeleton/base/b0001/skl_{sourceRace}b0001.sklb";
        var skeletonDescriptor = sqPack.GetFile(skeletonPath)
            ?? throw new FileNotFoundException(
                "The vanilla skeleton required by the custom PAP is unavailable.",
                skeletonPath);
        return new ResolvedAnimationSource(
            trusted.DisplayPath,
            trusted.Bytes,
            skeletonPath,
            skeletonDescriptor.File.RawData.ToArray());
    }

    private static void ValidateRequest(AnimationLibraryRequest request)
    {
        if (request.SchemaVersion is not 1 and not QueueSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported animation queue schema {request.SchemaVersion}.");
        }

        if (!Guid.TryParseExact(request.RequestId, "D", out _))
        {
            throw new InvalidDataException("Animation request ID is not a canonical GUID.");
        }

        if (!PlayerRaceCodes.Contains(request.RaceCode))
        {
            throw new InvalidDataException($"Race code {request.RaceCode:D4} is not a player race/sex rig.");
        }

        if (request.SchemaVersion == 1 && request.EmoteId is null or 0)
        {
            throw new InvalidDataException("Animation request has no emote ID.");
        }

        if (request.SchemaVersion >= 2
            && (string.IsNullOrWhiteSpace(request.EntryId) || !SafeEntryId().IsMatch(request.EntryId)))
        {
            throw new InvalidDataException("Animation request has no valid catalog entry ID.");
        }

        if (string.IsNullOrWhiteSpace(request.GameVersion) || request.GameVersion.Length > 128)
        {
            throw new InvalidDataException("Animation request has no valid game build.");
        }

        if (string.IsNullOrWhiteSpace(request.VariantId)
            || !SafeVariantId().IsMatch(request.VariantId))
        {
            throw new InvalidDataException("Animation request has no valid catalog variant ID.");
        }

    }

    private static void ValidateVariantRequest(
        AnimationLibraryRequest request,
        AnimationCatalogVariant variant)
    {
        if (!string.IsNullOrWhiteSpace(request.FaceSkeleton)
            && !FaceSkeletonId().IsMatch(request.FaceSkeleton))
        {
            throw new InvalidDataException("The captured face skeleton must look like f0002.");
        }

        if (variant.Kind == "Face" && string.IsNullOrWhiteSpace(request.FaceSkeleton))
        {
            throw new InvalidDataException("Facial animations require a captured face skeleton such as f0002.");
        }
    }

    private void WriteResponse(AnimationLibraryResponse response)
    {
        var outputPath = Path.Combine(ResponsesDirectory, $"{response.RequestId}.json");
        AtomicWriteJson(outputPath, response);
    }

    private static string ResolveClipRelativePath(
        AnimationCatalogVariant variant,
        AnimationLibraryRequest request)
    {
        var faceKey = string.IsNullOrWhiteSpace(request.FaceSkeleton) ? "noface" : request.FaceSkeleton;
        return variant.CacheRelativePathTemplate
            .Replace("{race}", $"c{request.RaceCode:D4}", StringComparison.Ordinal)
            .Replace("{face}", request.FaceSkeleton ?? string.Empty, StringComparison.Ordinal)
            .Replace("{faceKey}", faceKey, StringComparison.Ordinal)
            .Replace('/', Path.DirectorySeparatorChar);
    }

    private static string ResolveBundleRelativePath(
        AnimationCatalogVariant variant,
        AnimationLibraryRequest request)
    {
        var faceKey = string.IsNullOrWhiteSpace(request.FaceSkeleton) ? "noface" : request.FaceSkeleton;
        return variant.BundleRelativePathTemplate
            .Replace("{race}", $"c{request.RaceCode:D4}", StringComparison.Ordinal)
            .Replace("{face}", request.FaceSkeleton ?? string.Empty, StringComparison.Ordinal)
            .Replace("{faceKey}", faceKey, StringComparison.Ordinal)
            .Replace('/', Path.DirectorySeparatorChar);
    }

    private static string ResolveInside(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var requiredPrefix = fullRoot + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!resolved.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Animation cache path resolved outside its build directory.");
        }

        return resolved;
    }

    private static bool IsSafeGameAssetPath(string path, string requiredExtension)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Contains('\\')
            || path.Contains(':')
            || !path.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1
               && segments.All(segment => segment is not "." and not "..")
               && string.Equals(segments[0], "chara", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeVfxGamePath(string? path)
    {
        return IsSafeCanonicalVfxPath(path, ".avfx", MaximumBundleVfxGamePathLength);
    }

    private static bool IsSafeVfxTexturePath(string? path)
    {
        return IsSafeCanonicalVfxPath(path, ".atex", 4_096);
    }

    private static bool IsSafeCanonicalVfxPath(
        string? path,
        string requiredExtension,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > maximumLength
            || Path.IsPathRooted(path)
            || path.Contains('\\')
            || path.Contains(':')
            || path.Contains('\0')
            || !path.StartsWith("vfx/", StringComparison.Ordinal)
            || !path.EndsWith(requiredExtension, StringComparison.Ordinal)
            || path.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '-' or '.')))
        {
            return false;
        }

        var segments = path.Split('/');
        return segments.Length > 1
               && segments.All(segment => segment is not "" and not "." and not "..");
    }

    private static bool IsSafeLibraryAssetPath(string path, string requiredExtension)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > 32_768
            || Path.IsPathRooted(path)
            || path.Contains('\\')
            || path.Contains(':')
            || !path.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = path.Split('/');
        return segments.Length > 1
               && segments.All(segment => segment is not "" and not "." and not "..");
    }

    private static bool IsLowerHexSha256(string value)
    {
        return value.Length == 64
               && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool IsValidBundleVfxAsset(AnimationBundleVisualEvent item)
    {
        try
        {
            if (!HasValidVfxMetadataShape(item)) return false;
            if (item.SourceRelativePath is null)
            {
                return item.AssetStatus is AnimationVfxAssetStatuses.SyncControl
                    or AnimationVfxAssetStatuses.MissingAsset
                    or AnimationVfxAssetStatuses.AnalysisFailed
                    or AnimationVfxAssetStatuses.ExportFailed;
            }

            var sourcePath = ResolveInside(LibraryRoot, item.SourceRelativePath);
            byte[] bytes;
            using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length < 8
                    || stream.Length > AvfxAnalysisOptions.Default.MaximumFileBytes
                    || stream.Length > int.MaxValue)
                {
                    return false;
                }

                bytes = new byte[(int)stream.Length];
                stream.ReadExactly(bytes);
            }

            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(hash, item.ContentSha256, StringComparison.Ordinal)) return false;

            var analysis = AvfxAnalyzer.Analyze(bytes, item.GamePath);
            if (analysis.IsSyncControl
                || analysis.RequiresApricotRuntime != item.RequiresApricotRuntime
                || analysis.EmbeddedModels.Count != item.EmbeddedModelCount
                || analysis.RenderableModelCount != item.RenderableModelCount
                || analysis.EmbeddedModels.Sum(model => model.Vertices.Count) != item.EmbeddedVertexCount
                || analysis.EmbeddedModels.Sum(model => model.Triangles.Count) != item.EmbeddedTriangleCount)
            {
                return false;
            }

            if (item.AssetStatus != AnimationVfxAssetStatuses.ExportFailed
                && !string.Equals(
                    analysis.PreviewStatus.ToString(),
                    item.AssetStatus,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var expectedParticles = analysis.ParticleTypeHistogram
                .OrderBy(value => value.Key)
                .Select(value => new AnimationBundleVfxParticleType(
                    value.Key,
                    Enum.IsDefined(typeof(AvfxParticleType), value.Key)
                        ? ((AvfxParticleType)value.Key).ToString()
                        : $"Unknown({value.Key})",
                    value.Value))
                .ToArray();
            if (!expectedParticles.SequenceEqual(item.ParticleTypes)
                || !analysis.ReferencedTexturePaths.SequenceEqual(
                    item.TextureReferences,
                    StringComparer.Ordinal))
            {
                return false;
            }

            if (item.AssetStatus != AnimationVfxAssetStatuses.StaticEmbeddedMeshPreview)
            {
                return true;
            }

            var previewPath = ResolveInside(LibraryRoot, item.StaticPreviewRelativePath!);
            if (!IsValidAnimationGlb(previewPath)) return false;
            using var previewStream = new FileStream(
                previewPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var previewHash = Convert.ToHexString(SHA256.HashData(previewStream)).ToLowerInvariant();
            return string.Equals(
                previewHash,
                item.StaticPreviewSha256,
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or ArgumentException
                or OverflowException)
        {
            return false;
        }
    }

    private static bool IsValidAnimationGlb(string path)
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
            return BinaryPrimitives.ReadUInt32LittleEndian(header) == 0x46546C67 // glTF
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

    private static string? ResolveKind(byte loadType, string key)
    {
        return loadType switch
        {
            2 => "Body",
            0 when key.StartsWith("facial/pose/", StringComparison.Ordinal) => "Face",
            _ => null,
        };
    }

    private static string GetBuildRoot(AnimationCatalog value)
    {
        var safeVersion = SafeDirectorySegment().Replace(value.GameVersion, "_");
        var safeLanguage = SafeDirectorySegment().Replace(value.Language, "_");
        return Path.Combine(
            LibraryRoot,
            "builds",
            safeVersion,
            safeLanguage,
            $"converter-{value.ConverterVersion}");
    }

    private string ReadGameVersion()
    {
        // SqPack already resolved the actual install root and parsed the base
        // repository's adjacent ffxivgame.ver. Using CWD would collapse every
        // patch into "unknown" when Dalamud starts from the install root.
        var version = sqPack.Repositories
            .FirstOrDefault(repository =>
                repository.ExpansionId is null
                && string.Equals(
                    Path.GetFileName(repository.Path),
                    "ffxiv",
                    StringComparison.OrdinalIgnoreCase))
            ?.Version
            .Trim();
        if (string.IsNullOrWhiteSpace(version) || version.Length > 128)
        {
            throw new InvalidOperationException(
                "XivBlend could not identify the live FFXIV game build, so it refused to publish a stale-prone animation cache.");
        }

        return version;
    }

    private static void AtomicWriteJson<T>(string outputPath, T value, long? maximumBytes = null)
    {
        var parent = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("JSON output has no parent directory.");
        Directory.CreateDirectory(parent);
        var tempPath = Path.Combine(parent, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                if (maximumBytes is { } limit && stream.Length > limit)
                {
                    throw new InvalidDataException(
                        $"JSON output exceeded XivBlend's {limit:N0}-byte safety limit.");
                }

                stream.Flush(true);
            }

            File.Move(tempPath, outputPath, true);
        }
        finally
        {
            TryDeletePrivateFile(tempPath);
        }
    }

    private static void TryDeletePrivateFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Private temp/queue cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Private temp/queue cleanup is best effort.
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        framework.Update -= OnFrameworkUpdate;
        disposeToken.Cancel();
        // Dispose is called on Dalamud's framework thread. Blocking here can
        // deadlock a queued RunOnFrameworkThread sample, and disposing the gate
        // early lets a worker later call Release() on a disposed semaphore.
        // Keep the tiny primitives alive until both workers have actually left.
        _ = Task.WhenAll(prepareTask, requestTask).ContinueWith(
            _ =>
            {
                operationGate.Dispose();
                disposeToken.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_/-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeAnimationKey();

    [GeneratedRegex(@"^f\d{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex FaceSkeletonId();

    [GeneratedRegex(@"^[a-zA-Z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeVariantId();

    [GeneratedRegex(@"^[a-zA-Z0-9:_-]{1,192}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeEntryId();

    [GeneratedRegex(@"[^a-zA-Z0-9._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex SafeDirectorySegment();

    private sealed record ResolvedAnimationSource(
        string PapPath,
        byte[] PapBytes,
        string SkeletonPath,
        byte[] SkeletonBytes);

    private sealed record BuiltAnimationBundle(AnimationBundleManifest Manifest);
}
