using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Meddle.Plugin.Models;
using Meddle.Plugin.Models.Layout;
using Microsoft.Extensions.Logging;

namespace Meddle.Plugin.Services;

/// <summary>
/// Attributes each resolved character model to the Penumbra mod that supplied
/// its model, material, or texture. Penumbra remains authoritative for the
/// installed mod names; the live resolved file handles remain authoritative for
/// what was actually drawn.
/// </summary>
public sealed class CharacterPartProvenanceService : IService
{
    private const string ModelRole = "Model";
    private const string MaterialRole = "Material";
    private const string TextureRole = "Texture";
    private const string GameOrSwapSource = "GameOrSwap";
    private const int MaximumMods = 4_096;
    private const int MaximumParts = 2_048;
    private const int MaximumCharacterNodes = 256;
    private const int MaximumContributorsPerPart = 32;
    private const int MaximumIdentifierLength = 255;
    private const int MaximumDisplayNameLength = 256;
    private const int MaximumGamePathLength = 1_024;

    private readonly ILogger<CharacterPartProvenanceService> logger;
    private readonly ICallGateSubscriber<Dictionary<string, string>> getModList;
    private readonly ICallGateSubscriber<string> getModDirectory;

    public CharacterPartProvenanceService(
        ILogger<CharacterPartProvenanceService> logger,
        IDalamudPluginInterface pluginInterface)
    {
        this.logger = logger;
        getModList = pluginInterface.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");
        getModDirectory = pluginInterface.GetIpcSubscriber<string>("Penumbra.GetModDirectory");
    }

    /// <summary>
    /// This method is intentionally fail-open and should be called on Dalamud's
    /// framework thread. Failure only removes human-readable mod attribution;
    /// it never invalidates the captured character geometry.
    /// </summary>
    public CharacterPartProvenanceResult Capture(ParsedCharacterInfo characterInfo)
    {
        ArgumentNullException.ThrowIfNull(characterInfo);

        IReadOnlyList<ModRoot> modRoots = [];
        var warnings = new List<string>();
        try
        {
            modRoots = CaptureModRoots();
        }
        catch (Exception exception)
        {
            warnings.Add(
                "Penumbra mod names were unavailable; Blender part collections will use Vanilla / Game labels.");
            logger.LogWarning(exception, "Could not capture Penumbra character-part provenance");
        }

        var partSources = BuildPartSources(characterInfo, modRoots, warnings);
        return new CharacterPartProvenanceResult(partSources, warnings);
    }

    private IReadOnlyList<ModRoot> CaptureModRoots()
    {
        var baseValue = getModDirectory.InvokeFunc();
        if (string.IsNullOrWhiteSpace(baseValue) || !Path.IsPathRooted(baseValue))
        {
            throw new InvalidDataException("Penumbra did not return an absolute mod directory.");
        }

        var baseRoot = Path.GetFullPath(baseValue);
        if (!Directory.Exists(baseRoot))
        {
            throw new DirectoryNotFoundException("Penumbra's physical mod directory is unavailable.");
        }

        var mods = getModList.InvokeFunc()
                   ?? throw new InvalidDataException("Penumbra returned no installed-mod list.");
        if (mods.Count > MaximumMods)
        {
            throw new InvalidDataException(
                $"Penumbra returned more than {MaximumMods:N0} installed mods.");
        }

        var result = new List<ModRoot>(mods.Count);
        foreach (var (rawIdentifier, rawDisplayName) in mods)
        {
            if (!TrySanitizeIdentifier(rawIdentifier, out var identifier))
            {
                continue;
            }

            var physicalRoot = Path.GetFullPath(Path.Combine(baseRoot, identifier));
            if (!IsInside(physicalRoot, baseRoot) || !Directory.Exists(physicalRoot))
            {
                continue;
            }

            var displayName = SanitizeDisplayName(rawDisplayName, identifier);
            result.Add(new ModRoot(identifier, displayName, physicalRoot));
        }

        // A deepest-root match is deterministic even if a future Penumbra
        // version permits nested mod roots.
        return result
            .OrderByDescending(item => item.PhysicalRoot.Length)
            .ThenBy(item => item.Identifier, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<CharacterPartSource> BuildPartSources(
        ParsedCharacterInfo characterInfo,
        IReadOnlyList<ModRoot> modRoots,
        ICollection<string> warnings)
    {
        var result = new List<CharacterPartSource>();
        var seenCharacters = new HashSet<ParsedCharacterInfo>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<ParsedCharacterInfo>();
        pending.Push(characterInfo);
        var characterNodes = 0;
        var partsTruncated = false;

        while (pending.Count > 0 && result.Count < MaximumParts)
        {
            var current = pending.Pop();
            if (!seenCharacters.Add(current))
            {
                continue;
            }

            characterNodes++;
            if (characterNodes > MaximumCharacterNodes)
            {
                warnings.Add(
                    $"Character attachment provenance was limited to {MaximumCharacterNodes:N0} attachment nodes.");
                break;
            }

            foreach (var model in current.Models)
            {
                if (result.Count >= MaximumParts)
                {
                    partsTruncated = true;
                    break;
                }

                result.Add(BuildPartSource(result.Count, model, modRoots));
            }

            // Stack the children in reverse for deterministic depth-first order.
            for (var index = current.Attaches.Count - 1; index >= 0; index--)
            {
                pending.Push(current.Attaches[index]);
            }
        }

        if (partsTruncated || result.Count >= MaximumParts && pending.Count > 0)
        {
            warnings.Add($"Character part provenance was limited to {MaximumParts:N0} models.");
        }

        return result;
    }

    private static CharacterPartSource BuildPartSource(
        int partIndex,
        ParsedModelInfo model,
        IReadOnlyList<ModRoot> modRoots)
    {
        var contributions = new Dictionary<string, MutableContribution>(StringComparer.OrdinalIgnoreCase);
        var modelOwners = new List<ModRoot>();
        var materialOwners = new List<ModRoot>();
        var textureOwners = new List<ModRoot>();

        AddOwner(model.Path.FullPath, ModelRole, modRoots, modelOwners, contributions);
        foreach (var material in model.Materials)
        {
            if (material is null)
            {
                continue;
            }

            AddOwner(material.Path.FullPath, MaterialRole, modRoots, materialOwners, contributions);
            foreach (var texture in material.Textures)
            {
                AddOwner(texture.Path.FullPath, TextureRole, modRoots, textureOwners, contributions);
            }
        }

        var (sourceLevel, primaryOwners) = modelOwners.Count > 0
            ? (ModelRole, DistinctOwners(modelOwners))
            : materialOwners.Count > 0
                ? (MaterialRole, DistinctOwners(materialOwners))
                : textureOwners.Count > 0
                    ? (TextureRole, DistinctOwners(textureOwners))
                    : (GameOrSwapSource, Array.Empty<ModRoot>());
        var primary = primaryOwners.FirstOrDefault();
        var contributors = contributions.Values
            .OrderBy(item => RoleOrder(item.Roles))
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Identifier, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumContributorsPerPart)
            .Select(item => new CharacterPartContributor(
                item.Identifier,
                item.DisplayName,
                item.Roles.OrderBy(RoleOrder).ToArray()))
            .ToArray();

        var gamePath = SanitizeGamePath(model.Path.GamePath);
        return new CharacterPartSource(
            PartIndex: partIndex,
            ModelGamePath: gamePath,
            Category: ClassifyCategory(gamePath),
            SourceLevel: sourceLevel,
            PrimaryModIdentifier: primary?.Identifier,
            PrimaryModName: primary?.DisplayName,
            PrimarySourceAmbiguous: primaryOwners.Count > 1,
            Contributors: contributors);
    }

    private static void AddOwner(
        string path,
        string role,
        IReadOnlyList<ModRoot> modRoots,
        ICollection<ModRoot> owners,
        IDictionary<string, MutableContribution> contributions)
    {
        var owner = FindOwner(path, modRoots);
        if (owner is null)
        {
            return;
        }

        owners.Add(owner);
        if (!contributions.TryGetValue(owner.Identifier, out var contribution))
        {
            contribution = new MutableContribution(owner.Identifier, owner.DisplayName);
            contributions.Add(owner.Identifier, contribution);
        }

        contribution.Roles.Add(role);
    }

    private static ModRoot? FindOwner(string value, IReadOnlyList<ModRoot> modRoots)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
        {
            return null;
        }

        string path;
        try
        {
            path = Path.GetFullPath(value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return modRoots.FirstOrDefault(root => IsInside(path, root.PhysicalRoot));
    }

    private static IReadOnlyList<ModRoot> DistinctOwners(IEnumerable<ModRoot> values)
    {
        var result = new List<ModRoot>();
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (identifiers.Add(value.Identifier))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static int RoleOrder(IEnumerable<string> roles)
    {
        if (roles.Contains(ModelRole, StringComparer.Ordinal)) return 0;
        if (roles.Contains(MaterialRole, StringComparer.Ordinal)) return 1;
        if (roles.Contains(TextureRole, StringComparer.Ordinal)) return 2;
        return 3;
    }

    private static int RoleOrder(string role) => role switch
    {
        ModelRole => 0,
        MaterialRole => 1,
        TextureRole => 2,
        _ => 3,
    };

    private static string SanitizeGamePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var normalized = value.Trim().Replace('\\', '/');
        if (normalized.Length > MaximumGamePathLength
            || normalized.StartsWith('/')
            || normalized.Contains(':')
            || normalized.Split('/').Any(segment => segment is "." or "..")
            || normalized.Any(char.IsControl))
        {
            return "unknown";
        }

        return normalized;
    }

    private static string ClassifyCategory(string gamePath)
    {
        var path = gamePath.ToLowerInvariant();
        if (path.Contains("/obj/hair/", StringComparison.Ordinal)) return "Hair";
        if (path.Contains("/obj/face/", StringComparison.Ordinal)) return "Face";
        if (path.Contains("/obj/tail/", StringComparison.Ordinal)
            || path.Contains("/obj/zear/", StringComparison.Ordinal)) return "Tail / Ears";
        if (path.Contains("/obj/body/", StringComparison.Ordinal)) return "Body";
        if (path.Contains("/weapon/", StringComparison.Ordinal)) return "Weapon";

        if (path.EndsWith("_met.mdl", StringComparison.Ordinal)) return "Clothes · Head";
        if (path.EndsWith("_top.mdl", StringComparison.Ordinal)) return "Clothes · Upper Body";
        if (path.EndsWith("_glv.mdl", StringComparison.Ordinal)) return "Clothes · Hands";
        if (path.EndsWith("_dwn.mdl", StringComparison.Ordinal)) return "Clothes · Legs";
        if (path.EndsWith("_sho.mdl", StringComparison.Ordinal)) return "Clothes · Feet";
        if (path.EndsWith("_ear.mdl", StringComparison.Ordinal)) return "Accessory · Ears";
        if (path.EndsWith("_nek.mdl", StringComparison.Ordinal)) return "Accessory · Neck";
        if (path.EndsWith("_wrs.mdl", StringComparison.Ordinal)) return "Accessory · Wrists";
        if (path.EndsWith("_rir.mdl", StringComparison.Ordinal)) return "Accessory · Right Ring";
        if (path.EndsWith("_ril.mdl", StringComparison.Ordinal)) return "Accessory · Left Ring";
        if (path.EndsWith("_gls.mdl", StringComparison.Ordinal)) return "Accessory · Glasses";
        if (path.Contains("/accessory/", StringComparison.Ordinal)) return "Accessory";
        if (path.Contains("/equipment/", StringComparison.Ordinal)) return "Clothes";
        return "Character Part";
    }

    private static bool TrySanitizeIdentifier(string? value, out string identifier)
    {
        identifier = value?.Trim() ?? string.Empty;
        return identifier.Length is > 0 and <= MaximumIdentifierLength
               && identifier is not "." and not ".."
               && string.Equals(Path.GetFileName(identifier), identifier, StringComparison.Ordinal)
               && identifier.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) < 0
               && !identifier.Any(char.IsControl);
    }

    private static string SanitizeDisplayName(string? value, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var characters = source
            .Take(MaximumDisplayNameLength)
            .Select(character => char.IsControl(character)
                || character is '\\' or '/' or ':'
                    ? ' '
                    : character)
            .ToArray();
        var sanitized = string.Join(' ', new string(characters)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return sanitized.Length == 0 ? fallback : sanitized;
    }

    private static bool IsInside(string candidate, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedCandidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ModRoot(string Identifier, string DisplayName, string PhysicalRoot);

    private sealed class MutableContribution(string identifier, string displayName)
    {
        public string Identifier { get; } = identifier;
        public string DisplayName { get; } = displayName;
        public HashSet<string> Roles { get; } = new(StringComparer.Ordinal);
    }
}
