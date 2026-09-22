using System.Globalization;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.App.ViewModels.V2.Intel;

/// <summary>How one photographed offer stands against what the item can be sold for.</summary>
public enum FleaRowVerdict
{
    /// <summary>Nothing to compare it with.</summary>
    Unknown = 0,

    /// <summary>A trader pays more than this offer costs.</summary>
    ProfitToTrader,

    /// <summary>Resold at the 24-hour average it returns more than it costs, after the fee.</summary>
    ProfitOnFlea,

    /// <summary>Cheaper than the 24-hour average, but the fee eats the difference.</summary>
    UnderAverage,

    /// <summary>At or over the 24-hour average.</summary>
    OverAverage,
}

/// <summary>One row of a photographed flea screen, worded for the list.</summary>
public sealed class FleaScanRowViewModel
{
    public FleaScanRowViewModel(FleaScanRow row, FleaScanResult scan, int rank, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(scan);
        AutomationId = $"v2-flea-scan-row-{rank.ToString(CultureInfo.InvariantCulture)}";
        RankLabel = rank == 1 ? "#1 best buy" : $"#{rank.ToString(culture)}";
        PriceLabel = $"{Roubles(row.PriceRoubles, culture)} each";
        StackLabel = row.Quantity switch
        {
            null => "count not read",
            1 => "1 unit",
            { } count => $"{count.ToString("N0", culture)} units · {Roubles(row.StackRoubles ?? row.PriceRoubles, culture)} for the lot",
        };
        Verdict = Judge(row.PriceRoubles, scan.TraderRoubles, scan.Average24HourRoubles, row.ResaleNetRoubles);
        VerdictLabel = Verdict switch
        {
            FleaRowVerdict.ProfitToTrader => "Good buy",
            FleaRowVerdict.ProfitOnFlea => "Good buy",
            FleaRowVerdict.UnderAverage => "Under average",
            FleaRowVerdict.OverAverage => "Over average",
            _ => "No comparison",
        };
        WhyLabel = Verdict switch
        {
            FleaRowVerdict.ProfitToTrader =>
                $"{scan.TraderName ?? "A trader"} pays {Roubles(scan.TraderRoubles!.Value - row.PriceRoubles, culture)} more than this",
            FleaRowVerdict.ProfitOnFlea =>
                $"Resold at the 24 h average it clears {Roubles(row.ResaleNetRoubles!.Value - row.PriceRoubles, culture)} after the fee",
            FleaRowVerdict.UnderAverage when row.ResaleNetRoubles is not null =>
                $"{Roubles(scan.Average24HourRoubles!.Value - row.PriceRoubles, culture)} under the 24 h average, less than its fee",
            FleaRowVerdict.UnderAverage =>
                $"{Roubles(scan.Average24HourRoubles!.Value - row.PriceRoubles, culture)} under the 24 h average · fee not known",
            FleaRowVerdict.OverAverage =>
                $"{Roubles(row.PriceRoubles - scan.Average24HourRoubles!.Value, culture)} over the 24 h average",
            _ => "The catalog has no trader or flea price for it",
        };
        ConfidenceLabel = $"read {row.Confidence.ToString("P0", culture)} sure";
    }

    public string AutomationId { get; }

    public string RankLabel { get; }

    public string PriceLabel { get; }

    public string StackLabel { get; }

    public FleaRowVerdict Verdict { get; }

    public string VerdictLabel { get; }

    public string WhyLabel { get; }

    public string ConfidenceLabel { get; }

    public bool IsGoodBuy => Verdict is FleaRowVerdict.ProfitToTrader or FleaRowVerdict.ProfitOnFlea;

    /// <summary>
    /// "Good buy" means one thing: bought at this price and sold again, it returns more than it
    /// cost. To a trader that is certain; on the flea it is as good as the 24-hour average.
    /// </summary>
    internal static FleaRowVerdict Judge(long price, long? trader, long? average, long? resaleNet) =>
        trader is { } pays && pays > price ? FleaRowVerdict.ProfitToTrader
        : resaleNet is { } net && net > price ? FleaRowVerdict.ProfitOnFlea
        : average is { } mean && price < mean ? FleaRowVerdict.UnderAverage
        : average is not null ? FleaRowVerdict.OverAverage
        : FleaRowVerdict.Unknown;

    internal static string Roubles(long value, CultureInfo culture) => "₽" + value.ToString("N0", culture);
}

/// <summary>A photographed flea screen as Intel &gt; Flea shows it.</summary>
public sealed class FleaScanViewModel
{
    public FleaScanViewModel(
        FleaScanResult scan,
        CultureInfo? culture = null,
        bool offline = false,
        TimeProvider? timeProvider = null)
    {
        Scan = scan ?? throw new ArgumentNullException(nameof(scan));
        var format = culture ?? CultureInfo.CurrentCulture;
        Heading = scan.ItemName is { } name ? $"Offers for {name}" : "Offers you photographed";
        ItemLabel = scan.ItemName is null
            ? "The item's name was not legible, so the rows stand alone."
            : scan.Alternates.Count == 0
                ? "Read from your screenshot."
                : $"Read from your screenshot · could also be {string.Join(", ", scan.Alternates.Take(2).Select(item => item.DisplayName))}";
        TraderLabel = scan.TraderRoubles is { } trader
            ? $"{scan.TraderName ?? "Best trader"} pays {FleaScanRowViewModel.Roubles(trader, format)}"
            : "No trader buys it";
        AverageLabel = (scan.Average24HourRoubles, scan.AverageFeeRoubles) switch
        {
            ({ } average, { } fee) =>
                $"24 h average {FleaScanRowViewModel.Roubles(average, format)} · {FleaScanRowViewModel.Roubles(average - fee, format)} after a {FleaScanRowViewModel.Roubles(fee, format)} fee",
            ({ } average, null) => $"24 h average {FleaScanRowViewModel.Roubles(average, format)} · fee not known",
            _ => "No 24 h flea average",
        };
        ObservedLabel = $"Photographed {LocalTime.Moment(scan.ObservedUtc)}";
        Rows =
        [
            .. scan.Rows
                .Select((row, sourceIndex) => (Row: row, SourceIndex: sourceIndex))
                .OrderBy(candidate => candidate.Row.PriceRoubles)
                .ThenBy(candidate => candidate.SourceIndex)
                .Select((candidate, index) => new FleaScanRowViewModel(candidate.Row, scan, index + 1, format)),
        ];
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var stale = scan.PriceUpdatedUtc is { } priceTime && now - priceTime > TimeSpan.FromDays(1);
        MarketDataNote = (offline, stale, scan.PriceUpdatedUtc) switch
        {
            (true, true, { } offlineTime) => $"Offline · comparison prices are from {LocalTime.Moment(offlineTime)}",
            (true, _, _) => "Offline · comparison prices are cached",
            (false, true, { } onlineTime) => $"Price comparison is over a day old · {LocalTime.Moment(onlineTime)}",
            _ => string.Empty,
        };
        SummaryLabel = Rows.Count(row => row.IsGoodBuy) switch
        {
            0 => $"{Rows.Count.ToString(format)} rows read · none would pay to resell",
            var good => $"{Rows.Count.ToString(format)} rows read · {good.ToString(format)} would pay to resell",
        };
    }

    public FleaScanResult Scan { get; }

    public string Heading { get; }

    public string ItemLabel { get; }

    public string TraderLabel { get; }

    public string AverageLabel { get; }

    public string ObservedLabel { get; }

    public string SummaryLabel { get; }

    public string MarketDataNote { get; }

    public bool HasMarketDataNote => MarketDataNote.Length > 0;

    public IReadOnlyList<FleaScanRowViewModel> Rows { get; }
}
