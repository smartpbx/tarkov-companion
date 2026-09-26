using System.Globalization;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

/// <summary>One session length the player can say they have.</summary>
public sealed record PlanSessionLengthOption(int Minutes, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One raid of the session strip: "1 · Customs", and the quests it moves.</summary>
public sealed record PlanSessionRaidViewModel(string Label, string Detail, bool IsNext)
{
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>
/// [#712 2-3] "I have two hours": the session strip under the next-raid suggestion, and the quests
/// that only need a trip to their trader.
/// </summary>
/// <remarks>
/// <see cref="SessionPlanner"/> makes the plan; this reads the inputs, the player's own board, raid
/// history and hideout, and re-plans whenever the board is read again. A raid ending re-reads the
/// board, which is how the plan follows each recap. The session length is one layout key,
/// registered in SettingsRegistry, so Backup &amp; reset covers it.
/// </remarks>
public sealed partial class PlanWorkspaceViewModel
{
    private IRaidHistoryService? _raidHistory;
    private IRequirementCatalog? _hideoutRequirements;
    private PlanSessionLengthOption _sessionLength = null!;
    private IReadOnlyList<PlanSessionRaidViewModel> _sessionRaids = [];
    private string _sessionSummary = string.Empty;
    private string _sessionProgress = string.Empty;
    private string _sessionEmpty = string.Empty;
    private IReadOnlyList<string> _handIns = [];
    private SessionProgress _sessionClock = SessionProgress.Fresh;
    private int _hideoutItemsNeeded;

    public IReadOnlyList<PlanSessionLengthOption> SessionLengths { get; } =
    [
        .. SessionPlanner.SessionLengths.Select(minutes => new PlanSessionLengthOption(
            minutes,
            PlanText.SessionHours((minutes / 60d).ToString("0.#", CultureInfo.CurrentCulture)))),
    ];

    private void AttachSession(IRaidHistoryService? raidHistory, IRaidEndSignal? raidEnds, IRequirementCatalog? hideoutRequirements)
    {
        _raidHistory = raidHistory;
        _hideoutRequirements = hideoutRequirements;
        _sessionLength = OptionFor(SessionPlanner.ParseSessionMinutes(LearnMode.Layout?.Get(WorkspaceLayoutKeys.PlanSessionMinutes)));
        if (LearnMode.Layout is { } layout)
        {
            layout.Replaced += (_, _) =>
            {
                _sessionLength = OptionFor(SessionPlanner.ParseSessionMinutes(layout.Get(WorkspaceLayoutKeys.PlanSessionMinutes)));
                OnPropertyChanged(nameof(SelectedSessionLength));
                Replan();
            };
        }

        if (raidEnds is not null)
        {
            // The recap's raid is in the history now, and the quests it moved are on the board soon
            // after; one refresh picks up both. Raised off the interface thread.
            raidEnds.RaidEnded += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                RefreshAsync(CancellationToken.None).Observe("plan", "re-plan the session after a raid"));
        }
    }

    private PlanSessionLengthOption OptionFor(int minutes) =>
        SessionLengths.FirstOrDefault(option => option.Minutes == minutes)
        ?? SessionLengths.First(option => option.Minutes == SessionPlanner.DefaultSessionMinutes);

    /// <summary>"You said 2 h": persisted, and the plan is re-made from it at once.</summary>
    public PlanSessionLengthOption SelectedSessionLength
    {
        get => _sessionLength;
        set
        {
            if (value is not null && SetProperty(ref _sessionLength, value))
            {
                LearnMode.Layout?.Set(WorkspaceLayoutKeys.PlanSessionMinutes, value.Minutes.ToString(CultureInfo.InvariantCulture));
                Replan();
            }
        }
    }

    public IReadOnlyList<PlanSessionRaidViewModel> SessionRaids
    {
        get => _sessionRaids;
        private set
        {
            if (SetProperty(ref _sessionRaids, value))
            {
                OnPropertyChanged(nameof(HasSessionRaids));
            }
        }
    }

    public bool HasSessionRaids => _sessionRaids.Count > 0;

    /// <summary>"Estimate · about 30 min a raid": how the slots were counted.</summary>
    public string SessionSummary { get => _sessionSummary; private set => SetProperty(ref _sessionSummary, value); }

    /// <summary>"2 raids played · 1 h 10 min left", once tonight's session has a raid in it.</summary>
    public string SessionProgressLabel
    {
        get => _sessionProgress;
        private set
        {
            if (SetProperty(ref _sessionProgress, value))
            {
                OnPropertyChanged(nameof(HasSessionProgress));
            }
        }
    }

    public bool HasSessionProgress => _sessionProgress.Length > 0;

    /// <summary>Why the strip has no raids, or empty.</summary>
    public string SessionEmpty
    {
        get => _sessionEmpty;
        private set
        {
            if (SetProperty(ref _sessionEmpty, value))
            {
                OnPropertyChanged(nameof(HasSessionEmpty));
            }
        }
    }

    public bool HasSessionEmpty => _sessionEmpty.Length > 0;

    /// <summary>"Golden Swag is ready to hand in to Skier", one line a quest.</summary>
    public IReadOnlyList<string> HandIns
    {
        get => _handIns;
        private set
        {
            if (SetProperty(ref _handIns, value))
            {
                OnPropertyChanged(nameof(HasHandIns));
            }
        }
    }

    public bool HasHandIns => _handIns.Count > 0;

    /// <summary>Reads the raid history and the hideout's shortfall, then re-plans. Called with each board read.</summary>
    private async Task RefreshSessionAsync(IReadOnlyDictionary<string, int> hideoutLevels, CancellationToken cancellationToken)
    {
        try
        {
            var now = _clock.GetUtcNow();
            _sessionClock = _raidHistory is null
                ? SessionProgress.Fresh
                : SessionPlanner.Measure(await _raidHistory.ListAsync(cancellationToken).ConfigureAwait(true), now);
            if (_hideoutRequirements is { } catalog)
            {
                var stations = await catalog.GetStationsAsync(cancellationToken).ConfigureAwait(true);
                var requirements = await catalog.GetHideoutRequirementsAsync(cancellationToken).ConfigureAwait(true);
                var plans = HideoutPlanner.Plan(stations, hideoutLevels, requirements, _ownedItems);
                _hideoutItemsNeeded = HideoutPlanner.Shortfall(plans, _ownedItems).Sum(shortfall => shortfall.Remaining);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The plan still stands on the board alone: a fresh session, no loot runs.
            _sessionClock = SessionProgress.Fresh;
            _hideoutItemsNeeded = 0;
            WorkspaceFault.Record("plan", "read the session's raids", exception.Message);
        }

        Replan();
    }

    private void Replan()
    {
        if (_board is not { } board)
        {
            SessionRaids = [];
            HandIns = [];
            SessionSummary = string.Empty;
            SessionProgressLabel = string.Empty;
            SessionEmpty = string.Empty;
            return;
        }

        var plan = SessionPlanner.Plan(
            board.Tasks,
            TimeSpan.FromMinutes(_sessionLength.Minutes),
            _sessionClock,
            NameOfMap,
            _activeEventRules,
            _hideoutItemsNeeded);
        SessionRaids =
        [
            .. plan.Raids.Select(raid => new PlanSessionRaidViewModel(
                PlanText.SessionRaid(raid.Number, raid.IsLootRun ? PlanText.SessionLootRun : raid.MapLabel),
                string.Join(", ", raid.QuestNames),
                raid.Number == 1)),
        ];
        var perRaid = UnitText.Duration(TimeSpan.FromMinutes(Math.Round(plan.PerRaid.TotalMinutes)));
        SessionSummary = plan.PerRaidMeasured ? PlanText.SessionPerRaidMeasured(perRaid) : PlanText.SessionPerRaid(perRaid);
        SessionProgressLabel = plan.RaidsPlayed > 0
            ? PlanText.SessionPlayed(plan.RaidsPlayed, UnitText.Duration(TimeSpan.FromMinutes(Math.Floor(plan.Remaining.TotalMinutes))))
            : string.Empty;
        SessionEmpty = plan.Raids.Count > 0 ? string.Empty
            : plan.Remaining < plan.PerRaid ? PlanText.SessionNoTime
            : PlanText.SessionEmpty;
        HandIns =
        [
            .. HandInReminders.Find(board.Tasks, _ownedItems).Select(reminder => reminder.Reason == HandInReason.Ready
                ? PlanText.HandInReady(reminder.TaskName, reminder.TraderLabel)
                : PlanText.HandInItemsHeld(reminder.TaskName, reminder.TraderLabel)),
        ];
    }
}
