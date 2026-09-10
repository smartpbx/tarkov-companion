using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.App.ViewModels.Quests;

public sealed record QuestBoardFilter(string Id, string Label);

public sealed class QuestObjectiveViewModel
{
    private readonly QuestsPageViewModel _owner;

    internal QuestObjectiveViewModel(
        QuestObjectiveReadModel objective,
        RecordedTaskState taskState,
        QuestsPageViewModel owner)
    {
        Model = objective;
        _owner = owner;
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
        Maps = Model.MapIds.Count == 0 ? "No map association" : $"Map: {string.Join(", ", Model.MapIds)}";
        Items = QuestItemRequirementFormatter.DescribeForQuest(Model, taskState);
    }

    public QuestObjectiveReadModel Model { get; }

    public string ObjectiveId => Model.ObjectiveId;

    public string Description => Model.Description;

    public string Kind => Model.Kind.ToString();

    public string Status { get; }

    public string Source { get; }

    public string Maps { get; }

    public string Items { get; }

    public bool HasItems => Model.ItemTargets.Count > 0;

    public bool HasFoundInRaidRule => Model.FoundInRaidRequired is not null;

    public string FoundInRaidRule => Model.FoundInRaidRequired == true
        ? "Objective source requires found-in-raid items."
        : "Objective source does not require found-in-raid items.";

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
    }

    public QuestSummaryReadModel Model { get; }

    public string TaskId => Model.TaskId;

    public string Name => Model.Name;

    public string RecordedState => Model.RecordedState.ToString();

    public string Eligibility => Model.Eligibility.State.ToString();

    public string EligibilityDetail => Model.Eligibility.Reasons.Count == 0
        ? "No catalog eligibility warnings."
        : string.Join(" · ", Model.Eligibility.Reasons.Select(reason => reason.Detail));

    public string ObjectiveSummary => $"{Objectives.Count(objective => objective.Model.RecordedState == RecordedObjectiveState.Completed)}/{Objectives.Count} recorded complete · {Model.RecordedObjectivesSatisfied}";

    public string Source => Model.ProgressModifiedUtc is { } modified
        ? $"{Model.ProgressSource} · {QuestsPageViewModel.FormatAge(modified, _owner.NowUtc)}"
        : Model.ProgressSource;

    public string Trader => string.IsNullOrWhiteSpace(Model.TraderId) ? "Trader not supplied" : $"Trader: {Model.TraderId}";

    public string Map => string.IsNullOrWhiteSpace(Model.PrimaryMapId) ? "No primary map" : $"Primary map: {Model.PrimaryMapId}";

    public string PinLabel => Model.IsPinned ? "Unpin task" : "Pin task";

    public string BranchNotes => string.Join(" · ", new[]
    {
        Model.Restartable == true ? "Restartable" : null,
        Model.HasFailureConditions
            ? $"Source-authored failure conditions: {string.Join("; ", Model.FailureConditionNotes)}"
            : null,
    }.OfType<string>());

    public string Prerequisites => Model.Prerequisites.Count == 0
        ? "No catalog prerequisites."
        : string.Join(" · ", Model.Prerequisites.Select(requirement =>
            $"{requirement.RequiredTaskId}: {requirement.RecordedState} (requires {string.Join("/", requirement.RequiredStatuses)})"));

    public IReadOnlyList<QuestObjectiveViewModel> Objectives { get; }

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
    private readonly MapViewModel _map;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IReadOnlyList<QuestTaskViewModel> _allTasks = [];
    private IReadOnlyList<QuestTaskViewModel> _tasks = [];
    private IReadOnlyList<string> _orphanedProgress = [];
    private QuestTaskViewModel? _selectedTask;
    private QuestBoardFilter _selectedFilter = Filters[0];
    private QuestProfileScope? _scope;
    private string _scopeStatus = "Profile scope not loaded";
    private string _catalogStatus = "Quest catalog not loaded";
    private string _status = "Loading local quest progress…";
    private DateTimeOffset? _lastRuntimeDataUtc;
    private bool _initialized;

    public QuestsPageViewModel(
        IPlayerProfileService profileService,
        IQuestReadService readService,
        IQuestProgressCommandService commandService,
        MapViewModel map,
        TimeProvider timeProvider)
        : base(
            "Quests",
            "Manual, local-first quest progress and source-honest static map links",
            "Quest state not loaded")
    {
        _profileService = profileService;
        _readService = readService;
        _commandService = commandService;
        _map = map;
        _timeProvider = timeProvider;
        RefreshCommand = new AsyncDelegateCommand(RefreshAsync);
    }

    public IReadOnlyList<QuestBoardFilter> AvailableFilters => Filters;

    public DateTimeOffset NowUtc => _timeProvider.GetUtcNow();

    public QuestBoardFilter SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value))
            {
                ApplyFilter();
            }
        }
    }

    public IReadOnlyList<QuestTaskViewModel> Tasks
    {
        get => _tasks;
        private set => SetProperty(ref _tasks, value);
    }

    public QuestTaskViewModel? SelectedTask
    {
        get => _selectedTask;
        private set => SetProperty(ref _selectedTask, value);
    }

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

    public AsyncDelegateCommand RefreshCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _initialized = true;
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    public void ApplyRuntime(ApplicationRuntimeSnapshot snapshot)
    {
        Evidence = $"{snapshot.Data.Availability} · local profile and database-backed progress";
        if (_initialized && snapshot.Data.UpdatedUtc != _lastRuntimeDataUtc)
        {
            _lastRuntimeDataUtc = snapshot.Data.UpdatedUtc;
            _ = RefreshAsync();
        }
    }

    public Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var selectedTaskId = SelectedTask?.TaskId;
            var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(true);
            _scope = new(profile.Id, profile.GameMode, profile.ProfileGeneration);
            ScopeStatus = $"{profile.Name} · exact mode {profile.GameMode} · generation {profile.ProfileGeneration}";
            var board = await _readService.GetQuestBoardAsync(_scope, cancellationToken).ConfigureAwait(true);
            _allTasks = board.Tasks.Select(task => new QuestTaskViewModel(task, this)).ToArray();
            OrphanedProgress = board.OrphanedProgress
                .Select(orphan => $"{orphan.EntityKind} {orphan.ExternalId}: {orphan.RecordedValue}")
                .ToArray();
            CatalogStatus = board.CatalogProvenance is { } provenance
                ? $"Catalog {provenance.SourceMode}/{provenance.Language} · validated {FormatAge(provenance.ValidatedUtc, NowUtc)} · {provenance.Source}"
                : board.UnavailableReason ?? "Quest catalog unavailable.";
            Evidence = board.CatalogProvenance is null
                ? "Quest catalog unavailable · local progress retained"
                : $"{board.Tasks.Count} catalog quests · revision {board.ProgressRevision} · {board.CatalogProvenance.SourceMode}";
            ApplyFilter(selectedTaskId);
            Status = board.UnavailableReason ?? (Tasks.Count == 0
                ? "No quests match this filter. Choose All quests to inspect the catalog."
                : $"Showing {Tasks.Count} of {_allTasks.Count} quests. Changes are manual and local.");
            await _map.RefreshQuestLayerAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Tasks = [];
            SelectedTask = null;
            Status = $"Quest view unavailable: {exception.Message}";
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    internal void Select(QuestTaskViewModel task) => SelectedTask = task;

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
            Status = "The exact profile scope is not loaded; no progress was changed.";
            return;
        }

        try
        {
            await mutation(_scope).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"Progress was not changed: {exception.Message}";
        }
    }

    private void ApplyFilter(string? selectedTaskId = null)
    {
        Tasks = _allTasks.Where(MatchesSelectedFilter).ToArray();
        SelectedTask = Tasks.FirstOrDefault(task => task.TaskId == selectedTaskId) ?? Tasks.FirstOrDefault();
        if (_initialized)
        {
            Status = Tasks.Count == 0
                ? "No quests match this filter. Choose All quests to inspect the catalog."
                : $"Showing {Tasks.Count} of {_allTasks.Count} quests. Changes are manual and local.";
        }
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
