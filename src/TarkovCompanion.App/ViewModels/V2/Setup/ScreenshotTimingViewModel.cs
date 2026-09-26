using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.CaptureSessions;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>
/// [#712 0-12] Setup › Updates &amp; Diagnostics: p50 and p95 per screenshot kind and milestone,
/// measured since the file's name was first seen, over this session's timelines.
/// </summary>
/// <remarks>
/// One line per kind rather than a grid, because the kinds reach different milestones (a
/// position is applied and sent to the relay, a loot frame is decoded, classified and shown) and a
/// grid of mostly empty cells says less than the line does.
/// </remarks>
public sealed class ScreenshotTimingViewModel : BindableViewModel
{
    private readonly ICaptureStageTimeline? _timeline;
    private readonly Action<Action> _post;
    private IReadOnlyList<string> _rows = [];

    public ScreenshotTimingViewModel(ICaptureStageTimeline? timeline, Action<Action>? post = null)
    {
        _timeline = timeline;
        _post = post ?? (action => action());
        if (timeline is not null)
        {
            timeline.Recorded += () => _post(Refresh);
        }

        Refresh();
    }

    public string Heading => SetupText.ScreenshotTimingHeading;

    public string Hint => SetupText.ScreenshotTimingHint;

    public IReadOnlyList<string> Rows
    {
        get => _rows;
        private set
        {
            if (SetProperty(ref _rows, value))
            {
                OnPropertyChanged(nameof(HasRows));
                OnPropertyChanged(nameof(Empty));
            }
        }
    }

    public bool HasRows => _rows.Count > 0;

    public string Empty => HasRows ? string.Empty : SetupText.ScreenshotTimingNoneYet;

    public void Refresh() => Rows = Describe(_timeline?.Statistics() ?? []);

    /// <summary>"Loot (12): settled 90 ms / 210 ms · decoded 0.4 s / 0.6 s · shown 1.1 s / 1.9 s".</summary>
    public static IReadOnlyList<string> Describe(IReadOnlyList<CaptureStageStatistic> statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        return
        [
            .. statistics
                .GroupBy(statistic => statistic.Kind, StringComparer.Ordinal)
                .Select(kind => SetupText.ScreenshotTimingRow(
                    kind.Key,
                    kind.Max(statistic => statistic.Count),
                    string.Join(" · ", kind.Select(statistic => SetupText.ScreenshotTimingMilestone(
                        statistic.Milestone,
                        LootScanStageText.Milliseconds(statistic.P50Milliseconds),
                        LootScanStageText.Milliseconds(statistic.P95Milliseconds)))))),
        ];
    }
}
