using TarkovCompanion.Application.Services.Runtime;
using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.Application.Services.Recognition;

public sealed class ScanUseCase : IScanUseCase
{
    /// <summary>
    /// Codes the recogniser gives a reading that lost nothing: a conclusion about complete text.
    /// </summary>
    /// <remarks>
    /// Every other code on a recognition names missing evidence, such as a timed-out pass,
    /// missing tiles, truncated lines, an exhausted budget or a failed provider. The list is of
    /// the harmless codes rather than the degraded ones on purpose. A degradation code nobody
    /// has listed yet then makes a scan Partial instead of letting it publish Complete, and the
    /// scan tests that run a complete provider through the real recogniser for every context
    /// fail if a harmless code is missing here.
    /// </remarks>
    private static readonly HashSet<string> ConclusionCodes = new(StringComparer.Ordinal)
    {
        "context_unknown",
        "extract_context",
        "item_not_auto_selected",
        "no_match",
        "ambiguous",
        "ambiguous_runner_up",
        "low_confidence_candidates",
        "ocr_no_text",
        "ocr_region_empty",
    };

    private readonly IScreenCaptureService _capture;
    private readonly IRecognitionService _recognition;
    private readonly IExtractRecognitionService _extracts;
    private readonly IContainerRecognitionService _containers;
    private readonly IFleaRecognitionService _flea;
    private readonly IMapDataService _maps;
    private readonly IRaidActivityRecorder _raid;
    private readonly IItemRepository _items;
    private readonly IRecommendationEngine _recommendations;
    private readonly IScanRecommendationContextProvider _recommendationContext;
    private readonly IScanEventRepository _events;
    private readonly IScanResultPublisher _publisher;
    private readonly ILogger<ScanUseCase>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ScanFrameOptions _frameOptions;

    public ScanUseCase(
        IScreenCaptureService capture,
        IRecognitionService recognition,
        IExtractRecognitionService extracts,
        IContainerRecognitionService containers,
        IFleaRecognitionService flea,
        IMapDataService maps,
        IRaidActivityRecorder raid,
        IItemRepository items,
        IRecommendationEngine recommendations,
        IScanRecommendationContextProvider recommendationContext,
        IScanEventRepository events,
        IScanResultPublisher publisher,
        ILogger<ScanUseCase>? logger = null,
        TimeProvider? timeProvider = null,
        ScanFrameOptions? frameOptions = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _recognition = recognition ?? throw new ArgumentNullException(nameof(recognition));
        _extracts = extracts ?? throw new ArgumentNullException(nameof(extracts));
        _containers = containers ?? throw new ArgumentNullException(nameof(containers));
        _flea = flea ?? throw new ArgumentNullException(nameof(flea));
        _maps = maps ?? throw new ArgumentNullException(nameof(maps));
        _raid = raid ?? throw new ArgumentNullException(nameof(raid));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _recommendations = recommendations ?? throw new ArgumentNullException(nameof(recommendations));
        _recommendationContext = recommendationContext ?? throw new ArgumentNullException(nameof(recommendationContext));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger;
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _frameOptions = ScanFrameDeadline.Validate(frameOptions);
    }

    public async Task<ScanOutcome> ScanAsync(ScanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scanId = Guid.NewGuid();
        CapturedImage image;
        // A scan is logged at every step it can fail at.
        //
        // It used to log nothing at all, on success or failure, so "the scan is not working"
        // could not be told apart from the shortcut never firing, the capture never happening,
        // or the recogniser reading the screen and finding nothing. Somebody reading the log
        // from outside could establish none of it, and neither could I. The raid path had the
        // same hole earlier tonight and logging every transition closed it in one line.
        _logger?.LogInformation("Scan {ScanId} requested for {Capture}.", scanId, request.Capture);
        try
        {
            image = await _capture.CaptureAsync(request.Capture, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
        {
            _logger?.LogWarning(exception, "Scan {ScanId} could not capture the screen.", scanId);
            var observedUtc = _timeProvider.GetUtcNow();
            var unavailable = new ScanOutcome(
                scanId,
                ScanCompletionStatus.Unavailable,
                ScanContext.Unknown,
                observedUtc,
                new(ScanContext.Unknown, [], observedUtc, "capture_unavailable"),
                null,
                null,
                null,
                null,
                [new("capture_unavailable", exception.Message, Confidence.Unknown, observedUtc)],
                "capture_unavailable");
            return await FinishAsync(unavailable, null, cancellationToken).ConfigureAwait(false);
        }

        _logger?.LogInformation(
            "Scan {ScanId} captured {Width}x{Height} from {Source}.",
            scanId,
            image.Width,
            image.Height,
            image.Source);

        return await ReadAsync(scanId, image, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Scans a picture the player already took, rather than capturing the screen.
    /// </summary>
    /// <remarks>
    /// The game's own screenshot key is a better trigger than a shortcut of ours. It is a key
    /// the player already presses, it needs no window to be found and no application to hold
    /// focus, and the resulting file is exactly what was on their screen at the moment they
    /// chose. One press then yields the position from the filename and whatever the picture
    /// shows, instead of two keys doing half the job each.
    ///
    /// The pixels are the player's own file, read and discarded. Nothing is written back and
    /// nothing leaves the machine, which is the same promise the screen capture makes.
    /// </remarks>
    public Task<ScanOutcome> ScanImageAsync(CapturedImage image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        var scanId = Guid.NewGuid();
        _logger?.LogInformation(
            "Scan {ScanId} reading {Width}x{Height} from {Source}.",
            scanId,
            image.Width,
            image.Height,
            image.Source);
        return ReadAsync(scanId, image, cancellationToken);
    }

    /// <summary>
    /// Reads the frame under one deadline, then records what was read under the caller's token.
    /// </summary>
    /// <remarks>
    /// Recording is not reading. The scan event, the publication and the raid's extract record
    /// take the caller's token, so a frame that ran out of budget still records what it found and
    /// that it stopped, instead of vanishing along with the reason it did.
    /// </remarks>
    private async Task<ScanOutcome> ReadAsync(Guid scanId, CapturedImage image, CancellationToken cancellationToken)
    {
        ScanOutcome outcome;
        using (var frame = ScanFrameDeadline.Start(_frameOptions.Timeout, cancellationToken, _timeProvider))
        {
            outcome = await ReadFrameAsync(scanId, image, frame, cancellationToken).ConfigureAwait(false);
        }

        return await FinishAsync(outcome, new(0, 0, image.Width, image.Height), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ScanOutcome> ReadFrameAsync(
        Guid scanId,
        CapturedImage image,
        ScanFrameDeadline frame,
        CancellationToken cancellationToken)
    {
        var (recognized, recognition) = await WithinFrameAsync(
                frame,
                token => _recognition.RecognizeAsync(image, token))
            .ConfigureAwait(false);
        if (!recognized)
        {
            recognition = new(ScanContext.Unknown, [], image.CapturedUtc, ScanFrameDeadline.DiagnosticCode)
            {
                Detail = $"The {frame.Timeout.TotalSeconds:F0}s frame budget ran out before recognition finished.",
            };
        }

        _logger?.LogInformation(
            "Scan {ScanId} recognised context {Context} with {Candidates} candidate(s). {Diagnostic} {Detail}",
            scanId,
            recognition.Context,
            recognition.Candidates.Count,
            recognition.DiagnosticCode ?? "No diagnostic.",
            recognition.Detail ?? "No detail.");

        var evidence = new List<ScanEvidence>
        {
            new(
                "capture",
                "Captured in memory from " + image.Source + "; pixels were not persisted.",
                Confidence.Certain,
                image.CapturedUtc.ToUniversalTime()),
        };
        if (recognition.Hud is { } hud)
        {
            // Worth recording on every scan, including the ones that found nothing. The game
            // fades its display out of roughly one screenshot in eight, and a scan that came
            // back empty off a frame with no display in it is not the same failure as one off a
            // frame that had everything and still read nothing.
            evidence.Add(new(
                "game_display",
                hud.IsPresent
                    ? hud.Detail + " " + hud.Silhouette.Detail
                    : hud.Detail,
                hud.IsPresent ? Confidence.Certain : new Confidence(0.5),
                image.CapturedUtc.ToUniversalTime()));
        }

        foreach (var candidate in recognition.Candidates)
        {
            evidence.Add(new(
                "recognition_candidate",
                candidate.CanonicalId + ": " + candidate.Evidence,
                candidate.Confidence,
                recognition.ObservedUtc.ToUniversalTime()));
        }

        ExtractRecognitionResult? extracts = null;
        ContainerScanResult? container = null;
        FleaRecognitionResult? flea = null;
        RecommendationResult? recommendation = null;
        long? economicValue = null;
        long? valuePerSlot = null;
        var status = ScanCompletionStatus.Complete;
        string? diagnostic = null;
        var recognitionDegradation = DegradationOf(recognition);

        switch (recognition.Context)
        {
            case ScanContext.SingleItem:
                (recommendation, status, diagnostic, economicValue, valuePerSlot) =
                    await RecommendAsync(recognition, evidence, frame).ConfigureAwait(false);
                break;

            case ScanContext.ExtractList:
                (extracts, status, diagnostic) = await RecognizeExtractsAsync(image, evidence, frame, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case ScanContext.Container:
                var (containerRead, containerResult) = await WithinFrameAsync(
                        frame,
                        token => _containers.RecognizeAsync(image, token))
                    .ConfigureAwait(false);
                if (!containerRead)
                {
                    status = ScanCompletionStatus.Partial;
                    diagnostic = ScanFrameDeadline.DiagnosticCode;
                    break;
                }

                container = containerResult;
                // A code on a result that does not call itself partial is still a code. Nothing
                // produces that today; the scan must not be the place that finds out.
                status = container.IsPartial || container.DiagnosticCode is not null
                    ? ScanCompletionStatus.Partial
                    : ScanCompletionStatus.Complete;
                diagnostic = container.DiagnosticCode;
                evidence.Add(new(
                    "container_grid",
                    "Resolved " + container.Items.Count + " item groups; unresolved=" +
                    container.UnresolvedCells.Count + "; ambiguous=" + container.AmbiguousCells.Count + ".",
                    container.Confidence,
                    image.CapturedUtc.ToUniversalTime()));
                break;

            case ScanContext.FleaListings:
                var (fleaRead, fleaResult) = await WithinFrameAsync(
                        frame,
                        token => _flea.RecognizeAsync(image, token))
                    .ConfigureAwait(false);
                if (!fleaRead)
                {
                    status = ScanCompletionStatus.Partial;
                    diagnostic = ScanFrameDeadline.DiagnosticCode;
                    break;
                }

                flea = fleaResult;
                // Rows that parsed are not a complete page when the read that found them was
                // degraded. The flea recogniser carries that code, and it used to be published
                // beside a Complete status.
                status = !flea.ProviderAvailable
                    ? UnavailableUnlessOutOfTime(flea.DiagnosticCode)
                    : flea.Listings.Count == 0 || flea.DiagnosticCode is not null
                        ? ScanCompletionStatus.Partial
                        : ScanCompletionStatus.Complete;
                diagnostic = flea.DiagnosticCode;
                evidence.Add(new(
                    "flea_rows",
                    "Parsed " + flea.Listings.Count + " visible local OCR rows; no market action was performed.",
                    flea.Confidence,
                    flea.ObservedUtc));
                break;

            default:
                status = recognition.DiagnosticCode == "ocr_provider_unavailable"
                    ? ScanCompletionStatus.Unavailable
                    : ScanCompletionStatus.Partial;
                diagnostic = recognition.DiagnosticCode ?? "context_unknown";
                // Already the scan's whole diagnostic; there is nothing to add it to.
                recognitionDegradation = null;
                break;
        }

        if (recognitionDegradation is not null)
        {
            // The reading that chose this dispatch was itself degraded. Each dispatch used to
            // decide the scan's status and code from its own reading alone, so a context found in
            // a timed-out or tile-short frame, followed by a clean extract, container or flea
            // read, published Complete, and a single item whose price lookup then failed had its
            // degraded code overwritten. The code is kept, first, beside whatever the dispatch
            // said, and the scan can be no better than Partial.
            status = status == ScanCompletionStatus.Unavailable
                ? ScanCompletionStatus.Unavailable
                : ScanCompletionStatus.Partial;
            diagnostic = Combine(recognitionDegradation, diagnostic);
        }

        if (!string.IsNullOrWhiteSpace(diagnostic))
        {
            evidence.Add(new("diagnostic", diagnostic, Confidence.Unknown, image.CapturedUtc.ToUniversalTime()));
        }

        return new ScanOutcome(
            scanId,
            status,
            recognition.Context,
            image.CapturedUtc.ToUniversalTime(),
            recognition,
            extracts,
            container,
            flea,
            recommendation,
            evidence,
            diagnostic)
        {
            EconomicValue = economicValue,
            ValuePerSlot = valuePerSlot,
        };
    }

    /// <summary>
    /// Prices the recognised item and, where it can, advises on it.
    /// </summary>
    /// <remarks>
    /// The value is returned separately from the advice because they are different questions.
    /// This method already fetched the price before deciding whether a recommendation was
    /// possible, and used to throw it away when it was not, so the application reported "value
    /// unavailable" for an item whose price was sitting in a local variable one line above.
    ///
    /// Every lookup spends from the frame's budget. They used to run on the caller's token after
    /// recognition had already spent its own, so a slow catalog or context provider could hold
    /// a scan open indefinitely. Whatever was fetched before the budget ran out is kept.
    /// </remarks>
    private async Task<(RecommendationResult? Result, ScanCompletionStatus Status, string? Diagnostic, long? Value, long? PerSlot)> RecommendAsync(
        RecognitionResult recognition,
        List<ScanEvidence> evidence,
        ScanFrameDeadline frame)
    {
        if (recognition.Selected is not { } selected)
        {
            return (null, ScanCompletionStatus.Partial, recognition.DiagnosticCode ?? "item_not_auto_selected", null, null);
        }

        var (itemRead, item) = await WithinFrameAsync(
                frame,
                token => _items.GetAsync(selected.CanonicalId, token))
            .ConfigureAwait(false);
        if (!itemRead)
        {
            return (null, ScanCompletionStatus.Partial, ScanFrameDeadline.DiagnosticCode, null, null);
        }

        var (priceRead, price) = await WithinFrameAsync(
                frame,
                token => _items.GetPriceAsync(selected.CanonicalId, token))
            .ConfigureAwait(false);
        if (!priceRead)
        {
            return (null, ScanCompletionStatus.Partial, ScanFrameDeadline.DiagnosticCode, null, null);
        }

        if (item is not { } knownItem || price is not { } knownPrice)
        {
            return (null, ScanCompletionStatus.Partial, "canonical_item_or_price_unavailable", null, null);
        }

        var value = knownPrice.BestEconomicValue;
        var perSlot = value / Math.Max(1, knownItem.Dimensions.Width * knownItem.Dimensions.Height);

        var (contextRead, context) = await WithinFrameAsync(
                frame,
                token => _recommendationContext.GetAsync(knownItem, selected, token))
            .ConfigureAwait(false);
        if (!contextRead)
        {
            return (null, ScanCompletionStatus.Partial, ScanFrameDeadline.DiagnosticCode, value, perSlot);
        }

        if (context is null)
        {
            // No advice, but the price is known and the player asked what this is. Withholding
            // a recommendation should suppress the recommendation and nothing else.
            return (null, ScanCompletionStatus.Partial, "recommendation_context_unavailable", value, perSlot);
        }

        if (frame.IsExpired)
        {
            return (null, ScanCompletionStatus.Partial, ScanFrameDeadline.DiagnosticCode, value, perSlot);
        }

        var recommendation = _recommendations.Recommend(knownItem, knownPrice, context, ValueTierThresholds.Default);
        evidence.Add(new(
            "recommendation",
            recommendation.Action + ": " + recommendation.Explanation,
            recommendation.Confidence,
            recognition.ObservedUtc.ToUniversalTime()));
        // A degraded recognition still gets its advice; ReadFrameAsync marks the scan Partial with
        // the code, here and on every earlier return that has a code of its own.
        return (recommendation, ScanCompletionStatus.Complete, null, value, perSlot);
    }

    /// <summary>
    /// Runs one stage of reading a frame on the frame's token, and says whether the frame's budget
    /// let it finish.
    /// </summary>
    /// <remarks>
    /// The budget is checked before the stage starts as well as observed during it, so a stage
    /// whose implementation never looks at its token still does not start once the budget has
    /// run out. The caller cancelling still throws.
    /// </remarks>
    private static async Task<(bool Completed, T Value)> WithinFrameAsync<T>(
        ScanFrameDeadline frame,
        Func<CancellationToken, Task<T>> stage)
    {
        try
        {
            frame.Token.ThrowIfCancellationRequested();
            return (true, await stage(frame.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (frame.IsExpired)
        {
            return (false, default!);
        }
    }

    /// <summary>A result that could not read is unavailable, unless it was the frame's budget that stopped it.</summary>
    private static ScanCompletionStatus UnavailableUnlessOutOfTime(string? diagnostic) =>
        diagnostic == ScanFrameDeadline.DiagnosticCode
            ? ScanCompletionStatus.Partial
            : ScanCompletionStatus.Unavailable;

    /// <summary>The code of whatever degraded the recognition's own reading, or null.</summary>
    /// <remarks>
    /// The recogniser gives an auto-selected item a code only when the text it was read from was
    /// degraded, so any code on a selected item counts, whatever it says.
    /// </remarks>
    private static string? DegradationOf(RecognitionResult recognition) =>
        recognition.DiagnosticCode is { } code &&
        (recognition.Selected is not null || !ConclusionCodes.Contains(code))
            ? code
            : null;

    /// <summary>The recognition's degradation first, then the dispatch's own code where it adds one.</summary>
    private static string Combine(string degradation, string? dispatch) =>
        dispatch is null ||
        dispatch.Split(';', StringSplitOptions.TrimEntries).Contains(degradation, StringComparer.Ordinal)
            ? dispatch ?? degradation
            : degradation + "; " + dispatch;

    private async Task<(ExtractRecognitionResult? Result, ScanCompletionStatus Status, string? Diagnostic)> RecognizeExtractsAsync(
        CapturedImage image,
        List<ScanEvidence> evidence,
        ScanFrameDeadline frame,
        CancellationToken cancellationToken)
    {
        if (_raid.Current.MapId is not { } mapId || string.IsNullOrWhiteSpace(mapId))
        {
            return (null, ScanCompletionStatus.Partial, "current_map_unavailable");
        }

        var (mapRead, map) = await WithinFrameAsync(frame, token => _maps.GetAsync(mapId, token))
            .ConfigureAwait(false);
        if (!mapRead)
        {
            return (null, ScanCompletionStatus.Partial, ScanFrameDeadline.DiagnosticCode);
        }

        if (map is not { } currentMap)
        {
            return (null, ScanCompletionStatus.Partial, "current_map_catalog_unavailable");
        }

        var (read, result) = await WithinFrameAsync(
                frame,
                token => _extracts.RecognizeAsync(image, currentMap, token))
            .ConfigureAwait(false);
        if (!read)
        {
            // Nothing was matched, so nothing is written to the raid: an empty extract list would
            // replace the exits the last good scan found.
            return (null, ScanCompletionStatus.Partial, ScanFrameDeadline.DiagnosticCode);
        }

        if (!result.ProviderAvailable)
        {
            return (
                result,
                UnavailableUnlessOutOfTime(result.DiagnosticCode),
                result.DiagnosticCode ?? "ocr_provider_unavailable");
        }

        // The game draws the remaining time on this screen, so the same picture that named the
        // exits also carries the clock.
        //
        // Read from every line, not from the ones that failed to match an exit. The clock is on
        // the panel header — "Find an extraction point 0:12:28" — which matching strips and then
        // discards as a header, so it reached neither diagnostic list. It arrived only when OCR
        // happened to break it onto a line of its own.
        //
        // The leftovers are still what goes into the raid record as the unreadable rows, which
        // is what they are for; they are simply no longer the road the clock travels on.
        var leftover = result.UnmatchedLines.Concat(result.AmbiguousLines).ToArray();
        var clock = RaidTimer.Read(result.RawLines.Count > 0 ? result.RawLines : leftover);
        // Through the coordinator, not past it. The direct call left the map waiting for the
        // next log line to redraw, and left ApplyExtractsAsync — the only writer of an
        // "extracts" raid event — with no callers at all, so no raid has ever recorded which
        // exits it was offered.
        //
        // On the caller's token: this records exits already read, and a raid write abandoned
        // halfway because the frame's reading budget ran out would be worse than either outcome.
        // A reading the budget cut before it matched anything records nothing: its empty list
        // would replace the exits the last complete scan found with a claim nobody checked.
        if (result.Observations.Count > 0 || result.DiagnosticCode != ScanFrameDeadline.DiagnosticCode)
        {
            await _raid.ApplyExtractsAsync(
                result.Extracts,
                image.CapturedUtc,
                cancellationToken,
                clock,
                leftover,
                result.Transits).ConfigureAwait(false);
        }
        foreach (var observation in result.Observations)
        {
            evidence.Add(new(
                "extract_" + observation.Status.ToString().ToLowerInvariant(),
                observation.ExtractId + ": " + observation.Source,
                observation.Confidence,
                observation.ObservedUtc));
        }

        // A code is partial on its own. The extract recogniser gives a degraded full-frame or panel
        // reading its code even when every row that arrived matched, and the scan used to ask
        // only whether any rows had failed.
        var partial = result.AmbiguousLines.Count > 0 ||
                      result.UnmatchedLines.Count > 0 ||
                      result.CatalogGapLines.Count > 0 ||
                      result.DiagnosticCode is not null;
        return (
            result,
            partial ? ScanCompletionStatus.Partial : ScanCompletionStatus.Complete,
            partial ? result.DiagnosticCode ?? "extracts_partial" : null);
    }

    private async Task<ScanOutcome> FinishAsync(
        ScanOutcome outcome,
        PixelRect? geometry,
        CancellationToken cancellationToken)
    {
        var candidates = outcome.Container is { Items.Count: > 0 } container
            ? container.Items
            : outcome.Recognition.Candidates;
        var confidence = outcome.Recognition.Selected?.Confidence ??
                         candidates.FirstOrDefault()?.Confidence ??
                         outcome.Flea?.Confidence ??
                         outcome.Extracts?.Observations.OrderByDescending(value => value.Confidence.Value).FirstOrDefault()?.Confidence ??
                         Confidence.Unknown;
        await _events.SaveAsync(
            new(
                outcome.ScanId,
                outcome.ObservedUtc,
                outcome.Context,
                outcome.Recognition.Selected?.CanonicalId,
                confidence,
                candidates,
                outcome.Recommendation?.Action.ToString(),
                geometry,
                outcome.DiagnosticCode),
            cancellationToken).ConfigureAwait(false);
        await _publisher.PublishAsync(outcome, cancellationToken).ConfigureAwait(false);
        return outcome;
    }
}

/// <summary>How long reading one captured frame may take, from recognition to the last lookup.</summary>
public sealed record ScanFrameOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The one deadline a captured frame is read under, which every stage reading it spends from.
/// </summary>
/// <remarks>
/// <para>
/// Each recognition component used to start its own. The recogniser's coordinator took thirty
/// seconds, the extract, flea or container recogniser then started again on the caller's token, the
/// container recogniser gave itself a fresh thirty seconds, and resolver, price and context lookups
/// ran outside all of them. One capture could take a minute and then some, with the lookups after it
/// unbounded. A scan now starts this once, and a component handed its token joins it rather than
/// starting a budget of its own. A component started outside a scan starts one, and the components
/// it calls with that token join it in turn, so no budget is ever nested inside another.
/// </para>
/// <para>
/// Joining is by token, not merely by flow. The deadline is found through the asynchronous flow
/// that started it, but a component joins only when the token it was handed is this deadline's own,
/// so unrelated work started inside a scan with some other token cannot be cut short by it.
/// </para>
/// <para>
/// Expiry is a measured outcome rather than cancellation: a stage it stops keeps whatever finished
/// and says <see cref="DiagnosticCode"/>. The caller cancelling still throws.
/// </para>
/// </remarks>
public sealed class ScanFrameDeadline : IDisposable
{
    public const string DiagnosticCode = "ocr_pipeline_timeout";

    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromDays(1);
    private static readonly AsyncLocal<ScanFrameDeadline?> CurrentFrame = new();

    private readonly CancellationTokenSource _timer;
    private readonly CancellationTokenSource _source;
    private readonly CancellationToken _caller;
    private readonly ScanFrameDeadline? _previous;
    private int _disposed;

    private ScanFrameDeadline(TimeSpan timeout, CancellationToken caller, TimeProvider timeProvider)
    {
        _caller = caller;
        _timer = new CancellationTokenSource(timeout, timeProvider);
        _source = CancellationTokenSource.CreateLinkedTokenSource(caller, _timer.Token);
        // Kept rather than read from the source, which refuses once disposed; a disposed
        // deadline must still compare unequal to a live token instead of throwing.
        Token = _source.Token;
        Timeout = timeout;
        _previous = CurrentFrame.Value;
    }

    /// <summary>The whole budget the frame was given.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Cancelled when the budget runs out or the caller cancels.</summary>
    public CancellationToken Token { get; }

    /// <summary>True when the budget, rather than the caller, ended the work.</summary>
    public bool IsExpired => _timer.IsCancellationRequested && !_caller.IsCancellationRequested;

    public static ScanFrameOptions Validate(ScanFrameOptions? options)
    {
        options ??= new ScanFrameOptions();
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The frame deadline must be positive and bounded.");
        }

        return options;
    }

    /// <summary>
    /// Starts the frame's deadline and makes it the one components in this asynchronous flow join.
    /// </summary>
    public static ScanFrameDeadline Start(
        TimeSpan timeout,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        Validate(new ScanFrameOptions { Timeout = timeout });
        var frame = new ScanFrameDeadline(timeout, cancellationToken, timeProvider ?? TimeProvider.System);
        CurrentFrame.Value = frame;
        return frame;
    }

    /// <summary>The frame deadline this token belongs to, if a frame in this flow issued it.</summary>
    public static ScanFrameDeadline? Joining(CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled &&
        CurrentFrame.Value is { } frame &&
        frame.Token == cancellationToken
            ? frame
            : null;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (ReferenceEquals(CurrentFrame.Value, this))
        {
            CurrentFrame.Value = _previous;
        }

        _source.Dispose();
        _timer.Dispose();
    }
}

public sealed class LatestScanResultPublisher : IScanResultPublisher
{
    private readonly object _gate = new();
    private ScanOutcome? _current;

    public event Action<ScanOutcome>? Published;

    public ScanOutcome? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public Task PublishAsync(ScanOutcome result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _current = result;
        }

        Published?.Invoke(result);
        return Task.CompletedTask;
    }
}
