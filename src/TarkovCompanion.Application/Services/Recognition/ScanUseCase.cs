using Microsoft.Extensions.Logging;
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
    private readonly IRaidStateService _raidState;
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
        IRaidStateService raidState,
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
        _raidState = raidState ?? throw new ArgumentNullException(nameof(raidState));
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
            "Scan {ScanId} recognised context {Context} with {Candidates} candidate(s). {Diagnostic}",
            scanId,
            recognition.Context,
            recognition.Candidates.Count,
            recognition.DiagnosticCode ?? "No diagnostic.");

        var evidence = new List<ScanEvidence>
        {
            new(
                "capture",
                "Captured in memory from " + image.Source + "; pixels were not persisted.",
                Confidence.Certain,
                image.CapturedUtc.ToUniversalTime()),
        };
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
        var status = ScanCompletionStatus.Complete;
        string? diagnostic = null;

        switch (recognition.Context)
        {
            case ScanContext.SingleItem:
                (recommendation, status, diagnostic) = await RecommendAsync(recognition, evidence, cancellationToken)
                    .ConfigureAwait(false);
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
            diagnostic);
        return await FinishAsync(outcome, new(0, 0, image.Width, image.Height), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(RecommendationResult? Result, ScanCompletionStatus Status, string? Diagnostic)> RecommendAsync(
        RecognitionResult recognition,
        List<ScanEvidence> evidence,
        CancellationToken cancellationToken)
    {
        var selected = recognition.Selected;
        if (selected is null)
        {
            return (null, ScanCompletionStatus.Partial, recognition.DiagnosticCode ?? "item_not_auto_selected");
        }

        var item = await _items.GetAsync(selected.CanonicalId, cancellationToken).ConfigureAwait(false);
        var price = await _items.GetPriceAsync(selected.CanonicalId, cancellationToken).ConfigureAwait(false);
        if (item is null || price is null)
        {
            return (null, ScanCompletionStatus.Partial, "canonical_item_or_price_unavailable");
        }

        var context = await _recommendationContext.GetAsync(item, selected, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return (null, ScanCompletionStatus.Partial, "recommendation_context_unavailable");
        }

        var recommendation = _recommendations.Recommend(item, price, context, ValueTierThresholds.Default);
        evidence.Add(new(
            "recommendation",
            recommendation.Action + ": " + recommendation.Explanation,
            recommendation.Confidence,
            recognition.ObservedUtc.ToUniversalTime()));
        return (recommendation, ScanCompletionStatus.Complete, null);
    }

    private async Task<(ExtractRecognitionResult? Result, ScanCompletionStatus Status, string? Diagnostic)> RecognizeExtractsAsync(
        CapturedImage image,
        List<ScanEvidence> evidence,
        CancellationToken cancellationToken)
    {
        var mapId = _raidState.Current.MapId;
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

        _raidState.ApplyExtracts(result.Extracts, image.CapturedUtc);
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
