using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.App.ViewModels.Quests;

public sealed record QuestBoardFilter(string Id, string Label);

public sealed class QuestImportProposalViewModel(
    QuestImportProposal proposal,
    QuestImportResolution? resolution = null)
{
    public string Key => proposal.Key;

    public string Classification => proposal.Classification switch
    {
        QuestImportClassification.SafeMonotonic => "Safe monotonic",
        QuestImportClassification.Conflict => "Conflict — confirmation required",
        QuestImportClassification.IgnoredUnchanged => "Ignored unchanged",
        QuestImportClassification.UnresolvedUnknownId => "Unresolved unknown id",
        QuestImportClassification.UnresolvedSourceRecord => "Unresolved source record",
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
    private QuestProfileScope? _scope;
    private string _scopeStatus = "Profile scope not loaded";
    private string _catalogStatus = "Quest catalog not loaded";
    private string _status = "Loading local quest progress…";
    private DateTimeOffset? _lastRuntimeDataUtc;
    private bool _initialized;
    private string _exchangePath;
    private string _exchangeStatus = "Project JSON exchange is local-only and has not run.";
    private string _importPreviewSummary = "No import preview loaded.";
    private IReadOnlyList<QuestImportProposalViewModel> _importProposals = [];
    private QuestProgressImportPreview? _importPreview;
    private Dictionary<string, QuestImportResolution> _importResolutions = new(StringComparer.Ordinal);
    private Guid? _lastImportId;
    private string _tarkovTrackerToken = string.Empty;
    private string _tarkovTrackerStatus = "Optional TarkovTracker connection status not loaded.";
    private bool _canConnectTarkovTracker;
    private bool _canRefreshTarkovTracker;
    private bool _canDisconnectTarkovTracker;

    public QuestsPageViewModel(
        IPlayerProfileService profileService,
        IQuestReadService readService,
        IQuestProgressCommandService commandService,
        IQuestProgressExchangeService exchangeService,
        ITarkovTrackerIntegrationService tarkovTracker,
        AppDataPaths paths,
        MapViewModel map,
        TimeProvider timeProvider)
        : base(
            "Quests",
            "Local-first quest progress, reviewed project exchange, and source-honest static map links",
            "Quest state not loaded")
    {
        _profileService = profileService;
        _readService = readService;
        _commandService = commandService;
        _exchangeService = exchangeService;
        _tarkovTracker = tarkovTracker;
        _exchangePath = Path.Combine(paths.Support, "quest-progress.json");
        _map = map;
        _timeProvider = timeProvider;
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
            await RefreshTarkovTrackerStatusAsync(_scope, cancellationToken).ConfigureAwait(true);
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

    private async Task ConnectTarkovTrackerAsync()
    {
        if (_scope is null)
        {
            TarkovTrackerStatus = "Load the exact profile scope before connecting TarkovTracker.";
            return;
        }

        try
        {
            var status = await _tarkovTracker.ConnectAsync(
                _scope,
                TarkovTrackerToken,
                CancellationToken.None).ConfigureAwait(true);
            UpdateTarkovTrackerStatus(status,
                "Token validated with canonical GET /token and saved in protected storage.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TarkovTrackerStatus = $"Connection failed without saving a token: {exception.Message}";
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
            TarkovTrackerStatus = "Load the exact profile scope before disconnecting TarkovTracker.";
            return;
        }

        try
        {
            var status = await _tarkovTracker.DisconnectAsync(_scope, CancellationToken.None)
                .ConfigureAwait(true);
            if (_importPreview?.Source == QuestProgressImportSource.TarkovTracker)
            {
                ClearImportPreview("TarkovTracker was disconnected; its pending preview was discarded.");
            }

            UpdateTarkovTrackerStatus(status, "Disconnected and deleted the protected token.");
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
            TarkovTrackerStatus = "Load the exact profile scope before refreshing TarkovTracker.";
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
                    ? "GET /progress returned 304; the cached snapshot was re-previewed and the request still counted against quota."
                    : "GET /progress fetched a read-only snapshot for review; nothing was applied automatically.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TarkovTrackerStatus = $"Refresh failed; local progress is unchanged: {exception.Message}";
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
            TarkovTrackerStatus = $"TarkovTracker status unavailable: {exception.Message}";
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
            ? "Integration disabled because protected secret storage is unavailable."
            : !status.FeatureEnabled
                ? "Optional integration disabled by feature flag."
                : !status.NetworkAccessEnabled
                    ? "Integration unavailable in offline mode; all local quest features remain available."
                    : status.RequiresReconnect
                        ? "Saved credential was rejected; reconnect is required."
                        : status.Connected
                            ? $"Connected for exact mode {status.GameMode}."
                            : $"Not connected for exact mode {status.GameMode}.";
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
            ExchangeStatus = "Load the exact profile scope before exporting.";
            return;
        }

        try
        {
            var result = await _exchangeService.ExportAsync(
                _scope,
                ExchangePath,
                CancellationToken.None).ConfigureAwait(true);
            ExchangeStatus = $"Exported {result.RecordCount} owned records · SHA-256 {result.PayloadSha256[..12]}…";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ExchangeStatus = $"Export failed without changing progress: {exception.Message}";
        }
    }

    private async Task PreviewImportAsync()
    {
        if (_scope is null)
        {
            ExchangeStatus = "Load the exact profile scope before previewing an import.";
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
            ExchangeStatus = $"Preview failed; the database is unchanged: {exception.Message}";
        }
    }

    private void ResolveConflicts(QuestImportResolution resolution)
    {
        if (_importPreview is null)
        {
            ExchangeStatus = "Load a quest progress preview before resolving conflicts.";
            return;
        }

        _importResolutions = _importPreview.Conflicts.ToDictionary(
            value => value.Key,
            _ => resolution,
            StringComparer.Ordinal);
        RefreshImportProposalRows();
        ExchangeStatus = _importPreview.Conflicts.Count == 0
            ? "The preview has no conflicts; safe monotonic changes are ready to apply."
            : $"Confirmed {resolution} for {_importPreview.Conflicts.Count} conflicts. Review the list, then apply.";
    }

    private async Task ApplyImportAsync()
    {
        if (_importPreview is null)
        {
            ExchangeStatus = "Preview a quest progress source before applying it.";
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
                ? $"This normalized payload was already applied as import {result.ImportId}. No duplicate changes were made."
                : $"Applied {result.AppliedChangeCount} changes atomically · kept {result.KeptLocalCount} local · retained {result.UnresolvedCount} unresolved.";
            ClearImportPreview("Import applied. Preview again before another apply.");
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ExchangeStatus = $"Import was not applied: {exception.Message}";
        }
    }

    private async Task UndoLastImportAsync()
    {
        if (_scope is null || _lastImportId is null)
        {
            ExchangeStatus = "No import from this session is available to undo.";
            return;
        }

        try
        {
            var result = await _exchangeService.UndoImportAsync(
                _scope,
                _lastImportId.Value,
                CancellationToken.None).ConfigureAwait(true);
            ExchangeStatus = result.AlreadyUndone
                ? "That import was already undone; no duplicate journal entry was created."
                : $"Undid {result.RestoredChangeCount} imported changes as journal revision {result.Revision}.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ExchangeStatus = $"Import undo was refused without changing progress: {exception.Message}";
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
                $"Legacy profile settings v1 · {preview.SafeProposals.Count} explicit promotions · {preview.Conflicts.Count} conflicts · absent entities ignored",
            QuestProgressImportSource.TarkovTracker =>
                $"TarkovTracker fetched snapshot (not source edit time; source generation unavailable) · revision {preview.BaseRevision} · {preview.SafeProposals.Count} safe · {preview.Conflicts.Count} conflicts · {preview.Ignored.Count} unchanged · {preview.Unresolved.Count} unresolved",
            _ =>
                $"Project JSON v2 · revision {preview.BaseRevision} · {preview.SafeProposals.Count} safe · {preview.Conflicts.Count} conflicts · {preview.Ignored.Count} unchanged · {preview.Unresolved.Count} unresolved",
        };
        ExchangeStatus = preview.Conflicts.Count == 0
            ? "Preview ready. Safe monotonic changes can be applied; absent records never delete local state."
            : "Preview ready. Choose Keep local or Use incoming for every conflict before applying.";
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
