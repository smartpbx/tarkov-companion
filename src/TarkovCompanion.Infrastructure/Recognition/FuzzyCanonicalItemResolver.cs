using System.Buffers;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed record CanonicalItemResolution(
    IReadOnlyList<RecognitionCandidate> Candidates,
    RecognitionDecision Decision)
{
    public RecognitionCandidate? Best => Candidates.FirstOrDefault();
}

public sealed class FuzzyCanonicalItemResolver
{
    private const int MaximumPrefilterCandidates = 96;
    private const int MaximumComparedTextLength = 160;
    private readonly IReadOnlyList<IndexedName> _names;
    private readonly IReadOnlyDictionary<string, int[]> _exactIndex;
    private readonly IReadOnlyDictionary<string, int[]> _trigramIndex;
    private readonly OcrTextNormalizer _normalizer;

    public FuzzyCanonicalItemResolver(
        IEnumerable<CanonicalItemReference> items,
        OcrTextNormalizer? normalizer = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        _normalizer = normalizer ?? new OcrTextNormalizer();

        _names = items.SelectMany(Index).ToArray();
        _exactIndex = _names
            .Select((name, index) => (name.Normalized, index))
            .GroupBy(pair => pair.Normalized, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.index).ToArray(),
                StringComparer.Ordinal);
        _trigramIndex = _names
            .SelectMany((name, index) => Trigrams(name.Normalized).Select(trigram => (trigram, index)))
            .GroupBy(pair => pair.trigram, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.index).Distinct().ToArray(),
                StringComparer.Ordinal);
    }

    /// <param name="ocrConfidence">
    /// What the engine thought of the reading, or null where it does not score at all. Null and
    /// zero are different answers and are treated differently: see <see cref="OcrLine"/>.
    /// </param>
    public CanonicalItemResolution Resolve(
        string observedText,
        Confidence? ocrConfidence,
        int limit = 5,
        PixelRect? bounds = null)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var normalized = _normalizer.NormalizeForLookup(observedText);
        if (normalized.Length == 0 || normalized.Length > MaximumComparedTextLength)
        {
            return new([], RecognitionDecision.NoMatch);
        }

        var candidates = Prefilter(normalized)
            .Select(index => Score(_names[index], normalized, ocrConfidence, bounds))
            .Where(candidate => candidate.Confidence.Value >= RecognitionThresholds.Candidate)
            .GroupBy(candidate => candidate.CanonicalId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(candidate => candidate.Confidence.Value)
                .ThenBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
                .First())
            .OrderByDescending(candidate => candidate.Confidence.Value)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();

        if (candidates.Length == 0)
        {
            return new([], RecognitionDecision.NoMatch);
        }

        var decision = RecognitionThresholds.Classify(candidates[0].Confidence);
        if (decision == RecognitionDecision.AutoSelected &&
            candidates.Length > 1 &&
            candidates[0].Confidence.Value - candidates[1].Confidence.Value < RecognitionThresholds.MinimumRunnerUpLead)
        {
            decision = RecognitionDecision.Ambiguous;
        }

        return new(candidates, decision);
    }

    private IEnumerable<IndexedName> Index(CanonicalItemReference item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.DisplayName);

        return new[] { item.DisplayName }
            .Concat(item.Aliases ?? [])
            .Select(name => (Original: name, Normalized: _normalizer.NormalizeForLookup(name)))
            .Where(name => name.Normalized.Length is > 0 and <= MaximumComparedTextLength)
            .DistinctBy(name => name.Normalized, StringComparer.Ordinal)
            .Select(name => new IndexedName(item.Id, item.DisplayName, name.Original, name.Normalized));
    }

    private IReadOnlyList<int> Prefilter(string observed)
    {
        if (_exactIndex.TryGetValue(observed, out var exact))
        {
            return exact;
        }

        var hits = new Dictionary<int, int>();
        foreach (var trigram in Trigrams(observed))
        {
            if (!_trigramIndex.TryGetValue(trigram, out var indexes))
            {
                continue;
            }

            foreach (var index in indexes)
            {
                hits[index] = hits.GetValueOrDefault(index) + 1;
            }
        }

        if (hits.Count > 0)
        {
            return hits
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => Math.Abs(_names[pair.Key].Normalized.Length - observed.Length))
                .ThenBy(pair => _names[pair.Key].DisplayName, StringComparer.Ordinal)
                .Take(MaximumPrefilterCandidates)
                .Select(pair => pair.Key)
                .ToArray();
        }

        var lengthWindow = Math.Max(4, observed.Length / 3);
        return _names
            .Select((name, index) => (name, index))
            .Where(pair => Math.Abs(pair.name.Normalized.Length - observed.Length) <= lengthWindow)
            .OrderBy(pair => Math.Abs(pair.name.Normalized.Length - observed.Length))
            .ThenBy(pair => pair.name.DisplayName, StringComparer.Ordinal)
            .Take(MaximumPrefilterCandidates)
            .Select(pair => pair.index)
            .ToArray();
    }

    private static RecognitionCandidate Score(
        IndexedName item,
        string observed,
        Confidence? ocrConfidence,
        PixelRect? bounds)
    {
        var similarity = FuzzyTextSimilarity.Score(observed, item.Normalized);
        // An engine with no opinion is not an engine with a bad opinion. Blending a missing
        // score in as zero capped a character-perfect read at 0.80 against an auto-select
        // threshold of 0.90, so the scanner could never select anything at all.
        var combined = ocrConfidence is { } reported
            ? Math.Clamp((similarity * 0.80) + (reported.Value * 0.20), 0, 1)
            : similarity;
        return new(
            item.Id,
            item.DisplayName,
            new Confidence(combined),
            $"ocr-fuzzy; observed={observed}; matched={item.Original}; similarity={similarity:F3}",
            bounds);
    }

    private static IEnumerable<string> Trigrams(string value)
    {
        if (value.Length < 3)
        {
            yield return value;
            yield break;
        }

        for (var index = 0; index <= value.Length - 3; index++)
        {
            yield return value.Substring(index, 3);
        }
    }

    private sealed record IndexedName(string Id, string DisplayName, string Original, string Normalized);
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
        var rowLength = right.Length + 1;
        var pool = ArrayPool<int>.Shared;
        var previousPrevious = pool.Rent(rowLength);
        var previous = pool.Rent(rowLength);
        var current = pool.Rent(rowLength);
        try
        {
            for (var rightIndex = 0; rightIndex <= right.Length; rightIndex++)
            {
                previous[rightIndex] = rightIndex;
                previousPrevious[rightIndex] = rightIndex;
            }

            for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
            {
                current[0] = leftIndex;
                for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
                {
                    var substitutionCost = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                    var distance = Math.Min(
                        Math.Min(previous[rightIndex] + 1, current[rightIndex - 1] + 1),
                        previous[rightIndex - 1] + substitutionCost);

                    if (leftIndex > 1 &&
                        rightIndex > 1 &&
                        left[leftIndex - 1] == right[rightIndex - 2] &&
                        left[leftIndex - 2] == right[rightIndex - 1])
                    {
                        distance = Math.Min(distance, previousPrevious[rightIndex - 2] + 1);
                    }

                    current[rightIndex] = distance;
                }

                (previousPrevious, previous, current) = (previous, current, previousPrevious);
            }

            return previous[right.Length];
        }
        finally
        {
            pool.Return(previousPrevious);
            pool.Return(previous);
            pool.Return(current);
        }
    }
}
