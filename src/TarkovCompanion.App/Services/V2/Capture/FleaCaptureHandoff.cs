using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>One flea offer the player photographed, beside what the catalog says the item is worth.</summary>
/// <param name="PriceRoubles">The price on the row, for one unit.</param>
/// <param name="Quantity">Units in the offer, where that was legible.</param>
/// <param name="ResaleNetRoubles">What one unit returns resold at the 24-hour average, after the fee; null where unknown.</param>
public sealed record FleaScanRow(long PriceRoubles, int? Quantity, double Confidence, string? SourceText, long? ResaleNetRoubles)
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
    IReadOnlyList<FleaScanRow> Rows);

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
    ILogger<FleaCaptureHandoff>? logger = null) : ICaptureResultHandoff
{
    private readonly IItemRepository _items = items ?? throw new ArgumentNullException(nameof(items));
    private readonly ILogger<FleaCaptureHandoff> _logger = logger ?? NullLogger<FleaCaptureHandoff>.Instance;

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
            ListingsRead?.Invoke(this, await BuildAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not price the flea rows of capture {Artifact}.", request.ArtifactId);
        }

        return CaptureHandoffResult.Accepted;
    }

    /// <summary>Public so the render preview and tests can price a frame without a capture session.</summary>
    public async Task<FleaScanResult> BuildAsync(CaptureHandoffRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identified = request.Analysis.Identified;
        var best = identified.Count > 0 ? identified[0] : null;
        var definition = best is null ? null : await _items.GetAsync(best.CanonicalId, cancellationToken).ConfigureAwait(false);
        var price = best is null ? null : await _items.GetPriceAsync(best.CanonicalId, cancellationToken).ConfigureAwait(false);
        var average = definition is { FleaEligible: true } ? price?.Average24HourRoubles ?? price?.FleaPriceRoubles : null;
        var fee = average is { } asking and > 0 && best is not null
            ? await FeeAsync(best.CanonicalId, asking, cancellationToken).ConfigureAwait(false)
            : null;
        var resaleNet = average is { } value && fee is { } cost ? value - cost : (long?)null;
        return new(
            request.SessionId,
            request.ArtifactId,
            request.CapturedUtc,
            best?.CanonicalId,
            definition?.Name ?? best?.DisplayName,
            [.. identified.Skip(1).Take(4)],
            price?.BestTrader?.ValueRoubles,
            price?.BestTrader?.TraderName,
            average,
            fee,
            price?.Provenance.SourceUpdatedUtc ?? price?.Provenance.ObservedUtc,
            [
                .. request.Analysis.FleaListings.Select(row => new FleaScanRow(
                    row.PriceRoubles,
                    row.Quantity,
                    row.Confidence.Value,
                    row.SourceText,
                    resaleNet)),
            ]);
    }

    private async Task<long?> FeeAsync(string itemId, long askingRoubles, CancellationToken cancellationToken)
    {
        if (market is null)
        {
            return null;
        }

        try
        {
            var rates = await market.GetFleaRatesAsync(cancellationToken).ConfigureAwait(false);
            var facts = await market.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
            return rates is not null && facts?.BasePriceRoubles is { } basePrice and > 0
                ? FleaMarketFee.Calculate(basePrice, askingRoubles, 1, rates)
                : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }
}
