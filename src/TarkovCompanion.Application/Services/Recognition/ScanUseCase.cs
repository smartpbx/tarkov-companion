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
        TimeProvider? timeProvider = null)
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

    private async Task<ScanOutcome> ReadAsync(Guid scanId, CapturedImage image, CancellationToken cancellationToken)
    {
        var recognition = await _recognition.RecognizeAsync(image, cancellationToken).ConfigureAwait(false);
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

        switch (recognition.Context)
        {
            case ScanContext.SingleItem:
                (recommendation, status, diagnostic, economicValue, valuePerSlot) =
                    await RecommendAsync(recognition, evidence, cancellationToken).ConfigureAwait(false);
                break;

            case ScanContext.ExtractList:
                (extracts, status, diagnostic) = await RecognizeExtractsAsync(image, evidence, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case ScanContext.Container:
                container = await _containers.RecognizeAsync(image, cancellationToken).ConfigureAwait(false);
                status = container.IsPartial ? ScanCompletionStatus.Partial : ScanCompletionStatus.Complete;
                diagnostic = container.DiagnosticCode;
                evidence.Add(new(
                    "container_grid",
                    "Resolved " + container.Items.Count + " item groups; unresolved=" +
                    container.UnresolvedCells.Count + "; ambiguous=" + container.AmbiguousCells.Count + ".",
                    container.Confidence,
                    image.CapturedUtc.ToUniversalTime()));
                break;

            case ScanContext.FleaListings:
                flea = await _flea.RecognizeAsync(image, cancellationToken).ConfigureAwait(false);
                status = !flea.ProviderAvailable
                    ? ScanCompletionStatus.Unavailable
                    : flea.Listings.Count == 0
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
                break;
        }

        if (!string.IsNullOrWhiteSpace(diagnostic))
        {
            evidence.Add(new("diagnostic", diagnostic, Confidence.Unknown, image.CapturedUtc.ToUniversalTime()));
        }

        var outcome = new ScanOutcome(
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
        return await FinishAsync(outcome, new(0, 0, image.Width, image.Height), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Prices the recognised item and, where it can, advises on it.
    /// </summary>
    /// <remarks>
    /// The value is returned separately from the advice because they are different questions.
    /// This method already fetched the price before deciding whether a recommendation was
    /// possible, and used to throw it away when it was not, so the application reported "value
    /// unavailable" for an item whose price was sitting in a local variable one line above.
    /// </remarks>
    private async Task<(RecommendationResult? Result, ScanCompletionStatus Status, string? Diagnostic, long? Value, long? PerSlot)> RecommendAsync(
        RecognitionResult recognition,
        List<ScanEvidence> evidence,
        CancellationToken cancellationToken)
    {
        var selected = recognition.Selected;
        if (selected is null)
        {
            return (null, ScanCompletionStatus.Partial, recognition.DiagnosticCode ?? "item_not_auto_selected", null, null);
        }

        var item = await _items.GetAsync(selected.CanonicalId, cancellationToken).ConfigureAwait(false);
        var price = await _items.GetPriceAsync(selected.CanonicalId, cancellationToken).ConfigureAwait(false);
        if (item is null || price is null)
        {
            return (null, ScanCompletionStatus.Partial, "canonical_item_or_price_unavailable", null, null);
        }

        var value = price.BestEconomicValue;
        var perSlot = value / Math.Max(1, item.Dimensions.Width * item.Dimensions.Height);

        var context = await _recommendationContext.GetAsync(item, selected, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            // No advice, but the price is known and the player asked what this is. Withholding
            // a recommendation should suppress the recommendation and nothing else.
            return (null, ScanCompletionStatus.Partial, "recommendation_context_unavailable", value, perSlot);
        }

        var recommendation = _recommendations.Recommend(item, price, context, ValueTierThresholds.Default);
        evidence.Add(new(
            "recommendation",
            recommendation.Action + ": " + recommendation.Explanation,
            recommendation.Confidence,
            recognition.ObservedUtc.ToUniversalTime()));
        // The recogniser gives a selected item a code only when the text it was read from was
        // degraded: a timed-out pass, missing tiles, truncated lines. The advice still stands, but
        // the scan used to call itself complete on that evidence, and now says what was missing.
        return recognition.DiagnosticCode is { } degraded
            ? (recommendation, ScanCompletionStatus.Partial, degraded, value, perSlot)
            : (recommendation, ScanCompletionStatus.Complete, null, value, perSlot);
    }

    private async Task<(ExtractRecognitionResult? Result, ScanCompletionStatus Status, string? Diagnostic)> RecognizeExtractsAsync(
        CapturedImage image,
        List<ScanEvidence> evidence,
        CancellationToken cancellationToken)
    {
        var mapId = _raid.Current.MapId;
        if (string.IsNullOrWhiteSpace(mapId))
        {
            return (null, ScanCompletionStatus.Partial, "current_map_unavailable");
        }

        var map = await _maps.GetAsync(mapId, cancellationToken).ConfigureAwait(false);
        if (map is null)
        {
            return (null, ScanCompletionStatus.Partial, "current_map_catalog_unavailable");
        }

        var result = await _extracts.RecognizeAsync(image, map, cancellationToken).ConfigureAwait(false);
        if (!result.ProviderAvailable)
        {
            return (result, ScanCompletionStatus.Unavailable, result.DiagnosticCode ?? "ocr_provider_unavailable");
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
        await _raid.ApplyExtractsAsync(
            result.Extracts,
            image.CapturedUtc,
            cancellationToken,
            clock,
            leftover,
            result.Transits).ConfigureAwait(false);
        foreach (var observation in result.Observations)
        {
            evidence.Add(new(
                "extract_" + observation.Status.ToString().ToLowerInvariant(),
                observation.ExtractId + ": " + observation.Source,
                observation.Confidence,
                observation.ObservedUtc));
        }

        var partial = result.AmbiguousLines.Count > 0 || result.UnmatchedLines.Count > 0;
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
