namespace TarkovCompanion.Application.Services.Ask;

/// <summary>One name a question could mean, and how well it fits.</summary>
public sealed record AskNameMatch<T>(T Value, string Name, double Score);

/// <summary>
/// Fuzzy matching of a typed name against quest and hideout station names, word by word.
/// </summary>
/// <remarks>
/// <para>
/// Whole-string similarity is wrong for the way players shorten quest names: "Gunsmith 5" is
/// "Gunsmith Master - Part 5" in the 2026 catalog, and by edit distance it is closer to
/// "Gunsmith - M4A1". So every word typed has to be found among the name's words: a number only as
/// that exact number (5 is never 15), a word as a prefix or within a letter or two
/// (<see cref="FuzzyMatcher.Similarity"/>). Filler the catalog writes and players do not ("Part",
/// "the", "-") is dropped from both sides.
/// </para>
/// <para>
/// A name with every typed word scores between 0.7 and 1 (more for a name with fewer extra words);
/// a name missing a typed word scores below 0.6 and is only ever offered as a closest match.
/// </para>
/// </remarks>
public static class AskNameMatcher
{
    /// <summary>The score from which a name is taken as the one meant.</summary>
    public const double Accept = 0.7;

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "part", "the", "a", "an", "of", "level", "lvl", "lv", "quest", "task", "station",
    };

    public static double Score(string query, string name)
    {
        var typed = Words(query);
        var named = Words(name);
        if (typed.Length == 0 || named.Length == 0)
        {
            return 0;
        }

        var matchedTyped = 0;
        var used = new bool[named.Length];
        foreach (var word in typed)
        {
            for (var index = 0; index < named.Length; index++)
            {
                if (!used[index] && WordMatches(word, named[index]))
                {
                    used[index] = true;
                    matchedTyped++;
                    break;
                }
            }
        }

        var typedShare = (double)matchedTyped / typed.Length;
        var nameShare = (double)used.Count(value => value) / named.Length;
        return typedShare < 1
            ? typedShare * 0.6 * (0.5 + (0.5 * nameShare))
            : 0.7 + (0.3 * nameShare);
    }

    /// <summary>The candidates ranked best first, ties broken by the shorter name.</summary>
    public static IReadOnlyList<AskNameMatch<T>> Rank<T>(string query, IEnumerable<T> candidates, Func<T, string> name) =>
    [
        .. candidates
            .Select(candidate => new AskNameMatch<T>(candidate, name(candidate), Score(query, name(candidate))))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Name.Length)
            .ThenBy(match => match.Name, StringComparer.OrdinalIgnoreCase),
    ];

    private static bool WordMatches(string typed, string named)
    {
        if (typed == named)
        {
            return true;
        }

        if (typed.All(char.IsDigit) || named.All(char.IsDigit))
        {
            return false;
        }

        if (typed.Length >= 3 && named.StartsWith(typed, StringComparison.Ordinal))
        {
            return true;
        }

        // "dorms" for "dorm", "gunsmiths": a plural typed against a singular name.
        if (typed.Length >= 4 && typed.EndsWith('s') && typed[..^1] == named)
        {
            return true;
        }

        return typed.Length >= 4 && named.Length >= 4 && FuzzyMatcher.Similarity(typed, named) >= 0.8;
    }

    private static string[] Words(string text) =>
        [.. TextNormalizer.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => !Filler.Contains(word))];
}
