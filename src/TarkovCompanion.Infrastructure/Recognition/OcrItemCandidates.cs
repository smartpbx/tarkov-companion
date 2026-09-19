using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

/// <summary>
/// Turns the lines a text engine read into ranked catalog items.
/// </summary>
/// <remarks>
/// Lifted out of <see cref="RecognitionService"/> unchanged, because the V2 capture pipeline needs
/// the same answer from the same read. The alternative was a second recognizer over the same
/// frame, which is both the slowest possible way to ask and a licence for two code paths to
/// disagree about what a screenshot showed.
/// </remarks>
public static class OcrItemCandidates
{
    /// <summary>
    /// Words the game's own chrome puts on an item screen, which are not item names.
    /// </summary>
    /// <remarks>
    /// Without this the fuzzy resolver happily matches "Weight" and "Price" against the catalog
    /// and a screenshot of a scope comes back as a list of trade goods.
    /// </remarks>
    private static readonly HashSet<string> UiChromeTerms = new(StringComparer.Ordinal)
    {
        "inspect",
        "durability",
        "ergonomics",
        "weight",
        "stash",
        "sorting table",
        "pockets",
        "extracts",
        "exfil",
        "flea market",
        "filter by item",
        "purchase",
        "price",
        "trader rating",
    };

    /// <summary>How many alternates are worth showing beside the answer.</summary>
    public const int MaximumCandidates = 5;

    /// <summary>The best catalog matches for one frame's lines, most confident first.</summary>
    public static IReadOnlyList<RecognitionCandidate> Rank(
        OcrResult result,
        FuzzyCanonicalItemResolver resolver,
        OcrTextNormalizer normalizer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(normalizer);

        return Resolve(result, resolver, normalizer, cancellationToken)
            .GroupBy(candidate => candidate.CanonicalId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(candidate => candidate.Confidence.Value).First())
            .OrderByDescending(candidate => candidate.Confidence.Value)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
            .Take(MaximumCandidates)
            .ToList();
    }

    private static IEnumerable<RecognitionCandidate> Resolve(
        OcrResult result,
        FuzzyCanonicalItemResolver resolver,
        OcrTextNormalizer normalizer,
        CancellationToken cancellationToken)
    {
        foreach (var line in result.Lines)
        {
            // A provider may return thousands of lines, and each is a fuzzy search of the catalog.
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = normalizer.NormalizeForLookup(line.Text);
            if (normalized.Length < 2 || UiChromeTerms.Contains(normalized))
            {
                continue;
            }

            var resolution = resolver.Resolve(line.Text, line.Confidence, bounds: line.Bounds);
            foreach (var candidate in resolution.Candidates)
            {
                yield return candidate with { Evidence = $"{candidate.Evidence}; engine={result.Engine}" };
            }
        }
    }
}
