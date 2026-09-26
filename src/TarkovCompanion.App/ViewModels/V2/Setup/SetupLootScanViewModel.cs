using TarkovCompanion.App.Localization;
using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>One "return to map after" choice: Off or a number of seconds.</summary>
public sealed class LootAutoReturnChoiceViewModel(string label, int? seconds, bool isCurrent, Action choose)
{
    public string Label { get; } = label;

    public int? Seconds { get; } = seconds;

    public bool IsCurrent { get; } = isCurrent;

    public string AutomationId { get; } = $"v2-setup-loot-return-{(seconds is { } value ? value.ToString(CultureInfo.InvariantCulture) : "off")}";

    public ICommand ChooseCommand { get; } = new DelegateCommand(choose);
}

/// <summary>
/// Setup's loot-scan controls (#572): how long an automatic Loot result stays before the app
/// returns to the Raid map, and how long the last scan took, stage by stage.
/// </summary>
/// <remarks>
/// <see cref="LootAutoReturnPolicy"/> has had a configurable countdown since #580 and nothing
/// ever configured it: every player got fifteen seconds. The choice is remembered in the same
/// small layout store the Raid panel uses, and handed to the shell through
/// <see cref="TimeoutChanged"/>. The timing is <see cref="ICaptureStageTimeline.LastCompleted"/>,
/// the summary #586 kept "for a future Setup › Diagnostics control" and nothing read.
/// </remarks>
public sealed class SetupLootScanViewModel : BindableViewModel
{
    private static readonly int?[] Options = [null, 5, 10, 15, 30, 60];
    private readonly IWorkspaceLayoutStore? _layout;
    private readonly ICaptureStageTimeline? _timeline;
    private readonly Action<Action> _post;
    private TimeSpan? _timeout;
    private IReadOnlyList<LootAutoReturnChoiceViewModel> _choices = [];
    private CaptureStageSummary? _lastScan;
    private bool _tabletOnly;

    public SetupLootScanViewModel(
        IWorkspaceLayoutStore? layout,
        ICaptureStageTimeline? timeline,
        Action<Action>? post = null,
        TimeProvider? clock = null)
    {
        _layout = layout;
        _timeline = timeline;
        _post = post ?? (action => action());
        _timeout = Parse(layout?.Get(WorkspaceLayoutKeys.LootAutoReturnSeconds));
        _tabletOnly = string.Equals(layout?.Get(WorkspaceLayoutKeys.LootOnTabletOnly), "on", StringComparison.Ordinal);
        ToggleTabletOnlyCommand = new DelegateCommand(() => TabletOnly = !TabletOnly);
        Progress = new LootScanProgressViewModel(timeline, _post, clock);
        Screenshots = new ScreenshotTimingViewModel(timeline, _post);
        _lastScan = timeline?.LastCompleted;
        if (timeline is not null)
        {
            timeline.Progressed += OnProgressed;
        }

        RebuildChoices();
        if (layout is not null)
        {
            layout.Replaced += (_, _) => _post(ReadStored);
        }
    }

    /// <summary>Raised on the interface thread after the player picks a different countdown.</summary>
    public event Action<TimeSpan?>? TimeoutChanged;

    /// <summary>The Loot page's per-stage progress line, fed by the same timeline.</summary>
    public LootScanProgressViewModel Progress { get; }

    /// <summary>[#712 0-12] Every kind of screenshot's p50/p95, beside the last loot scan.</summary>
    public ScreenshotTimingViewModel Screenshots { get; }

    /// <summary>The remembered countdown; null means the timed return is off.</summary>
    public TimeSpan? Timeout => _timeout;

    public string ReturnHeading => SetupText.LootScanReturnHeading;

    public string ReturnHint => SetupText.LootScanReturnHint;

    public IReadOnlyList<LootAutoReturnChoiceViewModel> Choices
    {
        get => _choices;
        private set => SetProperty(ref _choices, value);
    }

    /// <summary>
    /// [#572] A Loot result goes to the paired tablet and the desktop stays on the Raid map. Only
    /// honoured while a tablet is paired, so a result never goes nowhere.
    /// </summary>
    public bool TabletOnly
    {
        get => _tabletOnly;
        set
        {
            if (SetProperty(ref _tabletOnly, value))
            {
                _layout?.Set(WorkspaceLayoutKeys.LootOnTabletOnly, value ? "on" : "off");
            }
        }
    }

    public ICommand ToggleTabletOnlyCommand { get; }

    public string TabletOnlyLabel => SetupText.LootScanTabletOnlyLabel;

    public string TabletOnlyHint => SetupText.LootScanTabletOnlyHint;

    public string LastScanHeading => SetupText.LootScanLastHeading;

    public bool HasLastScan => _lastScan is not null;

    public string LastScanSummary => _lastScan is { } scan
        ? SetupText.LootScanLastSummary(LootScanStageText.Seconds(scan.TotalMilliseconds), LocalTime.Time(scan.FileSeenUtc))
        : SetupText.LootScanNoneYet;

    public IReadOnlyList<string> LastScanStages => _lastScan is { } scan ? LootScanStageText.Rows(scan) : [];

    /// <summary>Reads a stored value: "off", or whole seconds clamped to the policy's range.</summary>
    public static TimeSpan? Parse(string? stored)
    {
        if (string.Equals(stored, "off", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, LootAutoReturnPolicy.MinimumSeconds, LootAutoReturnPolicy.MaximumSeconds))
            : TimeSpan.FromSeconds(LootAutoReturnPolicy.DefaultSeconds);
    }

    public void Choose(int? seconds)
    {
        var next = seconds is { } value ? TimeSpan.FromSeconds(value) : (TimeSpan?)null;
        if (next == _timeout)
        {
            return;
        }

        _timeout = next;
        _layout?.Set(
            WorkspaceLayoutKeys.LootAutoReturnSeconds,
            seconds is { } stored ? stored.ToString(CultureInfo.InvariantCulture) : "off");
        RebuildChoices();
        OnPropertyChanged(nameof(Timeout));
        TimeoutChanged?.Invoke(_timeout);
    }

    /// <summary>[#902] Backup &amp; reset replaced the layout: take what it holds now, as a restart would.</summary>
    private void ReadStored()
    {
        var timeout = Parse(_layout?.Get(WorkspaceLayoutKeys.LootAutoReturnSeconds));
        // The field, not the property: the property saves, and a reset must not write the default back.
        var tabletOnly = string.Equals(_layout?.Get(WorkspaceLayoutKeys.LootOnTabletOnly), "on", StringComparison.Ordinal);
        if (tabletOnly != _tabletOnly)
        {
            _tabletOnly = tabletOnly;
            OnPropertyChanged(nameof(TabletOnly));
        }

        if (timeout == _timeout)
        {
            return;
        }

        _timeout = timeout;
        RebuildChoices();
        OnPropertyChanged(nameof(Timeout));
        TimeoutChanged?.Invoke(_timeout);
    }

    private void RebuildChoices() =>
        Choices = [.. Options.Select(seconds => new LootAutoReturnChoiceViewModel(
            seconds is { } value ? SetupText.LootScanReturnSeconds(value) : SetupText.LootScanReturnOff,
            seconds,
            seconds is { } current ? _timeout == TimeSpan.FromSeconds(current) : _timeout is null,
            () => Choose(seconds)))];

    private void OnProgressed(CaptureStageStep progress)
    {
        // #712 0-12: every kind completes a timeline now; this card is the last Loot scan's.
        if (progress.Summary is not { IsLoot: true } summary)
        {
            return;
        }

        _post(() =>
        {
            _lastScan = summary;
            OnPropertyChanged(nameof(HasLastScan));
            OnPropertyChanged(nameof(LastScanSummary));
            OnPropertyChanged(nameof(LastScanStages));
        });
    }
}
