namespace TarkovCompanion.Application.Services;

public static class FuzzyMatcher
{
    public static double Similarity(string? left, string? right)
    {
        var normalizedLeft = TextNormalizer.Normalize(left);
        var normalizedRight = TextNormalizer.Normalize(right);

        if (normalizedLeft == normalizedRight)
        {
            return 1;
        }

        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0)
        {
            return 0;
        }

        if (normalizedLeft.Contains(normalizedRight, StringComparison.Ordinal) ||
            normalizedRight.Contains(normalizedLeft, StringComparison.Ordinal))
        {
            var ratio = (double)Math.Min(normalizedLeft.Length, normalizedRight.Length) /
                Math.Max(normalizedLeft.Length, normalizedRight.Length);
            return Math.Max(0.82, ratio);
        }

        var distance = LevenshteinDistance(normalizedLeft, normalizedRight);
        return 1 - ((double)distance / Math.Max(normalizedLeft.Length, normalizedRight.Length));
    }

    /// <summary>
    /// How well a typed query matches an item's short name.
    /// </summary>
    /// <remarks>
    /// <see cref="Similarity"/> scores any containment at 0.82 or better, which is right for a
    /// name and wrong for a short name: "AP" is inside "graphics", and "Car" is inside "card", so
    /// "Graphics card" matched every ".300 Blackout AP" round and every car key. A short name
    /// therefore counts as contained only when it is a whole word of the query (or the query a
    /// whole word of it); a substring inside a word is compared by edit distance like any other
    /// non-match, and a one- or two-letter short name matches only when typed in full. Prefix
    /// typing ("m4" for "M4A1") is found by the prefix pass in the repository, not here.
    /// The general <see cref="Similarity"/> is unchanged, including for recognition.
    /// </remarks>
    public static double ShortNameSimilarity(string? query, string? shortName)
    {
        var normalizedQuery = TextNormalizer.Normalize(query);
        var normalizedShortName = TextNormalizer.Normalize(shortName);
        if (normalizedQuery == normalizedShortName)
        {
            return 1;
        }

        if (normalizedQuery.Length == 0 || normalizedShortName.Length < 3)
        {
            return 0;
        }

        var containedAsWords = ContainsWholeWords(normalizedQuery, normalizedShortName) ||
            ContainsWholeWords(normalizedShortName, normalizedQuery);
        var containedInsideAWord = !containedAsWords &&
            (normalizedQuery.Contains(normalizedShortName, StringComparison.Ordinal) ||
             normalizedShortName.Contains(normalizedQuery, StringComparison.Ordinal));
        return containedInsideAWord
            ? 1 - ((double)LevenshteinDistance(normalizedQuery, normalizedShortName) /
                Math.Max(normalizedQuery.Length, normalizedShortName.Length))
            : Similarity(normalizedQuery, normalizedShortName);
    }

    private static bool ContainsWholeWords(string haystack, string needle) =>
        $" {haystack} ".Contains($" {needle} ", StringComparison.Ordinal);

    private static int LevenshteinDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var substitution = previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
