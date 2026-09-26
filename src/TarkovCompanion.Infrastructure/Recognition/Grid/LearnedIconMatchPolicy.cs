namespace TarkovCompanion.Infrastructure.Recognition.Grid;

/// <summary>
/// When the player's own corrected icon crops may name a cell the catalog art could not.
/// </summary>
/// <remarks>
/// Epic #712 decision 9: recognition stays never wrong. A learned crop is only ever consulted
/// for a cell the catalog references refused, so a cell the catalog already named keeps that
/// name whatever the player has taught; "0 wrong stays 0 wrong" holds by construction for every
/// previously named cell. For a refused cell the learned item must clear a floor and a margin
/// over every other learned item, and the catalog art of that same item must still look like
/// the cell (a lookalike score), so a learned crop cannot name something its own reference art
/// says it is not. The figures are measured on the real stash corpus by
/// <c>LearnedReferenceStudyTests</c>; see docs/RECOGNITION.md.
/// </remarks>
public static class LearnedIconMatchPolicy
{
    /// <summary>A learned crop must correlate at least this well with the cell.</summary>
    public const double MinimumCorrelation = 0.90;

    /// <summary>...and stand this far clear of any other item's learned crop.</summary>
    public const double MinimumMargin = 0.04;

    /// <summary>The item's own catalog art must score at least this (the lookalike floor).</summary>
    public const double MinimumCatalogCorrelation = 0.60;

    /// <summary>
    /// The item the learned crops name, or null. <paramref name="catalogScores"/> is the best
    /// catalog score per item id for the cell; <paramref name="learnedScores"/> the best learned
    /// score per item id.
    /// </summary>
    public static string? Choose(
        IReadOnlyDictionary<string, double> catalogScores,
        IReadOnlyDictionary<string, double> learnedScores,
        double minimumCorrelation = MinimumCorrelation,
        double minimumMargin = MinimumMargin)
    {
        ArgumentNullException.ThrowIfNull(catalogScores);
        ArgumentNullException.ThrowIfNull(learnedScores);
        string? best = null;
        var bestScore = double.NegativeInfinity;
        var second = double.NegativeInfinity;
        foreach (var (itemId, score) in learnedScores)
        {
            if (score > bestScore || (score == bestScore && string.CompareOrdinal(itemId, best) < 0))
            {
                second = Math.Max(second, bestScore);
                best = itemId;
                bestScore = score;
            }
            else if (score > second)
            {
                second = score;
            }
        }

        if (best is null || bestScore < minimumCorrelation || bestScore - second < minimumMargin)
        {
            return null;
        }

        return catalogScores.TryGetValue(best, out var catalog) && catalog >= MinimumCatalogCorrelation
            ? best
            : null;
    }
}
