using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Wiki;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

/// <summary>One objective, under whichever map group(s) it belongs to.</summary>
public sealed class PlanObjectiveRowViewModel : BindableViewModel
{
    private readonly PlanWorkspaceViewModel _owner;

    internal PlanObjectiveRowViewModel(
        QuestSummaryReadModel task,
        QuestObjectiveReadModel objective,
        PlanWorkspaceViewModel owner)
    {
        Task = task;
        Objective = objective;
        _owner = owner;
        OpenWikiCommand = new DelegateCommand(() => _owner.TryOpenWiki(task.WikiUri));
        MarkObjectiveDoneCommand = new AsyncDelegateCommand(
            () => _owner.MarkObjectiveDoneAsync(objective.ObjectiveId, objective.TargetCount ?? objective.RecordedCount));
        MarkQuestDoneCommand = new AsyncDelegateCommand(() => _owner.MarkQuestDoneAsync(task.TaskId));
        ShowOnMapCommand = new AsyncDelegateCommand(() => _owner.ShowOnMapAsync(objective.MapIds[0]));
    }

    internal QuestSummaryReadModel Task { get; }

    internal QuestObjectiveReadModel Objective { get; }

    public string TaskName => Task.Name;

    public string TraderLabel => string.IsNullOrWhiteSpace(Task.TraderId)
        ? "No trader"
        : Task.TraderName ?? Task.TraderId;

    public string Description => Objective.Description;

    public bool IsUnsupported => Objective.IsUnsupported;

    /// <summary>Hand-in versus find-in-raid, kept distinct only where the catalog says which.</summary>
    public string HandlingLabel => PlanWorkspaceViewModel.DescribeHandling(Objective);

    public bool HasHandlingLabel => HandlingLabel.Length > 0;

    public string RemainingLabel => PlanWorkspaceViewModel.DescribeRemaining(Objective);

    public bool HasWikiLink => WikiLinkPolicy.IsAllowed(Task.WikiUri);

    public bool CanShowOnMap => Objective.MapIds.Count == 1;

    public ICommand OpenWikiCommand { get; }

    public ICommand MarkObjectiveDoneCommand { get; }

    public ICommand MarkQuestDoneCommand { get; }

    public ICommand ShowOnMapCommand { get; }
}

/// <summary>Every objective on one map (or "Any map" for one that names none).</summary>
public sealed record PlanMapGroupViewModel(string MapLabel, IReadOnlyList<PlanObjectiveRowViewModel> Objectives);

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
public sealed class PlanWorkspaceViewModel : BindableViewModel
{
    private const string AnyMapKey = "";

    private readonly IPlayerProfileService _profileService;
    private readonly IQuestReadService _readService;
    private readonly IQuestProgressCommandService _commandService;
    private readonly MapViewModel _map;
    private readonly IWikiLinkOpener _wikiOpener;
    private QuestBoardReadModel? _board;
    private QuestProfileScope? _scope;
    private bool _showAll;
    private string _status = "Loading your quest board…";
    private string _scopeLabel = "No profile loaded";
    private IReadOnlyList<PlanMapGroupViewModel> _groups = [];

    public PlanWorkspaceViewModel(
        IPlayerProfileService profileService,
        IQuestReadService readService,
        IQuestProgressCommandService commandService,
        MapViewModel map,
        IWikiLinkOpener wikiOpener)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _readService = readService ?? throw new ArgumentNullException(nameof(readService));
        _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _wikiOpener = wikiOpener ?? throw new ArgumentNullException(nameof(wikiOpener));
        RefreshCommand = new AsyncDelegateCommand(RefreshAsync);
    }

    /// <summary>Raised after this workspace has moved the shared map to the requested one.</summary>
    public event EventHandler<EventArgs>? ShowOnMapRequested;

    public IReadOnlyList<PlanMapGroupViewModel> Groups
    {
        get => _groups;
        private set => SetProperty(ref _groups, value);
    }

    public bool HasGroups => Groups.Count > 0;

    public bool ShowAll
    {
        get => _showAll;
        set
        {
            if (SetProperty(ref _showAll, value))
            {
                ApplyFilter();
            }
        }
    }

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

    public AsyncDelegateCommand RefreshCommand { get; }

    public Task LoadAsync() => RefreshAsync(CancellationToken.None);

    public Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(true);
            _scope = new(profile.Id, profile.GameMode, profile.ProfileGeneration);
            ScopeLabel = $"{profile.Name} · {profile.GameMode}";
            _board = await _readService.GetQuestBoardAsync(_scope, cancellationToken).ConfigureAwait(true);
            ApplyFilter();
            Status = _board.UnavailableReason ?? (HasGroups
                ? $"{Groups.Sum(group => group.Objectives.Count)} objective(s) across {Groups.Count} map(s)"
                : ShowAll
                    ? "No quests recorded yet."
                    : "Nothing active or unfinished. Toggle Show everything to see the rest.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _board = null;
            Groups = [];
            Status = $"Unavailable · {exception.Message}";
        }
    }

    private void ApplyFilter()
    {
        if (_board is null)
        {
            Groups = [];
            return;
        }

        var buckets = new Dictionary<string, List<PlanObjectiveRowViewModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Bucket(_board.Tasks, ShowAll))
        {
            if (!buckets.TryGetValue(entry.MapKey, out var list))
            {
                buckets[entry.MapKey] = list = [];
            }

            list.Add(new PlanObjectiveRowViewModel(entry.Task, entry.Objective, this));
        }

        Groups = buckets
            .Select(bucket => new PlanMapGroupViewModel(NameOfMap(bucket.Key), bucket.Value))
            .OrderBy(group => group.MapLabel == "Any map" ? 1 : 0)
            .ThenBy(group => group.MapLabel, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>One objective, tagged with one map key it belongs to (an objective on several
    /// maps produces one entry per map; an objective with none produces one <see cref="AnyMapKey"/>
    /// entry).</summary>
    internal readonly record struct PlanObjectiveBucketEntry(
        string MapKey,
        QuestSummaryReadModel Task,
        QuestObjectiveReadModel Objective);

    /// <summary>
    /// Actionable-by-default filtering and map bucketing, kept free of the map/wiki/command
    /// services so it can be tested without them.
    /// </summary>
    /// <remarks>
    /// With <paramref name="showAll"/> false: only active quests, and only their objectives that
    /// are not yet recorded complete. With it true: every quest and every objective, unfiltered.
    /// </remarks>
    internal static IReadOnlyList<PlanObjectiveBucketEntry> Bucket(
        IReadOnlyList<QuestSummaryReadModel> tasks,
        bool showAll)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var entries = new List<PlanObjectiveBucketEntry>();
        foreach (var task in tasks)
        {
            if (!showAll && task.RecordedState != RecordedTaskState.Active)
            {
                continue;
            }

            foreach (var objective in task.Objectives)
            {
                if (!showAll && objective.RecordedState == RecordedObjectiveState.Completed)
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
        true => "Find in raid",
        false => "Hand in",
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
        return string.Create(
            CultureInfo.CurrentCulture,
            $"{remaining:0.##} remaining of {target:0.##}");
    }

    private string NameOfMap(string mapId)
    {
        if (mapId.Length == 0)
        {
            return "Any map";
        }

        return _map.Locations
            .FirstOrDefault(location =>
                string.Equals(location.Id, mapId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(location.SourceId, mapId, StringComparison.OrdinalIgnoreCase))?.Name
            ?? mapId;
    }

    internal bool TryOpenWiki(string? wikiUri) => _wikiOpener.TryOpen(wikiUri);

    internal Task MarkObjectiveDoneAsync(string objectiveId, decimal? count) => MutateAsync(scope =>
        _commandService.SetObjectiveProgressAsync(
            scope,
            objectiveId,
            RecordedObjectiveState.Completed,
            count,
            CancellationToken.None));

    internal Task MarkQuestDoneAsync(string taskId) => MutateAsync(scope =>
        _commandService.SetTaskStateAsync(scope, taskId, RecordedTaskState.Completed, CancellationToken.None));

    /// <summary>Follows the shared map to the objective's map and tells the shell to show it.</summary>
    internal async Task ShowOnMapAsync(string mapId)
    {
        try
        {
            await _map.FollowRaidAsync(mapId).ConfigureAwait(true);
            ShowOnMapRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"Could not switch the map: {exception.Message}";
        }
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
            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"Not changed · {exception.Message}";
        }
    }
}
