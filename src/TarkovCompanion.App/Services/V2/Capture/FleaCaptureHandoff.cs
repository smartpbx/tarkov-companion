using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Recommendations;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recommendations;
using RecommendationAction = TarkovCompanion.Core.Abstractions.V2.RecommendationAction;
using RecommendationResult = TarkovCompanion.Core.Abstractions.V2.RecommendationResult;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>One flea offer the player photographed, beside what the catalog says the item is worth.</summary>
/// <param name="PriceRoubles">The price on the row, for one unit.</param>
/// <param name="Quantity">Units in the offer, where that was legible.</param>
/// <remarks>
/// Recommendation and alternatives are the #274 engine's complete, versioned explanation shape;
/// the UI does not reconstruct a verdict from these scalar fields.
/// </remarks>
public sealed record FleaScanAlternative(
    string ItemId,
    string ItemName,
    double Confidence,
    long? ResaleMarginRoubles,
    RecommendationResult Recommendation);

public sealed record FleaScanRow(
    long PriceRoubles,
    int? Quantity,
    double Confidence,
    string? SourceText,
    string CurrencyCode,
    long OriginalPrice,
    long CurrencyRateRoubles,
    ItemConditionReading? Condition,
    long? ResaleMarginRoubles,
    RecommendationResult Recommendation,
    IReadOnlyList<FleaScanAlternative> Alternatives)
{
    /// <summary>The whole offer, where the count was read.</summary>
    public long? StackRoubles => Quantity is { } count and > 1 ? checked(PriceRoubles * count) : null;
}

/// <summary>A photographed flea screen: the item it was for, its rows, and the catalog's figures.</summary>
public sealed record FleaScanResult(
    CaptureSessionId SessionId,
    string ArtifactId,
    DateTimeOffset ObservedUtc,
    string? ItemId,
    string? ItemName,
    IReadOnlyList<CaptureIdentifiedItem> Alternates,
    long? TraderRoubles,
    string? TraderName,
    long? Average24HourRoubles,
    long? AverageFeeRoubles,
    DateTimeOffset? PriceUpdatedUtc,
    IReadOnlyList<FleaScanRow> Rows)
{
    /// <summary>The published flea fee rates the fee was worked out from; null when none were synced.</summary>
    public FleaMarketRates? FeeRates { get; init; }
}

/// <summary>
/// The reviewed-result consumer for a flea screen the player opened and photographed (#284).
/// </summary>
/// <remarks>
/// <para>
/// V1 parsed the visible rows and then reduced them to "Parsed N visible local OCR rows"; V2
/// never called the parser and <c>FleaListingRecognition</c> had no producer. The rows now reach
/// Intel &gt; Flea, each beside what a trader pays for the item and what the 24-hour flea average
/// returns after its fee, so a row says whether buying it would pay.
/// </para>
/// <para>
/// This reads a screenshot. It never opens the market, searches it, refreshes it or buys
/// anything; what the player does with the answer is theirs to do by hand.
/// </para>
/// </remarks>
public sealed class FleaCaptureHandoff(
    IItemRepository items,
    IItemMarketFactSource? market = null,
    ILogger<FleaCaptureHandoff>? logger = null,
    ExplainableRecommendationEngine? engine = null,
    RecommendationPolicyService? policies = null,
    // #712 1-12: the item the player said the screen was for, kept for the frame and, picked
    // twice for the same reading, learned as a name.
    TarkovCompanion.Infrastructure.Recognition.CorrectionMemory? corrections = null) : ICaptureResultHandoff
{
    /// <summary>The key a flea screen's corrected item is kept under, beside the frame's hash.</summary>
    public const string CorrectionTarget = "flea:item";

    private readonly Lock _lastGate = new();
    private CaptureHandoffRequest? _last;
    private IReadOnlyList<CaptureIdentifiedItem> _lastIdentified = [];
    private static readonly ProducerIdentity Producer = new("Tarkov Companion flea offer", "flea-offer-1");

    private readonly IItemRepository _items = items ?? throw new ArgumentNullException(nameof(items));
    private readonly ILogger<FleaCaptureHandoff> _logger = logger ?? NullLogger<FleaCaptureHandoff>.Instance;
    private readonly ExplainableRecommendationEngine _engine = engine ?? new ExplainableRecommendationEngine();

    private ExplainableRecommendationEngine Engine => policies?.CreateEngine() ?? _engine;

    /// <summary>Raised when a capture held at least one legible flea row. Never raised otherwise.</summary>
    public event EventHandler<FleaScanResult>? ListingsRead;

    public async ValueTask<CaptureHandoffResult> AcceptAsync(CaptureHandoffRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Analysis.FleaListings.Count == 0)
        {
            return CaptureHandoffResult.Accepted;
        }

        try
        {
            var identified = await WithKeptCorrectionAsync(request, cancellationToken).ConfigureAwait(false);
            lock (_lastGate)
            {
                _last = request;
                _lastIdentified = identified;
            }

            ListingsRead?.Invoke(this, await BuildAsync(request, identified, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not price the flea rows of capture {Artifact}.", request.ArtifactId);
        }

        return CaptureHandoffResult.Accepted;
    }

    /// <summary>
    /// The player said the last flea screen was for <paramref name="itemId"/>, one of the items it
    /// could be: the offers are priced again for it, the choice is kept with the frame, and the
    /// reading the name was taken from counts one pick towards a learned name.
    /// </summary>
    /// <returns>False when no flea screen was read, or the item was not one it could be.</returns>
    public async Task<bool> CorrectItemAsync(string itemId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        CaptureHandoffRequest? request;
        IReadOnlyList<CaptureIdentifiedItem> identified;
        lock (_lastGate)
        {
            request = _last;
            identified = _lastIdentified;
        }

        if (request is null || identified.Count == 0 ||
            identified.FirstOrDefault(item => string.Equals(item.CanonicalId, itemId, StringComparison.Ordinal)) is not { } chosen)
        {
            return false;
        }

        var reordered = FleaItemCorrection.PutFirst(identified, itemId);
        if (corrections is not null && !ReferenceEquals(reordered, identified))
        {
            await corrections.RecordFrameCorrectionAsync(request.Analysis.ResultId, CorrectionTarget, itemId, cancellationToken)
                .ConfigureAwait(false);
            if (FleaItemCorrection.ObservedText(identified[0].Evidence) is { } observed)
            {
                await corrections.RecordNamePickAsync(observed, itemId, chosen.DisplayName, cancellationToken).ConfigureAwait(false);
            }
        }

        lock (_lastGate)
        {
            if (ReferenceEquals(_last, request))
            {
                _lastIdentified = reordered;
            }
        }

        ListingsRead?.Invoke(this, await BuildAsync(request, reordered, cancellationToken).ConfigureAwait(false));
        return true;
    }

    private async Task<IReadOnlyList<CaptureIdentifiedItem>> WithKeptCorrectionAsync(
        CaptureHandoffRequest request,
        CancellationToken cancellationToken)
    {
        if (corrections is null)
        {
            return request.Analysis.Identified;
        }

        var kept = await corrections.FrameCorrectionsAsync(request.Analysis.ResultId, cancellationToken).ConfigureAwait(false);
        return kept.TryGetValue(CorrectionTarget, out var itemId)
            ? FleaItemCorrection.PutFirst(request.Analysis.Identified, itemId)
            : request.Analysis.Identified;
    }

    /// <summary>Public so the render preview and tests can price a frame without a capture session.</summary>
    public Task<FleaScanResult> BuildAsync(CaptureHandoffRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return BuildAsync(request, request.Analysis.Identified, cancellationToken);
    }

    private async Task<FleaScanResult> BuildAsync(
        CaptureHandoffRequest request,
        IReadOnlyList<CaptureIdentifiedItem> identifiedItems,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identified = identifiedItems.Take(5).ToArray();
        var evaluatedUtc = request.SubmittedUtc;
        var rates = await ReadFleaRatesAsync(cancellationToken).ConfigureAwait(false);
        var candidates = new List<CandidateFacts>(identified.Length);
        foreach (var candidate in identified)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.Add(await ReadCandidateAsync(candidate, rates, cancellationToken).ConfigureAwait(false));
        }

        var rows = new List<FleaScanRow>(request.Analysis.FleaListings.Count);
        for (var index = 0; index < request.Analysis.FleaListings.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = request.Analysis.FleaListings[index];
            var evaluations = candidates.Count == 0
                ? [EvaluateUnknown(request, row, index, evaluatedUtc)]
                : candidates
                    .Select((candidate, candidateIndex) => Evaluate(
                        request,
                        row,
                        candidate,
                        index,
                        candidateIndex,
                        evaluatedUtc,
                        cancellationToken))
                    .ToArray();
            var primary = evaluations[0];
            rows.Add(new(
                row.PriceRoubles,
                row.Quantity,
                row.Confidence.Value,
                row.SourceText,
                row.CurrencyCode,
                row.OriginalPrice ?? row.PriceRoubles,
                row.CurrencyRateRoubles,
                row.Condition,
                primary.MarginRoubles,
                primary.Recommendation,
                [
                    .. evaluations.Skip(1).Select(alternative => new FleaScanAlternative(
                        alternative.ItemId!,
                        alternative.ItemName!,
                        alternative.Confidence,
                        alternative.MarginRoubles,
                        alternative.Recommendation)),
                ]));
        }

        var ranked = rows
            .Select((row, sourceIndex) => (Row: row, SourceIndex: sourceIndex))
            .OrderBy(candidate => Rank(candidate.Row.Recommendation))
            .ThenByDescending(candidate => candidate.Row.ResaleMarginRoubles)
            .ThenBy(candidate => candidate.Row.PriceRoubles)
            .ThenBy(candidate => candidate.SourceIndex)
            .Select(candidate => candidate.Row)
            .ToArray();
        var best = candidates.FirstOrDefault();
        return new(
            request.SessionId,
            request.ArtifactId,
            request.CapturedUtc,
            best?.Candidate.CanonicalId,
            best?.Definition?.Name ?? best?.Candidate.DisplayName,
            [.. identified.Skip(1).Take(4)],
            best?.Price?.BestTrader?.ValueRoubles,
            best?.Price?.BestTrader?.TraderName,
            best?.AverageRoubles,
            best?.FeeRoubles,
            best?.Price?.Provenance.SourceUpdatedUtc ?? best?.Price?.Provenance.ObservedUtc,
            ranked)
        {
            FeeRates = rates,
        };
    }

    private async Task<FleaMarketRates?> ReadFleaRatesAsync(CancellationToken cancellationToken)
    {
        if (market is null)
        {
            return null;
        }

        try
        {
            return await market.GetFleaRatesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<CandidateFacts> ReadCandidateAsync(
        CaptureIdentifiedItem candidate,
        FleaMarketRates? rates,
        CancellationToken cancellationToken)
    {
        var definition = await _items.GetAsync(candidate.CanonicalId, cancellationToken).ConfigureAwait(false);
        var price = await _items.GetPriceAsync(candidate.CanonicalId, cancellationToken).ConfigureAwait(false);
        ItemMarketFacts? facts = null;
        if (market is not null)
        {
            try
            {
                facts = await market.GetAsync(candidate.CanonicalId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                facts = null;
            }
        }

        var average = definition is { FleaEligible: true }
            ? price?.Average24HourRoubles ?? price?.FleaPriceRoubles
            : null;
        long? fee = average is { } asking and > 0 && rates is not null && facts?.BasePriceRoubles is { } basePrice and > 0
            ? FleaMarketFee.Calculate(basePrice, asking, 1, rates)
            : null;
        return new(candidate, definition, price, rates, average, fee, average - fee);
    }

    private Evaluation Evaluate(
        CaptureHandoffRequest request,
        CaptureFleaListing row,
        CandidateFacts candidate,
        int rowIndex,
        int candidateIndex,
        DateTimeOffset evaluatedUtc,
        CancellationToken cancellationToken)
    {
        var identity = ScreenshotProvenance(
            request,
            $"item/{candidate.Candidate.CanonicalId}",
            candidate.Candidate.Confidence.Value,
            evaluatedUtc);
        var priceProvenance = OfferPriceProvenance(request, row, rowIndex, evaluatedUtc);
        var economics = Economics(
            candidate,
            row,
            evaluatedUtc,
            ScreenshotProvenance(request, $"row/{rowIndex}/condition", row.Confidence.Value, evaluatedUtc));
        var recommendation = Engine.EvaluateFleaOffer(
            new FleaOfferRecommendationRequest(
                $"flea-{request.ArtifactId}-{rowIndex}-{candidateIndex}",
                Complete("offer.item", candidate.Candidate.CanonicalId, identity),
                evaluatedUtc,
                request.SessionId,
                Complete<long?>("offer.price-roubles", row.PriceRoubles, priceProvenance),
                economics),
            cancellationToken);
        var bestResale = BestResale(economics);
        var margin = recommendation.Decision.Value?.Action is RecommendationAction.Take or RecommendationAction.Leave &&
                     bestResale is { } resale
            ? resale - row.PriceRoubles
            : (long?)null;
        return new(
            candidate.Candidate.CanonicalId,
            candidate.Definition?.Name ?? candidate.Candidate.DisplayName,
            candidate.Candidate.Confidence.Value,
            margin,
            recommendation);
    }

    private Evaluation EvaluateUnknown(
        CaptureHandoffRequest request,
        CaptureFleaListing row,
        int rowIndex,
        DateTimeOffset evaluatedUtc)
    {
        var provenance = ScreenshotProvenance(request, "item/unread", 0, evaluatedUtc);
        var unknown = UnknownEconomics(provenance);
        var recommendation = Engine.EvaluateFleaOffer(new FleaOfferRecommendationRequest(
            $"flea-{request.ArtifactId}-{rowIndex}-unread",
            Unknown<string>("offer.item", "identity.unread", provenance),
            evaluatedUtc,
            request.SessionId,
            Complete<long?>("offer.price-roubles", row.PriceRoubles, OfferPriceProvenance(request, row, rowIndex, evaluatedUtc)),
            unknown));
        return new(null, null, 0, null, recommendation);
    }

    private static RecommendationEconomics Economics(
        CandidateFacts candidate,
        CaptureFleaListing row,
        DateTimeOffset evaluatedUtc,
        EvidenceProvenance conditionProvenance)
    {
        var catalog = CatalogProvenance(candidate, evaluatedUtc);
        var unavailable = new ResultStatus(ResultCompleteness.Unavailable, FreshnessState.Current, "catalog.not-published");
        var unknown = new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Unknown, "catalog.unread");
        var average = candidate.AverageRoubles;
        var fee = candidate.FeeRoubles;
        var fleaGross = average is { } gross
            ? Complete<long?>("economics.flea-gross", gross, catalog)
            : new EvidencedValue<long?>("economics.flea-gross", null, unavailable, catalog);
        var feeProvenance = FeeProvenance(candidate, catalog, evaluatedUtc);
        var fleaFee = average is null
            ? new EvidencedValue<long?>("economics.flea-fee", null, unavailable, catalog)
            : fee is { } knownFee
                ? Complete<long?>("economics.flea-fee", knownFee, feeProvenance)
                : new EvidencedValue<long?>("economics.flea-fee", null, unknown, catalog);
        var netProvenance = fee is null
            ? catalog
            : new EvidenceProvenance(
                EvidenceSourceClass.DerivedCalculation,
                $"flea-offer/net/{candidate.Candidate.CanonicalId}",
                evaluatedUtc,
                EvidenceConfidence.Certain,
                Producer,
                generatedUtc: evaluatedUtc,
                inputs: [catalog, feeProvenance]);
        var fleaNet = candidate.ResaleNetRoubles is { } net
            ? Complete<long?>("economics.flea-net", net, netProvenance)
            : new EvidencedValue<long?>(
                "economics.flea-net",
                null,
                average is null ? unavailable : unknown,
                catalog);
        var trader = candidate.Price?.BestTrader?.ValueRoubles is { } paid
            ? Complete<long?>("economics.trader", paid, catalog)
            : new EvidencedValue<long?>("economics.trader", null, unavailable, catalog);
        var squares = candidate.Definition?.Dimensions.Slots is { } slots
            ? Complete<int?>("economics.squares", slots, catalog)
            : new EvidencedValue<int?>("economics.squares", null, unknown, catalog);
        var condition = row.Condition is { Current: { } current, Maximum: { } maximum }
            ? Complete<double?>(
                "economics.condition",
                current / maximum,
                conditionProvenance)
            : new EvidencedValue<double?>("economics.condition", null, unavailable, catalog);
        return new(fleaGross, fleaFee, fleaNet, trader, squares, condition);
    }

    private static RecommendationEconomics UnknownEconomics(EvidenceProvenance provenance)
    {
        return new(
            Unknown<long?>("economics.flea-gross", "catalog.unread", provenance),
            Unknown<long?>("economics.flea-fee", "catalog.unread", provenance),
            Unknown<long?>("economics.flea-net", "catalog.unread", provenance),
            Unknown<long?>("economics.trader", "catalog.unread", provenance),
            Unknown<int?>("economics.squares", "catalog.unread", provenance),
            Unknown<double?>("economics.condition", "condition.unread", provenance));
    }

    private static EvidenceProvenance CatalogProvenance(CandidateFacts candidate, DateTimeOffset evaluatedUtc)
    {
        var source = candidate.Price?.Provenance ?? candidate.Definition?.Provenance;
        var observed = Earlier(source?.SourceUpdatedUtc ?? source?.ObservedUtc ?? evaluatedUtc, evaluatedUtc);
        return new(
            EvidenceSourceClass.PublicStructuredData,
            $"json.tarkov.dev/items/{candidate.Candidate.CanonicalId}",
            observed,
            EvidenceConfidence.Certain,
            Producer);
    }

    private static EvidenceProvenance FeeProvenance(
        CandidateFacts candidate,
        EvidenceProvenance catalog,
        DateTimeOffset evaluatedUtc)
    {
        if (candidate.Rates is not { } rates)
        {
            return catalog;
        }

        var publishedRates = new EvidenceProvenance(
            EvidenceSourceClass.PublicStructuredData,
            "json.tarkov.dev/items/flea-market-rates",
            Earlier(rates.ObservedUtc, evaluatedUtc),
            EvidenceConfidence.Certain,
            Producer);
        return new EvidenceProvenance(
            EvidenceSourceClass.DerivedCalculation,
            $"flea-offer/fee/{candidate.Candidate.CanonicalId}",
            evaluatedUtc,
            EvidenceConfidence.Certain,
            Producer,
            generatedUtc: evaluatedUtc,
            inputs: [catalog, publishedRates]);
    }

    private static EvidenceProvenance OfferPriceProvenance(
        CaptureHandoffRequest request,
        CaptureFleaListing row,
        int rowIndex,
        DateTimeOffset evaluatedUtc)
    {
        var screenshot = ScreenshotProvenance(request, $"row/{rowIndex}/price", row.Confidence.Value, evaluatedUtc);
        if (row.CurrencyCode == "RUB" || row.CurrencyRateProvenance is not { } rateSource)
        {
            return screenshot;
        }

        var rateObserved = Earlier(rateSource.SourceUpdatedUtc ?? rateSource.ObservedUtc, evaluatedUtc);
        var rate = new EvidenceProvenance(
            EvidenceSourceClass.PublicStructuredData,
            $"json.tarkov.dev/currency/{row.CurrencyCode}",
            rateObserved,
            EvidenceConfidence.Certain,
            Producer);
        return new EvidenceProvenance(
            EvidenceSourceClass.DerivedCalculation,
            $"flea-offer/currency/{row.CurrencyCode}-to-RUB",
            evaluatedUtc,
            screenshot.Confidence,
            Producer,
            generatedUtc: evaluatedUtc,
            inputs: [screenshot, rate]);
    }

    private static EvidenceProvenance ScreenshotProvenance(
        CaptureHandoffRequest request,
        string suffix,
        double confidence,
        DateTimeOffset evaluatedUtc) =>
        new(
            EvidenceSourceClass.GameWrittenScreenshot,
            $"{request.Provenance.SourceIdentifier}/flea/{suffix}",
            Earlier(request.Provenance.ObservedUtc, evaluatedUtc),
            new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, confidence),
            Producer);

    private static int Rank(RecommendationResult recommendation) => recommendation.Decision.Value?.Action switch
    {
        RecommendationAction.Take => 0,
        RecommendationAction.Leave => 1,
        _ => 2,
    };

    private static long? BestResale(RecommendationEconomics economics) =>
        (economics.FleaNetRoubles.Value, economics.TraderRoubles.Value) switch
        {
            ({ } flea, { } trader) => Math.Max(flea, trader),
            ({ } flea, null) => flea,
            (null, { } trader) => trader,
            _ => null,
        };

    private static DateTimeOffset Earlier(DateTimeOffset value, DateTimeOffset ceiling) =>
        value <= ceiling ? value : ceiling;

    private static EvidencedValue<T> Complete<T>(string fieldId, T value, EvidenceProvenance provenance) =>
        new(fieldId, value, new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "complete"), provenance);

    private static EvidencedValue<T> Unknown<T>(string fieldId, string code, EvidenceProvenance provenance) =>
        new(fieldId, default!, new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Unknown, code), provenance);

    private sealed record CandidateFacts(
        CaptureIdentifiedItem Candidate,
        ItemDefinition? Definition,
        ItemPriceSnapshot? Price,
        FleaMarketRates? Rates,
        long? AverageRoubles,
        long? FeeRoubles,
        long? ResaleNetRoubles);

    private sealed record Evaluation(
        string? ItemId,
        string? ItemName,
        double Confidence,
        long? MarginRoubles,
        RecommendationResult Recommendation);
}

/// <summary>How a flea screen's corrected item is put first, and which reading it corrects.</summary>
public static class FleaItemCorrection
{
    /// <summary>What a player's pick is recorded as in an identified item's evidence.</summary>
    public const string PickedEvidence = "picked by the player";

    /// <summary>
    /// The list with <paramref name="itemId"/> first, as certain as the player's word, and the rest
    /// in their order; the same list when it was already picked or is not in the list at all (a
    /// pick is only ever a listed item).
    /// </summary>
    /// <remarks>
    /// The pick carries confidence one: priced at the reader's own 0.52 the rows read "item name
    /// uncertain" and compared with nothing, although the player had just said what it was.
    /// </remarks>
    public static IReadOnlyList<CaptureIdentifiedItem> PutFirst(IReadOnlyList<CaptureIdentifiedItem> identified, string itemId)
    {
        ArgumentNullException.ThrowIfNull(identified);
        var index = identified.ToList().FindIndex(item => string.Equals(item.CanonicalId, itemId, StringComparison.Ordinal));
        if (index < 0 || identified[index].Evidence.StartsWith(PickedEvidence, StringComparison.Ordinal))
        {
            return identified;
        }

        var picked = identified[index] with
        {
            Confidence = new Confidence(1),
            Evidence = $"{PickedEvidence}; {identified[index].Evidence}",
        };
        return [picked, .. identified.Where((_, position) => position != index)];
    }

    /// <summary>
    /// The text the name reader matched, from its evidence line (<c>observed=...;</c>), or null
    /// when the evidence does not carry one.
    /// </summary>
    public static string? ObservedText(string? evidence)
    {
        if (string.IsNullOrEmpty(evidence))
        {
            return null;
        }

        const string marker = "observed=";
        var start = evidence.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = evidence.IndexOf(';', start);
        var text = (end < 0 ? evidence[start..] : evidence[start..end]).Trim();
        return text.Length == 0 ? null : text;
    }
}
