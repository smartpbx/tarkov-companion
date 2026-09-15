using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed record ContextAnchorDefinition(
    ScanContext Context,
    string Term,
    double Weight,
    string Provenance);

public sealed record ContextAnchorMatch(
    string Term,
    PixelRect Bounds,
    double Weight,
    string Provenance);

public sealed record ContextDetection(
    ScanContext Context,
    Confidence Confidence,
    double EstimatedUiScale,
    string Evidence,
    PixelRect? AnchorBounds,
    IReadOnlyList<ContextAnchorMatch> Anchors);

public sealed class RecognitionAnchorCatalog
{
    private const string BundledResource =
        "TarkovCompanion.Infrastructure.Recognition.anchors.en.json";
    private readonly IReadOnlyList<ContextAnchorDefinition> _definitions;

    public RecognitionAnchorCatalog(IEnumerable<ContextAnchorDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = definitions.ToArray();
        if (_definitions.Count == 0)
        {
            throw new ArgumentException("At least one recognition anchor is required.", nameof(definitions));
        }

        if (_definitions.Any(anchor =>
                anchor.Context == ScanContext.Unknown ||
                string.IsNullOrWhiteSpace(anchor.Term) ||
                string.IsNullOrWhiteSpace(anchor.Provenance) ||
                anchor.Weight is <= 0 or > 1))
        {
            throw new ArgumentException("Recognition anchors must have a known context, term, provenance, and 0..1 weight.", nameof(definitions));
        }
    }

    public IReadOnlyList<ContextAnchorDefinition> For(ScanContext context) =>
        _definitions.Where(anchor => anchor.Context == context).ToArray();

    public static RecognitionAnchorCatalog LoadBundled()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(BundledResource)
            ?? throw new InvalidOperationException("The bundled recognition-anchor catalog is unavailable.");
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };
        var definitions = JsonSerializer.Deserialize<IReadOnlyList<ContextAnchorDefinition>>(stream, options)
            ?? throw new InvalidDataException("The bundled recognition-anchor catalog is empty.");
        return new(definitions);
    }
}

public sealed class ScanContextDetector
{
    private const double DetectionThreshold = 0.55;
    private const double MinimumLead = 0.10;
    private readonly RecognitionAnchorCatalog _anchors;
    private readonly OcrTextNormalizer _normalizer;

    public ScanContextDetector(
        RecognitionAnchorCatalog? anchors = null,
        OcrTextNormalizer? normalizer = null)
    {
        _anchors = anchors ?? RecognitionAnchorCatalog.LoadBundled();
        _normalizer = normalizer ?? new OcrTextNormalizer();
    }

    public ContextDetection Detect(CapturedImage image, OcrResult ocr)
    {
        CapturedImagePixels.Validate(image);
        ArgumentNullException.ThrowIfNull(ocr);

        var frame = new PixelRect(0, 0, image.Width, image.Height);
        var visibleLines = ocr.Lines
            .Where(line => CapturedImagePixels.Intersects(frame, line.Bounds))
            .ToArray();
        var normalized = visibleLines
            .Select(line => (Line: line, Text: _normalizer.NormalizeForLookup(line.Text)))
            .Where(line => line.Text.Length > 0)
            .ToArray();
        var estimatedScale = EstimateScale(visibleLines, image.Height);

        var scored = Enum.GetValues<ScanContext>()
            .Where(context => context != ScanContext.Unknown)
            .Select(context => Score(context, normalized))
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Context)
            .ToArray();
        var best = scored[0];
        var runnerUp = scored[1];
        if (best.Score < DetectionThreshold || best.Score - runnerUp.Score < MinimumLead)
        {
            return new(
                ScanContext.Unknown,
                new Confidence(Math.Clamp(best.Score, 0, 1)),
                estimatedScale,
                $"no-unique-context; best={best.Context}:{best.Score:F2}; next={runnerUp.Context}:{runnerUp.Score:F2}; "
                + DescribeMiss(normalized),
                null,
                []);
        }

        var bounds = Union(best.Matches.Select(match => match.Bounds).ToArray());
        var provenance = string.Join(',', best.Matches.Select(match => match.Provenance).Distinct(StringComparer.Ordinal));
        return new(
            best.Context,
            new Confidence(best.Score),
            estimatedScale,
            $"ocr-anchors; score={best.Score:F2}; provenance={provenance}; resolution={image.Width}x{image.Height}",
            bounds,
            best.Matches);
    }

    /// <summary>
    /// Says what was looked for and what was read, when nothing matched.
    /// </summary>
    /// <remarks>
    /// A score of zero says nothing matched. It does not say what the gap is, and without that
    /// the only way forward is to guess at anchor terms and wait for somebody to run the game
    /// again. This prints both sides of the comparison so one screenshot settles it.
    ///
    /// It exists because the anchors were derived from a simulated interface and never checked
    /// against the real game, which the detail line has been admitting in the word
    /// "live-unvalidated" the whole time. On real screenshots they score exactly zero while
    /// eighty lines of perfectly good text sit on screen.
    ///
    /// Bounded hard. This goes in a log file somebody pastes into a chat, not into a report.
    /// </remarks>
    private string DescribeMiss(IReadOnlyList<(OcrLine Line, string Text)> lines)
    {
        var wanted = Enum.GetValues<ScanContext>()
            .Where(context => context != ScanContext.Unknown)
            .SelectMany(context => _anchors.For(context).Select(anchor => $"{context}:{anchor.Term}"))
            .Take(16);
        // The raw text, not the normalised form used for matching. Normalisation strips
        // spacing and punctuation, so "Grenade case" becomes "grenadecase", and the whole
        // point of this is for a person to read what the game's interface actually says and
        // write anchors from it. The matcher's view of the text is the wrong view for that.
        //
        // Longest first, because a UI caption is longer than a stray character the engine
        // found in the artwork, and captions are what the anchors are supposed to match.
        var read = lines
            .Select(line => line.Line.Text.Trim())
            .Where(text => text.Length >= 3)
            .OrderByDescending(text => text.Length)
            .Take(12)
            .Select(text => text.Length <= 40 ? text : text[..40]);
        return $"wanted=[{string.Join(" | ", wanted)}]; read=[{string.Join(" | ", read)}]";
    }

    private ContextScore Score(
        ScanContext context,
        IReadOnlyList<(OcrLine Line, string Text)> lines)
    {
        var matches = new List<ContextAnchorMatch>();
        foreach (var anchor in _anchors.For(context))
        {
            var match = lines
                .Where(line => line.Text.Contains(_normalizer.NormalizeForLookup(anchor.Term), StringComparison.Ordinal))
                // An unscored line ranks with a confident one rather than last. Ordering the
                // engine that ships to the bottom of its own output would be a strange way to
                // pick the best evidence.
                .OrderByDescending(line => line.Line.Confidence?.Value ?? 1)
                .FirstOrDefault();
            if (match.Line is not null)
            {
                matches.Add(new(anchor.Term, match.Line.Bounds, anchor.Weight, anchor.Provenance));
            }
        }

        return new(context, Math.Clamp(matches.Sum(match => match.Weight), 0, 1), matches);
    }

    private static double EstimateScale(IReadOnlyList<OcrLine> lines, int frameHeight)
    {
        var normalizedHeights = lines
            .Where(line => line.Bounds.Height is >= 8 and <= 160)
            .Select(line => (double)line.Bounds.Height / frameHeight)
            .Order()
            .ToArray();
        if (normalizedHeights.Length == 0)
        {
            return 1;
        }

        const double NominalTextHeightAt1080P = 20d / 1080d;
        return Math.Clamp(
            normalizedHeights[normalizedHeights.Length / 2] / NominalTextHeightAt1080P,
            0.50,
            2.50);
    }

    private static PixelRect Union(IReadOnlyList<PixelRect> bounds)
    {
        var left = bounds.Min(rectangle => rectangle.X);
        var top = bounds.Min(rectangle => rectangle.Y);
        var right = bounds.Max(rectangle => rectangle.X + rectangle.Width);
        var bottom = bounds.Max(rectangle => rectangle.Y + rectangle.Height);
        return new(left, top, right - left, bottom - top);
    }

    private sealed record ContextScore(
        ScanContext Context,
        double Score,
        IReadOnlyList<ContextAnchorMatch> Matches);
}

public sealed record CoordinatedOcrResult(
    ContextDetection Detection,
    OcrResult FullFrame,
    OcrResult Contextual,
    OcrResult Candidates,
    bool UsedFullFrameSupplement)
{
    public required SupplementalOcrSignals SupplementalSignals { get; init; }

    public bool IsPartial { get; init; }

    /// <summary>
    /// Every pass that ran was available, nothing degraded it, and no text came back. Distinct
    /// from an unavailable provider and from a partial read that happened to keep no lines.
    /// </summary>
    public bool IsEmpty { get; init; }

    public string? DiagnosticCode { get; init; }
}

public sealed class OcrCoordinator
{
    private readonly IOcrEngine _engine;
    private readonly ScanContextDetector _contextDetector;
    private readonly SupplementalOcrSignalDetector _supplementalDetector;
    private readonly OcrPipelineOptions _pipeline;

    public OcrCoordinator(
        IOcrEngine engine,
        ScanContextDetector contextDetector,
        SupplementalOcrSignalDetector? supplementalDetector = null,
        OcrPipelineOptions? pipelineOptions = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _contextDetector = contextDetector ?? throw new ArgumentNullException(nameof(contextDetector));
        _supplementalDetector = supplementalDetector ?? new SupplementalOcrSignalDetector();
        _pipeline = OcrPipelineDeadline.Validate(pipelineOptions);
    }

    /// <summary>
    /// Reads the frame and then its detected context under one deadline linked to the caller.
    /// </summary>
    /// <remarks>
    /// The deadline expiring is a measured outcome, not cancellation: whatever a finished pass
    /// read is kept and the result says <c>ocr_pipeline_timeout</c>. Caller cancellation still
    /// throws. A provider that ran out of memory on the frame is not asked for a second pass.
    /// </remarks>
    public async Task<CoordinatedOcrResult> RecognizeAsync(
        CapturedImage image,
        CancellationToken cancellationToken)
    {
        CapturedImagePixels.Validate(image);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = OcrPipelineDeadline.Start(_pipeline, cancellationToken);
        var fullFrame = await ReadAsync(image, new OcrRequest(ScanContext.Unknown), null, deadline)
            .ConfigureAwait(false);
        var detection = _contextDetector.Detect(image, fullFrame);
        var supplemental = _supplementalDetector.Detect(fullFrame);
        if (!fullFrame.IsAvailable ||
            detection.Context == ScanContext.Unknown ||
            OcrOutcome.IsMemoryExhausted(fullFrame))
        {
            var empty = OcrOutcome.IsEmpty(fullFrame);
            return new(detection, fullFrame, fullFrame, fullFrame, false)
            {
                SupplementalSignals = supplemental,
                IsPartial = fullFrame.IsAvailable && OcrOutcome.IsDegraded(fullFrame),
                IsEmpty = empty,
                DiagnosticCode = empty ? OcrOutcome.NoText : fullFrame.DiagnosticCode,
            };
        }

        var region = ContextRegionPlanner.For(image, detection);
        var contextual = await ReadAsync(
                image,
                new OcrRequest(detection.Context, region),
                fullFrame.Engine,
                deadline)
            .ConfigureAwait(false);
        var merge = OcrLineDeduplicator.Merge(contextual.Lines, fullFrame.Lines);
        // The candidates are what the recogniser resolves items from, so they carry the
        // degradation of every pass that fed them. A partial pass that was still available used
        // to keep its lines here and lose its code, and the merged set then read as complete.
        var degradation =
            OcrOutcome.Degradation(contextual) ??
            OcrOutcome.Degradation(fullFrame) ??
            (merge.IsExhaustive ? null : OcrOutcome.DeduplicationBudgetExhausted);
        var candidates = new OcrResult(
            merge.Lines,
            contextual.Duration + fullFrame.Duration,
            contextual.Engine,
            contextual.IsAvailable || fullFrame.IsAvailable,
            degradation);
        return new(detection, fullFrame, contextual, candidates, merge.Lines.Count > contextual.Lines.Count)
        {
            SupplementalSignals = supplemental,
            IsPartial = degradation is not null,
            DiagnosticCode = degradation,
        };
    }

    private async Task<OcrResult> ReadAsync(
        CapturedImage image,
        OcrRequest request,
        string? engineName,
        OcrPipelineDeadline deadline)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            return await _engine.RecognizeAsync(image, request, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsExpired)
        {
            return new OcrResult(
                [],
                watch.Elapsed,
                engineName ?? (_engine as IOcrEngineStatus)?.Availability.Provider ?? _engine.GetType().Name,
                false,
                OcrPipelineDeadline.DiagnosticCode);
        }
    }
}

public static class ContextRegionPlanner
{
    public static PixelRect For(CapturedImage image, ContextDetection detection)
    {
        CapturedImagePixels.Validate(image);
        if (detection.Context == ScanContext.Unknown || detection.AnchorBounds is null)
        {
            return new(0, 0, image.Width, image.Height);
        }

        var anchor = detection.AnchorBounds;
        var leftExpansion = detection.Context switch
        {
            ScanContext.ExtractList => 0.12,
            _ => 0.10,
        };
        var rightExpansion = detection.Context switch
        {
            ScanContext.SingleItem => 0.55,
            ScanContext.ExtractList => 0.42,
            _ => 0.82,
        };
        var bottomExpansion = detection.Context switch
        {
            ScanContext.SingleItem => 0.72,
            ScanContext.ExtractList => 0.88,
            _ => 0.84,
        };

        return Clamp(
            image,
            anchor.X - (int)Math.Round(image.Width * leftExpansion),
            anchor.Y - (int)Math.Round(image.Height * 0.04),
            anchor.X + anchor.Width + (int)Math.Round(image.Width * rightExpansion),
            anchor.Y + anchor.Height + (int)Math.Round(image.Height * bottomExpansion));
    }

    private static PixelRect Clamp(CapturedImage image, int left, int top, int right, int bottom)
    {
        left = Math.Clamp(left, 0, image.Width);
        top = Math.Clamp(top, 0, image.Height);
        right = Math.Clamp(right, left, image.Width);
        bottom = Math.Clamp(bottom, top, image.Height);
        return new(left, top, right - left, bottom - top);
    }
}
