using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed class RecognitionService : IRecognitionService
{
    private static readonly string[] UiChromeTerms =
    [
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
    ];

    private readonly OcrCoordinator _coordinator;
    private readonly FuzzyCanonicalItemResolver _itemResolver;
    private readonly IIconMatcher? _iconMatcher;
    private readonly OcrTextNormalizer _normalizer;

    public RecognitionService(
        OcrCoordinator coordinator,
        FuzzyCanonicalItemResolver itemResolver,
        IIconMatcher? iconMatcher = null,
        OcrTextNormalizer? normalizer = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _itemResolver = itemResolver ?? throw new ArgumentNullException(nameof(itemResolver));
        _iconMatcher = iconMatcher;
        _normalizer = normalizer ?? new OcrTextNormalizer();
    }

    public async Task<RecognitionResult> RecognizeAsync(
        CapturedImage image,
        CancellationToken cancellationToken)
    {
        var coordinated = await _coordinator.RecognizeAsync(image, cancellationToken).ConfigureAwait(false);
        var context = coordinated.Detection.Context;
        if (context == ScanContext.Unknown)
        {
            return new(context, [], image.CapturedUtc, "context_unknown");
        }

        if (context == ScanContext.ExtractList)
        {
            return new(context, [], image.CapturedUtc, "extract_context");
        }

        var candidates = ResolveOcrCandidates(coordinated.Contextual)
            .GroupBy(candidate => candidate.CanonicalId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(candidate => candidate.Confidence.Value).First())
            .OrderByDescending(candidate => candidate.Confidence.Value)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
            .Take(5)
            .ToList();

        if ((candidates.Count == 0 || candidates[0].Confidence.Value < RecognitionPolicy.AmbiguityThreshold) &&
            _iconMatcher is not null)
        {
            var iconCandidates = await _iconMatcher.MatchAsync(image, 5, cancellationToken).ConfigureAwait(false);
            candidates.AddRange(iconCandidates);
            candidates = candidates
                .GroupBy(candidate => candidate.CanonicalId, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(candidate => candidate.Confidence.Value).First())
                .OrderByDescending(candidate => candidate.Confidence.Value)
                .Take(5)
                .ToList();
        }

        var diagnostic = candidates.Count == 0
            ? "no_match"
            : RecognitionPolicy.Classify(candidates[0].Confidence) switch
            {
                RecognitionDecision.AutoSelected => null,
                RecognitionDecision.Ambiguous => "ambiguous",
                RecognitionDecision.Candidate => "low_confidence_candidates",
                _ => "no_match",
            };
        return new(context, candidates, image.CapturedUtc, diagnostic);
    }

    private IEnumerable<RecognitionCandidate> ResolveOcrCandidates(OcrResult result)
    {
        foreach (var line in result.Lines)
        {
            var normalized = _normalizer.NormalizeForLookup(line.Text);
            if (normalized.Length < 2 || IsUiChrome(normalized))
            {
                continue;
            }

            var resolution = _itemResolver.Resolve(line.Text, line.Confidence, bounds: line.Bounds);
            foreach (var candidate in resolution.Candidates)
            {
                yield return candidate with { Evidence = $"{candidate.Evidence}; engine={result.Engine}" };
            }
        }
    }

    private static bool IsUiChrome(string normalized) => UiChromeTerms.Any(term =>
        normalized.Equals(term, StringComparison.Ordinal) ||
        normalized.StartsWith($"{term} ", StringComparison.Ordinal));
}
