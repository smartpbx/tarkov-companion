using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed class RecognitionService : IRecognitionService
{
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
        //
        // Not over the pixel ceiling, which the providers have already refused the frame for.
        var hud = CapturedImagePixels.ExceedsPixelCeiling(image) ? null : HudProbe.Read(image);
        if (!coordinated.FullFrame.IsAvailable)
        {
            // A timeout or a rejected frame is not a missing provider. Only an actually absent
            // provider keeps the code the scan use case turns into "unavailable".
            var unavailable = coordinated.FullFrame.DiagnosticCode is null or "ocr_language_unavailable"
                ? "ocr_provider_unavailable"
                : coordinated.FullFrame.DiagnosticCode;
            return new RecognitionResult(ScanContext.Unknown, [], image.CapturedUtc, unavailable)
            {
                Detail = detail,
                Hud = hud,
            };
        }

        // A degraded read's code wins over every code derived from that read. "context_unknown",
        // "no_match" or no code at all, said of evidence that is missing tiles, a timed-out pass
        // or truncated lines, is a conclusion the evidence cannot support; the partial read used to
        // reach only the detail line, so an auto-selected item from half a frame looked complete.
        var degraded = coordinated.IsPartial ? coordinated.DiagnosticCode : null;
        var context = coordinated.Detection.Context;
        if (context == ScanContext.Unknown)
        {
            // Reading nothing and reading text that matched no context are different failures,
            // and only the second one is an anchor problem.
            var unknown = degraded ?? (coordinated.IsEmpty ? OcrOutcome.NoText : "context_unknown");
            return new RecognitionResult(context, [], image.CapturedUtc, unknown)
            {
                Detail = detail,
                Hud = hud,
            };
        }

        if (context == ScanContext.ExtractList)
        {
            return new RecognitionResult(context, [], image.CapturedUtc, degraded ?? "extract_context")
            {
                Detail = detail,
                Hud = hud,
            };
        }

        // Loading the catalog and resolving every line is work on this frame, so inside a scan it
        // spends from the frame's deadline like the passes that read the lines. It used to run on
        // the caller's token after the passes had spent theirs. The deadline running out keeps the
        // candidates resolved so far and says so; the caller cancelling still throws.
        IReadOnlyList<RecognitionCandidate> candidates = [];
        try
        {
            var resolver = await _resolverCache.GetAsync(cancellationToken).ConfigureAwait(false);
            candidates = OcrItemCandidates.Rank(coordinated.Candidates, resolver, _normalizer, cancellationToken);
        }
        catch (OperationCanceledException) when (OcrPipelineDeadline.HasExpired(cancellationToken))
        {
            degraded ??= OcrPipelineDeadline.DiagnosticCode;
        }

        var result = new RecognitionResult(context, candidates, image.CapturedUtc) { Detail = detail, Hud = hud };
        var diagnostic = degraded ?? (candidates.Count == 0
            ? "no_match"
            : result.Selected is not null
                ? null
                : RecognitionThresholds.Classify(candidates[0].Confidence) switch
                {
                    RecognitionDecision.AutoSelected => "ambiguous_runner_up",
                    RecognitionDecision.Ambiguous => "ambiguous",
                    RecognitionDecision.Candidate => "low_confidence_candidates",
                    _ => "no_match",
                });
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
        $"by {coordinated.FullFrame.Engine} in {coordinated.FullFrame.Duration.TotalMilliseconds:F0}ms; " +
        $"partial={coordinated.IsPartial}; empty={coordinated.IsEmpty}; " +
        $"diagnostic={coordinated.DiagnosticCode ?? "none"}; " +
        $"health-character={coordinated.SupplementalSignals.HealthAndCharacter.DiagnosticCode}; " +
        $"version-strip={coordinated.SupplementalSignals.VersionStrip.DiagnosticCode}; " +
        coordinated.Detection.Evidence;
}
