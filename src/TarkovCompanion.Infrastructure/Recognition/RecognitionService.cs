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
        var detail = Describe(image, coordinated);
        // Read off the same pixels whatever the context turns out to be, and read before the
        // early returns, because the frames that come back with nothing are exactly the ones
        // where knowing the game had faded its display out is worth having.
        var hud = HudProbe.Read(image);
        if (!coordinated.FullFrame.IsAvailable)
        {
            return new RecognitionResult(ScanContext.Unknown, [], image.CapturedUtc, "ocr_provider_unavailable")
            {
                Detail = detail,
                Hud = hud,
            };
        }

        var context = coordinated.Detection.Context;
        if (context == ScanContext.Unknown)
        {
            return new RecognitionResult(context, [], image.CapturedUtc, "context_unknown")
            {
                Detail = detail,
                Hud = hud,
            };
        }

        if (context == ScanContext.ExtractList)
        {
            return new RecognitionResult(context, [], image.CapturedUtc, "extract_context")
            {
                Detail = detail,
                Hud = hud,
            };
        }

        var resolver = await _resolverCache.GetAsync(cancellationToken).ConfigureAwait(false);
        var candidates = ResolveOcrCandidates(coordinated.Candidates, resolver)
            .GroupBy(candidate => candidate.CanonicalId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(candidate => candidate.Confidence.Value).First())
            .OrderByDescending(candidate => candidate.Confidence.Value)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
            .Take(5)
            .ToList();

        var result = new RecognitionResult(context, candidates, image.CapturedUtc) { Detail = detail, Hud = hud };
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

    /// <summary>
    /// Says what the text engine saw and how the contexts scored, in one line.
    /// </summary>
    /// <remarks>
    /// The line count is the part that separates the two ways a scan comes back empty. Zero
    /// lines means the picture was never read, which is a text-engine problem and nothing to do
    /// with anchors. Plenty of lines and no winner means it was read and looked like nothing we
    /// recognise, which is an anchor problem. Guessing between those cost a night already.
    ///
    /// The frame size is carried because the game is not always played on one ordinary screen,
    /// and a very wide frame makes the text small relative to it.
    /// </remarks>
    private static string Describe(CapturedImage image, CoordinatedOcrResult coordinated) =>
        $"{coordinated.FullFrame.Lines.Count} text line(s) read from {image.Width}x{image.Height} " +
        $"by {coordinated.FullFrame.Engine}; {coordinated.Detection.Evidence}";

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
