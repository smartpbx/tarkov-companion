using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed record CanonicalItemReference(
    string Id,
    string DisplayName,
    IReadOnlyList<string>? Aliases = null);

public sealed record CanonicalItemResolution(
    IReadOnlyList<RecognitionCandidate> Candidates,
    RecognitionDecision Decision)
{
    public RecognitionCandidate? Best => Candidates.FirstOrDefault();
}

public sealed class FuzzyCanonicalItemResolver
{
    private readonly IReadOnlyList<IndexedItem> _items;
    private readonly OcrTextNormalizer _normalizer;

    public FuzzyCanonicalItemResolver(
        IEnumerable<CanonicalItemReference> items,
        OcrTextNormalizer? normalizer = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        _normalizer = normalizer ?? new OcrTextNormalizer();
        _items = items.Select(Index).ToArray();
    }

    public CanonicalItemResolution Resolve(
        string observedText,
        Confidence ocrConfidence,
        int limit = 5,
        PixelRect? bounds = null)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var normalized = _normalizer.NormalizeForLookup(observedText);
        if (normalized.Length == 0)
        {
            return new([], RecognitionDecision.NoMatch);
        }

        var candidates = _items
            .Select(item => Score(item, normalized, ocrConfidence, bounds))
            .Where(candidate => candidate.Confidence.Value >= RecognitionPolicy.CandidateFloor)
            .OrderByDescending(candidate => candidate.Confidence.Value)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();

        var decision = candidates.Length == 0
            ? RecognitionDecision.NoMatch
            : RecognitionPolicy.Classify(candidates[0].Confidence);
        return new(candidates, decision);
    }

    private IndexedItem Index(CanonicalItemReference item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.DisplayName);

        var names = new[] { item.DisplayName }
            .Concat(item.Aliases ?? [])
            .Select(_normalizer.NormalizeForLookup)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new(item.Id, item.DisplayName, names);
    }

    private static RecognitionCandidate Score(
        IndexedItem item,
        string observed,
        Confidence ocrConfidence,
        PixelRect? bounds)
    {
        var similarity = item.Names.Max(name => FuzzyTextSimilarity.Score(observed, name));
        var combined = Math.Clamp((similarity * 0.80) + (ocrConfidence.Value * 0.20), 0, 1);
        return new(
            item.Id,
            item.DisplayName,
            new Confidence(combined),
            $"ocr-fuzzy; normalized={observed}; similarity={similarity:F3}",
            bounds);
    }

    private sealed record IndexedItem(string Id, string DisplayName, IReadOnlyList<string> Names);
}

public static class FuzzyTextSimilarity
{
    public static double Score(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1;
        }

        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        var editScore = 1d - ((double)DamerauLevenshteinDistance(left, right) / Math.Max(left.Length, right.Length));
        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var intersection = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        var tokenScore = leftTokens.Count + rightTokens.Count == 0
            ? 0
            : (2d * intersection) / (leftTokens.Count + rightTokens.Count);
        return Math.Clamp(Math.Max(editScore, (editScore * 0.75) + (tokenScore * 0.25)), 0, 1);
    }

    private static int DamerauLevenshteinDistance(string left, string right)
    {
        var distances = new int[left.Length + 1, right.Length + 1];
        for (var leftIndex = 0; leftIndex <= left.Length; leftIndex++)
        {
            distances[leftIndex, 0] = leftIndex;
        }

        for (var rightIndex = 0; rightIndex <= right.Length; rightIndex++)
        {
            distances[0, rightIndex] = rightIndex;
        }

        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitutionCost = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                var distance = Math.Min(
                    Math.Min(
                        distances[leftIndex - 1, rightIndex] + 1,
                        distances[leftIndex, rightIndex - 1] + 1),
                    distances[leftIndex - 1, rightIndex - 1] + substitutionCost);

                if (leftIndex > 1 &&
                    rightIndex > 1 &&
                    left[leftIndex - 1] == right[rightIndex - 2] &&
                    left[leftIndex - 2] == right[rightIndex - 1])
                {
                    distance = Math.Min(distance, distances[leftIndex - 2, rightIndex - 2] + 1);
                }

                distances[leftIndex, rightIndex] = distance;
            }
        }

        return distances[left.Length, right.Length];
    }
}
