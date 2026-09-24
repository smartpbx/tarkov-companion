using System.Globalization;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.Core.Abstractions.V2;
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
        : this(row, scan, rank, culture, scan.ObservedUtc)
    {
    }

    public FleaScanRowViewModel(FleaScanRow row, FleaScanResult scan, int rank, CultureInfo culture, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(scan);
        AutomationId = $"v2-flea-scan-row-{rank.ToString(CultureInfo.InvariantCulture)}";
        RankLabel = rank == 1 ? "#1 best buy" : $"#{rank.ToString(culture)}";
        PriceLabel = row.CurrencyCode == "RUB"
            ? $"{Roubles(row.PriceRoubles, culture)} each"
            : $"{OriginalPrice(row, culture)} · {Roubles(row.PriceRoubles, culture)} each";
        StackLabel = row.Quantity switch
        {
            null => "count not read",
            1 => "1 unit",
            { } count => $"{count.ToString("N0", culture)} units · {Roubles(row.StackRoubles ?? row.PriceRoubles, culture)} for the lot",
        };
        var decision = row.Recommendation.Decision.Value;
        var economicReason = decision?.Reasons.FirstOrDefault(reason => reason.Category == RecommendationReasonCategory.Economics);
        Verdict = decision?.Action switch
        {
            RecommendationAction.Take when economicReason?.Code.Contains("trader", StringComparison.Ordinal) == true => FleaRowVerdict.ProfitToTrader,
            RecommendationAction.Take => FleaRowVerdict.ProfitOnFlea,
            RecommendationAction.Leave when scan.Average24HourRoubles is { } average && row.PriceRoubles < average => FleaRowVerdict.UnderAverage,
            RecommendationAction.Leave => FleaRowVerdict.OverAverage,
            _ => FleaRowVerdict.Unknown,
        };
        VerdictLabel = Verdict switch
        {
            FleaRowVerdict.ProfitToTrader => "Good buy",
            FleaRowVerdict.ProfitOnFlea => "Good buy",
            FleaRowVerdict.UnderAverage => "Under average",
            FleaRowVerdict.OverAverage => "Over average",
            _ => "No comparison",
        };
        // #842: the line is the player's; the engine's sentence, its rules version and the raw
        // read are the row's details, shown on hover.
        WhyLabel = FleaRowReason.Describe(row, scan, now, culture);
        ConfidenceLabel = $"read {row.Confidence.ToString("P0", culture)} sure";
        ConditionLabel = row.Condition is { } condition
            ? $"{ConditionName(condition.Kind)} {condition.Current!.Value.ToString("N1", culture).TrimEnd('0').TrimEnd(culture.NumberFormat.NumberDecimalSeparator[0])}/{condition.Maximum!.Value.ToString("N1", culture).TrimEnd('0').TrimEnd(culture.NumberFormat.NumberDecimalSeparator[0])}"
            : "condition not read";
        AlternativesLabel = row.Alternatives.Count == 0
            ? string.Empty
            : $"Other reads: {string.Join(" · ", row.Alternatives.Take(2).Select(alternative => $"{alternative.ItemName} — {ActionLabel(alternative.Recommendation)}"))}";
        EvidenceLabel = row.SourceText is { Length: > 0 } source ? $"Read: {source}" : string.Empty;
        DetailsLabel = string.Join(
            Environment.NewLine,
            new[]
            {
                economicReason?.Explanation ?? decision?.Reasons.FirstOrDefault()?.Explanation,
                $"Rules {row.Recommendation.RulesetVersion}",
                EvidenceLabel,
            }.Where(line => !string.IsNullOrEmpty(line)));
    }

    public string AutomationId { get; }

    public string RankLabel { get; }

    public string PriceLabel { get; }

    public string StackLabel { get; }

    public FleaRowVerdict Verdict { get; }

    public string VerdictLabel { get; }

    public string WhyLabel { get; }

    public string ConfidenceLabel { get; }

    public string ConditionLabel { get; }

    public string AlternativesLabel { get; }

    public bool HasAlternatives => AlternativesLabel.Length > 0;

    public string EvidenceLabel { get; }

    public bool HasEvidence => EvidenceLabel.Length > 0;

    /// <summary>The engine's own explanation, its rules version and the raw read, for the tooltip.</summary>
    public string DetailsLabel { get; }

    public bool IsGoodBuy => Verdict is FleaRowVerdict.ProfitToTrader or FleaRowVerdict.ProfitOnFlea;

    internal static string Roubles(long value, CultureInfo culture) => "₽" + value.ToString("N0", culture);

    private static string OriginalPrice(FleaScanRow row, CultureInfo culture) => row.CurrencyCode switch
    {
        "EUR" => "€" + row.OriginalPrice.ToString("N0", culture),
        "USD" => "$" + row.OriginalPrice.ToString("N0", culture),
        _ => row.OriginalPrice.ToString("N0", culture) + " " + row.CurrencyCode,
    };

    private static string ConditionName(ItemConditionKind kind) => kind switch
    {
        ItemConditionKind.Uses => "Uses",
        ItemConditionKind.Charges => "Charges",
        ItemConditionKind.Resource => "Resource",
        _ => "Durability",
    };

    private static string ActionLabel(RecommendationResult recommendation) => recommendation.Decision.Value?.Action switch
    {
        RecommendationAction.Take => "good buy",
        RecommendationAction.Leave => "skip",
        _ => "review",
    };
}

/// <summary>A photographed flea screen as Intel &gt; Flea shows it.</summary>
public sealed class FleaScanViewModel : BindableViewModel
{
    private readonly TimeProvider _clock;
    private readonly CultureInfo _culture;
    private string _observedLabel = string.Empty;
    private bool _isOfferStale;

    public FleaScanViewModel(
        FleaScanResult scan,
        CultureInfo? culture = null,
        bool offline = false,
        TimeProvider? timeProvider = null)
    {
        Scan = scan ?? throw new ArgumentNullException(nameof(scan));
        var format = culture ?? CultureInfo.CurrentCulture;
        _culture = format;
        _clock = timeProvider ?? TimeProvider.System;
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
            // #842: the fee is worked out from the rates an items refresh keeps; a catalog synced
            // before they were kept has none, and a refresh is what fixes it.
            ({ } average, null) when scan.FeeRates is null =>
                $"24 h average {FleaScanRowViewModel.Roubles(average, format)} · fee rates not synced, refresh",
            ({ } average, null) => $"24 h average {FleaScanRowViewModel.Roubles(average, format)} · no base price for the fee",
            _ => "No 24 h flea average",
        };
        var now = _clock.GetUtcNow();
        Rows = [.. scan.Rows.Select((row, index) => new FleaScanRowViewModel(row, scan, index + 1, format, now))];
        RefreshAge();
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

    /// <summary>#284: when the offers were seen; the ranking is only as good as that moment.</summary>
    public string ObservedLabel
    {
        get => _observedLabel;
        private set => SetProperty(ref _observedLabel, value);
    }

    /// <summary>The offers were photographed long enough ago that some may have sold.</summary>
    public bool IsOfferStale
    {
        get => _isOfferStale;
        private set => SetProperty(ref _isOfferStale, value);
    }

    /// <summary>Re-words <see cref="ObservedLabel"/> for the current time; the page calls it while shown.</summary>
    public void RefreshAge()
    {
        var now = _clock.GetUtcNow();
        ObservedLabel = FleaOfferAge.Describe(Scan.ObservedUtc, now, _culture);
        IsOfferStale = FleaOfferAge.IsStale(Scan.ObservedUtc, now);
    }

    public string SummaryLabel { get; }

    public string MarketDataNote { get; }

    public bool HasMarketDataNote => MarketDataNote.Length > 0;

    public IReadOnlyList<FleaScanRowViewModel> Rows { get; }
}
