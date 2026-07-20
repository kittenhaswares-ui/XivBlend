using System.Buffers.Binary;
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
/// skeletal motion read from the live SqPack. Penumbra paths, combat actions,
/// weapon timelines, VFX, props, mounts, movement sets and NPC animations are
/// never queried. Current Dalamud builds cannot prove that a live SqPack index
/// has not previously been modified by TexTools; that boundary is surfaced in
/// the UI and documentation rather than presented as a cryptographic guarantee.
/// </summary>
public sealed partial class AnimationLibraryService : IService, IDisposable
{
    public const int CatalogSchemaVersion = 1;
    public const int QueueSchemaVersion = 1;

    private const string ScopeDescription =
        "Vanilla player emotes and facial expressions: primary icon-click skeletal motion only. " +
        "Combat, job/weapon timelines, weapons, VFX, props, mounts, movement, NPC and modded PAPs are excluded.";

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
        VanillaPapAnimationExporter papExporter)
    {
        this.logger = logger;
        this.dataManager = dataManager;
        this.clientState = clientState;
        this.framework = framework;
        this.sqPack = sqPack;
        this.papExporter = papExporter;

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
        return $"{CatalogEntryCount} primary clips. {source}";
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

            var key = timeline.Value.Key.ToString().Trim().Replace('\\', '/');
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
                : $"clips/{{race}}/body/{variantId}.glb";
            var variant = new AnimationCatalogVariant(
                VariantId: variantId,
                ActionTimelineId: timeline.Value.RowId,
                Slot: 0,
                Label: "Default / Loop",
                Kind: kind,
                Key: key,
                LoadType: timeline.Value.LoadType,
                IsDefault: true,
                IsLoop: timeline.Value.IsLoop,
                CacheRelativePathTemplate: cacheTemplate);

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
                EmoteId: emote.RowId,
                Name: name,
                Command: command,
                IconId: emote.Icon,
                ResolvedIconId: resolvedIconId,
                IconRelativePath: $"icons/{resolvedIconId:D6}.png",
                Category: category,
                DefaultVariantId: variantId,
                Variants: [variant]));
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException("The live Emote sheet produced no scoped player animations.");
        }

        return new AnimationCatalog(
            SchemaVersion: CatalogSchemaVersion,
            ConverterVersion: VanillaPapAnimationExporter.ConverterVersion,
            GameVersion: ReadGameVersion(),
            Language: clientState.ClientLanguage.ToString(),
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Scope: ScopeDescription,
            Entries: entries.OrderBy(entry => entry.EmoteId).ToArray());
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

                Status = $"Animation browser ready: {preparedCatalog.Entries.Count} vanilla player clips.";
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

                if (HasModifiedGameData)
                {
                    throw new InvalidOperationException(
                        "Dalamud reports modified live SqPack files. XivBlend refused the request because " +
                        "this library is vanilla-only; disable/restore TexTools index modifications first.");
                }

                var entry = activeCatalog.Entries.FirstOrDefault(item => item.EmoteId == request.EmoteId)
                    ?? throw new KeyNotFoundException($"Emote {request.EmoteId} is not in the scoped player catalog.");
                var variant = entry.Variants.FirstOrDefault(item => item.VariantId == request.VariantId)
                    ?? throw new KeyNotFoundException($"Animation variant '{request.VariantId}' was not found.");
                ValidateVariantRequest(request, variant);

                var buildRoot = GetBuildRoot(activeCatalog);
                var relativeClipPath = ResolveClipRelativePath(variant, request);
                var outputPath = ResolveInside(buildRoot, relativeClipPath);
                if (IsValidAnimationGlb(outputPath))
                {
                    WriteResponse(new AnimationLibraryResponse(
                        QueueSchemaVersion,
                        request.RequestId,
                        "ready",
                        Path.GetRelativePath(LibraryRoot, outputPath).Replace(Path.DirectorySeparatorChar, '/'),
                        null,
                        null,
                        null,
                        null,
                        null));
                    Status = $"Animation cache hit: {entry.Name}.";
                    return;
                }

                if (File.Exists(outputPath))
                {
                    logger.LogWarning("Rebuilding invalid XivBlend animation cache file {Path}", outputPath);
                }

                Status = $"Decoding vanilla animation: {entry.Name}...";
                var source = ResolveVanillaSource(request, variant);
                var actionName = $"XIV Emote - {entry.Name}";
                var sampled = await framework.RunOnFrameworkThread(
                    () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return papExporter.Sample(
                            source.PapBytes,
                            source.SkeletonBytes,
                            actionName,
                            variant.Key,
                            variant.Kind == "Face");
                    })
                    .ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
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

                WriteResponse(new AnimationLibraryResponse(
                    QueueSchemaVersion,
                    request.RequestId,
                    "ready",
                    Path.GetRelativePath(LibraryRoot, outputPath).Replace(Path.DirectorySeparatorChar, '/'),
                    null,
                    sampled.FrameCount,
                    sampled.DurationSeconds,
                    sampled.AnimatedBoneCount,
                    source.PapPath));
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
                    QueueSchemaVersion,
                    request.RequestId,
                    "error",
                    null,
                    exception.Message,
                    null,
                    null,
                    null,
                    null));
            }
        }
        finally
        {
            TryDeletePrivateFile(processingPath);
        }
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

    private static void ValidateRequest(AnimationLibraryRequest request)
    {
        if (request.SchemaVersion != QueueSchemaVersion)
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

        if (request.EmoteId == 0)
        {
            throw new InvalidDataException("Animation request has no emote ID.");
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
        if (variant.Kind == "Face"
            && (string.IsNullOrWhiteSpace(request.FaceSkeleton)
                || !FaceSkeletonId().IsMatch(request.FaceSkeleton)))
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
        return variant.CacheRelativePathTemplate
            .Replace("{race}", $"c{request.RaceCode:D4}", StringComparison.Ordinal)
            .Replace("{face}", request.FaceSkeleton ?? string.Empty, StringComparison.Ordinal)
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

    private static void AtomicWriteJson<T>(string outputPath, T value)
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

    [GeneratedRegex(@"[^a-zA-Z0-9._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex SafeDirectorySegment();

    private sealed record ResolvedAnimationSource(
        string PapPath,
        byte[] PapBytes,
        string SkeletonPath,
        byte[] SkeletonBytes);
}
