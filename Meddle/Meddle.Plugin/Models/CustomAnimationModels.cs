namespace Meddle.Plugin.Models;

public sealed record PenumbraAnimationModChoice(
    string ModIdentifier,
    string DisplayName);

public sealed record CustomAnimationRegistry(
    int SchemaVersion,
    IReadOnlyList<CustomAnimationSource> Sources);

public sealed record CustomAnimationSource(
    string ModIdentifier,
    string DisplayName,
    DateTimeOffset ImportedAtUtc,
    Guid CollectionId,
    string CollectionName,
    IReadOnlyList<string> SelectedOptions,
    IReadOnlyList<CustomAnimationBinding> Bindings);

public sealed record CustomAnimationBinding(
    string EntryId,
    string VariantId,
    string? BaseEntryId,
    uint? BaseEmoteId,
    ushort TargetRaceCode,
    ushort SourceRaceCode,
    string CanonicalGamePath,
    string ModRelativePath,
    string ContentSha256,
    long ContentLength);

public sealed record TrustedCustomPap(
    string DisplayPath,
    byte[] Bytes,
    ushort SourceRaceCode);
