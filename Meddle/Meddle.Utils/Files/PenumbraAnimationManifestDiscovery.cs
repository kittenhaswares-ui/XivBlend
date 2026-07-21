using System.Text.Json;

namespace Meddle.Utils.Files;

/// <summary>
/// Reads only canonical player-body PAP keys from Penumbra mod JSON. It does
/// not interpret option state: the caller must still ask Penumbra to resolve
/// every returned game path and accept only files won by the selected mod.
/// </summary>
public static class PenumbraAnimationManifestDiscovery
{
    public const int MaximumJsonDepth = 64;

    private const string Prefix = "chara/human/c";
    private const string AnimationRoot = "/animation/a0001/bt_common/";
    private const string Suffix = ".pap";

    public static IReadOnlyList<PenumbraBodyPapReference> ReadBodyPapReferences(
        Stream jsonStream,
        ushort targetRaceCode,
        int maximumReferences)
    {
        ArgumentNullException.ThrowIfNull(jsonStream);
        if (!PlayerRaceCode(targetRaceCode))
        {
            throw new ArgumentOutOfRangeException(nameof(targetRaceCode));
        }

        if (maximumReferences <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumReferences));
        }

        using var document = JsonDocument.Parse(
            jsonStream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = MaximumJsonDepth,
            });
        var references = new Dictionary<string, PenumbraBodyPapReference>(
            StringComparer.OrdinalIgnoreCase);
        Visit(document.RootElement, targetRaceCode, maximumReferences, references);
        return references.Values
            .OrderBy(item => item.SourceRaceCode == targetRaceCode ? 0 : 1)
            .ThenBy(item => item.CanonicalGamePath, StringComparer.Ordinal)
            .ToArray();
    }

    public static bool TryParseCanonicalBodyPapPath(
        string? value,
        out PenumbraBodyPapReference reference)
    {
        reference = default!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4_096)
        {
            return false;
        }

        var normalized = value.Replace('\\', '/');
        if (!normalized.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || !normalized.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        var raceStart = Prefix.Length;
        var raceText = normalized.AsSpan(raceStart, Math.Min(4, normalized.Length - raceStart));
        if (normalized.Length <= raceStart + 4 + AnimationRoot.Length + Suffix.Length
            || raceText.Length != 4
            || raceText[0] is < '0' or > '9'
            || raceText[1] is < '0' or > '9'
            || raceText[2] is < '0' or > '9'
            || raceText[3] is < '0' or > '9'
            || !ushort.TryParse(raceText, out var sourceRaceCode)
            || !PlayerRaceCode(sourceRaceCode))
        {
            return false;
        }

        var rootStart = raceStart + 4;
        if (!normalized.AsSpan(rootStart).StartsWith(AnimationRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var keyStart = rootStart + AnimationRoot.Length;
        var animationKey = normalized[keyStart..^Suffix.Length];
        if (string.IsNullOrWhiteSpace(animationKey)
            || animationKey[0] == '/'
            || animationKey[^1] == '/'
            || animationKey.Split('/').Any(segment => segment is "" or "." or "..")
            || animationKey.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not '_' and not '-' and not '.' and not '/'))
        {
            return false;
        }

        var canonical = $"{Prefix}{sourceRaceCode:D4}{AnimationRoot}{animationKey}{Suffix}"
            .ToLowerInvariant();
        reference = new PenumbraBodyPapReference(
            canonical,
            sourceRaceCode,
            animationKey.ToLowerInvariant());
        return true;
    }

    private static void Visit(
        JsonElement element,
        ushort targetRaceCode,
        int maximumReferences,
        IDictionary<string, PenumbraBodyPapReference> references)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (TryParseCanonicalBodyPapPath(property.Name, out var reference)
                        && (reference.SourceRaceCode == targetRaceCode || reference.SourceRaceCode == 101)
                        && !references.ContainsKey(reference.CanonicalGamePath))
                    {
                        if (references.Count >= maximumReferences)
                        {
                            throw new InvalidDataException(
                                $"Penumbra animation metadata exceeds the {maximumReferences:N0}-path safety limit.");
                        }

                        references.Add(reference.CanonicalGamePath, reference);
                    }

                    Visit(property.Value, targetRaceCode, maximumReferences, references);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Visit(item, targetRaceCode, maximumReferences, references);
                }

                break;
        }
    }

    private static bool PlayerRaceCode(ushort value) =>
        value is >= 101 and <= 1801 && value % 100 == 1;
}

public sealed record PenumbraBodyPapReference(
    string CanonicalGamePath,
    ushort SourceRaceCode,
    string AnimationKey);
