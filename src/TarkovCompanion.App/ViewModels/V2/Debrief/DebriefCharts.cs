using System.Globalization;
using TarkovCompanion.App.Localization;
using System.Windows.Input;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.App.ViewModels.V2.Debrief;

/// <summary>One map's coverage row: raids, how they ended, and the extracts used of those offered.</summary>
public sealed record DebriefMapCoverageRowViewModel(
    string MapLabel,
    string RaidsLabel,
    string ExtractedLabel,
    string DiedLabel,
    string SurvivalLabel,
    string ExtractsLabel,
    string OfferedNote);

/// <summary>One row of the value-per-raid table, the chart's text equivalent.</summary>
public sealed record DebriefValueRowViewModel(string StartedLabel, string MapLabel, string ValueLabel);

/// <summary>One map's survival bar, and the same numbers as a table row.</summary>
public sealed record DebriefSurvivalBarViewModel(string MapLabel, double Rate, string RateLabel, string CountLabel)
{
    /// <summary>Bar length in pixels for a fixed-width track; a zero rate still shows a sliver.</summary>
    public double BarWidth => Math.Max(2, Rate * DebriefChartViewModel.BarTrackWidth);
}

/// <summary>
/// A chart that can be read as a table instead. The table is the accessible form: it carries every
/// value the drawing does, and the toggle is an ordinary button a screen reader or keyboard reaches.
/// </summary>
public abstract class DebriefChartViewModel : BindableViewModel
{
    public const double BarTrackWidth = 220;
    private bool _showTable;

    protected DebriefChartViewModel()
    {
        ToggleTableCommand = new DelegateCommand(() => ShowTable = !ShowTable);
    }

    public bool ShowTable
    {
        get => _showTable;
        set
        {
            if (SetProperty(ref _showTable, value))
            {
                OnPropertyChanged(nameof(ShowChart));
                OnPropertyChanged(nameof(ToggleLabel));
            }
        }
    }

    public bool ShowChart => !ShowTable && HasData;

    public string ToggleLabel => ShowTable ? DebriefText.Chart : DebriefText.Table;

    public ICommand ToggleTableCommand { get; }

    public abstract bool HasData { get; }

    public bool HasNoData => !HasData;

    /// <summary>One sentence a screen reader gives for the drawing.</summary>
    public string Summary { get; protected set; } = string.Empty;

    // An empty name tells a binding every property changed: the chart is replaced as a whole.
    protected void RaiseData() => OnPropertyChanged(string.Empty);
}

/// <summary>Value brought out per raid, oldest first, from the value typed on each raid.</summary>
public sealed class DebriefValueChartViewModel : DebriefChartViewModel
{
    public IReadOnlyList<double> Values { get; private set; } = [];

    public IReadOnlyList<DebriefValueRowViewModel> Rows { get; private set; } = [];

    public string MaxLabel { get; private set; } = string.Empty;

    public string FirstLabel { get; private set; } = string.Empty;

    public string LastLabel { get; private set; } = string.Empty;

    public override bool HasData => Values.Count > 0;

    public string EmptyLabel => DebriefText.ValueChartEmpty;

    internal void Update(IReadOnlyList<RaidValuePoint> points, Func<string?, string> mapLabel)
    {
        Values = [.. points.Select(point => (double)point.ValueRoubles)];
        Rows = [.. points.Select(point => new DebriefValueRowViewModel(
            LocalTime.Moment(point.StartedUtc) ?? DebriefText.Unknown,
            mapLabel(point.MapId),
            Roubles(point.ValueRoubles)))];
        MaxLabel = points.Count == 0 ? string.Empty : Roubles(points.Max(point => point.ValueRoubles));
        FirstLabel = points.Count == 0 ? string.Empty : LocalTime.Moment(points[0].StartedUtc) ?? string.Empty;
        LastLabel = points.Count < 2 ? string.Empty : LocalTime.Moment(points[^1].StartedUtc) ?? string.Empty;
        Summary = points.Count == 0
            ? DebriefText.NoValueEntered
            : DebriefText.ValueSummary(points.Count, MaxLabel, Roubles((long)points.Average(point => point.ValueRoubles)));
        RaiseData();
    }

    private static string Roubles(long value) => UnitText.Roubles(value);
}

/// <summary>Survival rate per map: extracted over raids whose outcome is recorded.</summary>
public sealed class DebriefSurvivalChartViewModel : DebriefChartViewModel
{
    public IReadOnlyList<DebriefSurvivalBarViewModel> Bars { get; private set; } = [];

    public override bool HasData => Bars.Count > 0;

    public string EmptyLabel => DebriefText.SurvivalChartEmpty;

    internal void Update(IReadOnlyList<RaidMapCoverage> maps, Func<string?, string> mapLabel)
    {
        Bars = [.. maps
            .Where(map => map.SurvivalRate is not null)
            .Select(map => new DebriefSurvivalBarViewModel(
                mapLabel(map.MapId),
                map.SurvivalRate!.Value,
                Percent(map.SurvivalRate.Value),
                DebriefText.Of(map.Extracted, map.OutcomesRecorded)))];
        Summary = Bars.Count == 0
            ? DebriefText.NoOutcomeRecorded
            : string.Join("; ", Bars.Select(bar => $"{bar.MapLabel} {bar.RateLabel}")) + ".";
        RaiseData();
    }

    internal static string Percent(double rate) => (rate * 100).ToString("0", CultureInfo.CurrentCulture) + "%";
}
