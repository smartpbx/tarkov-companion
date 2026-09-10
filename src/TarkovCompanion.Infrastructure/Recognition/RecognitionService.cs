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
    private readonly CanonicalItemResolverCache _resolverCache;
    private readonly OcrTextNormalizer _normalizer;

    public RecognitionService(
        OcrCoordinator coordinator,
        CanonicalItemResolverCache resolverCache,
        OcrTextNormalizer? normalizer = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _resolverCache = resolverCache ?? throw new ArgumentNullException(nameof(resolverCache));
        _normalizer = normalizer ?? new OcrTextNormalizer();
    }

    public async Task<RecognitionResult> RecognizeAsync(
        CapturedImage image,
        CancellationToken cancellationToken)
    {
        var coordinated = await _coordinator.RecognizeAsync(image, cancellationToken).ConfigureAwait(false);
        if (!coordinated.FullFrame.IsAvailable)
        {
            return new(ScanContext.Unknown, [], image.CapturedUtc, "ocr_provider_unavailable");
        }

        var context = coordinated.Detection.Context;
        if (context == ScanContext.Unknown)
        {
            return new(context, [], image.CapturedUtc, "context_unknown");
        }

        if (context == ScanContext.ExtractList)
        {
            return new(context, [], image.CapturedUtc, "extract_context");
        }

        var resolver = await _resolverCache.GetAsync(cancellationToken).ConfigureAwait(false);
        var candidates = ResolveOcrCandidates(coordinated.Candidates, resolver)
            .GroupBy(candidate => candidate.CanonicalId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(candidate => candidate.Confidence.Value).First())
            .OrderByDescending(candidate => candidate.Confidence.Value)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
            .Take(5)
            .ToList();

        var result = new RecognitionResult(context, candidates, image.CapturedUtc);
        var diagnostic = candidates.Count == 0
            ? "no_match"
            : result.Selected is not null
                ? null
                : RecognitionThresholds.Classify(candidates[0].Confidence) switch
                {
                    RecognitionDecision.AutoSelected => "ambiguous_runner_up",
                    RecognitionDecision.Ambiguous => "ambiguous",
                    RecognitionDecision.Candidate => "low_confidence_candidates",
                    _ => "no_match",
                };
        return result with { DiagnosticCode = diagnostic };
    }

    private IEnumerable<RecognitionCandidate> ResolveOcrCandidates(
        OcrResult result,
        FuzzyCanonicalItemResolver resolver)
    {
        foreach (var line in result.Lines)
        {
            var normalized = _normalizer.NormalizeForLookup(line.Text);
            if (normalized.Length < 2 || IsUiChrome(normalized))
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

    private static bool IsUiChrome(string normalized) =>
        UiChromeTerms.Contains(normalized, StringComparer.Ordinal);
}
