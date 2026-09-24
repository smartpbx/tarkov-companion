namespace TarkovCompanion.Core.Domain.Maps;

/// <summary>
/// A map's slug ("streets-of-tarkov") as a player reads it ("Streets of Tarkov").
/// </summary>
/// <remarks>
/// tarkov.dev's maps.json names a location only by its slug, and three places each turned the slug
/// back into a name their own way: the map catalog parser and Loot Scan capitalised every word
/// ("Streets Of Tarkov"), Team a slightly different rule. The catalog's names feed the desktop map
/// chooser, and the tablet's list is copied from that chooser (#806), so both read it wrong.
/// </remarks>
public static class MapDisplayName
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["streets-of-tarkov"] = "Streets of Tarkov",
        ["the-lab"] = "The Lab",
        ["laboratory"] = "The Lab",
        ["lab"] = "The Lab",
        ["ground-zero"] = "Ground Zero",
        ["ground-zero-21"] = "Ground Zero 21+",
        ["the-labyrinth"] = "The Labyrinth",
        ["labyrinth"] = "The Labyrinth",
        ["night-factory"] = "Night Factory",
    };

    private static readonly HashSet<string> MinorWords = new(StringComparer.OrdinalIgnoreCase) { "of", "the", "and" };

    /// <summary>The player-facing name for a slug; empty for nothing.</summary>
    public static string FromId(string? mapId)
    {
        if (string.IsNullOrWhiteSpace(mapId))
        {
            return string.Empty;
        }

        var trimmed = mapId.Trim();
        if (Known.TryGetValue(trimmed, out var known))
        {
            return known;
        }

        var words = trimmed.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select((word, index) =>
            index > 0 && MinorWords.Contains(word)
                ? word.ToLowerInvariant()
                : char.ToUpperInvariant(word[0]) + word[1..]));
    }
}
