using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Wiki;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

/// <summary>One objective, under whichever map group(s) it belongs to.</summary>
public sealed class PlanObjectiveRowViewModel : BindableViewModel
{
    private readonly PlanWorkspaceViewModel _owner;
    private bool? _hasMapPosition;

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
        Number = number;
        IsLast = isLast;
        OpenWikiCommand = new DelegateCommand(() => _owner.TryOpenWiki(task.WikiUri));
        MarkObjectiveDoneCommand = new AsyncDelegateCommand(
            () => _owner.MarkObjectiveDoneAsync(objective.ObjectiveId, objective.TargetCount ?? objective.RecordedCount));
        MarkQuestDoneCommand = new AsyncDelegateCommand(() => _owner.MarkQuestDoneAsync(task.TaskId));
        ShowOnMapCommand = new AsyncDelegateCommand(() => _owner.ShowOnMapAsync(objective.MapIds[0]));
    }

    internal QuestSummaryReadModel Task { get; }

    internal QuestObjectiveReadModel Objective { get; }

    /// <summary>1-based position within its map group: the numbered step the Plan list draws.</summary>
    public int Number { get; }

    /// <summary>The last step draws no connector line below its number.</summary>
    public bool IsLast { get; }

    public bool HasNext => !IsLast;

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
            }
        }
    }

    public bool ShowsNoMapPosition => HasMapPosition == false;

    /// <summary>A bare recorded state ("Unknown") says nothing on the page; only counts are shown.</summary>
    public bool HasRemainingLabel => Objective.TargetCount is not null || Objective.RecordedCount is not null;

    public bool HasWikiLink => WikiLinkPolicy.IsAllowed(Task.WikiUri);

    public bool CanShowOnMap => Objective.MapIds.Count == 1;

    public ICommand OpenWikiCommand { get; }

    public ICommand MarkObjectiveDoneCommand { get; }

    public ICommand MarkQuestDoneCommand { get; }

    public ICommand ShowOnMapCommand { get; }
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
        Quests = objectives
            .GroupBy(row => row.Task.TaskId, StringComparer.Ordinal)
            .Select(group => new PlanQuestSummaryViewModel(
                group.First().TaskName,
                group.First().TraderLabel,
                PlanWorkspaceViewModel.CountLabel(group.Count(), "objective")))
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

    public IReadOnlyList<PlanQuestSummaryViewModel> Quests { get; }

    /// <summary>"4 objectives · 3 quests", the bundle card's second line.</summary>
    public string Summary => $"{PlanWorkspaceViewModel.CountLabel(Objectives.Count, "objective")} · {PlanWorkspaceViewModel.CountLabel(Quests.Count, "quest")}";

    /// <summary>Objectives whose items must be found in raid, where the catalog says so.</summary>
    public int FindInRaidCount { get; }

    /// <summary>Objectives whose items may be bought and handed in, where the catalog says so.</summary>
    public int HandInCount { get; }

    public bool HasItemObjectives => FindInRaidCount + HandInCount > 0;

    /// <summary>"Objectives (4)", the context panel's list heading.</summary>
    public string ObjectivesHeading => $"Objectives ({Objectives.Count})";

    /// <summary>Only a real map can be opened on the Raid map.</summary>
    public bool CanOpenInRaid => MapId is not null;

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public ICommand SelectCommand { get; }

    public ICommand OpenInRaidCommand { get; }
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
public sealed class PlanWorkspaceViewModel : BindableViewModel
{
    private const string AnyMapKey = "";

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
    private QuestProfileScope? _scope;
    private bool _showAll;
    private string _status = "Loading your quest board…";
    private string _scopeLabel = "No profile loaded";
    private IReadOnlyList<PlanMapGroupViewModel> _groups = [];
    private PlanMapGroupViewModel? _selectedGroup;

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
        QuestMapProjectionService? projection = null)
    {
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
        RefreshCommand = new AsyncDelegateCommand(RefreshAsync);
        OpenHideoutCommand = new DelegateCommand(() => OpenHideoutRequested?.Invoke(this, EventArgs.Empty));
        // The map catalog usually finishes loading after the first quest board read; the groups
        // are named from it, so rebuild them when it arrives rather than showing catalog ids.
        _map.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MapViewModel.Locations))
            {
                ApplyFilter();
            }
            else if (e.PropertyName == nameof(MapViewModel.RenderModel))
            {
                RefreshMapPreview();
            }
        };
    }

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
                OnPropertyChanged(nameof(HasGroups));
            }
        }
    }

    public bool HasGroups => Groups.Count > 0;

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
            _ = FollowSelectedGroupAsync();
        }
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

                _mapPreview = value;
                if (value is not null)
                {
                    value.ViewChangeRequested += MapPreviewViewChangeRequested;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(HasMapPreview));
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
            _mapNames = await ResolveMapNamesAsync(_board, cancellationToken).ConfigureAwait(true);
            _projected.Clear();
            ApplyFilter();
            Status = _board.UnavailableReason ?? (HasGroups
                ? $"{CountLabel(Groups.Sum(group => group.Objectives.Count), "objective")} across {CountLabel(Groups.Count, "map")}"
                : ShowAll
                    ? "No quests recorded yet."
                    : "No active quests. Turn on Show everything to see the rest.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The raw exception belongs in the log, not on the page: the usual cause is simply
            // that game data has not finished loading, and the shell reloads this workspace when
            // it does.
            _board = null;
            Groups = [];
            SelectedGroup = null;
            Status = "Quest data isn't available yet.";
            System.Diagnostics.Trace.TraceWarning($"Plan workspace refresh failed: {exception}");
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
        var buckets = new Dictionary<string, List<PlanObjectiveBucketEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Bucket(_board.Tasks, ShowAll))
        {
            if (!buckets.TryGetValue(entry.MapKey, out var list))
            {
                buckets[entry.MapKey] = list = [];
            }

            list.Add(entry);
        }

        Groups = buckets
            .Select(bucket => new PlanMapGroupViewModel(
                bucket.Key.Length == 0 ? null : bucket.Key,
                NameOfMap(bucket.Key),
                bucket.Value
                    .Select((entry, index) => new PlanObjectiveRowViewModel(
                        entry.Task, entry.Objective, this, index + 1, index == bucket.Value.Count - 1))
                    .ToArray(),
                group => SelectedGroup = group,
                OpenInRaidAsync))
            .OrderBy(group => group.MapId is null ? 1 : 0)
            .ThenBy(group => group.MapLabel, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        SelectedGroup = Groups.FirstOrDefault(group =>
                previousMapKey is not null &&
                string.Equals(group.MapId ?? AnyMapKey, previousMapKey, StringComparison.OrdinalIgnoreCase))
            ?? Groups.FirstOrDefault();
    }

    /// <summary>"1 objective", "3 quests": the plural is regular for every noun this page counts.</summary>
    internal static string CountLabel(int count, string noun) =>
        string.Create(CultureInfo.CurrentCulture, $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}");

    private Task OpenInRaidAsync(string mapId) => ShowOnMapAsync(mapId);

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
            System.Diagnostics.Trace.TraceWarning($"Plan workspace could not place objectives on {gameMapId}: {exception.Message}");
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
            MapNote = "Loading map…";
            await _map.FollowRaidAsync(locationId).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning($"Plan workspace could not load map {mapId}: {exception.Message}");
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
            ClearMapPreview(group is null ? string.Empty : "These objectives can be done on any map.");
            return;
        }

        if (_raidCockpit is null || !ShowsGroupMap(group))
        {
            ClearMapPreview(_runtime?.Current.Raid.State == RaidLifecycleState.InRaid && _map.SelectedLocation is not null
                ? "The map follows your raid. It shows here after the raid."
                : "Loading map…");
            return;
        }

        if (!_projected.TryGetValue(group.MapId, out var projected))
        {
            _ = ProjectGroupAsync(group.MapId);
            projected = [];
        }

        var toPlan = _map.SelectedVariant is { } selectedVariant ? PlanPercentMapper(selectedVariant) : null;
        var elements = toPlan is null ? [] : BuildObjectiveMarkers(group.Objectives, projected, toPlan);
        var known = _projected.ContainsKey(group.MapId);
        foreach (var row in group.Objectives)
        {
            var number = row.Number.ToString(CultureInfo.InvariantCulture);
            row.HasMapPosition = known ? elements.Any(element => element.Label == number) : null;
        }

        var signature = $"{_map.RenderModel?.Location.Id}|{_map.RenderModel?.SelectedFloor?.Id}|{string.Join(',', elements.Select(element => $"{element.Label}@{element.Position.X:R},{element.Position.Y:R}"))}";
        if (MapPreview is not null && signature == _mapPreviewSignature)
        {
            return;
        }

        var preview = _raidCockpit.CreateObjectivePreview(elements, MapPreview);
        _mapPreviewSignature = preview is null ? null : signature;
        MapPreview = preview;
        MapNote = preview is null ? "This map has no 2D plan yet." : string.Empty;
    }

    private void ClearMapPreview(string note)
    {
        MapPreview = null;
        _mapPreviewSignature = null;
        MapNote = note;
        if (SelectedGroup is { } group)
        {
            foreach (var row in group.Objectives)
            {
                row.HasMapPosition = null;
            }
        }
    }

    /// <summary>
    /// One marker per objective the projection placed, labelled with the row's step number.
    /// A region is marked at the mean of its outline, which lands inside every convex zone the
    /// catalog publishes. The number is the marker's label, which the renderer draws as the
    /// marker itself (see MapSceneRendererObjectViewModel.IsNumberedStep).
    /// </summary>
    internal static IReadOnlyList<MapOverlayElement> BuildObjectiveMarkers(
        IReadOnlyList<PlanObjectiveRowViewModel> rows,
        IReadOnlyList<QuestMapObjectiveProjection> projected,
        Func<MapPoint, MapPoint> toPlan)
    {
        var markers = new List<MapOverlayElement>(rows.Count);
        foreach (var row in rows)
        {
            var match = projected.FirstOrDefault(objective =>
                string.Equals(objective.ObjectiveId, row.Objective.ObjectiveId, StringComparison.Ordinal) &&
                objective.GeometryKind is QuestMapGeometryKind.Point or QuestMapGeometryKind.Region &&
                objective.Points.Count > 0);
            if (match is null)
            {
                continue;
            }

            var position = toPlan(new MapPoint(match.Points.Average(point => point.X), match.Points.Average(point => point.Y)));
            if (position.X is < 0 or > 100 || position.Y is < 0 or > 100)
            {
                continue;
            }

            markers.Add(new MapOverlayElement(
                MapOverlayKind.QuestObjectives,
                position,
                row.Number.ToString(CultureInfo.InvariantCulture))
            {
                Detail = row.Description,
            });
        }

        return markers;
    }

    /// <summary>
    /// Projected map units to the V2 scene's 0–100 plan space. The Raid cockpit stretches the
    /// 2D plan artwork over that square, and the artwork spans the variant's SVG bounds (the map
    /// bounds when it publishes none) — the same extent the V1 canvas mapper places markers in
    /// (MapCanvasCoordinateMapper.Create). Null when the variant cannot be projected.
    /// </summary>
    internal static Func<MapPoint, MapPoint>? PlanPercentMapper(MapVariant variant)
    {
        if (variant.Transform is not { } transform || (variant.SvgBounds ?? variant.Bounds) is not { IsValid: true } bounds)
        {
            return null;
        }

        var corners = new[]
        {
            new WorldPosition(bounds.First.X, 0, bounds.First.Y),
            new WorldPosition(bounds.First.X, 0, bounds.Second.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.First.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.Second.Y),
        };
        var projected = new List<MapPoint>(corners.Length);
        foreach (var corner in corners)
        {
            if (!transform.TryProject(corner, out var point))
            {
                return null;
            }

            projected.Add(point);
        }

        var minimumX = projected.Min(point => point.X);
        var minimumY = projected.Min(point => point.Y);
        var width = projected.Max(point => point.X) - minimumX;
        var height = projected.Max(point => point.Y) - minimumY;
        return width <= 0 || height <= 0
            ? null
            : point => new((point.X - minimumX) / width * 100, (point.Y - minimumY) / height * 100);
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
                System.Diagnostics.Trace.TraceWarning($"Plan workspace could not name map {mapId}: {exception.Message}");
            }
        }

        return names;
    }

    private string NameOfMap(string mapId)
    {
        if (mapId.Length == 0)
        {
            return "Any map";
        }

        if (_mapNames.TryGetValue(mapId, out var synced))
        {
            return synced;
        }

        return _map.Locations
            .FirstOrDefault(location =>
                string.Equals(location.Id, mapId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(location.SourceId, mapId, StringComparison.OrdinalIgnoreCase))?.Name
            ?? "Other map";
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
            // Quest objectives name maps by game-data id; the map view follows catalog location
            // ids. Handing it the game id matched no location and silently did nothing.
            await _map.FollowRaidAsync(LocationIdFor(mapId) ?? mapId).ConfigureAwait(true);
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
