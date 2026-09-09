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
