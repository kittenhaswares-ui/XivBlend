namespace Meddle.Plugin.Models;

/// <summary>
/// JSON contracts shared by the Dalamud-side animation cache and the optional
/// Blender companion add-on. These contain metadata and local file locators
/// only; Square Enix assets are generated into the user's cache at runtime and
/// are never embedded in the plugin package.
/// </summary>
public sealed record AnimationCatalog(
    int SchemaVersion,
    int ConverterVersion,
    string GameVersion,
    string Language,
    DateTimeOffset GeneratedAtUtc,
    string Scope,
    IReadOnlyList<AnimationCatalogEntry> Entries);

public sealed record AnimationCatalogEntry(
    string EntryId,
    uint? EmoteId,
    string Name,
    string Command,
    uint IconId,
    uint ResolvedIconId,
    string IconRelativePath,
    string Category,
    string SourceKind,
    string SourceDisplayName,
    string DefaultVariantId,
    IReadOnlyList<AnimationCatalogVariant> Variants);

public sealed record AnimationCatalogVariant(
    string VariantId,
    uint ActionTimelineId,
    int Slot,
    string Label,
    string Kind,
    string Key,
    string TimelineKey,
    byte LoadType,
    bool IsDefault,
    bool IsLoop,
    string CacheRelativePathTemplate,
    string BundleRelativePathTemplate,
    string? ModIdentifier = null,
    string? CanonicalGamePath = null,
    string? ModRelativePath = null,
    string? ContentSha256 = null,
    ushort? SourceRaceCode = null,
    IReadOnlyList<ushort>? CompatibleRaceCodes = null);

public sealed record AnimationLibraryPointer(
    int SchemaVersion,
    string GameVersion,
    string Language,
    string CatalogRelativePath);

public sealed record AnimationLibraryRequest(
    int SchemaVersion,
    string RequestId,
    string GameVersion,
    string? EntryId,
    uint? EmoteId,
    string VariantId,
    ushort RaceCode,
    string? FaceSkeleton);

public sealed record AnimationLibraryResponse(
    int SchemaVersion,
    string RequestId,
    string Status,
    string? ClipRelativePath,
    string? BundleRelativePath,
    string? Error,
    int? FrameCount,
    float? DurationSeconds,
    int? AnimatedBoneCount,
    string? SourcePap,
    IReadOnlyList<string>? Warnings = null);

/// <summary>
/// Persistent, on-demand description of one synchronized emote. The GLBs and
/// visible effects remain external to the .blend and are loaded only when the
/// user clicks the corresponding card.
/// </summary>
public sealed record AnimationBundleManifest(
    int SchemaVersion,
    int ConverterVersion,
    string GameVersion,
    string EntryId,
    string VariantId,
    ushort RaceCode,
    string FaceSkeleton,
    string DisplayName,
    string SourceKind,
    int FramesPerSecond,
    int FrameStart,
    int FrameEnd,
    bool IsLoop,
    IReadOnlyList<AnimationBundleLayer> Layers,
    IReadOnlyList<AnimationBundleVisualEvent> VisualEffects,
    IReadOnlyList<AnimationBundlePropEvent> Props,
    IReadOnlyList<string> Warnings);

public sealed record AnimationBundleLayer(
    string Kind,
    string ClipRelativePath,
    int StartFrame,
    int DurationFrames,
    float SourceStartFrame,
    float SourceEndFrame,
    int TrackOrder,
    int ItemOrder,
    string SourcePap,
    string SourceAnimation);

public sealed record AnimationBundleVisualEvent(
    string Kind,
    int StartFrame,
    int DurationFrames,
    string GamePath,
    short BindPoint1,
    short BindPoint2,
    short BindPoint3,
    short BindPoint4,
    float ScaleX,
    float ScaleY,
    float ScaleZ,
    float RotationX,
    float RotationY,
    float RotationZ,
    float PositionX,
    float PositionY,
    float PositionZ,
    float ColorR,
    float ColorG,
    float ColorB,
    float ColorA,
    int Visibility,
    int TrackOrder,
    int ItemOrder);

public sealed record AnimationBundlePropEvent(
    string Kind,
    int StartFrame,
    int DurationFrames,
    ushort ModelId,
    ushort BodyId,
    int Variant,
    int TrackOrder,
    int ItemOrder);
