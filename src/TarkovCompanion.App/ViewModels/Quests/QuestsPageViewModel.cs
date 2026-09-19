using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Wiki;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.App.ViewModels.Quests;

public sealed record QuestBoardFilter(string Id, string Label);

/// <summary>One import already applied, with everything it refused underneath it.</summary>
/// <param name="Title">Whose progress it was and when it landed.</param>
/// <param name="Detail">What it did, in counts.</param>
/// <param name="Lines">Each thing it would not do, and why.</param>
public sealed record QuestImportHistoryRowViewModel(
    string Title,
    string Detail,
    IReadOnlyList<QuestImportHistoryLineViewModel> Lines)
{
    public bool HasLines => Lines.Count > 0;
}

/// <summary>One thing an import refused, named and explained.</summary>
public sealed record QuestImportHistoryLineViewModel(string What, string Why);

/// <summary>Your loyalty with one trader, and the stepper that sets it.</summary>
/// <param name="TraderId">The feed's id, which is what the profile is keyed by.</param>
/// <param name="Name">What the trader is called, which is what the row is labelled with.</param>
/// <param name="Level">0 to 4, held as decimal because that is what a spinner binds.</param>
public sealed record TraderLoyaltyViewModel(string TraderId, string Name, decimal Level);

public sealed class QuestImportProposalViewModel(
    QuestImportProposal proposal,
    QuestImportResolution? resolution = null)
{
    public string Key => proposal.Key;

    public string Classification => proposal.Classification switch
    {
        QuestImportClassification.SafeMonotonic => "Safe",
        QuestImportClassification.Conflict => "Conflict",
        QuestImportClassification.IgnoredUnchanged => "Unchanged",
        QuestImportClassification.UnresolvedUnknownId => "Unknown id",
        QuestImportClassification.UnresolvedSourceRecord => "Unreadable record",
        _ => proposal.Classification.ToString(),
    };

    public string Entity => $"{proposal.EntityKind}: {proposal.EntityId}";

    public string Detail => proposal.Reason;

    public string LocalValue => $"Local: {FormatValue(proposal.LocalValue)}";

    public string IncomingValue => $"Incoming: {FormatValue(proposal.IncomingValue)}";

    public string Resolution => resolution?.ToString() ??
        (proposal.Classification == QuestImportClassification.Conflict ? "Unresolved" : "Automatic");

    private static string FormatValue(QuestImportValue? value)
    {
        if (value is null)
        {
            return "no record";
        }

        if (value.TaskState is { } taskState)
        {
            return taskState.ToString();
        }

        if (value.ObjectiveState is { } objectiveState)
        {
            return value.ObjectiveCount is { } count
                ? string.Create(CultureInfo.InvariantCulture, $"{objectiveState} · count {count:0.##}")
                : objectiveState.ToString();
        }

        if (value.HoldingCount is { } holdingCount && value.HoldingFoundInRaid is { } foundInRaid)
        {
            return $"{holdingCount} · {(foundInRaid ? "found in raid" : "not found in raid")}";
        }

        if (value.PinTargetKind is { } pinKind && value.PinSortOrder is { } sortOrder)
        {
            var note = string.IsNullOrWhiteSpace(value.PinNote) ? "no note" : $"“{value.PinNote}”";
            return $"{pinKind} pin · order {sortOrder} · {note}";
        }

        return "unsupported value";
    }
}

public sealed class QuestObjectiveViewModel : BindableViewModel
{
    private readonly QuestsPageViewModel _owner;
    private readonly RecordedTaskState _taskState;
    private string _maps = string.Empty;
    private string _items = string.Empty;

    internal QuestObjectiveViewModel(
        QuestObjectiveReadModel objective,
        RecordedTaskState taskState,
        QuestsPageViewModel owner)
    {
        Model = objective;
        _owner = owner;
        _taskState = taskState;
        SetUnknownCommand = new AsyncDelegateCommand(() =>
            _owner.SetObjectiveAsync(this, RecordedObjectiveState.Unknown, null));
        SetInProgressCommand = new AsyncDelegateCommand(() =>
            _owner.SetObjectiveAsync(this, RecordedObjectiveState.InProgress, CountForInProgress(Model)));
        SetCompletedCommand = new AsyncDelegateCommand(() =>
            _owner.SetObjectiveAsync(this, RecordedObjectiveState.Completed, Model.TargetCount ?? Model.RecordedCount));
        DecrementCommand = new AsyncDelegateCommand(() =>
            _owner.SetObjectiveAsync(
                this,
                RecordedObjectiveState.InProgress,
                Math.Max(0, (Model.RecordedCount ?? 0) - 1)));
        IncrementCommand = new AsyncDelegateCommand(() =>
            _owner.SetObjectiveAsync(
                this,
                RecordedObjectiveState.InProgress,
                Model.TargetCount is { } target
                    ? Math.Min(target, (Model.RecordedCount ?? 0) + 1)
                    : (Model.RecordedCount ?? 0) + 1));
        TogglePinCommand = new AsyncDelegateCommand(() => _owner.ToggleObjectivePinAsync(this));

        Status = Model.RecordedCount is null
            ? Model.RecordedState.ToString()
            : string.Create(CultureInfo.InvariantCulture, $"{Model.RecordedState} · {Model.RecordedCount:0.##}/{Model.TargetCount?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?"}");
        Source = Model.ProgressModifiedUtc is { } modified
            ? $"{Model.ProgressSource} · {QuestsPageViewModel.FormatAge(modified, owner.NowUtc)}"
            : Model.ProgressSource;
        ApplyNames();
    }

    /// <summary>
    /// Rewrites the map and item lines from whatever the page has resolved so far.
    /// </summary>
    /// <remarks>
    /// Both used to print the source's own identifiers, so an objective read "Map:
    /// 5704e554d2720bac5b8b456e" and "markerItem: 5991b51486f77447b112d44f" where it meant
    /// Shoreline and an MS2000 Marker. Map names are in memory as soon as the catalog is, and
    /// item names arrive a moment later when the selected quest's are looked up, so this is
    /// called twice rather than the row being rebuilt.
    /// </remarks>
    internal void ApplyNames()
    {
        Maps = _owner.DescribeMaps(Model.MapIds);
        Items = QuestItemRequirementFormatter.DescribeForQuest(Model, _taskState, _owner.NameOfItem);
    }

    public QuestObjectiveReadModel Model { get; }

    public string ObjectiveId => Model.ObjectiveId;

    public string Description => Model.Description;

    public string Kind => Model.Kind.ToString();

    public string Status { get; }

    public string Source { get; }

    public string Maps
    {
        get => _maps;
        private set => SetProperty(ref _maps, value);
    }

    public string Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    public bool HasItems => Model.ItemTargets.Count > 0;

    public bool HasFoundInRaidRule => Model.FoundInRaidRequired is not null;

    public string FoundInRaidRule => Model.FoundInRaidRequired == true
        ? "Found in raid"
        : "Not found in raid";

    public bool IsUnsupported => Model.IsUnsupported;

    public bool IsOptional => Model.IsOptional == true;

    public bool HasCount => Model.TargetCount is not null || Model.RecordedCount is not null;

    public string PinLabel => Model.IsPinned ? "Unpin objective" : "Pin objective";

    public ICommand SetUnknownCommand { get; }

    public ICommand SetInProgressCommand { get; }

    public ICommand SetCompletedCommand { get; }

    public ICommand DecrementCommand { get; }

    public ICommand IncrementCommand { get; }

    public ICommand TogglePinCommand { get; }

    public static decimal? CountForInProgress(QuestObjectiveReadModel objective) =>
        objective.RecordedCount ?? (objective.TargetCount is null ? null : 0);
}

public sealed class QuestTaskViewModel
{
    private readonly QuestsPageViewModel _owner;
    private string? _searchableText;

    internal QuestTaskViewModel(QuestSummaryReadModel task, QuestsPageViewModel owner)
    {
        Model = task;
        _owner = owner;
        Objectives = task.Objectives.Select(objective => new QuestObjectiveViewModel(objective, task.RecordedState, owner)).ToArray();
        SelectCommand = new DelegateCommand(() => _owner.Select(this));
        SetUnknownCommand = new AsyncDelegateCommand(() => _owner.SetTaskAsync(this, RecordedTaskState.Unknown));
        SetNotStartedCommand = new AsyncDelegateCommand(() => _owner.SetTaskAsync(this, RecordedTaskState.NotStarted));
        SetActiveCommand = new AsyncDelegateCommand(() => _owner.SetTaskAsync(this, RecordedTaskState.Active));
        SetCompletedCommand = new AsyncDelegateCommand(() => _owner.SetTaskAsync(this, RecordedTaskState.Completed));
        SetFailedCommand = new AsyncDelegateCommand(() => _owner.SetTaskAsync(this, RecordedTaskState.Failed));
        TogglePinCommand = new AsyncDelegateCommand(() => _owner.ToggleTaskPinAsync(this));
        OpenWikiCommand = new DelegateCommand(() => _owner.TryOpenWiki(Model.WikiUri));
    }

    public QuestSummaryReadModel Model { get; }

    /// <summary>Whether the catalog gave this task an allowed wiki link to open.</summary>
    public bool HasWikiLink => WikiLinkPolicy.IsAllowed(Model.WikiUri);

    public ICommand OpenWikiCommand { get; }

    public string TaskId => Model.TaskId;

    public string Name => Model.Name;

    public string RecordedState => Model.RecordedState.ToString();

    /// <summary>Whether this quest can be picked up, in the player's words.</summary>
    /// <remarks>
    /// "Indeterminate" is the reason this exists. It is an accurate name for a state the
    /// evaluator can reach and a useless thing to show somebody deciding what to do next.
    /// </remarks>
    public string Eligibility => Model.Eligibility.State switch
    {
        QuestEligibilityState.Available => "Available now",
        QuestEligibilityState.Locked => "Locked",
        QuestEligibilityState.Delayed => "Waiting on a timer",
        _ => "Not known",
    };

    public string EligibilityDetail => Model.Eligibility.Reasons.Count == 0
        ? "No catalog eligibility warnings."
        : string.Join(" · ", Model.Eligibility.Reasons.Select(reason => reason.Detail));

    public string ObjectiveSummary => $"{Objectives.Count(objective => objective.Model.RecordedState == RecordedObjectiveState.Completed)}/{Objectives.Count} recorded complete · {Model.RecordedObjectivesSatisfied}";

    /// <summary>
    /// Where this quest's recorded state came from, and when.
    /// </summary>
    /// <remarks>
    /// The stored source is a code, and two of them are worth naming in words on the page: a
    /// state the player typed and a state the game announced. A player who has just handed a
    /// quest in wants to see that the companion noticed it by itself.
    /// </remarks>
    public string Source
    {
        get
        {
            var origin = Model.ProgressSource switch
            {
                QuestProgressSources.GameLog => "From the game",
                QuestProgressSources.Manual => "Recorded by you",
                var other => other,
            };
            return Model.ProgressModifiedUtc is { } modified
                ? $"{origin} · {QuestsPageViewModel.FormatAge(modified, _owner.NowUtc)}"
                : origin;
        }
    }

    /// <summary>Whether this quest's state is the game's own word rather than the player's.</summary>
    public bool StateCameFromTheGame =>
        string.Equals(Model.ProgressSource, QuestProgressSources.GameLog, StringComparison.Ordinal);

    /// <summary>Who gives this quest, named rather than identified.</summary>
    /// <remarks>
    /// Printed "Trader: 54cb50c76803fa8b248b4571" until now, which is what the feed puts in a
    /// task's trader field. The traders table has held the names since migration 0001 and
    /// nothing read it.
    ///
    /// Falls back to the id where the catalog has no name for it. Wrong is worse than ugly,
    /// and a trader the catalog does not know about is something to notice.
    /// </remarks>
    public string Trader => string.IsNullOrWhiteSpace(Model.TraderId)
        ? "Trader not supplied"
        : $"Trader: {Model.TraderName ?? Model.TraderId}";

    /// <summary>The map this quest is mostly on, named rather than identified.</summary>
    /// <remarks>
    /// Printed the catalog's own id until now, so a quest on Customs read "Primary map:
    /// 56f40101d2720b2a4d8b45d6". The objective rows beneath it have named their maps since
    /// those ids were first resolved; this is the same lookup, and it is also what makes the
    /// search box able to find a quest by the map it is on.
    /// </remarks>
    public string Map => string.IsNullOrWhiteSpace(Model.PrimaryMapId)
        ? "No primary map"
        : $"Primary map: {_owner.DescribeMaps([Model.PrimaryMapId])}";

    public string PinLabel => Model.IsPinned ? "Unpin task" : "Pin task";

    public string BranchNotes => string.Join(" · ", new[]
    {
        Model.Restartable == true ? "Restartable" : null,
        Model.HasFailureConditions
            ? $"Fails if: {string.Join("; ", Model.FailureConditionNotes)}"
            : null,
    }.OfType<string>());

    public string Prerequisites => Model.Prerequisites.Count == 0
        ? "No catalog prerequisites."
        : string.Join(" · ", Model.Prerequisites.Select(requirement =>
            $"{requirement.RequiredTaskId}: {requirement.RecordedState} (requires {string.Join("/", requirement.RequiredStatuses)})"));

    public IReadOnlyList<QuestObjectiveViewModel> Objectives { get; }

    /// <summary>
    /// Everything the search box reads on this quest, joined once and kept.
    /// </summary>
    /// <remarks>
    /// Item names are deliberately absent. They are looked up for the quest being read and for
    /// no other, so searching them would find a quest the player had already opened and
    /// silently miss an identical one they had not — worse than not searching them at all.
    /// Objective descriptions carry most of those names in prose anyway.
    ///
    /// The trader's id is in here as well as the name, which costs nothing and means a search
    /// pasted from the feed still finds its quest. Nobody will type one; somebody chasing a
    /// figure from upstream will paste one.
    /// </remarks>
    internal string SearchableText => _searchableText ??= string.Join(
        '\n',
        new[] { Name, Trader, Model.TraderId ?? string.Empty, Map }
            .Concat(Objectives.Select(objective => objective.Description))
            .Concat(Objectives.Select(objective => objective.Maps)));

    /// <summary>Forgets the joined text, for when the names it was built from have resolved.</summary>
    /// <remarks>
    /// The map catalog loads after the quest board does, so text built in between holds ids
    /// where it should hold names and a search for "Customs" finds nothing.
    /// </remarks>
    internal void ForgetSearchableText() => _searchableText = null;

    public bool IsPinned => Model.IsPinned;

    public bool HasBranchNotes => !string.IsNullOrWhiteSpace(BranchNotes);

    public ICommand SelectCommand { get; }

    public ICommand SetUnknownCommand { get; }

    public ICommand SetNotStartedCommand { get; }

    public ICommand SetActiveCommand { get; }

    public ICommand SetCompletedCommand { get; }

    public ICommand SetFailedCommand { get; }

    public ICommand TogglePinCommand { get; }
}

public sealed class QuestsPageViewModel : PageViewModel
{
    private static readonly IReadOnlyList<QuestBoardFilter> Filters =
    [
        new("focus", "Active / pinned"),
        new("all", "All quests"),
        new("available", "Available"),
        new("locked", "Locked"),
        new("indeterminate", "Indeterminate"),
        new("completed", "Completed"),
        new("failed", "Failed"),
        new("unsupported", "Unsupported objectives"),
    ];

    private readonly IPlayerProfileService _profileService;
    private readonly IQuestReadService _readService;
    private readonly IQuestProgressCommandService _commandService;
    private readonly IQuestProgressExchangeService _exchangeService;
    private readonly ITarkovTrackerIntegrationService _tarkovTracker;
    private readonly MapViewModel _map;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IReadOnlyList<QuestTaskViewModel> _allTasks = [];
    private IReadOnlyList<QuestTaskViewModel> _tasks = [];
    private IReadOnlyList<string> _orphanedProgress = [];
    private QuestTaskViewModel? _selectedTask;
    private QuestBoardFilter _selectedFilter = Filters[0];
    private string _searchQuery = string.Empty;
    private string _emptyBoardText = string.Empty;
    private int _namedMapCount = -1;
    private QuestProfileScope? _scope;
    private string _scopeStatus = "Profile scope not loaded";
    private string _catalogStatus = "Quest catalog not loaded";
    private string _status = "Loading local quest progress…";
    private DateTimeOffset? _lastRuntimeDataUtc;
    private bool _initialized;
    private string _exchangePath;
    private string _exchangeStatus = "Not run";
    private string _importPreviewSummary = "No import preview loaded.";
    private IReadOnlyList<QuestImportProposalViewModel> _importProposals = [];
    private QuestProgressImportPreview? _importPreview;
    private Dictionary<string, QuestImportResolution> _importResolutions = new(StringComparer.Ordinal);
    private Guid? _lastImportId;
    private string _tarkovTrackerToken = string.Empty;
    private string _tarkovTrackerStatus = "Not checked";
    private bool _canConnectTarkovTracker;
    private bool _canRefreshTarkovTracker;
    private bool _canDisconnectTarkovTracker;

    private readonly IItemRepository? _itemRepository;
    private readonly IQuestProgressImportHistory? _importHistory;
    private readonly ITraderCatalog? _traderCatalog;
    private readonly IWikiLinkOpener? _wikiLinkOpener;
    private IReadOnlyList<TraderLoyaltyViewModel> _traderLoyalty = [];
    private IReadOnlyList<QuestImportHistoryRowViewModel> _importHistoryRows = [];
    private readonly Dictionary<string, string> _itemNames = new(StringComparer.Ordinal);
    private readonly QuestLogProgressService? _questLog;
    private string _gameLogStatus = string.Empty;

    public QuestsPageViewModel(
        IPlayerProfileService profileService,
        IQuestReadService readService,
        IQuestProgressCommandService commandService,
        IQuestProgressExchangeService exchangeService,
        ITarkovTrackerIntegrationService tarkovTracker,
        AppDataPaths paths,
        MapViewModel map,
        TimeProvider timeProvider,
        // Optional so a composition without an item catalog is still a valid composition:
        // without one the objectives read as ids, which is what they did before.
        IItemRepository? itemRepository = null,
        // Optional for the same reason. Without it the page loses the record of what past
        // imports refused, which is what it had until now.
        IQuestProgressImportHistory? importHistory = null,
        // Optional again. Without it there is nothing to name the traders, so the loyalty rows
        // are not offered at all rather than offered as hexadecimal ids.
        ITraderCatalog? traderCatalog = null,
        // Optional for the same reason as the others: without it, a task with a wiki link has
        // nothing that can open one, so the Wiki action stays hidden instead of failing.
        IWikiLinkOpener? wikiLinkOpener = null,
        // Package 47: what the game's logs have said about quests this session. Without it this
        // page cannot tell "the game has reported nothing" from "the game reported nothing new",
        // and a quest handed in while the page is open does not appear until it is reloaded.
        QuestLogProgressService? questLog = null)
        : base(
            "Quests",
            "What you are working on, and what each one needs",
            "Not loaded")
    {
        _profileService = profileService;
        _readService = readService;
        _commandService = commandService;
        _exchangeService = exchangeService;
        _tarkovTracker = tarkovTracker;
        _exchangePath = Path.Combine(paths.Support, "quest-progress.json");
        _map = map;
        _timeProvider = timeProvider;
        _itemRepository = itemRepository;
        _importHistory = importHistory;
        _traderCatalog = traderCatalog;
        _wikiLinkOpener = wikiLinkOpener;
        _questLog = questLog;
        if (_questLog is not null)
        {
            _questLog.Changed += OnQuestLogChanged;
            UpdateGameLogStatus(_questLog.Reading);
        }

        RefreshCommand = new AsyncDelegateCommand(RefreshAsync);
        ExportProgressCommand = new AsyncDelegateCommand(ExportProgressAsync);
        PreviewImportCommand = new AsyncDelegateCommand(PreviewImportAsync);
        KeepLocalConflictsCommand = new DelegateCommand(() => ResolveConflicts(QuestImportResolution.KeepLocal));
        UseIncomingConflictsCommand = new DelegateCommand(() => ResolveConflicts(QuestImportResolution.UseIncoming));
        ApplyImportCommand = new AsyncDelegateCommand(ApplyImportAsync);
        UndoLastImportCommand = new AsyncDelegateCommand(UndoLastImportAsync);
        ConnectTarkovTrackerCommand = new AsyncDelegateCommand(ConnectTarkovTrackerAsync);
        DisconnectTarkovTrackerCommand = new AsyncDelegateCommand(DisconnectTarkovTrackerAsync);
        RefreshTarkovTrackerCommand = new AsyncDelegateCommand(RefreshTarkovTrackerAsync);
        ClearSearchCommand = new DelegateCommand(() => SearchQuery = string.Empty);
        ShowAllQuestsCommand = new DelegateCommand(() => SelectedFilter = Filters[1]);
    }

    internal bool TryOpenWiki(string? wikiUri) => _wikiLinkOpener?.TryOpen(wikiUri) ?? false;

    /// <summary>
    /// The imports already applied, and what each one refused.
    /// </summary>
    /// <remarks>
    /// Every import records exactly which changes it would not make and why, and nothing had
    /// ever read either table. So an import that half-worked said "kept 3 local · 2 unresolved"
    /// once, in a status line, and could never say which three or which two.
    /// </remarks>
    public IReadOnlyList<QuestImportHistoryRowViewModel> ImportHistory
    {
        get => _importHistoryRows;
        private set
        {
            SetProperty(ref _importHistoryRows, value);
            OnPropertyChanged(nameof(HasImportHistory));
        }
    }

    public bool HasImportHistory => _importHistoryRows.Count > 0;

    public IReadOnlyList<QuestBoardFilter> AvailableFilters => Filters;

    public DateTimeOffset NowUtc => _timeProvider.GetUtcNow();

    public QuestBoardFilter SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value))
            {
                OnPropertyChanged(nameof(CanShowAllQuests));
                ApplyFilter(SelectedTask?.TaskId);
            }
        }
    }

    /// <summary>What the player typed into the quest search box.</summary>
    /// <remarks>
    /// Five hundred quests behind one dropdown of eight states was every way of finding one
    /// except the way anybody actually looks for a quest, which is by its name. It narrows the
    /// chosen filter rather than replacing it, and when the two disagree the board says so
    /// instead of going blank.
    /// </remarks>
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                OnPropertyChanged(nameof(HasSearchQuery));
                ApplyFilter(SelectedTask?.TaskId);
            }
        }
    }

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(_searchQuery);

    /// <summary>Whether the board is showing nothing, and so owes the reader a way out.</summary>
    public bool HasVisibleTasks => Tasks.Count > 0;

    /// <summary>Why the board is empty, in the terms of whichever of the two emptied it.</summary>
    public string EmptyBoardText
    {
        get => _emptyBoardText;
        private set => SetProperty(ref _emptyBoardText, value);
    }

    /// <summary>Whether widening to every quest is a move that would change anything.</summary>
    public bool CanShowAllQuests => SelectedFilter.Id != "all" && _allTasks.Count > 0;

    public ICommand ClearSearchCommand { get; }

    public ICommand ShowAllQuestsCommand { get; }

    public IReadOnlyList<QuestTaskViewModel> Tasks
    {
        get => _tasks;
        private set
        {
            SetProperty(ref _tasks, value);
            OnPropertyChanged(nameof(HasVisibleTasks));
        }
    }

    public QuestTaskViewModel? SelectedTask
    {
        get => _selectedTask;
        private set
        {
            if (SetProperty(ref _selectedTask, value))
            {
                OnPropertyChanged(nameof(HasSelectedTask));
                OnPropertyChanged(nameof(HasNoSelectedTask));
            }
        }
    }

    /// <summary>
    /// Whether the detail column has a task to describe.
    /// </summary>
    /// <remarks>
    /// Bound through a null task, every chip and button in the detail column still rendered,
    /// as a row of empty boxes above a row of verbs with nothing to act on. The column is
    /// hidden outright until there is a task, and one line says why.
    /// </remarks>
    public bool HasSelectedTask => SelectedTask is not null;

    public bool HasNoSelectedTask => SelectedTask is null;

    public IReadOnlyList<string> OrphanedProgress
    {
        get => _orphanedProgress;
        private set
        {
            SetProperty(ref _orphanedProgress, value);
            OnPropertyChanged(nameof(HasOrphanedProgress));
        }
    }

    public bool HasOrphanedProgress => OrphanedProgress.Count > 0;

    public string ScopeStatus
    {
        get => _scopeStatus;
        private set => SetProperty(ref _scopeStatus, value);
    }

    public string CatalogStatus
    {
        get => _catalogStatus;
        private set => SetProperty(ref _catalogStatus, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// What the game's logs have told this session about quests, in one line.
    /// </summary>
    /// <remarks>
    /// The game announces every quest starting, failing and being handed in, and this page had
    /// no way of saying whether any of that had reached it. A player who finished quests in a
    /// raid and saw nothing change could not tell a companion that had read nothing from a
    /// companion that had read everything and found nothing new.
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

    public AsyncDelegateCommand ExportProgressCommand { get; }

    public AsyncDelegateCommand PreviewImportCommand { get; }

    public DelegateCommand KeepLocalConflictsCommand { get; }

    public DelegateCommand UseIncomingConflictsCommand { get; }

    public AsyncDelegateCommand ApplyImportCommand { get; }

    public AsyncDelegateCommand UndoLastImportCommand { get; }

    public AsyncDelegateCommand ConnectTarkovTrackerCommand { get; }

    public AsyncDelegateCommand DisconnectTarkovTrackerCommand { get; }

    public AsyncDelegateCommand RefreshTarkovTrackerCommand { get; }

    public string TarkovTrackerToken
    {
        get => _tarkovTrackerToken;
        set => SetProperty(ref _tarkovTrackerToken, value);
    }

    public string TarkovTrackerStatus
    {
        get => _tarkovTrackerStatus;
        private set => SetProperty(ref _tarkovTrackerStatus, value);
    }

    public bool CanConnectTarkovTracker
    {
        get => _canConnectTarkovTracker;
        private set => SetProperty(ref _canConnectTarkovTracker, value);
    }

    public bool CanRefreshTarkovTracker
    {
        get => _canRefreshTarkovTracker;
        private set => SetProperty(ref _canRefreshTarkovTracker, value);
    }

    public bool CanDisconnectTarkovTracker
    {
        get => _canDisconnectTarkovTracker;
        private set => SetProperty(ref _canDisconnectTarkovTracker, value);
    }

    public string ExchangePath
    {
        get => _exchangePath;
        set => SetProperty(ref _exchangePath, value);
    }

    public string ExchangeStatus
    {
        get => _exchangeStatus;
        private set => SetProperty(ref _exchangeStatus, value);
    }

    public string ImportPreviewSummary
    {
        get => _importPreviewSummary;
        private set => SetProperty(ref _importPreviewSummary, value);
    }

    public IReadOnlyList<QuestImportProposalViewModel> ImportProposals
    {
        get => _importProposals;
        private set
        {
            SetProperty(ref _importProposals, value);
            OnPropertyChanged(nameof(HasImportProposals));
        }
    }

    public bool HasImportProposals => ImportProposals.Count > 0;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _initialized = true;
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    public void ApplyRuntime(ApplicationRuntimeSnapshot snapshot)
    {
        Evidence = $"{snapshot.Data.Availability}";
        if (_initialized && snapshot.Data.UpdatedUtc != _lastRuntimeDataUtc)
        {
            _lastRuntimeDataUtc = snapshot.Data.UpdatedUtc;
            _ = RefreshAsync();
        }
    }

    /// <summary>The lowest and highest a player can be, so a typo cannot gate every quest.</summary>
    public const int MinimumLevel = 1;

    public const int MaximumLevel = 79;

    private int _playerLevel = MinimumLevel;

    /// <summary>
    /// What level this profile is, typed in because the game never says.
    /// </summary>
    /// <remarks>
    /// Reported as 515 quests all reading Unknown, with an active one saying "Recorded player
    /// level 1 is below required level 19". The stored profile is created at level 1 and
    /// nothing ever wrote to it again, so every level requirement on the page was measured
    /// against a number the player had no way to correct.
    ///
    /// The game is no help. Its logs carry a level on two thousand lines and every one of them
    /// belongs to somebody else: a party member, a dogtag, or a player accepting an invite.
    /// Notifications about the local player carry a profile id and no profile detail, and the
    /// rich block the level lives in only ever describes another player. Trader loyalty does
    /// not appear at all. So this is typed in, or it comes from a TarkovTracker import.
    ///
    /// Held as <see cref="decimal"/> because that is what the spinner binds, and a cleared box
    /// is ignored rather than treated as a level.
    /// </remarks>
    public decimal? PlayerLevel
    {
        get => _playerLevel;
        set => _ = SetPlayerLevelAsync(value);
    }

    /// <summary>
    /// Stores a typed level and re-reads the board, because every requirement line on the page
    /// is measured against it.
    /// </summary>
    /// <remarks>
    /// A cleared box is not a level and is ignored. Anything outside the game's own range is
    /// pulled back into it rather than refused, because a spinner holding 800 gates the whole
    /// catalog just as thoroughly as one holding 1.
    /// </remarks>
    public async Task SetPlayerLevelAsync(decimal? value)
    {
        if (value is not { } entered)
        {
            return;
        }

        var level = (int)Math.Clamp(entered, MinimumLevel, MaximumLevel);
        if (level == _playerLevel)
        {
            return;
        }

        _playerLevel = level;
        OnPropertyChanged(nameof(PlayerLevel));
        try
        {
            var profile = await _profileService.GetActiveAsync(CancellationToken.None).ConfigureAwait(true);
            if (profile.Level != level)
            {
                await _profileService
                    .SaveAsync(profile with { Level = level, UpdatedUtc = NowUtc }, CancellationToken.None)
                    .ConfigureAwait(true);
            }

            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"Level not saved · {exception.Message}";
        }
    }

    /// <summary>
    /// Your loyalty with each trader, which nothing in the application could set.
    /// </summary>
    /// <remarks>
    /// TraderLevels has been on the profile since it was written, is validated on save to the
    /// game's own range of 0 to 4, and is written by nothing anywhere. AmmoIntelligenceService
    /// reads it — ObtainableForProfile compares a rule's required loyalty against it — so every
    /// rule that needed any loyalty at all could never fire. The barter reader wants it too.
    ///
    /// Zero is a real answer and stays reachable. A trader you have not unlocked and a trader
    /// you have not told us about are both level 0, and there is no way to tell them apart
    /// without reading a profile the logs never write.
    /// </remarks>
    public IReadOnlyList<TraderLoyaltyViewModel> TraderLoyalty
    {
        get => _traderLoyalty;
        private set
        {
            SetProperty(ref _traderLoyalty, value);
            OnPropertyChanged(nameof(HasTraderLoyalty));
        }
    }

    public bool HasTraderLoyalty => _traderLoyalty.Count > 0;

    /// <summary>Records loyalty with one trader.</summary>
    /// <remarks>
    /// Clamped to the game's range here as well as in the spinner, because the profile writer
    /// refuses anything outside it and a refused save would lose the whole profile edit rather
    /// than one number.
    /// </remarks>
    public async Task SetTraderLevelAsync(string traderId, decimal level)
    {
        if (string.IsNullOrWhiteSpace(traderId))
        {
            return;
        }

        var wanted = (int)Math.Clamp(level, 0, 4);
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
                .SaveAsync(profile with { TraderLevels = levels, UpdatedUtc = NowUtc }, CancellationToken.None)
                .ConfigureAwait(true);
            UpdateTraderLoyalty(levels);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"Trader loyalty not saved · {exception.Message}";
        }
    }

    /// <summary>
    /// Reads the trader names once, so the rows have something to be called.
    /// </summary>
    /// <remarks>
    /// Without a catalog there are no rows at all. A stepper labelled
    /// 54cb50c76803fa8b248b4571 is worse than no stepper: nobody can tell which trader they
    /// are setting, so anything they set is as likely to be wrong as right.
    /// </remarks>
    private async Task LoadTraderNamesAsync(CancellationToken cancellationToken)
    {
        if (_traderCatalog is null || _traderNames is not null)
        {
            return;
        }

        try
        {
            _traderNames = await _traderCatalog.GetNamesAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The rest of the page is unaffected; there are simply no loyalty rows.
            _traderNames = new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void UpdateTraderLoyalty(IReadOnlyDictionary<string, int> levels) => TraderLoyalty = _traderNames is null
        ? []
        :
        [
            .. _traderNames
                .OrderBy(trader => trader.Value, StringComparer.CurrentCultureIgnoreCase)
                .Select(trader => new TraderLoyaltyViewModel(
                    trader.Key,
                    trader.Value,
                    levels.GetValueOrDefault(trader.Key))),
        ];

    private IReadOnlyDictionary<string, string>? _traderNames;

    public Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var selectedTaskId = SelectedTask?.TaskId;
            var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(true);
            _scope = new(profile.Id, profile.GameMode, profile.ProfileGeneration);
            if (_playerLevel != profile.Level)
            {
                // Assigned to the field rather than the property: the property saves, and this
                // is the stored value arriving rather than somebody typing one.
                _playerLevel = profile.Level;
                OnPropertyChanged(nameof(PlayerLevel));
            }

            ScopeStatus = $"{profile.Name} · {profile.GameMode}";
            await LoadTraderNamesAsync(cancellationToken).ConfigureAwait(true);
            UpdateTraderLoyalty(profile.TraderLevels);
            await RefreshTarkovTrackerStatusAsync(_scope, cancellationToken).ConfigureAwait(true);
            await LoadImportHistoryAsync(_scope, cancellationToken).ConfigureAwait(true);
            var board = await _readService.GetQuestBoardAsync(_scope, cancellationToken).ConfigureAwait(true);
            _allTasks = board.Tasks.Select(task => new QuestTaskViewModel(task, this)).ToArray();
            OrphanedProgress = board.OrphanedProgress
                .Select(orphan => $"{orphan.EntityKind} {orphan.ExternalId}: {orphan.RecordedValue}")
                .ToArray();
            CatalogStatus = board.CatalogProvenance is { } provenance
                ? $"Catalog {provenance.SourceMode}/{provenance.Language} · {FormatAge(provenance.ValidatedUtc, NowUtc)}"
                : board.UnavailableReason ?? "Quest catalog unavailable.";
            Evidence = board.CatalogProvenance is null
                ? "Catalog unavailable"
                : $"{board.Tasks.Count} quests · {board.CatalogProvenance.SourceMode}";
            ApplyFilter(selectedTaskId);
            UpdateGameLogStatus(_questLog?.Reading);
            Status = board.UnavailableReason ?? (Tasks.Count == 0
                ? EmptyBoardText
                : $"{Tasks.Count} of {_allTasks.Count} quests");
            await _map.RefreshQuestLayerAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Tasks = [];
            SelectedTask = null;
            Status = $"Unavailable · {exception.Message}";
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// The game said something about a quest, so the page the player is looking at reloads.
    /// </summary>
    /// <remarks>
    /// Raised on whichever thread was reading the log, so it is marshalled here. Without this,
    /// the board only ever refreshed after a change made on the page itself, and a hand-in read
    /// out of a log while the page was open sat in the database unseen.
    /// </remarks>
    private void OnQuestLogChanged(object? sender, QuestLogProgressReading reading)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnQuestLogChanged(sender, reading));
            return;
        }

        UpdateGameLogStatus(reading);
        _ = RefreshAsync(CancellationToken.None);
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
            GameLogStatus = "The game hasn't reported a quest yet this session.";
            return;
        }

        var when = reading.LastObservedUtc?.ToLocalTime();
        var heard = when is { } moment
            ? $"The game last reported a quest at {moment:HH:mm}"
            : "The game has reported quests";
        var what = reading.Recorded == 1
            ? "; 1 updated this board"
            : $"; {reading.Recorded} updated this board";
        var caveat = (reading.Unmatched, reading.Failed) switch
        {
            (0, 0) => ".",
            (> 0, 0) => $". {reading.Unmatched} not in the loaded catalog.",
            (0, > 0) => $". {reading.Failed} couldn't be saved.",
            var (unmatched, failed) => $". {unmatched} not in the catalog, {failed} couldn't be saved.",
        };
        GameLogStatus = heard + what + caveat;
    }

    internal void Select(QuestTaskViewModel task)
    {
        SelectedTask = task;
        // Only the quest being looked at. The catalog holds five hundred of them and
        // several thousand distinct item ids between them; naming the handful on screen
        // is one query each and naming all of them is a database walk per refresh.
        _ = NameItemsAsync(task);
    }

    /// <summary>
    /// Names the maps an objective is on, rather than listing the source's identifiers.
    /// </summary>
    /// <remarks>
    /// The quest catalog and the map catalog are separate feeds and agree on neither form of
    /// id, so a location is matched on either the slug the application uses or the upstream id
    /// it carries alongside, exactly as the quest map projection already does. An id that
    /// matches nothing is printed as it is: wrong is worse than ugly, and a missing map is
    /// something to notice.
    /// </remarks>
    internal string DescribeMaps(IReadOnlyList<string> mapIds)
    {
        if (mapIds.Count == 0)
        {
            return "No map";
        }

        var names = mapIds
            .Select(NameOfMap)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return string.Join(", ", names);
    }

    private string NameOfMap(string mapId) => _map.Locations
        .FirstOrDefault(location =>
            string.Equals(location.Id, mapId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(location.SourceId, mapId, StringComparison.OrdinalIgnoreCase))?.Name
        ?? mapId;

    /// <summary>What an item is called, or its id until the lookup comes back.</summary>
    /// <summary>What a task is called, or its id where the board has not loaded it.</summary>
    /// <remarks>
    /// Off the board this page already holds, rather than a second read of the catalog. The
    /// board is loaded on startup and refreshed on every change, so by the time a raid ends it
    /// is there.
    /// </remarks>
    internal string NameOfTask(string taskId) => _allTasks
        .FirstOrDefault(task => string.Equals(task.TaskId, taskId, StringComparison.Ordinal))?.Name ?? taskId;

    /// <summary>
    /// The board this page has loaded, for anything that needs to ask it a question.
    /// </summary>
    /// <remarks>
    /// The same board and the same reason as <see cref="NameOfTask"/>: it is loaded on startup
    /// and refreshed on every change, and a second read of the catalog elsewhere would be a
    /// second answer that could disagree with the one on screen.
    ///
    /// Every task rather than the filtered list. A caller asking which maps a squadmate's
    /// quests are on is asking about the catalog, not about what this page is showing.
    /// </remarks>
    internal IReadOnlyList<QuestSummaryReadModel> BoardTasks() =>
        [.. _allTasks.Select(task => task.Model)];

    internal string NameOfItem(string itemId) =>
        _itemNames.TryGetValue(itemId, out var name) ? name : itemId;

    /// <summary>
    /// Looks up the names of the items one quest asks for, once each.
    /// </summary>
    /// <remarks>
    /// Fire and forget, and cached: selecting the same quest twice costs nothing, and a quest
    /// whose items are not in the synced catalog keeps showing ids rather than blanks.
    /// </remarks>
    private async Task NameItemsAsync(QuestTaskViewModel task)
    {
        if (_itemRepository is null)
        {
            return;
        }

        var unknown = task.Objectives
            .SelectMany(objective => objective.Model.ItemTargets)
            .Select(target => target.ItemId)
            .Distinct(StringComparer.Ordinal)
            .Where(itemId => !_itemNames.ContainsKey(itemId))
            .ToArray();
        if (unknown.Length == 0)
        {
            return;
        }

        var resolved = false;
        foreach (var itemId in unknown)
        {
            try
            {
                if (await _itemRepository.GetAsync(itemId, CancellationToken.None).ConfigureAwait(true) is { } item)
                {
                    _itemNames[itemId] = item.Name;
                    resolved = true;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One unreadable row costs one name, not the page.
            }
        }

        if (!resolved || !ReferenceEquals(SelectedTask, task))
        {
            return;
        }

        foreach (var objective in task.Objectives)
        {
            objective.ApplyNames();
        }
    }

    internal Task SetTaskAsync(QuestTaskViewModel task, RecordedTaskState state) => MutateAsync(async scope =>
        await _commandService.SetTaskStateAsync(scope, task.TaskId, state, CancellationToken.None).ConfigureAwait(true));

    internal Task SetObjectiveAsync(
        QuestObjectiveViewModel objective,
        RecordedObjectiveState state,
        decimal? count) => MutateAsync(async scope =>
        await _commandService.SetObjectiveProgressAsync(
            scope,
            objective.ObjectiveId,
            state,
            count,
            CancellationToken.None).ConfigureAwait(true));

    internal Task ToggleTaskPinAsync(QuestTaskViewModel task) => MutateAsync(async scope =>
        await _commandService.SetPinAsync(
            scope,
            QuestPinTargetKind.Task,
            task.TaskId,
            !task.Model.IsPinned,
            0,
            null,
            CancellationToken.None).ConfigureAwait(true));

    internal Task ToggleObjectivePinAsync(QuestObjectiveViewModel objective) => MutateAsync(async scope =>
        await _commandService.SetPinAsync(
            scope,
            QuestPinTargetKind.Objective,
            objective.ObjectiveId,
            !objective.Model.IsPinned,
            0,
            null,
            CancellationToken.None).ConfigureAwait(true));

    private async Task ConnectTarkovTrackerAsync()
    {
        if (_scope is null)
        {
            TarkovTrackerStatus = "Load a profile first";
            return;
        }

        try
        {
            var status = await _tarkovTracker.ConnectAsync(
                _scope,
                TarkovTrackerToken,
                CancellationToken.None).ConfigureAwait(true);
            UpdateTarkovTrackerStatus(status,
                "Token checked and saved");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TarkovTrackerStatus = $"Not connected · {exception.Message}";
        }
        finally
        {
            TarkovTrackerToken = string.Empty;
        }
    }

    private async Task DisconnectTarkovTrackerAsync()
    {
        if (_scope is null)
        {
            TarkovTrackerStatus = "Load a profile first";
            return;
        }

        try
        {
            var status = await _tarkovTracker.DisconnectAsync(_scope, CancellationToken.None)
                .ConfigureAwait(true);
            if (_importPreview?.Source == QuestProgressImportSource.TarkovTracker)
            {
                ClearImportPreview("Disconnected · the pending preview went with it");
            }

            UpdateTarkovTrackerStatus(status, "Disconnected · token deleted");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TarkovTrackerStatus = $"Disconnect failed: {exception.Message}";
        }
    }

    private async Task RefreshTarkovTrackerAsync()
    {
        if (_scope is null)
        {
            TarkovTrackerStatus = "Load a profile first";
            return;
        }

        try
        {
            var result = await _tarkovTracker.RefreshPreviewAsync(
                _scope,
                TarkovTrackerRefreshKind.Manual,
                CancellationToken.None).ConfigureAwait(true);
            LoadImportPreview(result.Preview);
            UpdateTarkovTrackerStatus(
                result.Status,
                result.NotModified
                    ? "Unchanged since last time · still counted against your quota"
                    : "Fetched · review it, then apply");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TarkovTrackerStatus = $"Not refreshed · {exception.Message}";
        }
    }

    private async Task RefreshTarkovTrackerStatusAsync(
        QuestProfileScope scope,
        CancellationToken cancellationToken)
    {
        try
        {
            UpdateTarkovTrackerStatus(
                await _tarkovTracker.GetStatusAsync(scope, cancellationToken).ConfigureAwait(true));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CanConnectTarkovTracker = false;
            CanRefreshTarkovTracker = false;
            CanDisconnectTarkovTracker = false;
            TarkovTrackerStatus = $"Unavailable · {exception.Message}";
        }
    }

    private void UpdateTarkovTrackerStatus(
        TarkovTrackerIntegrationStatus status,
        string? operation = null)
    {
        CanConnectTarkovTracker = status.CanConnect;
        CanRefreshTarkovTracker = status.CanRefresh;
        CanDisconnectTarkovTracker = status.SecureStorageAvailable && status.Connected;
        var availability = !status.SecureStorageAvailable
            ? "Off · this machine has no protected storage for the token"
            : !status.FeatureEnabled
                ? "Off"
                : !status.NetworkAccessEnabled
                    ? "Off in offline mode"
                    : status.RequiresReconnect
                        ? "Token rejected · reconnect"
                        : status.Connected
                            ? $"Connected · {status.GameMode}"
                            : $"Not connected · {status.GameMode}";
        var quota = status.Quota.Remaining is { } remaining
            ? $" Read quota remaining: {remaining}/{status.Quota.Limit?.ToString(CultureInfo.InvariantCulture) ?? "?"}."
            : " Read quota is unknown.";
        var backoff = status.NextEligibleRefreshUtc is { } next
            ? $" Next eligible refresh: {next:O}."
            : string.Empty;
        TarkovTrackerStatus = string.Join(" ", new[] { operation, availability }
                .Where(value => !string.IsNullOrWhiteSpace(value))) + quota + backoff;
    }

    private async Task ExportProgressAsync()
    {
        if (_scope is null)
        {
            ExchangeStatus = "Load a profile first";
            return;
        }

        try
        {
            var result = await _exchangeService.ExportAsync(
                _scope,
                ExchangePath,
                CancellationToken.None).ConfigureAwait(true);
            ExchangeStatus = $"Exported {result.RecordCount} records";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ExchangeStatus = $"Not exported · {exception.Message}";
        }
    }

    private async Task PreviewImportAsync()
    {
        if (_scope is null)
        {
            ExchangeStatus = "Load a profile first";
            return;
        }

        try
        {
            var preview = await _exchangeService.PreviewImportAsync(
                _scope,
                ExchangePath,
                CancellationToken.None).ConfigureAwait(true);
            LoadImportPreview(preview);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ClearImportPreview("No valid import preview loaded.");
            ExchangeStatus = $"No preview · {exception.Message}";
        }
    }

    private void ResolveConflicts(QuestImportResolution resolution)
    {
        if (_importPreview is null)
        {
            ExchangeStatus = "Preview something first";
            return;
        }

        _importResolutions = _importPreview.Conflicts.ToDictionary(
            value => value.Key,
            _ => resolution,
            StringComparer.Ordinal);
        RefreshImportProposalRows();
        ExchangeStatus = _importPreview.Conflicts.Count == 0
            ? "No conflicts · ready to apply"
            : $"{resolution} for {_importPreview.Conflicts.Count} conflicts · review, then apply";
    }

    private async Task ApplyImportAsync()
    {
        if (_importPreview is null)
        {
            ExchangeStatus = "Preview something first";
            return;
        }

        try
        {
            var result = await _exchangeService.ApplyImportAsync(
                _importPreview,
                _importResolutions,
                CancellationToken.None).ConfigureAwait(true);
            _lastImportId = result.ImportId;
            ExchangeStatus = result.AlreadyApplied
                ? $"Already applied as import {result.ImportId}"
                : $"Applied {result.AppliedChangeCount} · kept {result.KeptLocalCount} local · {result.UnresolvedCount} unresolved";
            ClearImportPreview("Applied · preview again for another");
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ExchangeStatus = $"Not applied · {exception.Message}";
        }
    }

    /// <summary>
    /// Reads back what past imports did, and what they refused to do.
    /// </summary>
    /// <remarks>
    /// Also what makes an undo survive a restart: the id an undo needs was held in memory only,
    /// so closing the application between importing and regretting it lost the way back.
    /// </remarks>
    private async Task LoadImportHistoryAsync(QuestProfileScope scope, CancellationToken cancellationToken)
    {
        if (_importHistory is null)
        {
            return;
        }

        try
        {
            var records = await _importHistory.GetRecentAsync(scope, ImportHistoryDepth, cancellationToken)
                .ConfigureAwait(true);
            ImportHistory = records.Select(Describe).ToArray();
            _lastImportId ??= records.Count > 0 ? records[0].ImportId : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The record of past imports is an extra. A database that will not answer must not
            // cost the player the quest board, which is the rest of this page.
            ImportHistory = [];
            ExchangeStatus = $"Past imports could not be read: {exception.Message}";
        }
    }

    /// <summary>How many past imports are worth a glance. Beyond this is an audit, not a page.</summary>
    private const int ImportHistoryDepth = 5;

    private static QuestImportHistoryRowViewModel Describe(QuestImportRecord record) => new(
        string.Create(
            CultureInfo.CurrentCulture,
            $"{record.ProfileName} · {record.ImportedUtc.ToLocalTime():g}"),
        string.Create(
            CultureInfo.CurrentCulture,
            $"Applied {record.AppliedChangeCount} · kept {record.KeptLocalCount} local · {record.Unresolved.Count} unresolved · from {record.SourceAppVersion}"),
        [.. record.Conflicts.Select(conflict => new QuestImportHistoryLineViewModel(
            $"{conflict.EntityKind} {conflict.EntityId}",
            conflict.Resolution == QuestImportResolution.KeepLocal
                ? $"Kept what was here · {conflict.Reason}"
                : $"Took what arrived · {conflict.Reason}")),
         .. record.Unresolved.Select(unresolved => new QuestImportHistoryLineViewModel(
            $"{unresolved.EntityKind} {unresolved.EntityId}",
            $"Not applied · {unresolved.Reason}"))]);

    private async Task UndoLastImportAsync()
    {
        if (_scope is null || _lastImportId is null)
        {
            ExchangeStatus = "Nothing to undo";
            return;
        }

        try
        {
            var result = await _exchangeService.UndoImportAsync(
                _scope,
                _lastImportId.Value,
                CancellationToken.None).ConfigureAwait(true);
            ExchangeStatus = result.AlreadyUndone
                ? "Already undone"
                : $"Undid {result.RestoredChangeCount} changes";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ExchangeStatus = $"Not undone · {exception.Message}";
        }
    }

    private void RefreshImportProposalRows()
    {
        ImportProposals = _importPreview?.Proposals.Select(proposal =>
            new QuestImportProposalViewModel(
                proposal,
                _importResolutions.GetValueOrDefault(proposal.Key))).ToArray() ?? [];
    }

    private void LoadImportPreview(QuestProgressImportPreview preview)
    {
        _importPreview = preview;
        _importResolutions = new(StringComparer.Ordinal);
        RefreshImportProposalRows();
        ImportPreviewSummary = preview.Source switch
        {
            QuestProgressImportSource.LegacyProfileJsonV1 =>
                $"Legacy profile · {preview.SafeProposals.Count} safe · {preview.Conflicts.Count} conflicts",
            QuestProgressImportSource.TarkovTracker =>
                $"TarkovTracker · {preview.SafeProposals.Count} safe · {preview.Conflicts.Count} conflicts · {preview.Ignored.Count} unchanged · {preview.Unresolved.Count} unresolved",
            _ =>
                $"JSON file · {preview.SafeProposals.Count} safe · {preview.Conflicts.Count} conflicts · {preview.Ignored.Count} unchanged · {preview.Unresolved.Count} unresolved",
        };
        ExchangeStatus = preview.Conflicts.Count == 0
            ? "Ready · nothing missing from the source is ever deleted here"
            : "Choose Keep local or Use incoming for every conflict";
    }

    private void ClearImportPreview(string summary)
    {
        _importPreview = null;
        _importResolutions.Clear();
        ImportProposals = [];
        ImportPreviewSummary = summary;
    }

    internal static string FormatAge(DateTimeOffset timestamp, DateTimeOffset now)
    {
        var age = now - timestamp;
        if (age < TimeSpan.Zero)
        {
            return "future timestamp";
        }

        return age < TimeSpan.FromMinutes(1)
            ? $"{Math.Max(0, (int)age.TotalSeconds)}s ago"
            : age < TimeSpan.FromHours(1)
                ? $"{(int)age.TotalMinutes}m ago"
                : age < TimeSpan.FromDays(2)
                    ? $"{(int)age.TotalHours}h ago"
                    : $"{(int)age.TotalDays}d ago";
    }

    private async Task MutateAsync(Func<QuestProfileScope, Task> mutation)
    {
        if (_scope is null)
        {
            Status = "No profile loaded · nothing changed";
            return;
        }

        try
        {
            await mutation(_scope).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"Not changed · {exception.Message}";
        }
    }

    private void ApplyFilter(string? selectedTaskId = null)
    {
        // The map catalog loads after the quest board, so joined text built in between holds
        // ids where it should hold names. Counting the maps is how that arrival is noticed
        // without the catalog having to know a search box exists.
        if (_namedMapCount != _map.Locations.Count)
        {
            _namedMapCount = _map.Locations.Count;
            foreach (var task in _allTasks)
            {
                task.ForgetSearchableText();
            }
        }

        var terms = SearchTerms(SearchQuery);
        var found = terms.Length == 0
            ? _allTasks
            : _allTasks.Where(task => MatchesEveryTerm(task.SearchableText, terms)).ToArray();
        Tasks = found.Where(MatchesSelectedFilter).ToArray();
        SelectedTask = Tasks.FirstOrDefault(task => task.TaskId == selectedTaskId) ?? Tasks.FirstOrDefault();
        OnPropertyChanged(nameof(CanShowAllQuests));
        EmptyBoardText = DescribeEmptyBoard(
            Tasks.Count,
            found.Count,
            _allTasks.Count,
            SearchQuery.Trim(),
            SelectedFilter.Label);
        if (_initialized)
        {
            Status = Tasks.Count == 0
                ? EmptyBoardText
                : $"{Tasks.Count} of {_allTasks.Count} quests";
        }
    }

    /// <summary>The words a search is made of, empty when there is nothing to search for.</summary>
    public static string[] SearchTerms(string query) =>
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Whether a quest's text answers every word, in any order and anywhere in it.</summary>
    /// <remarks>
    /// Every word rather than any word. Typing a second word is how somebody narrows a list,
    /// and an any-word search widens it instead, which is the opposite of what the second word
    /// was typed for.
    /// </remarks>
    public static bool MatchesEveryTerm(string text, IReadOnlyList<string> terms)
    {
        foreach (var term in terms)
        {
            if (!text.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Says which of the search and the filter emptied the board, and implies the way out.
    /// </summary>
    /// <remarks>
    /// "Nothing matches this filter" was true of all four of these and useful in none of them.
    /// The one that matters is a search that found quests the filter then hid, because the
    /// player can see their quest does not exist when in fact it is two states away.
    /// </remarks>
    public static string DescribeEmptyBoard(
        int visible,
        int matchedSearch,
        int total,
        string query,
        string filterLabel)
    {
        if (visible > 0)
        {
            return string.Empty;
        }

        if (total == 0)
        {
            return "No quests loaded.";
        }

        if (query.Length > 0 && matchedSearch == 0)
        {
            return $"No quest matches “{query}”.";
        }

        if (query.Length > 0)
        {
            return matchedSearch == 1
                ? $"One quest matches “{query}” and it is not in “{filterLabel}”."
                : $"{matchedSearch} quests match “{query}” and none are in “{filterLabel}”.";
        }

        return $"No quest is in “{filterLabel}”.";
    }

    private bool MatchesSelectedFilter(QuestTaskViewModel task) => SelectedFilter.Id switch
    {
        "focus" => task.Model.RecordedState == RecordedTaskState.Active || task.Model.IsPinned ||
            task.Model.Objectives.Any(objective => objective.IsPinned),
        "available" => task.Model.Eligibility.State == QuestEligibilityState.Available,
        "locked" => task.Model.Eligibility.State == QuestEligibilityState.Locked,
        "indeterminate" => task.Model.Eligibility.State == QuestEligibilityState.Indeterminate,
        "completed" => task.Model.RecordedState == RecordedTaskState.Completed,
        "failed" => task.Model.RecordedState == RecordedTaskState.Failed,
        "unsupported" => task.Model.Objectives.Any(objective => objective.IsUnsupported),
        _ => true,
    };
}
