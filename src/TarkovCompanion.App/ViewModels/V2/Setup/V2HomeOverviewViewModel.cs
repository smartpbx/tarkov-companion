using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Debrief;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.ViewModels.V2.Shell;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>How a status reads at a glance: done, wants the player, or simply not known.</summary>
public enum V2HomeTone
{
    Done = 1,
    Attention,
    Muted,
}

/// <summary>One readiness check drawn as a step of "Get ready".</summary>
/// <remarks>
/// It keeps the shell's readiness automation id and name (<c>v2-shell-readiness-&lt;id&gt;</c>,
/// "Open Game log folder. …") because the Windows page gallery drives Setup through exactly those.
/// </remarks>
public sealed class V2HomeStepViewModel(V2ReadinessCheck check, V2ReadinessCheckViewModel row, bool isLast)
{
    public string Label => row.Label;
    public string Detail => row.Detail;
    public V2HomeTone Tone => V2HomeOverviewViewModel.ToneOf(check.Status);
    public bool IsDone => Tone == V2HomeTone.Done;
    public bool IsAttention => Tone == V2HomeTone.Attention;
    public bool IsMuted => Tone == V2HomeTone.Muted;
    public bool HasConnector => !isLast;
    public string ActionLabel => V2ShellText.Get($"V2.Home.Step.{check.Status}");
    public string AutomationId => row.AutomationId;
    public string AutomationName => row.AutomationName;
    public ICommand OpenCommand => row.OpenCommand;
}

/// <summary>One line of System health.</summary>
public sealed record V2HomeHealthRowViewModel(string Label, string Status, V2HomeTone Tone)
{
    public bool IsDone => Tone == V2HomeTone.Done;
    public bool IsAttention => Tone == V2HomeTone.Attention;
    public bool IsMuted => Tone == V2HomeTone.Muted;
}

/// <summary>One line of a card list: what it is, and a quieter fact beside it.</summary>
public sealed record V2HomeLineViewModel(string Text, string Aside);

/// <summary>
/// V2 rough package 17 (home): the Setup overview, laid out like
/// <c>docs/design/v2/v2-home-setup-concept.png</c> — Get ready, the current map and plan, system
/// health, recent raids and privacy — over the view models those pages already own.
/// </summary>
/// <remarks>
/// It invents nothing. Every number is pushed in from the readiness summary, the Plan and Debrief
/// workspaces, the map picker and the Settings page; a card whose source has nothing yet says so
/// in one line instead of borrowing the concept's placeholder values.
/// </remarks>
public sealed class V2HomeOverviewViewModel : BindableViewModel
{
    private const int PlanLines = 4;
    private const int RaidLines = 3;

    private readonly Action<V2RouteId> _navigate;
    private readonly Action<V2SetupSection> _selectSection;
    private V2ReadinessSummary _readiness = new([]);
    private Action<V2ReadinessCheck> _openCheck = _ => { };
    private string _readinessSummary = string.Empty;
    private string? _mapName;
    private IReadOnlyList<(string Map, IReadOnlyList<V2HomeLineViewModel> Objectives)> _plan = [];
    private string _planScope = string.Empty;
    private string _planStatus = string.Empty;
    private IReadOnlyList<V2HomeLineViewModel> _raids = [];
    private string _raidStatus = string.Empty;
    private bool _tidiesScreenshots;
    private string _retention = string.Empty;
    private string _dataFreshness = string.Empty;

    public V2HomeOverviewViewModel(Action<V2RouteId> navigate, Action<V2SetupSection> selectSection)
    {
        _navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));
        _selectSection = selectSection ?? throw new ArgumentNullException(nameof(selectSection));
        ExploreMapCommand = new DelegateCommand(() => _navigate(V2Routes.Raid));
        OpenPlanCommand = new DelegateCommand(() => _navigate(V2Routes.Plan));
        OpenDebriefCommand = new DelegateCommand(() => _navigate(V2Routes.Debrief));
        ReviewPrivacyCommand = new DelegateCommand(() => _selectSection(V2SetupSection.Privacy));
        AllSettingsCommand = new DelegateCommand(() => _selectSection(V2SetupSection.GameProfile));
        PrimaryCommand = new DelegateCommand(Primary);
    }

    public string HeroTitle => V2ShellText.Get("V2.Home.Hero.Title");
    public string HeroSubtitle => V2ShellText.Get(IsSetUp ? "V2.Home.Hero.SubtitleReady" : "V2.Home.Hero.Subtitle");

    public string MapEyebrow => V2ShellText.Get("V2.Home.Map.Eyebrow");
    public string MapTitle => _mapName ?? V2ShellText.Get("V2.Home.Map.None");
    public string MapDetail => V2ShellText.Get("V2.Home.Map.Detail");
    public string ExploreMapLabel => _mapName is { } map
        ? V2ShellText.Format("V2.Home.Map.Explore", CultureInfo.CurrentCulture, map)
        : V2ShellText.Get("V2.Home.Map.OpenRaid");
    public ICommand ExploreMapCommand { get; }

    public string StepsHeading => V2ShellText.Get("V2.Home.Steps.Heading");
    /// <summary>The shell's own "1 of 5 checks ready · …" sentence; the page gallery matches it.</summary>
    public string StepsSummary => _readinessSummary;
    public IReadOnlyList<V2HomeStepViewModel> Steps { get; private set; } = [];

    /// <summary>Every required check is ready: nothing is left for Finish setup to open.</summary>
    public bool IsSetUp => _readiness.Checks.Count > 0 && _readiness.ReadyCount == _readiness.RequiredCount;
    public string PrimaryLabel => V2ShellText.Get(IsSetUp ? "V2.Home.Primary.Raid" : "V2.Home.Primary.Finish");
    public ICommand PrimaryCommand { get; }
    public string AllSettingsLabel => V2ShellText.Get("V2.Home.AllSettings");
    public ICommand AllSettingsCommand { get; }

    public string PlanHeading => V2ShellText.Get("V2.Home.Plan.Heading");
    public bool HasPlan => _plan.Count > 0;
    public bool HasNoPlan => !HasPlan;
    public string PlanTitle => CurrentPlan is { } plan
        ? V2ShellText.Format("V2.Home.Plan.Title", CultureInfo.CurrentCulture, plan.Map, plan.Objectives.Count)
        : string.Empty;
    public string PlanScope => _planScope;
    public IReadOnlyList<V2HomeLineViewModel> PlanObjectives => CurrentPlan?.Objectives.Take(PlanLines).ToArray() ?? [];
    /// <summary>The Plan page's own status line, except a failure, which it words for itself.</summary>
    public string PlanEmpty => string.IsNullOrWhiteSpace(_planStatus)
        ? V2ShellText.Get("V2.Home.Plan.Empty")
        : _planStatus.StartsWith("Unavailable", StringComparison.Ordinal) ? V2ShellText.Get("V2.Home.Plan.Unavailable") : _planStatus;
    public string OpenPlanLabel => V2ShellText.Get("V2.Home.Plan.Open");
    public ICommand OpenPlanCommand { get; }

    public string HealthHeading => V2ShellText.Get("V2.Home.Health.Heading");
    public string HealthReadyLabel => V2ShellText.Format("V2.Home.Health.Ready", CultureInfo.CurrentCulture, _readiness.ReadyCount);
    public string HealthAttentionLabel => _readiness.NeedsActionCount > 0
        ? V2ShellText.Format("V2.Home.Health.NeedsAction", CultureInfo.CurrentCulture, _readiness.NeedsActionCount)
        : _readiness.UnconfirmedCount > 0
            ? V2ShellText.Format("V2.Home.Health.Unconfirmed", CultureInfo.CurrentCulture, _readiness.UnconfirmedCount)
            : V2ShellText.Get("V2.Home.Health.Clear");
    public bool HealthNeedsAttention => _readiness.NeedsActionCount > 0;
    public IReadOnlyList<V2HomeHealthRowViewModel> HealthRows { get; private set; } = [];

    public string RecentHeading => V2ShellText.Get("V2.Home.Recent.Heading");
    public string DataFreshness => _dataFreshness;
    public IReadOnlyList<V2HomeLineViewModel> RecentRaids => _raids;
    public bool HasRecentRaids => _raids.Count > 0;
    public bool HasNoRecentRaids => !HasRecentRaids;
    public string RecentEmpty => V2ShellText.Get("V2.Home.Recent.Empty");
    public string RecentRaidsLabel => string.IsNullOrWhiteSpace(_raidStatus) ? V2ShellText.Get("V2.Home.Recent.Raids") : _raidStatus;
    public string OpenDebriefLabel => V2ShellText.Get("V2.Home.Recent.Open");
    public ICommand OpenDebriefCommand { get; }

    public string PrivacyHeading => V2ShellText.Get("V2.Home.Privacy.Heading");
    public string CleanupTitle => V2ShellText.Get(_tidiesScreenshots ? "V2.Home.Privacy.CleanupOn" : "V2.Home.Privacy.CleanupOff");
    public string CleanupDetail => _tidiesScreenshots
        ? V2ShellText.Format("V2.Home.Privacy.CleanupOnDetail", CultureInfo.CurrentCulture, _retention)
        : V2ShellText.Get("V2.Home.Privacy.CleanupOffDetail");
    public string TelemetryTitle => V2ShellText.Get("V2.Home.Privacy.Telemetry");
    public string TelemetryDetail => V2ShellText.Get("V2.Home.Privacy.TelemetryDetail");
    public string ReviewPrivacyLabel => V2ShellText.Get("V2.Home.Privacy.Review");
    public ICommand ReviewPrivacyCommand { get; }

    private (string Map, IReadOnlyList<V2HomeLineViewModel> Objectives)? CurrentPlan =>
        _plan.Count == 0
            ? null
            : _plan.FirstOrDefault(group => string.Equals(group.Map, _mapName, StringComparison.CurrentCultureIgnoreCase)) is { Map: not null } match
                ? match
                : _plan[0];

    internal static V2HomeTone ToneOf(V2CheckStatus status) => status switch
    {
        V2CheckStatus.Ready => V2HomeTone.Done,
        V2CheckStatus.NeedsAction or V2CheckStatus.Failed => V2HomeTone.Attention,
        _ => V2HomeTone.Muted,
    };

    /// <summary>The readiness checks, in their own order, and the shell's action for each one.</summary>
    public void ApplyReadiness(V2ReadinessSummary readiness, string summary, Action<V2ReadinessCheck> open)
    {
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _openCheck = open ?? throw new ArgumentNullException(nameof(open));
        _readinessSummary = summary ?? string.Empty;
        var checks = readiness.Checks;
        Steps = checks
            .Select((check, index) => new V2HomeStepViewModel(
                check,
                new V2ReadinessCheckViewModel(check, () => _openCheck(check)),
                index == checks.Count - 1))
            .ToArray();
        HealthRows = checks
            .Where(check => check.Required)
            .Select(check => new V2HomeHealthRowViewModel(check.Label, V2ShellText.Get($"V2.Home.Health.Status.{check.Status}"), ToneOf(check.Status)))
            .ToArray();
        Raise(
            nameof(Steps), nameof(StepsSummary), nameof(HealthRows), nameof(IsSetUp), nameof(HeroSubtitle),
            nameof(PrimaryLabel), nameof(HealthReadyLabel), nameof(HealthAttentionLabel), nameof(HealthNeedsAttention));
    }

    public void ApplyDataFreshness(string freshness)
    {
        if (!string.Equals(_dataFreshness, freshness, StringComparison.Ordinal))
        {
            _dataFreshness = freshness ?? string.Empty;
            OnPropertyChanged(nameof(DataFreshness));
        }
    }

    public void ApplyMap(string? mapName)
    {
        _mapName = string.IsNullOrWhiteSpace(mapName) ? null : mapName;
        Raise(nameof(MapTitle), nameof(ExploreMapLabel), nameof(PlanTitle), nameof(PlanObjectives));
    }

    /// <summary>The Plan workspace's groups: a map, and that map's objectives as short lines.</summary>
    public void ApplyPlan(
        IReadOnlyList<(string Map, IReadOnlyList<V2HomeLineViewModel> Objectives)> groups,
        string scope,
        string status)
    {
        _plan = groups ?? [];
        _planScope = scope ?? string.Empty;
        _planStatus = status ?? string.Empty;
        Raise(nameof(HasPlan), nameof(HasNoPlan), nameof(PlanTitle), nameof(PlanScope), nameof(PlanObjectives), nameof(PlanEmpty));
    }

    public void ApplyRaids(IReadOnlyList<V2HomeLineViewModel> raids, string status)
    {
        _raids = (raids ?? []).Take(RaidLines).ToArray();
        _raidStatus = status ?? string.Empty;
        Raise(nameof(RecentRaids), nameof(HasRecentRaids), nameof(HasNoRecentRaids), nameof(RecentRaidsLabel));
    }

    public void ApplyPrivacy(bool tidiesScreenshots, string retention)
    {
        _tidiesScreenshots = tidiesScreenshots;
        _retention = retention ?? string.Empty;
        Raise(nameof(CleanupTitle), nameof(CleanupDetail));
    }

    /// <summary>Follows the pages the overview summarises, so it stays current while it is showing.</summary>
    public void Attach(PlanWorkspaceViewModel? plan, DebriefWorkspaceViewModel? debrief, SettingsPageViewModel? settings, RaidCockpitViewModel? raid)
    {
        if (plan is not null)
        {
            void PushPlan() => ApplyPlan(
                plan.Groups
                    .Select(group => (group.MapLabel, (IReadOnlyList<V2HomeLineViewModel>)group.Objectives
                        .Select(objective => new V2HomeLineViewModel(objective.Description, objective.TaskName))
                        .ToArray()))
                    .ToArray(),
                plan.ScopeLabel,
                plan.Status);
            plan.PropertyChanged += (_, _) => PushPlan();
            PushPlan();
        }

        if (debrief is not null)
        {
            void PushRaids() => ApplyRaids(
                debrief.Raids
                    .Select(raid => new V2HomeLineViewModel(
                        V2ShellText.Format("V2.Home.Recent.Raid", CultureInfo.CurrentCulture, raid.MapLabel, raid.DurationLabel),
                        raid.StartedLabel))
                    .ToArray(),
                string.Empty);
            debrief.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(DebriefWorkspaceViewModel.Raids))
                {
                    PushRaids();
                }
            };
            PushRaids();
        }

        if (settings is not null)
        {
            void PushPrivacy() => ApplyPrivacy(settings.TidiesScreenshots, settings.RetentionDisplay);
            settings.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(SettingsPageViewModel.TidiesScreenshots) or nameof(SettingsPageViewModel.RetentionDisplay))
                {
                    PushPrivacy();
                }
            };
            PushPrivacy();
        }

        if (raid is not null)
        {
            raid.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(RaidCockpitViewModel.SelectedMap) or nameof(RaidCockpitViewModel.MapPicker))
                {
                    ApplyMap(raid.SelectedMap?.Name);
                }
            };
            ApplyMap(raid.SelectedMap?.Name);
        }
    }

    private void Primary()
    {
        var next = _readiness.Checks.FirstOrDefault(check => check.Required && ToneOf(check.Status) == V2HomeTone.Attention)
            ?? _readiness.Checks.FirstOrDefault(check => check.Required && check.Status != V2CheckStatus.Ready);
        if (next is null)
        {
            _navigate(V2Routes.Raid);
        }
        else
        {
            _openCheck(next);
        }
    }

    private void Raise(params string[] names)
    {
        foreach (var name in names)
        {
            OnPropertyChanged(name);
        }
    }
}
