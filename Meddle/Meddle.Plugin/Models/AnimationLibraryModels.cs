namespace Meddle.Plugin.Models;

/// <summary>
/// JSON contracts shared by the Dalamud-side vanilla animation cache and the
/// optional Blender companion add-on.  These contain metadata and local file
/// locators only; Square Enix assets are generated into the user's cache at
/// runtime and are never embedded in the plugin package.
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
    uint EmoteId,
    string Name,
    string Command,
    uint IconId,
    uint ResolvedIconId,
    string IconRelativePath,
    string Category,
    string DefaultVariantId,
    IReadOnlyList<AnimationCatalogVariant> Variants);

public sealed record AnimationCatalogVariant(
    string VariantId,
    uint ActionTimelineId,
    int Slot,
    string Label,
    string Kind,
    string Key,
    byte LoadType,
    bool IsDefault,
    bool IsLoop,
    string CacheRelativePathTemplate);

public sealed record AnimationLibraryPointer(
    int SchemaVersion,
    string GameVersion,
    string Language,
    string CatalogRelativePath);

public sealed record AnimationLibraryRequest(
    int SchemaVersion,
    string RequestId,
    string GameVersion,
    uint EmoteId,
    string VariantId,
    ushort RaceCode,
    string? FaceSkeleton);

public sealed record AnimationLibraryResponse(
    int SchemaVersion,
    string RequestId,
    string Status,
    string? ClipRelativePath,
    string? Error,
    int? FrameCount,
    float? DurationSeconds,
    int? AnimatedBoneCount,
    string? SourcePap);
