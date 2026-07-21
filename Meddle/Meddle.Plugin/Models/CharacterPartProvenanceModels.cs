namespace Meddle.Plugin.Models;

/// <summary>
/// Path-free attribution metadata for one resolved character model. The
/// physical Penumbra paths used to determine ownership are deliberately not
/// retained here, so this record is safe to serialize into an export manifest.
/// </summary>
public sealed record CharacterPartSource(
    int PartIndex,
    string ModelGamePath,
    string Category,
    string SourceLevel,
    string? PrimaryModIdentifier,
    string? PrimaryModName,
    bool PrimarySourceAmbiguous,
    IReadOnlyList<CharacterPartContributor> Contributors);

/// <summary>
/// A Penumbra mod that contributed a model, material, or texture to a part.
/// ModIdentifier is a single directory name, never a filesystem path.
/// </summary>
public sealed record CharacterPartContributor(
    string ModIdentifier,
    string DisplayName,
    IReadOnlyList<string> Roles);

public sealed record CharacterPartProvenanceResult(
    IReadOnlyList<CharacterPartSource> PartSources,
    IReadOnlyList<string> Warnings);
