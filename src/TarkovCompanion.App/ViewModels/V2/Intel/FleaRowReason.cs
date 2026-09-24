using System.Globalization;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using ExplainableRecommendationPolicy = TarkovCompanion.Core.Domain.Recommendations.ExplainableRecommendationPolicy;

namespace TarkovCompanion.App.ViewModels.V2.Intel;

/// <summary>
/// #842: one short line per photographed flea offer, in the player's words, chosen from the
/// engine's reason codes.
/// </summary>
/// <remarks>
/// The rows used to show the engine's own explanation. With ten-day-old comparison prices every
/// row carried the engine's flea-net sentence, which lists every way a figure can fail (stale,
/// ambiguous, incomplete and so on) and so tells the player none of them. The code says which figure failed; the scan and
/// the reason's lineage say how, so the line can say what to do about it. The engine's sentence
/// stays in the row's details. Codes, never the sentence, are matched: the sentence is prose and
/// can be reworded.
/// </remarks>
public static class FleaRowReason
{
    /// <summary>The engine code when the photographed item's identity is not trusted.</summary>
    public const string IdentityUntrusted = "identity.untrusted";

    /// <summary>The engine code when the photographed price is not trusted.</summary>
    public const string OfferPriceUntrusted = "offer.price-untrusted";

    /// <summary>The engine code when the flea resale figure is not trusted.</summary>
    public const string FleaNetUntrusted = "economics.flea-net-untrusted";

    /// <summary>The engine code when the trader resale figure is not trusted.</summary>
    public const string TraderUntrusted = "economics.trader-untrusted";

    /// <summary>The engine code when neither resale figure is there.</summary>
    public const string PriceMissing = "economics.price-missing";

    /// <summary>The engine code when the item's size is not known.</summary>
    public const string FootprintMissing = "economics.footprint-missing";

    private const string OfferComparison = "economics.offer.";

    /// <summary>Why a row has, or has no, comparison, in one short line.</summary>
    /// <param name="now">The moment the line is read at; a price's age is counted to it.</param>
    /// <param name="maximumPriceAge">How old a price the engine accepts; its default when null.</param>
    public static string Describe(
        FleaScanRow row,
        FleaScanResult scan,
        DateTimeOffset now,
        CultureInfo culture,
        TimeSpan? maximumPriceAge = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(culture);
        var decision = row.Recommendation.Decision.Value;
        if (decision is null)
        {
            return "No comparison";
        }

        var comparison = decision.Reasons.FirstOrDefault(reason =>
            reason.Category == RecommendationReasonCategory.Economics &&
            reason.Code.StartsWith(OfferComparison, StringComparison.Ordinal));
        if (decision.Action is RecommendationAction.Take or RecommendationAction.Leave && comparison is not null)
        {
            return Comparison(comparison.Code, row, scan, culture);
        }

        var limit = maximumPriceAge ?? ExplainableRecommendationPolicy.Default.MaximumPriceAge;
        var codes = decision.Reasons.ToDictionary(reason => reason.Code, StringComparer.Ordinal);
        if (codes.ContainsKey(IdentityUntrusted))
        {
            return scan.ItemName is null ? "Item not read · no comparison" : "Item name uncertain · check";
        }

        if (codes.ContainsKey(OfferPriceUntrusted))
        {
            return "Price read uncertain · check";
        }

        foreach (var code in (string[])[PriceMissing, FleaNetUntrusted, TraderUntrusted])
        {
            if (codes.TryGetValue(code, out var reason) && Oldest(reason.Provenance) is { } oldest && now - oldest > limit)
            {
                return $"Prices {Age(now - oldest, culture)} old · refresh to compare";
            }
        }

        if (codes.ContainsKey(PriceMissing))
        {
            return "No flea or trader price";
        }

        if (codes.ContainsKey(FleaNetUntrusted))
        {
            return scan.Average24HourRoubles is null
                ? "No flea price for this item"
                : "Flea fee not known · no comparison";
        }

        if (codes.ContainsKey(TraderUntrusted))
        {
            return "Trader price not known · no comparison";
        }

        if (codes.ContainsKey(FootprintMissing))
        {
            return "Item size not known · no comparison";
        }

        return "Not enough to compare";
    }

    /// <summary>"10 days", "5 h", "40 min": how long ago, at the coarsest unit that is not zero.</summary>
    internal static string Age(TimeSpan age, CultureInfo culture) => age switch
    {
        { TotalDays: >= 2 } => $"{((int)age.TotalDays).ToString(culture)} days",
        { TotalDays: >= 1 } => "1 day",
        { TotalHours: >= 1 } => $"{((int)age.TotalHours).ToString(culture)} h",
        _ => $"{Math.Max(1, (int)age.TotalMinutes).ToString(culture)} min",
    };

    private static string Comparison(string code, FleaScanRow row, FleaScanResult scan, CultureInfo culture)
    {
        var viaTrader = code.EndsWith(".trader", StringComparison.Ordinal);
        var channel = viaTrader ? $"to {scan.TraderName ?? "a trader"}" : "on flea after fee";
        return row.ResaleMarginRoubles switch
        {
            > 0 and var profit => $"{FleaScanRowViewModel.Roubles(profit, culture)} profit reselling {channel}",
            < 0 and var loss => $"{FleaScanRowViewModel.Roubles(-loss, culture)} more than it resells for",
            0 => "Resells for what it costs",
            _ => viaTrader ? "Compared with the trader price" : "Compared with the flea average",
        };
    }

    /// <summary>The oldest moment anything in this lineage stands for.</summary>
    private static DateTimeOffset? Oldest(EvidenceProvenance provenance)
    {
        var oldest = provenance.EvidenceThroughUtc;
        foreach (var input in provenance.Inputs)
        {
            if (Oldest(input) is { } inner && inner < oldest)
            {
                oldest = inner;
            }
        }

        return oldest;
    }
}
