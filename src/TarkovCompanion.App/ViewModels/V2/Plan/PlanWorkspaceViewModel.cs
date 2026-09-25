using TarkovCompanion.App.ViewModels.V2.Shell;
using System.Globalization;
using TarkovCompanion.App.Localization;
using System.Windows.Input;
using Avalonia.Threading;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.Quests;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Application.Services.Events;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Wiki;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

/// <summary>One objective, under whichever map group(s) it belongs to.</summary>
public sealed partial class PlanObjectiveRowViewModel : BindableViewModel
{
    private readonly PlanWorkspaceViewModel _owner;
    private bool? _hasMapPosition;
    private int _number;
    private bool _isLast;
    private string _routeReason = string.Empty;
    private string _routeDistanceLabel = string.Empty;

    internal PlanObjectiveRowViewModel(
        QuestSummaryReadModel task,
        QuestObjectiveReadModel objective,
        PlanWorkspaceViewModel owner,
        int number = 1,
        bool isLast = true)
    {
        Task = task;
        Objective = objective;
        _owner = owner;
        _number = number;
        _isLast = isLast;
    }

    internal QuestSummaryReadModel Task { get; }

    internal QuestObjectiveReadModel Objective { get; }

    /// <summary>1-based position within its map group: the numbered step the Plan list draws.</summary>
    /// <remarks>
    /// Settable because a filter pass that keeps this row may still move it: a search that drops the
    /// objective above it makes this one step 2 instead of step 3, which is a renumbering rather
    /// than a reason to build the row, its twelve commands and its group again.
    /// </remarks>
    public int Number
    {
        get => _number;
        internal set => SetProperty(ref _number, value);
    }

    /// <summary>The last step draws no connector line below its number.</summary>
    public bool IsLast
    {
        get => _isLast;
        internal set
        {
            if (SetProperty(ref _isLast, value))
            {
                OnPropertyChanged(nameof(HasNext));
            }
        }
    }

    public bool HasNext => !IsLast;

    public string TaskName => Task.Name;

    public string TraderLabel => string.IsNullOrWhiteSpace(Task.TraderId)
        ? PlanText.NoTrader
        : Task.TraderName ?? Task.TraderId;

    public string Description => Objective.Description;

    public bool IsUnsupported => Objective.IsUnsupported;

    /// <summary>Hand-in versus find-in-raid, kept distinct only where the catalog says which.</summary>
    public string HandlingLabel => PlanWorkspaceViewModel.DescribeHandling(Objective);

    public bool HasHandlingLabel => HandlingLabel.Length > 0;

    public string RemainingLabel => PlanWorkspaceViewModel.DescribeRemaining(Objective);

    /// <summary>
    /// False once the centre map is showing this objective's map and the projection had no
    /// position for it; null while that is not known yet, so no note flickers in before the map.
    /// </summary>
    public bool? HasMapPosition
    {
        get => _hasMapPosition;
        internal set
        {
            if (_hasMapPosition != value)
            {
                _hasMapPosition = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowsNoMapPosition));
                OnPropertyChanged(nameof(MetadataLabel));
                OnPropertyChanged(nameof(HasMetadataLabel));
            }
        }
    }

    public bool ShowsNoMapPosition => HasMapPosition == false;

    /// <summary>
    /// Whether this quest's recorded state is the game's own word rather than the player's.
    /// </summary>
    /// <remarks>
    /// Worth four words on the row. A player who has just handed a quest in wants to know the
    /// companion noticed by itself, and a player whose board is wrong wants to know whether he
    /// typed it or the log did.
    /// </remarks>
    public bool StateCameFromTheGame =>
        string.Equals(Task.ProgressSource, QuestProgressSources.GameLog, StringComparison.Ordinal);

    public string StateSourceLabel => StateCameFromTheGame ? PlanText.FromTheGame : string.Empty;

    /// <summary>A bare recorded state ("Unknown") says nothing on the page; only counts are shown.</summary>
    public bool HasRemainingLabel => Objective.TargetCount is not null || Objective.RecordedCount is not null;

    public bool HasWikiLink => WikiLinkPolicy.IsAllowed(Task.WikiUri);

    /// <summary>Whose page the link opens and where, for the link's tooltip.</summary>
    public string WikiAttribution => WikiLinkPolicy.Attribution;

    public bool CanShowOnMap => Objective.MapIds.Count == 1;

    // A row built without its page (Learn Mode tests, previews) has no planner state to read.
    public string StateLabel => _owner is null ? string.Empty : PlanQuestRules.StateLabel(_owner.StateFor(Task).State);

    public string StateDetail => _owner is null ? string.Empty : PlanQuestRules.StateDetail(_owner.StateFor(Task));

    public bool HasStateDetail => StateDetail.Length > 0;

    /// <summary>The facts behind a blocked, future or unknown state.</summary>
    public string StatusLabel => StateDetail;

    public bool HasStatusLabel => StatusLabel.Length > 0;

    public string RouteReason
    {
        get => _routeReason;
        private set
        {
            if (SetProperty(ref _routeReason, value))
            {
                OnPropertyChanged(nameof(HasRouteReason));
            }
        }
    }

    public bool HasRouteReason => RouteReason.Length > 0;

    public string RouteDistanceLabel
    {
        get => _routeDistanceLabel;
        private set => SetProperty(ref _routeDistanceLabel, value);
    }

    internal void ApplyRouteStep(ObjectiveRouteStep? step)
    {
        RouteReason = step is null ? string.Empty : PlanText.ObjectiveRouteReason(step.Reason);
        RouteDistanceLabel = step is null
            ? string.Empty
            : PlanText.Metres(step.LegDistanceMetres);
    }

    /// <summary>The engine's compact reason, shown only while the shared Learn Mode switch is on.</summary>
    public string LearnReason => StatusLabel.Length > 0
        ? StatusLabel
        : HasHandlingLabel
            ? PlanText.LearnNeededFor(HandlingLabel, TaskName)
            : PlanText.LearnAdvances(TaskName);

    /// <summary>The row's compact secondary facts, without constructing a hidden control for each possible fact.</summary>
    public string MetadataLabel => string.Join(" · ", new[]
    {
        IsOptional ? PlanText.Optional : string.Empty,
        StatusLabel,
        StateSourceLabel,
        HandlingLabel,
        RemainingLabel,
        ShowsNoMapPosition ? PlanText.NoMapPosition : string.Empty,
        IsUnsupported ? PlanText.UnsupportedObjective : string.Empty,
    }.Where(value => value.Length > 0));

    public bool HasMetadataLabel => MetadataLabel.Length > 0;

    /// <summary>Not yet started, or failed and started over.</summary>
    public bool CanStartQuest => Task.RecordedState is RecordedTaskState.Unknown or RecordedTaskState.NotStarted or RecordedTaskState.Failed;

    public bool CanFailQuest => Task.RecordedState == RecordedTaskState.Active;

    public bool CanResetQuest => Task.RecordedState is RecordedTaskState.Active or RecordedTaskState.Completed or RecordedTaskState.Failed;

    public bool CanMarkQuestDone => Task.RecordedState != RecordedTaskState.Completed;

    public string PinQuestLabel => Task.IsPinned ? PlanText.UnpinQuest : PlanText.PinQuest;

    public string PinObjectiveLabel => Objective.IsPinned ? PlanText.UnpinObjective : PlanText.PinObjective;

    /// <summary>Only an objective with a target has a count to step.</summary>
    public bool CanChangeCount => Objective.TargetCount is not null;

    public bool IsOptional => Objective.IsOptional == true;

    public bool CanResetObjective => Objective.RecordedState != RecordedObjectiveState.Unknown;

    private ICommand? _openWikiCommand;
    private ICommand? _markObjectiveDoneCommand;
    private ICommand? _markQuestDoneCommand;
    private ICommand? _showOnMapCommand;
    private ICommand? _startQuestCommand;
    private ICommand? _failQuestCommand;
    private ICommand? _resetQuestCommand;
    private ICommand? _togglePinQuestCommand;
    private ICommand? _togglePinObjectiveCommand;
    private ICommand? _incrementCountCommand;
    private ICommand? _decrementCountCommand;
    private ICommand? _resetObjectiveCommand;

    public ICommand OpenWikiCommand => _openWikiCommand ??= new DelegateCommand(() => _owner.TryOpenWiki(Task.WikiUri));

    public ICommand MarkObjectiveDoneCommand => _markObjectiveDoneCommand ??= new AsyncDelegateCommand(
        () => _owner.MarkObjectiveDoneAsync(Objective.ObjectiveId, Objective.TargetCount ?? Objective.RecordedCount));

    public ICommand MarkQuestDoneCommand => _markQuestDoneCommand ??= new AsyncDelegateCommand(() => _owner.MarkQuestDoneAsync(Task.TaskId));

    public ICommand ShowOnMapCommand => _showOnMapCommand ??= new AsyncDelegateCommand(() => _owner.ShowOnMapAsync(Objective.MapIds[0], Objective.ObjectiveId));

    public ICommand StartQuestCommand => _startQuestCommand ??= new AsyncDelegateCommand(
        () => _owner.SetQuestStateAsync(Task.TaskId, RecordedTaskState.Active));

    public ICommand FailQuestCommand => _failQuestCommand ??= new AsyncDelegateCommand(
        () => _owner.SetQuestStateAsync(Task.TaskId, RecordedTaskState.Failed));

    public ICommand ResetQuestCommand => _resetQuestCommand ??= new AsyncDelegateCommand(
        () => _owner.SetQuestStateAsync(Task.TaskId, RecordedTaskState.NotStarted));

    public ICommand TogglePinQuestCommand => _togglePinQuestCommand ??= new AsyncDelegateCommand(
        () => _owner.TogglePinAsync(QuestPinTargetKind.Task, Task.TaskId, Task.IsPinned));

    public ICommand TogglePinObjectiveCommand => _togglePinObjectiveCommand ??= new AsyncDelegateCommand(
        () => _owner.TogglePinAsync(QuestPinTargetKind.Objective, Objective.ObjectiveId, Objective.IsPinned));

    public ICommand IncrementCountCommand => _incrementCountCommand ??= new AsyncDelegateCommand(
        () => _owner.SetObjectiveCountAsync(Objective, 1));

    public ICommand DecrementCountCommand => _decrementCountCommand ??= new AsyncDelegateCommand(
        () => _owner.SetObjectiveCountAsync(Objective, -1));

    public ICommand ResetObjectiveCommand => _resetObjectiveCommand ??= new AsyncDelegateCommand(
        () => _owner.SetObjectiveStateAsync(Objective, RecordedObjectiveState.Unknown));
}

/// <summary>One quest with objectives in a map group, for the context panel's quest list.</summary>
public sealed record PlanQuestSummaryViewModel(string Name, string TraderLabel, string ObjectivesLabel);

/// <summary>
/// Every objective on one map (or "Any map" for one that names none): one "bundle" card in the
/// Plan workspace's left column, and the whole centre and context panel while it is selected.
/// </summary>
public sealed class PlanMapGroupViewModel : BindableViewModel
{
    private bool _isSelected;
    private bool _isAvailable = true;
    private string _availabilityLabel = string.Empty;
    private IReadOnlyList<PlanObjectiveRowViewModel> _visitOrder;
    private ObjectiveRouteBundle? _route;
    private string _routeHint = PlanText.RouteNeedsOrigin;
    private string? _routeSignature;
    private int _unroutedObjectiveCount;

    internal PlanMapGroupViewModel(
        string? mapId,
        string mapLabel,
        IReadOnlyList<PlanObjectiveRowViewModel> objectives,
        Action<PlanMapGroupViewModel>? select = null,
        Func<string, Task>? openInRaid = null)
    {
        MapId = mapId;
        MapLabel = mapLabel;
        Objectives = objectives;
        _visitOrder = objectives;
        Quests = objectives
            .GroupBy(row => row.Task.TaskId, StringComparer.Ordinal)
            .Select(group => new PlanQuestSummaryViewModel(
                group.First().TaskName,
                group.First().TraderLabel,
                PlanText.ObjectiveCount(group.Count())))
            .ToArray();
        FindInRaidCount = objectives.Count(row => row.Objective.FoundInRaidRequired == true);
        HandInCount = objectives.Count(row => row.Objective.FoundInRaidRequired == false);
        SelectCommand = new DelegateCommand(() => select?.Invoke(this));
        OpenInRaidCommand = new AsyncDelegateCommand(() => mapId is null || openInRaid is null
            ? System.Threading.Tasks.Task.CompletedTask
            : openInRaid(mapId));
    }

    /// <summary>The catalog map id, or null for the "Any map" group.</summary>
    public string? MapId { get; }

    public string MapLabel { get; }

    public IReadOnlyList<PlanObjectiveRowViewModel> Objectives { get; }

    /// <summary>The objective rows in their planned visiting order, with unplaced rows last.</summary>
    public IReadOnlyList<PlanObjectiveRowViewModel> VisitOrder => _visitOrder;

    public IReadOnlyList<PlanQuestSummaryViewModel> Quests { get; }

    /// <summary>"4 objectives · 3 quests", the bundle card's second line.</summary>
    public string Summary => $"{PlanText.ObjectiveCount(Objectives.Count)} · {PlanText.QuestCount(Quests.Count)}";

    /// <summary>Objectives whose items must be found in raid, where the catalog says so.</summary>
    public int FindInRaidCount { get; }

    /// <summary>Objectives whose items may be bought and handed in, where the catalog says so.</summary>
    public int HandInCount { get; }

    public bool HasItemObjectives => FindInRaidCount + HandInCount > 0;

    /// <summary>"Objectives (4)", the context panel's list heading.</summary>
    public string ObjectivesHeading => PlanText.ObjectivesHeading(Objectives.Count);

    public ObjectiveRouteBundle? Route => _route;

    public bool HasRoute => Route is { Steps.Count: > 0 };

    public string RouteSummary => Route is { } route
        ? string.Join(" · ", new[]
        {
            PlanText.Stops(route.Steps.Count),
            PlanText.RouteFrom(route.TotalDistanceMetres, route.StartLabel),
            _unroutedObjectiveCount > 0
                ? PlanText.WithoutExactPosition(_unroutedObjectiveCount)
                : string.Empty,
            // #307: which rules ordered it, as a saved loot scan names its ruleset.
            route.PlannerVersion.Length > 0 ? PlannerVersions.Label(route.PlannerVersion) : string.Empty,
        }.Where(value => value.Length > 0))
        : string.Empty;

    public string RouteHint
    {
        get => _routeHint;
        private set => SetProperty(ref _routeHint, value);
    }

    public bool HasRouteHint => !HasRoute && RouteHint.Length > 0;

    public string RouteCaveat => PlanText.RouteCaveat;

    /// <summary>Only a real map can be opened on the Raid map.</summary>
    public bool CanOpenInRaid => MapId is not null;

    public bool IsAvailable
    {
        get => _isAvailable;
        private set => SetProperty(ref _isAvailable, value);
    }

    public string AvailabilityLabel
    {
        get => _availabilityLabel;
        private set => SetProperty(ref _availabilityLabel, value);
    }

    public bool HasAvailabilityLabel => AvailabilityLabel.Length > 0;

    internal void ApplyEventRules(ActiveEventRules rules)
    {
        var closures = MapId is null
            ? []
            : rules.Rules
                .Where(rule => rule.Effect is MapAvailabilityRule { Available: false } map &&
                               string.Equals(map.MapId, MapId, StringComparison.OrdinalIgnoreCase))
                .Select(rule => rule.EventName)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        IsAvailable = closures.Length == 0;
        AvailabilityLabel = IsAvailable ? string.Empty : PlanText.Closed(string.Join(", ", closures));
        OnPropertyChanged(nameof(HasAvailabilityLabel));
    }

    /// <summary>How many objective rows a map shows before the player asks for the rest.</summary>
    internal const int ObjectivePageSize = 4;

    private int _visibleObjectiveLimit = ObjectivePageSize;
    private bool _isSuggested;
    private ICommand? _showMoreObjectives;

    /// <summary>How many rows are added to the drawn list per dispatcher turn.</summary>
    internal const int ObjectiveChunkSize = 1;

    private ReconciledList<PlanObjectiveRowViewModel>? _visible;
    private bool _growthPosted;

    /// <summary>
    /// The rows the list draws. Each row is a card with chips, two buttons and a twelve-entry
    /// menu, and "All" on Customs is 150 of them: drawn at once they held the interface thread
    /// for over a second. The first page fits the panel; each press adds one bounded page.
    /// </summary>
    /// <remarks>
    /// [#453] Filled a few rows per dispatcher turn rather than every objective at once. All can
    /// put 149 cards on one map; the first screen is drawn now and later pages stay bounded too.
    /// The list is appended to rather than replaced, so the rows already drawn stay drawn.
    /// </remarks>
    public IReadOnlyList<PlanObjectiveRowViewModel> VisibleObjectives
    {
        get
        {
            if (_visible is null)
            {
                _visible = new();
                Grow();
            }

            return _visible;
        }
    }

    private int VisibleTarget => Math.Min(VisitOrder.Count, _visibleObjectiveLimit);

    private void Grow()
    {
        _growthPosted = false;
        if (_visible is null)
        {
            return;
        }

        var paceOnInterfaceThread = UiThreadPost.BehindInput() is not null;
        var target = VisibleTarget;
        var until = paceOnInterfaceThread ? Math.Min(target, _visible.Count + ObjectiveChunkSize) : target;
        for (var index = _visible.Count; index < until; index++)
        {
            _visible.Add(VisitOrder[index]);
        }

        if (_visible.Count < target && paceOnInterfaceThread && !_growthPosted)
        {
            _growthPosted = true;
            DispatcherTimer.RunOnce(Grow, TimeSpan.FromMilliseconds(50), DispatcherPriority.Background);
        }
    }

    public bool HasMoreObjectives => _visibleObjectiveLimit < Objectives.Count;

    public string MoreObjectivesLabel => PlanText.ShowMore(Math.Min(ObjectivePageSize, Objectives.Count - _visibleObjectiveLimit));

    public ICommand ShowMoreObjectivesCommand => _showMoreObjectives ??= new DelegateCommand(() =>
    {
        _visibleObjectiveLimit = Math.Min(Objectives.Count, _visibleObjectiveLimit + ObjectivePageSize);
        Grow();
        OnPropertyChanged(nameof(HasMoreObjectives));
        OnPropertyChanged(nameof(MoreObjectivesLabel));
    });

    /// <summary>The map <see cref="NextRaidPlanner"/> picks: one raid there moves the most quests.</summary>
    public bool IsSuggested
    {
        get => _isSuggested;
        internal set => SetProperty(ref _isSuggested, value);
    }

    private IReadOnlyList<PlanRequirementRowViewModel> _requirements = [];
    private IReadOnlyList<PlanRequirementRowViewModel> _visibleRequirements = [];
    private int _visibleRequirementLimit = RequirementPageSize;
    private ICommand? _showMoreRequirements;

    internal const int RequirementPageSize = 5;

    /// <summary>What this map's objectives ask the player to bring, hand in or find, against what they hold.</summary>
    public IReadOnlyList<PlanRequirementRowViewModel> Requirements
    {
        get => _requirements;
        internal set
        {
            if (SetProperty(ref _requirements, value))
            {
                _visibleRequirementLimit = RequirementPageSize;
                VisibleRequirements = RequirementPage(value, _visibleRequirementLimit);
                OnPropertyChanged(nameof(HasRequirements));
                OnPropertyChanged(nameof(StillNeededCount));
                OnPropertyChanged(nameof(RequirementsSummary));
                OnPropertyChanged(nameof(RequirementsReady));
                OnPropertyChanged(nameof(HasMoreRequirements));
                OnPropertyChanged(nameof(MoreRequirementsLabel));
            }
        }
    }

    public IReadOnlyList<PlanRequirementRowViewModel> VisibleRequirements
    {
        get => _visibleRequirements;
        private set => SetProperty(ref _visibleRequirements, value);
    }

    public bool HasMoreRequirements => _visibleRequirementLimit < Requirements.Count;

    public string MoreRequirementsLabel => PlanText.ShowMore(Math.Min(RequirementPageSize, Requirements.Count - _visibleRequirementLimit));

    public ICommand ShowMoreRequirementsCommand => _showMoreRequirements ??= new DelegateCommand(() =>
    {
        _visibleRequirementLimit = Math.Min(Requirements.Count, _visibleRequirementLimit + RequirementPageSize);
        VisibleRequirements = RequirementPage(Requirements, _visibleRequirementLimit);
        OnPropertyChanged(nameof(HasMoreRequirements));
        OnPropertyChanged(nameof(MoreRequirementsLabel));
    });

    internal static IReadOnlyList<T> RequirementPage<T>(IReadOnlyList<T> requirements, int count) =>
        requirements.Count <= count ? requirements : [.. requirements.Take(count)];

    public bool HasRequirements => Requirements.Count > 0;

    /// <summary>
    /// Whether this group's requirement rows have been worked out yet, so a filter pass that kept
    /// the group does not work them out again.
    /// </summary>
    internal bool RequirementsBuilt { get; set; }

    public int StillNeededCount => Requirements.Count(row => !row.IsSatisfied);

    public bool RequirementsReady => HasRequirements && StillNeededCount == 0;

    /// <summary>"All ready", "2 still needed", "3 to check", or empty where the objectives ask for no item.</summary>
    public string RequirementsSummary => !HasRequirements
        ? string.Empty
        : RequirementsReady
            ? PlanText.AllReady
            : PlanQuestRules.SummariseUnmet(Requirements.Where(row => !row.IsSatisfied));

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public ICommand SelectCommand { get; }

    public ICommand OpenInRaidCommand { get; }

    internal bool ApplyRoute(ObjectiveRouteBundle? route, string hint, int unroutedObjectiveCount = 0)
    {
        var signature = route is null
            ? $"none:{hint}"
            : $"{route.StartLabel}|{route.TotalDistanceMetres:R}|{unroutedObjectiveCount}|{string.Join(',', route.Steps.Select(step => step.ObjectiveId))}";
        if (signature == _routeSignature)
        {
            return false;
        }

        _routeSignature = signature;
        _route = route;
        _unroutedObjectiveCount = unroutedObjectiveCount;
        RouteHint = hint;
        var byId = route?.Steps.ToDictionary(step => step.ObjectiveId, StringComparer.Ordinal) ??
            new Dictionary<string, ObjectiveRouteStep>(StringComparer.Ordinal);
        _visitOrder =
        [
            .. Objectives
                .OrderBy(row => byId.TryGetValue(row.Objective.ObjectiveId, out var step) ? step.Number : int.MaxValue)
                .ThenBy(row => row.Number),
        ];
        for (var index = 0; index < _visitOrder.Count; index++)
        {
            var row = _visitOrder[index];
            row.Number = index + 1;
            row.IsLast = index == _visitOrder.Count - 1;
            row.ApplyRouteStep(byId.GetValueOrDefault(row.Objective.ObjectiveId));
        }

        _visible?.Clear();
        Grow();
        OnPropertyChanged(nameof(VisitOrder));
        OnPropertyChanged(nameof(Route));
        OnPropertyChanged(nameof(HasRoute));
        OnPropertyChanged(nameof(RouteSummary));
        OnPropertyChanged(nameof(HasRouteHint));
        return true;
    }
}

/// <summary>
/// Native V2 Plan workspace over the existing quest tracking services: active quests and their
/// unfinished objectives by default, with a toggle for everything else. Replaces the legacy
/// Quests passthrough on the Plan route.
/// </summary>
/// <remarks>
/// No new planning engine (#307 is out of scope for this pass). This reads and mutates through
/// the same <see cref="IQuestReadService"/> and <see cref="IQuestProgressCommandService"/> the V1
/// Quests page already uses, and reuses the shared <see cref="MapViewModel"/> singleton to name
/// maps and to move the Raid cockpit to one when asked.
/// </remarks>
public sealed partial class PlanWorkspaceViewModel : BindableViewModel
{
    private const string AnyMapKey = "";
    internal const int GroupPageSize = 5;

    private readonly IPlayerProfileService _profileService;
    private readonly IQuestReadService _readService;
    private readonly IQuestProgressCommandService _commandService;
    private readonly MapViewModel _map;
    private readonly IWikiLinkOpener _wikiOpener;
    private readonly IMapDataService? _mapData;
    private Dictionary<string, string> _mapNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly RaidCockpitViewModel? _raidCockpit;
    private readonly IRuntimeStateStore? _runtime;
    private readonly QuestMapProjectionService? _projection;
    private readonly Dictionary<string, IReadOnlyList<QuestMapObjectiveProjection>> _projected = new(StringComparer.OrdinalIgnoreCase);
    private string? _projectingMapId;
    private MapSceneRendererViewModel? _mapPreview;
    private string? _mapPreviewSignature;
    private string _mapNote = string.Empty;
    private QuestBoardReadModel? _board;
    private readonly AllergyWarningService? _allergies;
    private IReadOnlyDictionary<string, string> _allergyWarnings = new Dictionary<string, string>(StringComparer.Ordinal);
    private (QuestBoardReadModel Board, Dictionary<string, string> Names)? _taskNames;
    private QuestProfileScope? _scope;
    private string _status = PlanText.LoadingBoard;
    private PageLoadState _loadState = PageLoadState.Loading;
    private string _scopeLabel = PlanText.NoProfileLoaded;
    private IReadOnlyList<PlanMapGroupViewModel> _groups = [];
    private readonly ReconciledList<PlanMapGroupViewModel> _visibleGroups = new();
    private int _visibleGroupTarget;
    private bool _groupGrowthPosted;
    private int _visibleGroupLimit = GroupPageSize;
    private ICommand? _showMoreGroups;
    private PlanMapGroupViewModel? _selectedGroup;
    private PlanMapGroupViewModel? _presentedSelectedGroup;
    private int _selectionPresentationVersion;
    private readonly IItemRepository? _itemRepository;
    private readonly AppDataPaths? _paths;
    private readonly TimeProvider _clock;
    private string _exportStatus = string.Empty;
    private readonly Dictionary<string, string> _itemNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missingItems = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, int> _ownedItems = new Dictionary<string, int>(StringComparer.Ordinal);
    private static readonly PlanTraderOption AllTraders = new(null, PlanText.AllTraders);
    private PlanQuestFilter _filter = PlanQuestFilter.Active;
    private IReadOnlyList<PlanFilterChipViewModel>? _filterChips;
    private string _searchText = string.Empty;
    private IReadOnlyList<PlanTraderOption> _traders = [AllTraders];
    private PlanTraderOption _selectedTrader = AllTraders;
    private IReadOnlyList<PlanTraderLoyaltyViewModel> _traderLoyalty = [];
    private int _playerLevel = QuestsPageViewModel.MinimumLevel;
    private string _rollup = string.Empty;
    // [V2 rough package 45] What the search reads, joined when the board is read instead of when a
    // key is pressed, and the gate that keeps the filter off the keystroke's own stack.
    private PlanSearchIndex _searchIndex = PlanSearchIndex.Empty;
    private readonly Func<QuestSummaryReadModel, string> _searchableText;
    private readonly DeferredDispatch _filterRequest;
    // Held rather than made per pass: a filter pass allocating two closures per keystroke is the
    // kind of thing this package exists to stop doing.
    private readonly Action<PlanMapGroupViewModel> _selectGroup;
    private readonly Func<string, Task> _openInRaid;
    private readonly QuestLogProgressService? _questLog;
    private string _gameLogStatus = string.Empty;
    private readonly EventRuleService? _eventRuleService;
    private ActiveEventRules _activeEventRules = ActiveEventRules.Empty;
    private string _eventRuleSummary = string.Empty;
    private IReadOnlyDictionary<string, QuestStatePlan> _questStates =
        new Dictionary<string, QuestStatePlan>(StringComparer.Ordinal);

    public PlanWorkspaceViewModel(
        IPlayerProfileService profileService,
        IQuestReadService readService,
        IQuestProgressCommandService commandService,
        MapViewModel map,
        IWikiLinkOpener wikiOpener,
        // V2 rough package 17: quest objectives name maps by game-data id, which the map
        // catalog behind MapViewModel does not key on, so the groups showed raw ids. Optional so
        // a composition without the synced map table still builds this.
        IMapDataService? mapData = null,
        // Package 17: the centre map is a second scene built by the Raid cockpit (see
        // RaidCockpitViewModel.CreateObjectivePreview); the runtime store keeps Plan from moving
        // the shared map away from a raid in progress. Both optional, like mapData.
        RaidCockpitViewModel? raidCockpit = null,
        IRuntimeStateStore? runtime = null,
        QuestMapProjectionService? projection = null,
        // Package 28: names the items a map's objectives ask for. Optional like the rest, so a
        // composition without the item catalog still plans, with the ids as the names.
        IItemRepository? itemRepository = null,
        // Package 47: what the game's logs have said about quests this session. The board used to
        // refresh only after a mutation it had made itself, so a quest handed in while the player
        // was looking at this page did not appear until the page was left and come back to -- and
        // a session in which nothing was read said nothing at all.
        QuestLogProgressService? questLog = null,
        // [V2 rough package 61 — plan export] #288/#315: where an exported plan is written and
        // what clock stamps it. Optional like the rest, so a composition without app paths still
        // plans; without them Export copies to the clipboard and writes no file.
        AppDataPaths? paths = null,
        TimeProvider? clock = null,
        // #285: which foods and medicines the Events page records an allergy to.
        AllergyWarningService? allergies = null,
        EventRuleService? eventRuleService = null,
        LearnModeSetting? learnMode = null,
        // [#780] "Squad has it too" on a quest a squadmate also has active. Optional like the rest.
        TarkovCompanion.App.ViewModels.V2.Team.SquadQuestFeed? squadQuests = null)
    {
        LearnMode = learnMode ?? new();
        AttachSquadQuests(squadQuests);
        _allergies = allergies;
        _eventRuleService = eventRuleService;
        _paths = paths;
        _clock = clock ?? TimeProvider.System;
        _itemRepository = itemRepository;
        _searchableText = SearchableText;
        _selectGroup = group => SelectedGroup = group;
        _openInRaid = OpenInRaidAsync;
        // Package 45: typing must not wait on the filter. Posted behind queued input rather than
        // through Avalonia's own context, which posts above it: see UiThreadPost.
        _filterRequest = DeferredDispatch.Posting(UiThreadPost.BehindInput(), ApplyFilter);
        _questLog = questLog;
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _readService = readService ?? throw new ArgumentNullException(nameof(readService));
        _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _wikiOpener = wikiOpener ?? throw new ArgumentNullException(nameof(wikiOpener));
        _mapData = mapData;
        _raidCockpit = raidCockpit;
        _runtime = runtime;
        _projection = projection;
        if (_raidCockpit is not null)
        {
            _raidCockpit.SceneRebuilt += (_, _) => RefreshMapPreview();
        }

        if (_questLog is not null)
        {
            _questLog.Changed += OnQuestLogChanged;
            UpdateGameLogStatus(_questLog.Reading);
        }
        RefreshCommand = new AsyncDelegateCommand(RefreshAsync);
        ExportCommand = new AsyncDelegateCommand(() => ExportAsync(CancellationToken.None));
        OpenHideoutCommand = new DelegateCommand(() => OpenHideoutRequested?.Invoke(this, EventArgs.Empty));
        // The map catalog usually finishes loading after the first quest board read; the groups
        // are named from it, so rebuild them when it arrives rather than showing catalog ids.
        _map.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MapViewModel.Locations))
            {
                // The names the search reads come from here, so the index is stale until they land.
                RebuildSearchIndex();
                _filterRequest.Request();
            }
            else if (e.PropertyName == nameof(MapViewModel.RenderModel))
            {
                RefreshMapPreview();
            }
        };
    }

    public LearnModeSetting LearnMode { get; }

    /// <summary>Raised after this workspace has moved the shared map to the requested one.</summary>
    public event EventHandler<EventArgs>? ShowOnMapRequested;

    /// <summary>Raised when the Hideout card asks the shell for the Plan route's Hideout tab.</summary>
    public event EventHandler<EventArgs>? OpenHideoutRequested;

    public IReadOnlyList<PlanMapGroupViewModel> Groups
    {
        get => _groups;
        private set
        {
            if (SetProperty(ref _groups, value))
            {
                _visibleGroupLimit = GroupPageSize;
                _visibleGroupTarget = Math.Min(value.Count, _visibleGroupLimit);
                _visibleGroups.Clear();
                GrowVisibleGroups();
                OnPropertyChanged(nameof(HasGroups));
                OnPropertyChanged(nameof(ShowsNothingPlanned));
                OnPropertyChanged(nameof(HasMoreGroups));
                OnPropertyChanged(nameof(MoreGroupsLabel));
            }
        }
    }

    public bool HasGroups => Groups.Count > 0;

    /// <summary>#872: loading, empty, loaded or failed — the view shows one message for one state.</summary>
    public PageLoadState LoadState
    {
        get => _loadState;
        private set
        {
            if (SetProperty(ref _loadState, value))
            {
                OnPropertyChanged(nameof(IsLoading));
                OnPropertyChanged(nameof(ShowsNothingPlanned));
            }
        }
    }

    public bool IsLoading => _loadState == PageLoadState.Loading;

    /// <summary>"Nothing planned yet", only once the board has been read: not while it loads, not when it failed.</summary>
    public bool ShowsNothingPlanned => !HasGroups && _loadState.HasRead();

    public string EventRuleSummary
    {
        get => _eventRuleSummary;
        private set
        {
            if (SetProperty(ref _eventRuleSummary, value))
            {
                OnPropertyChanged(nameof(HasEventRuleSummary));
            }
        }
    }

    public bool HasEventRuleSummary => EventRuleSummary.Length > 0;

    public IReadOnlyList<PlanMapGroupViewModel> VisibleGroups => _visibleGroups;

    public bool HasMoreGroups => _visibleGroupLimit < Groups.Count;

    public string MoreGroupsLabel => PlanText.ShowMoreMaps(Math.Min(GroupPageSize, Groups.Count - _visibleGroupLimit));

    public ICommand ShowMoreGroupsCommand => _showMoreGroups ??= new DelegateCommand(() =>
    {
        _visibleGroupLimit = Math.Min(Groups.Count, _visibleGroupLimit + GroupPageSize);
        _visibleGroupTarget = _visibleGroupLimit;
        GrowVisibleGroups();
        OnPropertyChanged(nameof(HasMoreGroups));
        OnPropertyChanged(nameof(MoreGroupsLabel));
    });

    private void GrowVisibleGroups()
    {
        _groupGrowthPosted = false;
        if (_visibleGroups.Count < _visibleGroupTarget)
        {
            _visibleGroups.Add(Groups[_visibleGroups.Count]);
        }

        if (_visibleGroups.Count < _visibleGroupTarget && !_groupGrowthPosted)
        {
            _groupGrowthPosted = true;
            DispatcherTimer.RunOnce(GrowVisibleGroups, TimeSpan.FromMilliseconds(75), DispatcherPriority.Background);
        }
    }

    internal static IReadOnlyList<PlanMapGroupViewModel> GroupPage(
        IReadOnlyList<PlanMapGroupViewModel> groups,
        int count) => groups.Count <= count ? groups : [.. groups.Take(count)];


    /// <summary>The map bundle the centre list and context panel show; the first one by default.</summary>
    public PlanMapGroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        private set
        {
            if (ReferenceEquals(_selectedGroup, value))
            {
                return;
            }

            if (_selectedGroup is not null)
            {
                _selectedGroup.IsSelected = false;
            }

            _selectedGroup = value;
            if (value is not null)
            {
                value.IsSelected = true;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedGroup));
            PresentSelectionAfterLayout(value);
            _ = FollowSelectedGroupAsync();
            _ = ResolveRequirementNamesAsync(value);
        }
    }

    /// <summary>The selected map currently drawn in the context panel.</summary>
    /// <remarks>
    /// Changing the filter also replaces the map cards. Letting that layout settle before the
    /// objective panel changes keeps two independent visual trees out of one interface turn.
    /// Selection state and map following still change synchronously; only presentation waits one
    /// frame. The version prevents a quick second selection from drawing an older one.
    /// </remarks>
    public PlanMapGroupViewModel? PresentedSelectedGroup
    {
        get => _presentedSelectedGroup;
        private set
        {
            if (SetProperty(ref _presentedSelectedGroup, value))
            {
                OnPropertyChanged(nameof(HasPresentedSelectedGroup));
            }
        }
    }

    public bool HasPresentedSelectedGroup => PresentedSelectedGroup is not null;

    private void PresentSelectionAfterLayout(PlanMapGroupViewModel? value)
    {
        var version = ++_selectionPresentationVersion;
        DispatcherTimer.RunOnce(
            () =>
            {
                if (version != _selectionPresentationVersion)
                {
                    return;
                }

                // Detaching the previous dense panel and attaching the next one in the same
                // layout turn costs as much as drawing both. Give removal one render pass too.
                PresentedSelectedGroup = null;
                UiActivity.Step("plan:panel-cleared");
                DispatcherTimer.RunOnce(
                    () =>
                    {
                        if (version == _selectionPresentationVersion)
                        {
                            PresentedSelectedGroup = value;
                            UiActivity.Step("plan:panel-presented");
                        }
                    },
                    TimeSpan.FromMilliseconds(150),
                    DispatcherPriority.Background);
            },
            // A background callback posted immediately can still run before Avalonia's queued
            // layout. One tenth of a second remains below the interaction budget and gives the
            // map cards their own render pass before objective controls are attached.
            TimeSpan.FromMilliseconds(550),
            DispatcherPriority.Background);
    }

    /// <summary>The selected map with only its active objectives, numbered like the list.</summary>
    public MapSceneRendererViewModel? MapPreview
    {
        get => _mapPreview;
        private set
        {
            if (!ReferenceEquals(_mapPreview, value))
            {
                if (_mapPreview is not null)
                {
                    _mapPreview.ViewChangeRequested -= MapPreviewViewChangeRequested;
                }

                var dropped = _mapPreview;

                _mapPreview = value;
                if (value is not null)
                {
                    value.ViewChangeRequested += MapPreviewViewChangeRequested;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(HasMapPreview));
                // [#775] After the view has moved off it: its leases keep the cockpit's pictures alive.
                dropped?.ReleasePictures();
            }
        }
    }

    public bool HasMapPreview => MapPreview is not null;

    /// <summary>Why the centre map is not showing: short, plain, never an error dump.</summary>
    public string MapNote
    {
        get => _mapNote;
        private set => SetProperty(ref _mapNote, value);
    }

    public bool HasSelectedGroup => SelectedGroup is not null;

    public ICommand OpenHideoutCommand { get; }

    /// <summary>The older two-state view of <see cref="Filter"/>: everything, or the active quests.</summary>
    public bool ShowAll
    {
        get => Filter == PlanQuestFilter.All;
        set => Filter = value ? PlanQuestFilter.All : PlanQuestFilter.Active;
    }

    public PlanQuestFilter Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                foreach (var chip in FilterChips)
                {
                    chip.IsSelected = chip.Filter == value;
                }

                OnPropertyChanged(nameof(ShowAll));
                ApplyFilter();
            }
        }
    }

    public IReadOnlyList<PlanFilterChipViewModel> FilterChips => _filterChips ??= CreateFilterChips();

    /// <summary>Words that must all appear somewhere in a quest: its name, trader, maps or an objective.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasSearchText));
                // The box shows the character now and the list catches up on the dispatcher's next
                // turn, once, however many characters were typed before it came.
                _filterRequest.Request();
            }
        }
    }

    public bool HasSearchText => _searchText.Trim().Length > 0;

    public ICommand ClearSearchCommand => _clearSearch ??= new DelegateCommand(() => SearchText = string.Empty);

    private ICommand? _clearSearch;

    /// <summary>Every trader the board has a quest from, after "All traders".</summary>
    public IReadOnlyList<PlanTraderOption> Traders
    {
        get => _traders;
        private set => SetProperty(ref _traders, value);
    }

    public PlanTraderOption SelectedTrader
    {
        get => _selectedTrader;
        set
        {
            if (value is not null && SetProperty(ref _selectedTrader, value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>The player's level, which every level-gated quest is measured against.</summary>
    /// <remarks>
    /// Typed in because the game never writes its own player's down: left at the stored 1, a fresh
    /// profile gates hundreds of quests behind levels the player is long past.
    /// </remarks>
    public decimal PlayerLevel => _playerLevel;

    public IReadOnlyList<PlanTraderLoyaltyViewModel> TraderLoyalty
    {
        get => _traderLoyalty;
        private set
        {
            if (SetProperty(ref _traderLoyalty, value))
            {
                OnPropertyChanged(nameof(HasTraderLoyalty));
            }
        }
    }

    public bool HasTraderLoyalty => _traderLoyalty.Count > 0;

    /// <summary>"12 items still needed": the unmet requirements of every map on show, each item counted once.</summary>
    public string RequirementsRollup
    {
        get => _rollup;
        private set
        {
            if (SetProperty(ref _rollup, value))
            {
                OnPropertyChanged(nameof(HasRequirementsRollup));
            }
        }
    }

    public bool HasRequirementsRollup => _rollup.Length > 0;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>The active profile and game mode, read fresh every refresh — never hardcoded.</summary>
    public string ScopeLabel
    {
        get => _scopeLabel;
        private set => SetProperty(ref _scopeLabel, value);
    }

    /// <summary>
    /// What the game's logs have told this session about quests, in one line.
    /// </summary>
    /// <remarks>
    /// Silence is what made a whole raid's quest hand-ins go a day unnoticed: a board that had
    /// learned nothing looked exactly like a board with nothing to learn. So this says which it
    /// is, including when the answer is "nothing yet", and including when the game named a quest
    /// the loaded catalog does not have.
    /// </remarks>
    public string GameLogStatus
    {
        get => _gameLogStatus;
        private set
        {
            if (SetProperty(ref _gameLogStatus, value))
            {
                OnPropertyChanged(nameof(HasGameLogStatus));
            }
        }
    }

    public bool HasGameLogStatus => _gameLogStatus.Length > 0;

    public AsyncDelegateCommand RefreshCommand { get; }

    /// <summary>Copies the plan as text, and writes it beside the other exports.</summary>
    public AsyncDelegateCommand ExportCommand { get; }

    /// <summary>
    /// Where a copied plan goes. The view supplies it; without one, Export still writes its file.
    /// </summary>
    /// <remarks>
    /// The same seam the shell uses for Copy diagnostics: a view model cannot reach a clipboard
    /// without a top level, and a top level is exactly what a test does not have.
    /// </remarks>
    public Func<string, Task>? Clipboard { get; set; }

    /// <summary>What the last export did, or why it did nothing.</summary>
    public string ExportStatus
    {
        get => _exportStatus;
        private set
        {
            if (SetProperty(ref _exportStatus, value))
            {
                OnPropertyChanged(nameof(HasExportStatus));
            }
        }
    }

    public bool HasExportStatus => _exportStatus.Length > 0;

    /// <summary>
    /// Builds the plan as Markdown, puts it on the clipboard and saves it.
    /// </summary>
    /// <remarks>
    /// Both, not either. The clipboard is what a player actually wants nine times in ten — the
    /// plan goes straight into a squad chat — and the file is what makes it a plan they still
    /// have tomorrow. A clipboard that is not there, or a disk that refuses, is reported rather
    /// than thrown: neither is a reason to lose the other half.
    /// </remarks>
    public async Task ExportAsync(CancellationToken cancellationToken)
    {
        // Every map's item names first. The page resolves names only for the map being looked
        // at, which is right for a screen showing one map and wrong for a document covering all
        // of them: without this the same item appears as a name on one map and a raw id on the
        // next, in the same file.
        foreach (var group in Groups)
        {
            await ResolveRequirementNamesAsync(group).ConfigureAwait(true);
        }

        // Then rebuild all of them. A group whose ids were resolved by a *different* group's pass
        // is skipped by the resolver (it has nothing left to look up) and would otherwise keep the
        // rows it was built with, which is how one document ended up naming the same item twice,
        // once as a name and once as an id.
        foreach (var group in Groups)
        {
            group.Requirements = BuildRequirementsFor(group);
        }

        var document = PlanExport.Build(this, _clock.GetUtcNow(), NameOfTask);
        var markdown = document.ToMarkdown(CultureInfo.CurrentCulture);
        var copied = false;
        string? written = null;

        if (Clipboard is { } clipboard)
        {
            try
            {
                await clipboard(markdown).ConfigureAwait(true);
                copied = true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                copied = false;
            }
        }

        if (_paths is { } paths)
        {
            try
            {
                var directory = Path.Combine(paths.Config, "Exports");
                Directory.CreateDirectory(directory);
                // The local date and time in the name, because a folder of plans is sorted by eye
                // and "plan-2026-09-20-1432.md" is the one thing that makes that work.
                var name = string.Create(
                    CultureInfo.InvariantCulture,
                    $"plan-{LocalTime.ToLocal(_clock.GetUtcNow()):yyyy-MM-dd-HHmm}.md");
                written = Path.Combine(directory, name);
                await File.WriteAllTextAsync(written, markdown, cancellationToken).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                written = null;
            }
        }

        ExportStatus = (copied, written) switch
        {
            (true, { } path) => PlanText.CopiedAndSaved(path),
            (true, null) => PlanText.CopiedToClipboard,
            (false, { } path) => PlanText.SavedTo(path),
            _ => PlanText.NothingToExportTo,
        };
    }

    public Task LoadAsync() => RefreshAsync(CancellationToken.None);

    public Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    /// <summary>Shown in the pane when the board could not be read, with Retry (#453).</summary>
    public LoadFaultNoticeViewModel LoadFault => _loadFault ??= new(() => RefreshAsync(CancellationToken.None));

    private LoadFaultNoticeViewModel? _loadFault;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            UiActivity.Step("plan:start");
            LoadFaultInjection.ThrowIfInjected("plan");
            await LoadHold.WaitIfHeldAsync("plan", cancellationToken).ConfigureAwait(true);
            var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(true);
            UiActivity.Step("plan:profile");
            _scope = new(profile.Id, profile.GameMode, profile.ProfileGeneration);
            ScopeLabel = $"{profile.Name} · {profile.GameMode}";
            _ownedItems = profile.OwnedItemCounts;
            _missingItems.Clear();
            // Off the interface thread: the board is every quest and objective in the catalog
            // joined to the profile, and read here it held one turn for up to 2.5 s.
            var scope = _scope;
            // Quests that read the same as last time keep last time's instances, so a return to
            // the page keeps its groups and rows instead of building them all again (#453).
            var previousBoard = _board;
            _board = await OffInterfaceThread
                .Run(async () => QuestBoardReconciler.Reconcile(
                    previousBoard,
                    await _readService.GetQuestBoardAsync(scope, cancellationToken).ConfigureAwait(false)), cancellationToken)
                .ConfigureAwait(true);
            var boardUnchanged = previousBoard is not null && ReferenceEquals(previousBoard, _board);
            UiActivity.Step("plan:board");
            if (_allergies is { } allergies)
            {
                // Re-read with the board: the record is made on the Events tab beside this one.
                _allergyWarnings = await OffInterfaceThread
                    .Run(() => allergies.GetAsync(cancellationToken), cancellationToken)
                    .ConfigureAwait(true);
            }

            if (_eventRuleService is not null)
            {
                var eventRules = await OffInterfaceThread
                    .Run(() => _eventRuleService.ReadActiveAsync(cancellationToken), cancellationToken)
                    .ConfigureAwait(true);
                _activeEventRules = eventRules.Active;
                var summary = PlanText.EventRuleActiveSummary(_activeEventRules);
                EventRuleSummary = eventRules.InvalidDefinitions.Count > 0
                    ? string.Join(" · ", new[] { summary, PlanText.EventRuleFilesNeedAttention(eventRules.InvalidDefinitions.Count) }.Where(value => value.Length > 0))
                    : summary;
            }
            else
            {
                _activeEventRules = ActiveEventRules.Empty;
                EventRuleSummary = string.Empty;
            }

            var board = _board;
            _mapNames = await OffInterfaceThread.Run(() => ResolveMapNamesAsync(board, cancellationToken), cancellationToken).ConfigureAwait(true);
            UiActivity.Step("plan:mapnames");
            RebuildSearchIndex();
            UiActivity.Step("plan:searchindex");
            if (!boardUnchanged)
            {
                _projected.Clear();
            }

            ApplyProfile(profile.Level, profile.TraderLevels);
            UiActivity.Step("plan:applyprofile");
            ApplyFilter();
            UiActivity.Step("plan:applyfilter");
            UpdateGameLogStatus(_questLog?.Reading);
            await RefreshMapQuestLayerAsync().ConfigureAwait(true);
            UiActivity.Step("plan:questlayer");
            LoadFault.Clear();
            LoadState = PageLoadStates.Read(HasGroups);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The raw exception belongs in the log, not on the page: the usual cause is simply
            // that game data has not finished loading, and the shell reloads this workspace when
            // it does.
            _board = null;
            Groups = [];
            SelectedGroup = null;
            Status = PlanText.QuestDataUnavailable;
            LoadFault.Show(PlanText.QuestsDidNotLoad, PlanText.NothingLostRetry);
            LoadState = PageLoadState.Failed;
            WorkspaceFault.Record("plan", "refresh", exception);
        }
    }

    /// <summary>
    /// The game said something about a quest, so the board the player is looking at reloads.
    /// </summary>
    /// <remarks>
    /// Raised on whichever thread was reading the log, so it is marshalled here rather than in
    /// the service: every other caller of RefreshAsync is already on the UI thread.
    /// </remarks>
    private void OnQuestLogChanged(object? sender, QuestLogProgressReading reading)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnQuestLogChanged(sender, reading));
            return;
        }

        UpdateGameLogStatus(reading);
        RefreshAsync(CancellationToken.None).Observe("plan", "refresh after the game reported a quest");
    }

    private void UpdateGameLogStatus(QuestLogProgressReading? reading)
    {
        if (reading is null)
        {
            GameLogStatus = string.Empty;
            return;
        }

        if (!reading.HeardAnything)
        {
            GameLogStatus = PlanText.GameNotReported;
            return;
        }

        var heard = reading.LastObservedUtc is { } observed
            ? PlanText.GameLastReported(LocalTime.ShortTime(observed))
            : PlanText.GameReported;
        var what = reading.Recorded switch
        {
            0 => PlanText.NoneChangedBoard,
            1 => PlanText.OneChangedBoard,
            var many => PlanText.ManyChangedBoard(many),
        };
        var caveat = (reading.Unmatched, reading.Failed) switch
        {
            (0, 0) => PlanText.SentenceEnd,
            (> 0, 0) => PlanText.NotInLoadedCatalog(PlanText.QuestCount(reading.Unmatched)),
            (0, > 0) => PlanText.CouldNotBeSaved(PlanText.QuestCount(reading.Failed)),
            var (unmatched, failed) =>
                PlanText.NotInCatalogOrSaved(PlanText.QuestCount(unmatched), PlanText.QuestCount(failed)),
        };
        GameLogStatus = heard + what + caveat;
    }

    /// <summary>
    /// Tells the map what the player is now doing. It reads the active and pinned quests when it
    /// loads a map and when V1's Quests page changes them; starting, finishing or pinning a quest
    /// here left the Raid plan showing yesterday's objectives until a map was reopened.
    /// </summary>
    private async Task RefreshMapQuestLayerAsync()
    {
        try
        {
            await _map.RefreshQuestLayerAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WorkspaceFault.Record("plan", "refresh the map's quest layer", exception.Message);
        }
    }

    private void ApplyFilter()
    {
        if (_board is null)
        {
            Groups = [];
            return;
        }

        var previousMapKey = SelectedGroup is { } previous ? previous.MapId ?? AnyMapKey : null;
        var entries = Bucket(
            _board.Tasks,
            Filter,
            QuestsPageViewModel.SearchTerms(SearchText),
            SelectedTrader.TraderId,
            _searchableText,
            _questStates);
        UiActivity.Step("plan:bucketed");
        // Keeps whatever the last pass built and this one still wants; Groups only changes when
        // the result set does, so an unchanged list is not re-bound and not redrawn.
        var composed = ComposeGroups(entries, Groups, NameOfMap, this, _selectGroup, _openInRaid);
        var composedChanged = !ReferenceEquals(composed, Groups);
        Groups = composed;
        foreach (var group in composed)
        {
            group.ApplyEventRules(_activeEventRules);
        }

        var suggested = MarkSuggestedRaid(composed, _activeEventRules);
        _suggestedRaid = suggested;
        OnPropertyChanged(nameof(HasSuggestedRaid));
        OnPropertyChanged(nameof(SuggestedRaidLabel));
        UiActivity.Step("plan:composed");
        RebuildRequirements(composedChanged);
        UiActivity.Step("plan:requirements");
        PlanMapGroupViewModel? keep = null;
        if (previousMapKey is not null)
        {
            foreach (var group in Groups)
            {
                if (string.Equals(group.MapId ?? AnyMapKey, previousMapKey, StringComparison.OrdinalIgnoreCase))
                {
                    keep = group;
                    break;
                }
            }
        }

        SelectedGroup = keep ?? suggested ?? (Groups.Count > 0 ? Groups[0] : null);
        UiActivity.Step("plan:selected");
        UpdateStatus();
    }

    private PlanMapGroupViewModel? _suggestedRaid;
    private ICommand? _selectSuggestedRaid;

    public bool HasSuggestedRaid => _suggestedRaid is not null;

    /// <summary>"Suggested: Shoreline · 3 quests", under the page heading where it cannot scroll away.</summary>
    public string SuggestedRaidLabel => _suggestedRaid is { } group
        ? PlanText.Suggested(group.MapLabel, PlanText.QuestCount(group.Quests.Count))
        : string.Empty;

    public ICommand SelectSuggestedRaidCommand => _selectSuggestedRaid ??= new DelegateCommand(() =>
    {
        if (_suggestedRaid is { } group)
        {
            SelectedGroup = group;
        }
    });

    /// <summary>Flags the map the next raid should be on, and returns it. The choice is <see cref="NextRaidPlanner"/>'s.</summary>
    internal static PlanMapGroupViewModel? MarkSuggestedRaid(
        IReadOnlyList<PlanMapGroupViewModel> groups,
        ActiveEventRules? eventRules = null)
    {
        var best = NextRaidPlanner.Suggest(groups.Select(group =>
            new TarkovCompanion.Core.Domain.Planning.NextRaidCandidate(group.MapId ?? string.Empty, group.MapLabel, group.Quests.Count, group.Objectives.Count)), eventRules);
        PlanMapGroupViewModel? suggested = null;
        foreach (var group in groups)
        {
            group.IsSuggested = best is not null && string.Equals(group.MapId, best.MapKey, StringComparison.OrdinalIgnoreCase);
            suggested = group.IsSuggested ? group : suggested;
        }

        return suggested;
    }

    /// <summary>
    /// The map groups for a filtered result set, reusing every group and row the last set already
    /// had one of.
    /// </summary>
    /// <remarks>
    /// A row is the same row when its quest and its objective are the same instances, which they
    /// are for every pass over one board read and are not once the board has been re-read: a
    /// keystroke keeps its rows, marking an objective done replaces them. A kept row may still move
    /// up the list, so it is renumbered in place.
    ///
    /// A group is the same group when it ends up holding exactly the same rows in the same order.
    /// That is what lets the requirements rollup, the selection, the centre map and the item-name
    /// lookups all stay where they are: <see cref="SelectedGroup"/> compares by instance, so the
    /// same instance coming back out of a filter pass is not a selection change at all.
    ///
    /// Returns <paramref name="previous"/> itself when nothing changed, so the caller's property
    /// setter sees no change either.
    /// </remarks>
    internal static IReadOnlyList<PlanMapGroupViewModel> ComposeGroups(
        IReadOnlyList<PlanObjectiveBucketEntry> entries,
        IReadOnlyList<PlanMapGroupViewModel> previous,
        Func<string, string> nameOfMap,
        PlanWorkspaceViewModel? owner = null,
        Action<PlanMapGroupViewModel>? select = null,
        Func<string, Task>? openInRaid = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(nameOfMap);
        var buckets = new Dictionary<string, List<PlanObjectiveBucketEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!buckets.TryGetValue(entry.MapKey, out var list))
            {
                buckets[entry.MapKey] = list = [];
            }

            list.Add(entry);
        }

        var groups = new List<PlanMapGroupViewModel>(buckets.Count);
        foreach (var (mapKey, bucket) in buckets)
        {
            var existing = FindGroup(previous, mapKey);
            var rows = ComposeRows(bucket, existing?.Objectives, owner);
            groups.Add(existing is not null && SameRows(existing.Objectives, rows)
                ? existing
                : new PlanMapGroupViewModel(
                    mapKey.Length == 0 ? null : mapKey,
                    nameOfMap(mapKey),
                    rows,
                    select,
                    openInRaid));
        }

        // "Any map" last, then by name. The map id breaks a tie because several unnamed ids share
        // the "Other map" label, and an unstable sort would shuffle them between keystrokes.
        groups.Sort(static (left, right) =>
        {
            var byKind = (left.MapId is null ? 1 : 0) - (right.MapId is null ? 1 : 0);
            if (byKind != 0)
            {
                return byKind;
            }

            var byLabel = string.Compare(left.MapLabel, right.MapLabel, StringComparison.CurrentCultureIgnoreCase);
            return byLabel != 0 ? byLabel : string.CompareOrdinal(left.MapId, right.MapId);
        });
        return SameGroups(previous, groups) ? previous : groups;
    }

    private static PlanMapGroupViewModel? FindGroup(IReadOnlyList<PlanMapGroupViewModel> groups, string mapKey)
    {
        foreach (var group in groups)
        {
            if (string.Equals(group.MapId ?? AnyMapKey, mapKey, StringComparison.OrdinalIgnoreCase))
            {
                return group;
            }
        }

        return null;
    }

    /// <summary>
    /// One map's rows, taking each from <paramref name="existing"/> where it is already there.
    /// </summary>
    /// <remarks>
    /// Both lists run in board order, so one cursor through the old rows finds every survivor
    /// whether the result set narrowed or widened, without a dictionary per group per keystroke.
    /// </remarks>
    private static PlanObjectiveRowViewModel[] ComposeRows(
        List<PlanObjectiveBucketEntry> bucket,
        IReadOnlyList<PlanObjectiveRowViewModel>? existing,
        PlanWorkspaceViewModel? owner)
    {
        var rows = new PlanObjectiveRowViewModel[bucket.Count];
        var cursor = 0;
        for (var index = 0; index < bucket.Count; index++)
        {
            var entry = bucket[index];
            PlanObjectiveRowViewModel? reused = null;
            if (existing is not null)
            {
                for (var probe = cursor; probe < existing.Count; probe++)
                {
                    var candidate = existing[probe];
                    if (ReferenceEquals(candidate.Objective, entry.Objective) && ReferenceEquals(candidate.Task, entry.Task))
                    {
                        reused = candidate;
                        cursor = probe + 1;
                        break;
                    }
                }
            }

            var row = reused ?? new PlanObjectiveRowViewModel(entry.Task, entry.Objective, owner!);
            row.Number = index + 1;
            row.IsLast = index == bucket.Count - 1;
            rows[index] = row;
        }

        return rows;
    }

    private static bool SameRows(IReadOnlyList<PlanObjectiveRowViewModel> left, PlanObjectiveRowViewModel[] right)
    {
        if (left.Count != right.Length)
        {
            return false;
        }

        for (var index = 0; index < right.Length; index++)
        {
            if (!ReferenceEquals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameGroups(IReadOnlyList<PlanMapGroupViewModel> left, List<PlanMapGroupViewModel> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < right.Count; index++)
        {
            if (!ReferenceEquals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Joins what the search reads, for the board as it now is and the map names as they now are.</summary>
    private void RebuildSearchIndex() =>
        _searchIndex = _board is null ? PlanSearchIndex.Empty : PlanSearchIndex.Build(_board.Tasks, NameOfMap);

    private void UpdateStatus() => Status = _board?.UnavailableReason ?? (HasGroups
        ? PlanText.AcrossMaps(PlanText.ObjectiveCount(Groups.Sum(group => group.Objectives.Count)), PlanText.MapCount(Groups.Count))
        : PlanQuestRules.DescribeEmpty(Filter, SearchText.Trim(), SelectedTrader.TraderId is not null));

    /// <summary>
    /// Everything the search reads on a quest, so "customs" finds a quest by the map its objectives
    /// are on. Read from <see cref="PlanSearchIndex"/>, which joined it when the board was read.
    /// </summary>
    private string SearchableText(QuestSummaryReadModel task) => _searchIndex.TextFor(task);

    private IReadOnlyList<PlanFilterChipViewModel> CreateFilterChips()
    {
        var chips = Enum.GetValues<PlanQuestFilter>()
            .Select(filter => new PlanFilterChipViewModel(filter, selected => Filter = selected))
            .ToArray();
        foreach (var chip in chips)
        {
            chip.IsSelected = chip.Filter == _filter;
        }

        return chips;
    }

    /// <summary>Takes the profile's level and loyalty, and the traders the board actually has quests from.</summary>
    private void ApplyProfile(int level, IReadOnlyDictionary<string, int> traderLevels)
    {
        _playerLevel = level;
        OnPropertyChanged(nameof(PlayerLevel));
        _questStates = _board is null
            ? new Dictionary<string, QuestStatePlan>(StringComparer.Ordinal)
            : QuestStatePlanner.Plan(_board.Tasks, traderLevels, TaskNameOrNull);
        var named = _board is null
            ? []
            : _board.Tasks
                .Where(task => !string.IsNullOrWhiteSpace(task.TraderId))
                .GroupBy(task => task.TraderId!, StringComparer.Ordinal)
                .Select(group => new PlanTraderOption(
                    group.Key,
                    group.Select(task => task.TraderName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? group.Key))
                .OrderBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        // Kept when they read the same (#453): a new list rebuilds the combo box and every loyalty
        // spinner, on every return to the page.
        PlanTraderOption[] traders = [AllTraders, .. named];
        if (!Traders.SequenceEqual(traders))
        {
            Traders = traders;
        }

        var selected = Traders.FirstOrDefault(option => option.TraderId == _selectedTrader.TraderId) ?? AllTraders;
        if (!ReferenceEquals(selected, _selectedTrader))
        {
            _selectedTrader = selected;
            OnPropertyChanged(nameof(SelectedTrader));
        }

        PlanTraderLoyaltyViewModel[] loyalty =
        [
            .. named.Select(option => new PlanTraderLoyaltyViewModel(
                option.TraderId!,
                option.Name,
                traderLevels.GetValueOrDefault(option.TraderId!))),
        ];
        if (!TraderLoyalty.SequenceEqual(loyalty))
        {
            TraderLoyalty = loyalty;
        }
    }

    /// <summary>Stores a typed level and re-reads the board, because every level-gated quest is measured against it.</summary>
    /// <remarks>
    /// A cleared box is not a level and is ignored; anything outside the game's own range is pulled
    /// back into it, because a spinner holding 800 gates the catalog as thoroughly as one holding 1.
    /// </remarks>
    public async Task SetPlayerLevelAsync(decimal? value)
    {
        if (value is not { } entered)
        {
            return;
        }

        var level = (int)Math.Clamp(entered, QuestsPageViewModel.MinimumLevel, QuestsPageViewModel.MaximumLevel);
        if (level == _playerLevel)
        {
            return;
        }

        try
        {
            var profile = await _profileService.GetActiveAsync(CancellationToken.None).ConfigureAwait(true);
            if (profile.Level != level)
            {
                await _profileService
                    .SaveAsync(profile with { Level = level, UpdatedUtc = DateTimeOffset.UtcNow }, CancellationToken.None)
                    .ConfigureAwait(true);
            }

            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OnPropertyChanged(nameof(PlayerLevel));
            Status = PlanText.LevelNotSaved(exception.Message);
        }
    }

    /// <summary>Records loyalty with one trader (0 to 4) and re-reads the board, since what is available depends on it.</summary>
    public async Task SetTraderLevelAsync(string traderId, decimal level)
    {
        if (string.IsNullOrWhiteSpace(traderId))
        {
            return;
        }

        var wanted = (int)Math.Clamp(level, 0, 4);

        // The spinner reports its own initial value as a change each time the rows are rebuilt;
        // that is not an edit, and answering it would read the profile once per trader per refresh.
        if (_traderLoyalty.FirstOrDefault(row => row.TraderId == traderId) is { } shown && shown.Level == wanted)
        {
            return;
        }

        try
        {
            var profile = await _profileService.GetActiveAsync(CancellationToken.None).ConfigureAwait(true);
            if (profile.TraderLevels.GetValueOrDefault(traderId) == wanted)
            {
                return;
            }

            var levels = new Dictionary<string, int>(profile.TraderLevels, StringComparer.Ordinal)
            {
                [traderId] = wanted,
            };
            await _profileService
                .SaveAsync(profile with { TraderLevels = levels, UpdatedUtc = DateTimeOffset.UtcNow }, CancellationToken.None)
                .ConfigureAwait(true);
            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = PlanText.LoyaltyNotSaved(exception.Message);
        }
    }

    /// <summary>
    /// Works out the requirement rows of every group that does not have them yet, which after a
    /// filter pass that kept its groups is none of them.
    /// </summary>
    private void RebuildRequirements(bool groupsChanged = true)
    {
        var built = false;
        foreach (var group in Groups)
        {
            if (group.RequirementsBuilt)
            {
                continue;
            }

            group.Requirements = BuildRequirementsFor(group);
            group.RequirementsBuilt = true;
            built = true;
        }

        if (built || groupsChanged)
        {
            UpdateRollup();
        }
    }

    /// <summary>Names a quest by id, including one the plan does not itself contain.</summary>
    private string NameOfTask(string taskId) => TaskNameOrNull(taskId) ?? taskId;

    private IReadOnlyList<PlanRequirementRowViewModel> BuildRequirementsFor(PlanMapGroupViewModel group)
    {
        // Which items each objective's own quest also hands over, so a find objective beside a
        // hand-over of the same item is counted once (QuestRequirementPlanner says why).
        var handedOver = new Dictionary<QuestObjectiveReadModel, IReadOnlySet<string>>(ReferenceEqualityComparer.Instance);
        foreach (var rows in group.Objectives.GroupBy(row => row.Task.TaskId, StringComparer.Ordinal))
        {
            var items = QuestItemNeedPlanner.HandedOverItemIds(rows.First().Task);
            foreach (var row in rows)
            {
                handedOver[row.Objective] = items;
            }
        }

        return PlanQuestRules.BuildRequirements(
            group.Objectives.Select(row => row.Objective),
            NameOfItem,
            _ownedItems,
            objective => handedOver.TryGetValue(objective, out var items) ? items : EmptyItemIds,
            _allergyWarnings);
    }

    private static readonly IReadOnlySet<string> EmptyItemIds = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// What an item is called: its catalog name, or plainly that the catalog lacks it once that is
    /// known. An id would read as a bug; a name nobody has looked up yet is the id until they do.
    /// </summary>
    private string NameOfItem(string itemId) => _itemNames.TryGetValue(itemId, out var name)
        ? name
        : _missingItems.Contains(itemId) ? PlanText.ItemNotInCatalog : itemId;

    /// <summary>Each unmet requirement counted once however many maps ask for it.</summary>
    private void UpdateRollup()
    {
        var unmet = Groups
            .SelectMany(group => group.Requirements.Where(row => !row.IsSatisfied))
            .DistinctBy(row => (row.ItemName, row.HandlingLabel));
        RequirementsRollup = PlanQuestRules.SummariseUnmet(unmet, value => PlanText.ItemCount(value));
    }

    /// <summary>
    /// Names the items the selected map asks for, once each. Only the selected map's, because
    /// naming every item of every quest in "All" is thousands of catalog reads for names nobody
    /// is looking at; the others read by id until they are chosen.
    /// </summary>
    private async Task ResolveRequirementNamesAsync(PlanMapGroupViewModel? group)
    {
        if (group is null || _itemRepository is null)
        {
            return;
        }

        var unnamed = group.Objectives
            .SelectMany(row => row.Objective.ItemTargets)
            .Select(target => target.ItemId)
            .Distinct(StringComparer.Ordinal)
            .Where(id => !_itemNames.ContainsKey(id) && !_missingItems.Contains(id))
            .Take(150)
            .ToArray();
        if (unnamed.Length == 0)
        {
            return;
        }

        // Read off the interface thread and applied on it: up to 150 lookups, none of which the
        // view needs to watch happen. The dictionaries belong to this thread, so only the answers
        // come back.
        var repository = _itemRepository;
        var lookups = await OffInterfaceThread.Run(async () =>
        {
            var found = new List<(string Id, string? Name)>(unnamed.Length);
            foreach (var id in unnamed)
            {
                try
                {
                    var item = await repository.GetAsync(id, CancellationToken.None).ConfigureAwait(false);
                    found.Add((id, item?.Name));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One unreadable item costs one name, not the panel.
                    WorkspaceFault.Record("plan", $"name item {id}", exception.Message);
                }
            }

            return found;
        }).ConfigureAwait(true);

        var resolved = lookups.Count > 0;
        foreach (var (id, name) in lookups)
        {
            if (name is not null)
            {
                _itemNames[id] = name;
            }
            else
            {
                _missingItems.Add(id);
            }
        }

        if (resolved && Groups.Contains(group))
        {
            group.Requirements = BuildRequirementsFor(group);
            UpdateRollup();
        }
    }

    internal bool SelectMapForPreview(string map)
    {
        var group = Groups.FirstOrDefault(candidate =>
            string.Equals(candidate.MapId, map, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.MapLabel, map, StringComparison.OrdinalIgnoreCase));
        if (group is null)
        {
            return false;
        }

        SelectedGroup = group;
        return true;
    }

    internal void SendSelectedObjectiveRouteToRaidForPreview()
    {
        if (SelectedGroup is { MapId: { } mapId } group)
        {
            _raidCockpit?.SetObjectiveRoute(mapId, group.Route);
        }
    }

    /// <summary>The map catalog location a game-data map id belongs to, if the catalog has it.</summary>
    private string? LocationIdFor(string gameMapId) => _map.Locations
        .FirstOrDefault(location => IsLocationOf(location, gameMapId))
        ?.Id;

    /// <summary>
    /// Whether a map catalog location is the game-data map a quest names. The catalog does not
    /// always publish the game-data id (its entries can carry only the normalized name), so a
    /// location also matches by the synced maps table's name for that id.
    /// </summary>
    private bool IsLocationOf(MapLocation location, string gameMapId) =>
        string.Equals(location.Id, gameMapId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(location.SourceId, gameMapId, StringComparison.OrdinalIgnoreCase) ||
        location.Variants.Any(variant => variant.AlternateLocationIds.Contains(gameMapId, StringComparer.OrdinalIgnoreCase)) ||
        (_mapNames.TryGetValue(gameMapId, out var name) && string.Equals(location.Name, name, StringComparison.OrdinalIgnoreCase));

    private bool ShowsGroupMap(PlanMapGroupViewModel group) =>
        group.MapId is { } mapId &&
        _map.SelectedLocation is { } location &&
        IsLocationOf(location, mapId);

    /// <summary>
    /// Projects the active objectives onto the selected group's map with the same services the
    /// V1 quest layer uses, once per map per board refresh. The location handed over carries the
    /// game-data id as its source id, so zones named by that id match (see IsLocationOf).
    /// </summary>
    private async Task ProjectGroupAsync(string gameMapId)
    {
        if (_projection is null || _scope is null || _projectingMapId is not null ||
            _map.SelectedLocation is not { } location || _map.SelectedVariant is not { } variant ||
            _map.CatalogProvenance is not { } provenance)
        {
            return;
        }

        _projectingMapId = gameMapId;
        try
        {
            var located = location with { SourceId = gameMapId };
            var query = await _readService
                .GetActiveMapObjectivesAsync(_scope, [.. QuestMapProjectionService.CompatibleMapIds(located, variant)], CancellationToken.None)
                .ConfigureAwait(true);
            _projected[gameMapId] = _projection.Project(query, located, variant, selectedFloor: null, provenance).Objectives;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WorkspaceFault.Record("plan", $"place objectives on {gameMapId}", exception.Message);
            _projected[gameMapId] = [];
        }
        finally
        {
            _projectingMapId = null;
        }

        RefreshMapPreview();
    }

    /// <summary>
    /// Moves the shared map to the selected group's map so the centre can draw it — but never
    /// during a raid, when that map belongs to the raid and the Raid workspace follows it.
    /// </summary>
    private async Task FollowSelectedGroupAsync()
    {
        if (SelectedGroup is not { MapId: { } mapId } group || ShowsGroupMap(group))
        {
            RefreshMapPreview();
            return;
        }

        if (_runtime?.Current.Raid.State == RaidLifecycleState.InRaid && _map.SelectedLocation is not null)
        {
            RefreshMapPreview();
            return;
        }

        if (LocationIdFor(mapId) is not { } locationId)
        {
            RefreshMapPreview();
            return;
        }

        try
        {
            MapNote = PlanText.LoadingMap;
            await _map.FollowRaidAsync(locationId).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WorkspaceFault.Record("plan", $"load map {mapId}", exception.Message);
        }

        RefreshMapPreview();
    }

    /// <summary>
    /// Rebuilds the centre map for the selected group, numbering each placed objective like its
    /// row, and marks rows the projection could not place.
    /// </summary>
    internal void RefreshMapPreview()
    {
        var group = SelectedGroup;
        if (group is null || group.MapId is null)
        {
            ClearMapPreview(group is null ? string.Empty : PlanText.AnyMapObjectives);
            return;
        }

        if (_raidCockpit is null || !ShowsGroupMap(group))
        {
            ClearMapPreview(_runtime?.Current.Raid.State == RaidLifecycleState.InRaid && _map.SelectedLocation is not null
                ? PlanText.MapFollowsRaid
                : PlanText.LoadingMap);
            return;
        }

        if (!_projected.TryGetValue(group.MapId, out var projected))
        {
            _ = ProjectGroupAsync(group.MapId);
            projected = [];
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var renderModel = _map.RenderModel;
        var scene = renderModel is not null
            ? BuildObjectiveScene(group.VisitOrder, projected, renderModel, nowUtc)
            : QuestObjectiveScene.Empty;
        ObjectiveRouteScene? routeScene = null;
        if (renderModel is not null)
        {
            routeScene = BuildObjectiveRoute(group, scene, nowUtc);
            if (routeScene is not null)
            {
                // Applying a route renumbers and reorders rows. Build the objective markers once
                // more so their numbers agree with the visiting order the panel now shows.
                scene = BuildObjectiveScene(group.VisitOrder, projected, renderModel, nowUtc);
            }
        }
        var known = _projected.ContainsKey(group.MapId);
        foreach (var row in group.Objectives)
        {
            row.HasMapPosition = known
                ? scene.Entries.Any(entry => entry.IsPlaced && entry.ObjectiveId == row.Objective.ObjectiveId)
                : null;
        }

        var previewObjects = routeScene is null
            ? scene.Objects
            : [.. scene.Objects, .. routeScene.Objects.Where(item => item.Kind == MapSceneObjectKind.Route)];
        // [#775] The picture's hash too: the cockpit replaces its picture while tiles fill in, and a
        // preview that is not re-presented keeps drawing the one it replaced.
        var signature = $"{_raidCockpit.BackgroundSha}|{_map.RenderModel?.Location.Id}|{_map.RenderModel?.Variant.Key}|{_map.RenderModel?.SelectedFloor?.Id}|{string.Join(',', previewObjects.Select(item => $"{item.Id.Value}@{item.Geometry.Kind}:{string.Join(';', item.Geometry.Points.Select(point => FormattableString.Invariant($"{point.X:R},{point.Y:R}")))}"))}";
        if (MapPreview is not null && signature == _mapPreviewSignature)
        {
            return;
        }

        var preview = _raidCockpit.CreateObjectivePreview(
            previewObjects,
            MapPreview,
            routeScene is null ? [] : [routeScene.Layer]);
        _mapPreviewSignature = preview is null ? null : signature;
        MapPreview = preview;
        MapNote = preview is null ? PlanText.NoMapPlan : string.Empty;
    }

    private void ClearMapPreview(string note)
    {
        MapPreview = null;
        _mapPreviewSignature = null;
        MapNote = note;
        if (SelectedGroup is { } group)
        {
            group.ApplyRoute(null, note.Length > 0 ? note : PlanText.RouteNeedsMap);
            foreach (var row in group.Objectives)
            {
                row.HasMapPosition = null;
            }
        }
    }

    /// <summary>
    /// The objectives the list shows, as the plan draws them: each numbered like its row, an
    /// outline as an area with the number inside it, several possible locations as several spots,
    /// and the ones the projection could not place listed as having no location instead of being
    /// guessed onto the map. The scene points are the projection's own (Leaflet units, the units
    /// of the plan's bounds): the 0 to 100 box this used to convert into had stopped being the
    /// plan's space, which put most objectives off the artwork.
    /// </summary>
    internal static QuestObjectiveScene BuildObjectiveScene(
        IReadOnlyList<PlanObjectiveRowViewModel> rows,
        IReadOnlyList<QuestMapObjectiveProjection> projected,
        MapRenderModel model,
        DateTimeOffset nowUtc)
    {
        var numbers = rows
            .GroupBy(row => row.Objective.ObjectiveId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Number.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal);
        return new QuestObjectiveSceneBuilder().Build(
            projected.Where(item => numbers.ContainsKey(item.ObjectiveId)).ToArray(),
            model.Floors,
            id => numbers.GetValueOrDefault(id),
            nowUtc);
    }

    private ObjectiveRouteScene? BuildObjectiveRoute(
        PlanMapGroupViewModel group,
        QuestObjectiveScene scene,
        DateTimeOffset nowUtc)
    {
        if (_raidCockpit?.ObjectiveRouteOrigin() is not { } origin)
        {
            group.ApplyRoute(null, PlanText.RouteNeedsOrigin);
            return null;
        }

        var objects = scene.Objects.ToDictionary(item => item.Id);
        var labels = group.Objectives
            .DistinctBy(row => row.Objective.ObjectiveId, StringComparer.Ordinal)
            .ToDictionary(row => row.Objective.ObjectiveId, row => row.Description, StringComparer.Ordinal);
        var stops = new List<ObjectiveRouteStop>();
        foreach (var entry in scene.Entries.Where(entry => entry.IsPlaced).DistinctBy(entry => entry.ObjectiveId, StringComparer.Ordinal))
        {
            var points = entry.ObjectIds
                .Where(objects.ContainsKey)
                .Select(id => objects[id].Geometry)
                .Where(geometry => geometry.Kind == MapSceneGeometryKind.Point)
                .Select(geometry => geometry.Points[0])
                .Distinct()
                .ToArray();
            // Several candidates or authored spots do not name one honest place to visit. They
            // stay in the list, after the routed stops, with the existing placement explanation.
            if (points.Length != 1)
            {
                continue;
            }

            stops.Add(new(entry.ObjectiveId, labels.GetValueOrDefault(entry.ObjectiveId) ?? entry.Objective.Description, points[0])
            {
                FloorIds = entry.FloorIds,
            });
        }

        // [#307] A route already opened on the Raid map follows these stops from now on.
        if (_map.RenderModel?.Location.Id is { } locationId)
        {
            _raidCockpit.UpdateObjectiveRouteStops(locationId, stops);
        }

        if (stops.Count == 0)
        {
            group.ApplyRoute(null, PlanText.RouteNoExactPosition);
            return null;
        }

        var route = ObjectiveRoutePlanner.Plan(origin.At, origin.Label, stops, origin.UnitsPerMetre);
        group.ApplyRoute(route, string.Empty, group.Objectives.Count - stops.Count);
        return ObjectiveRouteSceneBuilder.Build(route, origin.At, nowUtc, PlanText.ObjectiveRouteWords());
    }

    private void MapPreviewViewChangeRequested(MapSceneViewChange change)
    {
        if (MapPreview is not { } preview)
        {
            return;
        }

        var result = MapSceneViewReducer.Apply(preview.Scene, change);
        if (result.Status is MapSceneViewChangeStatus.Applied or MapSceneViewChangeStatus.Unchanged)
        {
            preview.Present(result.Scene);
        }
    }

    /// <summary>One objective, tagged with one map key it belongs to (an objective on several
    /// maps produces one entry per map; an objective with none produces one <see cref="AnyMapKey"/>
    /// entry).</summary>
    internal readonly record struct PlanObjectiveBucketEntry(
        string MapKey,
        QuestSummaryReadModel Task,
        QuestObjectiveReadModel Objective);

    /// <summary>
    /// The older two-state form: active quests and their unfinished objectives, or everything.
    /// </summary>
    internal static IReadOnlyList<PlanObjectiveBucketEntry> Bucket(
        IReadOnlyList<QuestSummaryReadModel> tasks,
        bool showAll) => Bucket(tasks, showAll ? PlanQuestFilter.All : PlanQuestFilter.Active, [], null, null);

    /// <summary>
    /// Filtering and map bucketing, kept free of the map/wiki/command services so it can be
    /// tested without them.
    /// </summary>
    /// <remarks>
    /// A quest is kept when it belongs to the filter, is from the chosen trader (if one is chosen)
    /// and its text answers every search word. Its objectives are then kept unless recorded
    /// complete, except where the filter is one that shows finished work.
    /// </remarks>
    internal static IReadOnlyList<PlanObjectiveBucketEntry> Bucket(
        IReadOnlyList<QuestSummaryReadModel> tasks,
        PlanQuestFilter filter,
        IReadOnlyList<string> searchTerms,
        string? traderId,
        Func<QuestSummaryReadModel, string>? searchableText,
        IReadOnlyDictionary<string, QuestStatePlan>? questStates = null)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(searchTerms);
        var showFinished = PlanQuestRules.ShowsFinishedObjectives(filter);
        var entries = new List<PlanObjectiveBucketEntry>();
        foreach (var task in tasks)
        {
            if (!PlanQuestRules.Includes(task, filter, questStates?.GetValueOrDefault(task.TaskId)) ||
                (traderId is not null && !string.Equals(task.TraderId, traderId, StringComparison.OrdinalIgnoreCase)) ||
                (searchTerms.Count > 0 &&
                 !QuestsPageViewModel.MatchesEveryTerm(searchableText?.Invoke(task) ?? task.Name, searchTerms)))
            {
                continue;
            }

            foreach (var objective in task.Objectives)
            {
                if (!showFinished && objective.RecordedState == RecordedObjectiveState.Completed)
                {
                    continue;
                }

                IReadOnlyList<string> mapIds = objective.MapIds.Count == 0 ? [AnyMapKey] : objective.MapIds;
                foreach (var mapId in mapIds)
                {
                    entries.Add(new(mapId, task, objective));
                }
            }
        }

        return entries;
    }

    /// <summary>Hand-in versus find-in-raid, kept distinct only where the catalog says which.</summary>
    internal static string DescribeHandling(QuestObjectiveReadModel objective) => objective.FoundInRaidRequired switch
    {
        true => PlanText.FindInRaid,
        false => PlanText.HandIn,
        null => string.Empty,
    };

    internal static string DescribeRemaining(QuestObjectiveReadModel objective)
    {
        if (objective.TargetCount is not { } target)
        {
            return objective.RecordedCount is { } recordedOnly
                ? recordedOnly.ToString("0.##", CultureInfo.CurrentCulture)
                : objective.RecordedState.ToString();
        }

        var recorded = objective.RecordedCount ?? 0;
        var remaining = Math.Max(0, target - recorded);
        return PlanText.RemainingOf(remaining, target);
    }

    private async Task<Dictionary<string, string>> ResolveMapNamesAsync(
        QuestBoardReadModel board,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_mapData is null)
        {
            return names;
        }

        var mapIds = board.Tasks
            .SelectMany(task => task.Objectives)
            .SelectMany(objective => objective.MapIds)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var mapId in mapIds)
        {
            try
            {
                if (await _mapData.GetAsync(mapId, cancellationToken).ConfigureAwait(true) is { } map &&
                    !string.IsNullOrWhiteSpace(map.Name))
                {
                    names[mapId] = map.Name;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One unreadable map falls back to the catalog name below; it must not blank the page.
                WorkspaceFault.Record("plan", $"name map {mapId}", exception.Message);
            }
        }

        return names;
    }

    /// <summary>A quest's name by id, for the line that says which quest opens a locked one.</summary>
    internal string? TaskNameOrNull(string taskId)
    {
        if (_board is not { } board)
        {
            return null;
        }

        if (_taskNames is not { } cached || !ReferenceEquals(cached.Board, board))
        {
            var names = new Dictionary<string, string>(board.Tasks.Count, StringComparer.Ordinal);
            foreach (var task in board.Tasks)
            {
                names[task.TaskId] = task.Name;
            }

            _taskNames = cached = (board, names);
        }

        return cached.Names.GetValueOrDefault(taskId);
    }

    internal QuestStatePlan StateFor(QuestSummaryReadModel task) =>
        _questStates.GetValueOrDefault(task.TaskId) ??
        QuestStatePlanner.Derive(task, new Dictionary<string, int>(), TaskNameOrNull);

    private string NameOfMap(string mapId)
    {
        if (mapId.Length == 0)
        {
            return PlanText.AnyMap;
        }

        if (_mapNames.TryGetValue(mapId, out var synced))
        {
            return synced;
        }

        return _map.Locations
            .FirstOrDefault(location =>
                string.Equals(location.Id, mapId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(location.SourceId, mapId, StringComparison.OrdinalIgnoreCase))?.Name
            ?? PlanText.OtherMap;
    }

    internal bool TryOpenWiki(string? wikiUri) => _wikiOpener.TryOpen(wikiUri);

    internal Task MarkObjectiveDoneAsync(string objectiveId, decimal? count) => MutateAsync(scope =>
        _commandService.SetObjectiveProgressAsync(
            scope,
            objectiveId,
            RecordedObjectiveState.Completed,
            count,
            CancellationToken.None));

    internal Task MarkQuestDoneAsync(string taskId) => SetQuestStateAsync(taskId, RecordedTaskState.Completed);

    internal Task SetQuestStateAsync(string taskId, RecordedTaskState state) => MutateAsync(scope =>
        _commandService.SetTaskStateAsync(scope, taskId, state, CancellationToken.None));

    internal Task TogglePinAsync(QuestPinTargetKind kind, string targetId, bool isPinned) => MutateAsync(scope =>
        _commandService.SetPinAsync(scope, kind, targetId, !isPinned, 0, null, CancellationToken.None));

    internal Task SetObjectiveStateAsync(QuestObjectiveReadModel objective, RecordedObjectiveState state) => MutateAsync(scope =>
        _commandService.SetObjectiveProgressAsync(scope, objective.ObjectiveId, state, null, CancellationToken.None));

    /// <summary>Steps an objective's recorded count by one, never below nothing and never past its target.</summary>
    internal Task SetObjectiveCountAsync(QuestObjectiveReadModel objective, int direction) => MutateAsync(scope =>
        _commandService.SetObjectiveProgressAsync(
            scope,
            objective.ObjectiveId,
            RecordedObjectiveState.InProgress,
            direction < 0
                ? Math.Max(0, (objective.RecordedCount ?? 0) - 1)
                : objective.TargetCount is { } target
                    ? Math.Min(target, (objective.RecordedCount ?? 0) + 1)
                    : (objective.RecordedCount ?? 0) + 1,
            CancellationToken.None));

    /// <summary>Follows the shared map to the objective's map and tells the shell to show it.</summary>
    internal async Task ShowOnMapAsync(string mapId, string? objectiveId = null)
    {
        try
        {
            // Quest objectives name maps by game-data id; the map view follows catalog location
            // ids. Handing it the game id matched no location and silently did nothing.
            await _map.FollowRaidAsync(LocationIdFor(mapId) ?? mapId).ConfigureAwait(true);
            // [#802] The objectives layer on, and the objective selected once the map places it.
            _raidCockpit?.RevealQuestObjectives(objectiveId);
            ShowOnMapRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = PlanText.CouldNotSwitchMap(exception.Message);
        }
    }

    private async Task MutateAsync(Func<QuestProfileScope, Task> mutation)
    {
        if (_scope is null)
        {
            Status = PlanText.NoProfileNothingChanged;
            return;
        }

        try
        {
            await mutation(_scope).ConfigureAwait(true);
            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = PlanText.NotChanged(exception.Message);
        }
    }
}
