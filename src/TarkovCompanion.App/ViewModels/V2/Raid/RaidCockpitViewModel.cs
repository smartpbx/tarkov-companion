using System.Globalization;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Strategy;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Infrastructure.Maps;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>One entry in the manual map picker.</summary>
public sealed class RaidMapPickerItemViewModel(string mapId, string name, Func<string, Task> select) : BindableViewModel
{
    public string MapId { get; } = mapId;

    public string Name { get; } = name;

    public ICommand SelectCommand { get; } = new DelegateCommand(() => _ = select(mapId));
}

/// <summary>One local mark, for the marks list beside the map.</summary>
/// <remarks>
/// Its label is precomputed by <see cref="RaidCockpitViewModel.LabelMarksForMap"/> in the same
/// pass that labels the scene object this mark also became, rather than recomputed here — see
/// that method's remark on why a second pass is the wrong shape for this.
/// </remarks>
public sealed class RaidMarkRowViewModel : BindableViewModel
{
    private readonly Guid _id;
    private readonly Func<Guid, string?, Task> _rename;
    private string _editableName;

    public RaidMarkRowViewModel(RaidMark mark, string label, Func<Guid, string?, Task> rename, Func<Guid, Task> remove)
    {
        _id = mark.Id;
        _rename = rename;
        Kind = mark.Kind;
        Label = label;
        _editableName = mark.State.Label ?? string.Empty;
        RenameCommand = new DelegateCommand(() => _ = _rename(_id, EditableName));
        RemoveCommand = new DelegateCommand(() => _ = remove(_id));
    }

    /// <summary>
    /// V2 rough package 20: a mark the group shared, listed beside our own so every mark drawn on
    /// this map has a visible way to remove it in one place. Removal goes to the relay, which
    /// accepts both waypoints and pings; there is nothing local to rename.
    /// </summary>
    public RaidMarkRowViewModel(long groupId, RaidMarkKind kind, string label, string owner, Func<long, Task> remove)
    {
        ArgumentNullException.ThrowIfNull(remove);
        _id = Guid.Empty;
        _rename = static (_, _) => Task.CompletedTask;
        Kind = kind;
        Label = label;
        Owner = owner;
        IsGroupMark = true;
        _editableName = string.Empty;
        RenameCommand = new DelegateCommand(static () => { });
        RemoveCommand = new DelegateCommand(() => _ = remove(groupId));
    }

    /// <summary>Whose mark this is, when it came from the group; empty for our own.</summary>
    public string Owner { get; } = string.Empty;

    public bool HasOwner => Owner.Length > 0;

    /// <summary>True for a relay mark: removal goes to the group, and it is never renamed here.</summary>
    public bool IsGroupMark { get; }

    public Guid Id => _id;

    public RaidMarkKind Kind { get; }

    /// <summary>The number, custom name, or "Ping" — whichever the map dot beside this row shows.</summary>
    public string Label { get; }

    public string KindLabel => (Kind == RaidMarkKind.Ping ? "Ping" : "Waypoint") + (IsGroupMark ? " · group" : string.Empty);

    /// <summary>A ping is "look here now": it is never told apart from another ping by name.</summary>
    public bool CanRename => Kind == RaidMarkKind.Waypoint && !IsGroupMark;

    /// <summary>The rename box's current text. Committing it empty clears back to the number.</summary>
    public string EditableName
    {
        get => _editableName;
        set => SetProperty(ref _editableName, value);
    }

    public ICommand RenameCommand { get; }

    public ICommand RemoveCommand { get; }
}

/// <summary>
/// V2 rough package 15: one extract (or transit) on the current map for the Raid workspace's
/// "Extract options" list, taken from the canonical scene so it lists the same points the map
/// draws, with the same offer state.
/// </summary>
public sealed class RaidExtractRowViewModel(string name, string detail, MapSceneOfferState offerState)
{
    public string Name { get; } = name;

    /// <summary>Faction or "Transit": who can use it, in one or two words.</summary>
    public string Detail { get; } = detail;

    public bool IsOffered => offerState == MapSceneOfferState.Offered;

    public bool IsNotOffered => offerState == MapSceneOfferState.NotOffered;

    public string OfferLabel => offerState switch
    {
        MapSceneOfferState.Offered => "Offered",
        MapSceneOfferState.NotOffered => "Not offered",
        _ => "",
    };

    public bool HasOfferLabel => offerState != MapSceneOfferState.Unknown;
}

/// <summary>
/// Hosts the canonical map renderer (<see cref="MapSceneRendererViewModel"/>) as a raid cockpit:
/// map picker, layer toggles the renderer already draws, the raid timer and extract panel, and
/// local marks.
/// </summary>
/// <remarks>
/// This is the adapter <c>MapSceneAssembler</c> was built for. It never draws anything itself —
/// it turns the V1 map's render model plus loot, extract, quest, and mark data into one
/// <see cref="MapSceneSnapshot"/> and lets the renderer draw that. The renderer, the assembler,
/// and the high-value-loot layer are reused unchanged.
/// </remarks>
public sealed class RaidCockpitViewModel : BindableViewModel, IDisposable
{
    private static readonly MapSceneLayerId MarksLayerId = new("my-marks");
    // [V2 rough package 22] The three layers the V1 map drew that the cockpit never did. They
    // are ordinary scene layers, so each one gets a switch in the bottom strip for free and a
    // paired device would receive them with the rest of the scene.
    private static readonly MapSceneLayerId PlayerLayerId = new("you");
    private static readonly MapSceneLayerId VisitedLayerId = new("visited");
    private static readonly MapSceneLayerId SquadLayerId = new("squad");
    private static readonly MapSceneBounds PlanBounds = new(0, 0, 100, 100);
    private const string MarkIdPrefix = "mark:";
    private const string PlayerObjectId = "you:position";
    private const string PlayerTrailObjectId = "you:trail";

    /// <summary>What "Follow" zooms to, as a multiple of the whole plan fitted to the card.</summary>
    /// <remarks>
    /// The same judgement as V1's CentreOnPlayer: a whole map fitted to the panel is the right
    /// view before a raid and the wrong one during it, where the player is a dot among street
    /// names. Only ever zooms in — somebody who has zoomed further in to read a building is not
    /// pulled back out by their next screenshot.
    /// </remarks>
    private const double FollowZoom = 3.0;

    private readonly MapViewModel _map;
    private readonly RaidPageViewModel _raid;
    private readonly IRuntimeStateStore _stateStore;
    private readonly MapSceneAssembler _assembler;
    private readonly IHighValueLootRuntimeSource _lootSource;
    private readonly IRaidMarkStore _marks;
    private readonly GroupSessionService? _groupSession;
    private readonly TarkovDevMapAssetCache _assetCache;
    private readonly TimeProvider _timeProvider;
    private readonly MapSceneRendererPresentation _presentation;

    private long _revision;
    private CancellationTokenSource? _rebuildCancellation;
    private HighValueLootLayerFilterState _lootFilter = HighValueLootLayerFilterState.Default;
    private RaidMarkKind? _armedMarkKind;
    private string _unavailableReason = "Loading the map…";
    private string? _cachedAssetVariantKey;
    private string? _modelFloorId;
    private IReadOnlyDictionary<MapSceneObjectId, MapSceneObjectStyle> _objectStyles =
        new Dictionary<MapSceneObjectId, MapSceneObjectStyle>();
    private bool _followPending;
    private CachedMapAsset? _cachedAsset;
    private Bitmap? _backgroundImage;

    public RaidCockpitViewModel(
        MapViewModel map,
        RaidPageViewModel raid,
        IRuntimeStateStore stateStore,
        MapSceneAssembler assembler,
        IHighValueLootRuntimeSource lootSource,
        // Registered so the historical-traffic layer degrades honestly instead of not existing;
        // see TrafficLayerNotice.
        HistoricalTrafficRuntimeService traffic,
        IRaidMarkStore marks,
        TarkovDevMapAssetCache assetCache,
        TimeProvider timeProvider,
        // V2 rough package 20: so the marks list can offer "Remove" on a group waypoint or ping
        // too, instead of only on our own. Optional: a cockpit built without one simply lists no
        // group marks, which is what the unit tests and the map gallery want.
        GroupSessionService? groupSession = null)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _raid = raid ?? throw new ArgumentNullException(nameof(raid));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _assembler = assembler ?? throw new ArgumentNullException(nameof(assembler));
        _lootSource = lootSource ?? throw new ArgumentNullException(nameof(lootSource));
        ArgumentNullException.ThrowIfNull(traffic);
        _marks = marks ?? throw new ArgumentNullException(nameof(marks));
        _groupSession = groupSession;
        _assetCache = assetCache ?? throw new ArgumentNullException(nameof(assetCache));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _presentation = MapSceneRendererPresentation.English(CultureInfo.CurrentCulture, TimeZoneInfo.Local);

        RebuildMapPicker();

        PlaceWaypointCommand = new DelegateCommand(() => ArmMark(RaidMarkKind.Waypoint));
        PlacePingCommand = new DelegateCommand(() => ArmMark(RaidMarkKind.Ping));
        CancelPlacingCommand = new DelegateCommand(() => ArmMark(null));

        // [V2 rough package 22] Every one of these is V1's own behaviour on V1's own view model.
        // The cockpit owns where the control sits, not what pressing it means.
        ToggleFollowCommand = new DelegateCommand(_map.ToggleFollowPlayer);
        RotateCommand = new DelegateCommand(() => _ = _map.RotateAsync());
        ToggleVisitedCommand = new DelegateCommand(() => _ = _map.ToggleVisitedAsync());
        ToggleGroupNamesCommand = new DelegateCommand(() => _ = _map.ToggleGroupNamesAsync());
        ToggleAutoFloorCommand = new DelegateCommand(_map.ToggleAutoFloor);
        ToggleStackCommand = new DelegateCommand(() => _map.IsStacked = !_map.IsStacked);
        ToggleArtworkCommand = new DelegateCommand(() => _ = _map.ToggleArtworkAsync());
        ToggleHideControlsCommand = new DelegateCommand(() => _ = _map.ToggleHideControlsWhenIdleAsync());
        FrameAreaCommand = new DelegateCommand(FrameArea);

        _map.PropertyChanged += MapPropertyChanged;
        _map.PlayerFollowRequested += PlayerFollowRequested;
        _raid.PropertyChanged += RaidPropertyChanged;
        _stateStore.Changed += RuntimeStateChanged;
        _marks.Changed += MarksChanged;

        _ = InitializeAsync();
    }

    /// <summary>The canonical map renderer, once a reviewed map asset is available.</summary>
    public MapSceneRendererViewModel? Renderer { get; private set; }

    public bool HasRenderer => Renderer is not null;

    /// <summary>Why the map is not showing, while <see cref="HasRenderer"/> is false.</summary>
    public string UnavailableReason
    {
        get => _unavailableReason;
        private set => SetProperty(ref _unavailableReason, value);
    }

    public IReadOnlyList<RaidMapPickerItemViewModel> MapPicker { get; private set; } = [];
    /// <summary>The picker entry for the map currently shown, for a header selector that stays
    /// showing the current map name rather than resetting to a blank picker each time.</summary>
    public RaidMapPickerItemViewModel? SelectedMap =>
        MapPicker.FirstOrDefault(item => item.MapId == _map.RenderModel?.Location.Id);

    public IReadOnlyList<RaidMarkRowViewModel> Marks { get; private set; } = [];

    public bool HasMarks => Marks.Count > 0;

    /// <summary>Registered but not evaluated: see the traffic remark on the constructor.</summary>
    public string TrafficLayerNotice =>
        "Historical traffic — estimate, no installed model yet";

    // ---------------------------------------------------------------------------------------
    // [V2 rough package 22] V1 map parity. Every property below forwards to the one MapViewModel
    // this cockpit already holds; none of it is a second implementation of anything. Where a V1
    // control had no home in the #398/#410 concept it went into the floating group over the map
    // (the view toggles) or the right-hand context panel (the panels).
    // ---------------------------------------------------------------------------------------

    /// <summary>Move the map to the player when a screenshot places them.</summary>
    public ICommand ToggleFollowCommand { get; }

    /// <summary>Turn the plan a quarter, remembered per map.</summary>
    public ICommand RotateCommand { get; }

    /// <summary>Draw where past raids on this map put you.</summary>
    public ICommand ToggleVisitedCommand { get; }

    /// <summary>Write squadmates' names beside their markers.</summary>
    public ICommand ToggleGroupNamesCommand { get; }

    /// <summary>Change floor to the one the screenshot says you are standing on.</summary>
    public ICommand ToggleAutoFloorCommand { get; }

    /// <summary>Draw the floors one above another instead of one at a time.</summary>
    public ICommand ToggleStackCommand { get; }

    /// <summary>Swap between the drawn map and the photographic one.</summary>
    public ICommand ToggleArtworkCommand { get; }

    /// <summary>Fade the floating controls a few seconds after the pointer leaves the map.</summary>
    public ICommand ToggleHideControlsCommand { get; }

    /// <summary>Put the area the raid is happening in on screen.</summary>
    public ICommand FrameAreaCommand { get; }

    public bool FollowsPlayer => _map.FollowsPlayer;

    public string RotationLabel => _map.RotationLabel;

    public bool IsRotated => _map.IsRotated;

    public bool ShowsVisited => _map.ShowsVisited;

    public string VisitedLabel => _map.VisitedLabel;

    public bool ShowsGroupNames => _map.ShowsGroupNames;

    public bool AutoSelectsFloor => _map.AutoSelectsFloor;

    public bool IsStacked => _map.IsStacked;

    public bool CanStack => _map.CanStack;

    public bool HasFloorStack => _map.HasFloorStack;

    public string StackStatus => _map.StackStatus;

    public bool HasStackStatus => StackStatus.Length > 0;

    public bool HasArtworkChoice => _map.HasArtworkChoice;

    public bool PrefersDrawing => _map.PrefersDrawing;

    public bool HideControlsWhenIdle => _map.HideControlsWhenIdle;

    public bool CanFrameArea => _map.CanFrameArea;

    public string FrameAreaText => _map.FrameAreaText;

    public string FloorVariantHint => _map.FloorVariantHint;

    public bool HasFloorVariant => _map.HasFloorVariant;

    /// <summary>What the map itself is saying — V1's status line, under the map.</summary>
    public string StatusLine => _map.StatusLine;

    public bool HasStatusLine => !string.IsNullOrWhiteSpace(StatusLine);

    /// <summary>The player's own marker exists, so Follow has something to move to.</summary>
    public bool HasPlayerMarker => _map.HasPlayerMarker;

    public bool HasPlayerTrail => _map.HasPlayerTrail;

    // The V1 panels beside the map, listed unchanged.
    public IReadOnlyList<SpawnPanelViewModel> SpawnPanel => _map.SpawnPanel;

    public bool HasSpawnPanel => _map.HasSpawnPanel;

    public string SpawnPanelDetail => _map.SpawnPanelDetail;

    public bool HasSpawnPanelDetail => !string.IsNullOrWhiteSpace(SpawnPanelDetail);

    public IReadOnlyList<SpawnThreatViewModel> SpawnThreats => _map.SpawnThreats;

    public bool HasSpawnThreats => _map.HasSpawnThreats;

    public IReadOnlyList<ExtractPanelViewModel> ExtractPanel => _map.ExtractPanel;

    public bool HasExtractPanel => _map.HasExtractPanel;

    public IReadOnlyList<QuestPanelViewModel> QuestPanel => _map.QuestPanel;

    public bool HasQuestPanel => _map.HasQuestPanel;

    public IReadOnlyList<LootPanelViewModel> LootPanel => _map.LootPanel;

    public bool HasLootPanel => _map.HasLootPanel;

    public IReadOnlyList<GroupMemberPanelViewModel> GroupPanel => _map.GroupPanel;

    public bool HasGroupPanel => _map.HasGroupPanel;

    /// <summary>The group's waypoints as rows, with V1's clear-all and clear-reached.</summary>
    public IReadOnlyList<MarkListItemViewModel> MarkList => _map.MarkList;

    public bool HasMarkList => _map.HasMarkList;

    public bool HasReachedMarks => _map.HasReachedMarks;

    public ICommand ClearMarksCommand => _map.ClearMarksCommand;

    public ICommand ClearReachedMarksCommand => _map.ClearReachedMarksCommand;

    public ICommand RemoveMarkCommand => _map.RemoveMarkCommand;

    public ICommand PlaceWaypointCommand { get; }

    public ICommand PlacePingCommand { get; }

    public ICommand CancelPlacingCommand { get; }

    public bool IsPlacingWaypoint => _armedMarkKind == RaidMarkKind.Waypoint;

    public bool IsPlacingPing => _armedMarkKind == RaidMarkKind.Ping;

    public bool IsPlacingMark => _armedMarkKind is not null;

    public string TimeLeft => _raid.TimeLeft;

    public string TimeLeftDetail => _raid.TimeLeftDetail;

    public IReadOnlyList<ActiveExtractViewModel> Extracts => _raid.Extracts;

    public bool HasExtracts => Extracts.Count > 0;

    public string ExtractsNotMatched => _raid.ExtractsNotMatched;

    public bool HasExtractsNotMatched => !string.IsNullOrWhiteSpace(ExtractsNotMatched);

    public string Transits => _raid.Transits;

    public bool HasTransits => !string.IsNullOrWhiteSpace(Transits);

    /// <summary>The raid clock when one is running, otherwise a quiet "In raid" / "Not in raid"
    /// rather than the legacy page's "Unknown" plus a full sentence.</summary>
    public string RaidPhaseLabel =>
        !string.Equals(TimeLeft, "Unknown", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(TimeLeft)
            ? TimeLeft
            : _stateStore.Current.Raid.State == RaidLifecycleState.InRaid ? "In raid" : "Not in raid";

    /// <summary>False while no raid clock is running, so the quiet phase label stands alone.</summary>
    public bool HasRaidPhaseDetail =>
        !string.Equals(TimeLeft, "Unknown", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(TimeLeftDetail);

    /// <summary>The current map's name for the context panel header, e.g. "CUSTOMS".</summary>
    public string MapTitle => (SelectedMap?.Name ?? Renderer?.LocationLabel ?? string.Empty).ToUpper(CultureInfo.CurrentCulture);

    /// <summary>"12 extracts · 5 spawn areas" for the context panel header.</summary>
    public string MapSummary => string.Join(
        " · ",
        new[]
        {
            MapExtracts.Count == 0 ? null : $"{MapExtracts.Count} {(MapExtracts.Count == 1 ? "extract" : "extracts")}",
            SpawnAreas.Count == 0 ? null : $"{SpawnAreas.Count} {(SpawnAreas.Count == 1 ? "spawn area" : "spawn areas")}",
        }.Where(part => part is not null));

    /// <summary>Extracts and transits on the current map, offered first.</summary>
    public IReadOnlyList<RaidExtractRowViewModel> MapExtracts { get; private set; } = [];

    public bool HasMapExtracts => MapExtracts.Count > 0;

    /// <summary>Named spawn areas on the current map (potential spawns, never observed players).</summary>
    public IReadOnlyList<string> SpawnAreas { get; private set; } = [];

    public bool HasSpawnAreas => SpawnAreas.Count > 0;

    /// <summary>
    /// The host calls this from a plan click while a mark tool is armed. It is a no-op
    /// otherwise, so wiring it unconditionally to <c>MapSceneRendererView.PlanClicked</c> is
    /// always safe.
    /// </summary>
    public void PlaceArmedMarkAt(MapScenePoint point)
    {
        if (_armedMarkKind is not { } kind || _map.RenderModel is not { } model)
        {
            return;
        }

        var kindToPlace = kind;
        // The floor the mark belongs on is whichever one the V2 renderer is showing, not
        // whatever the V1 map last had selected — the two floor selections are independent.
        var floorId = Renderer?.Scene.View.SelectedFloorId ?? model.SelectedFloor?.Id;
        ArmMark(null);
        _ = PlaceMarkAsync(kindToPlace, model.Location.Id, floorId, point.X, point.Y);
    }

    /// <summary>
    /// The host calls this from a right-click that hit something on the plan. A ping or a
    /// waypoint of ours is removed; anything else — an extract, a loot spawn, another map
    /// object entirely — is left alone. A right-click that hit nothing never reaches here at
    /// all (<c>MapSceneRendererView.MarkerRightClicked</c> only fires on a hit), so bare-map
    /// right-click keeps whatever it already did, which is nothing.
    /// </summary>
    public void RemoveMarkAt(MapSceneObjectId objectId)
    {
        if (!TryParseMarkId(objectId, out var markId))
        {
            return;
        }

        _ = _marks.RemoveAsync(markId);
    }

    /// <summary>Whether a scene object id names one of ours, and which mark it is if so.</summary>
    /// <remarks>Internal for direct coverage of "a right-click that hit an extract, a loot spawn,
    /// or anything else that is not a mark of ours does nothing" — see the unit tests.</remarks>
    internal static bool TryParseMarkId(MapSceneObjectId objectId, out Guid markId)
    {
        markId = Guid.Empty;
        return objectId.Value.StartsWith(MarkIdPrefix, StringComparison.Ordinal) &&
            Guid.TryParse(objectId.Value.AsSpan(MarkIdPrefix.Length), out markId);
    }

    public void Dispose()
    {
        _map.PropertyChanged -= MapPropertyChanged;
        _map.PlayerFollowRequested -= PlayerFollowRequested;
        _raid.PropertyChanged -= RaidPropertyChanged;
        _stateStore.Changed -= RuntimeStateChanged;
        _marks.Changed -= MarksChanged;
        if (Renderer is { } renderer)
        {
            renderer.ViewChangeRequested -= ViewChangeRequested;
            renderer.HighValueLootFilterRequested -= HighValueLootFilterRequested;
        }

        _rebuildCancellation?.Cancel();
        _rebuildCancellation?.Dispose();
        _backgroundImage?.Dispose();
    }

    /// <summary>The V2 renderer's asset seam: the artwork for the reviewed background asset it
    /// is about to draw, decoded ahead of time in <see cref="RebuildCoreAsync"/> since this is
    /// called synchronously from the renderer's own present/rebuild pass.</summary>
    private IImage? ResolveBackgroundImage(MapSceneAsset asset) =>
        _cachedAsset is { } cached && string.Equals(asset.ContentSha256, cached.ContentSha256, StringComparison.OrdinalIgnoreCase)
            ? _backgroundImage
            : null;

    /// <summary>
    /// Swaps in newly decoded artwork, disposing the previous bitmap once the current render
    /// pass has finished with it rather than immediately, mirroring MapViewModel.ReleaseLater.
    /// </summary>
    private void ReplaceBackgroundImage(Bitmap? image)
    {
        var previous = _backgroundImage;
        _backgroundImage = image;
        if (previous is not null && !ReferenceEquals(previous, image))
        {
            Dispatcher.UIThread.Post(previous.Dispose, DispatcherPriority.Background);
        }
    }

    /// <summary>Decodes a cached rasterized-map image file off the UI thread.</summary>
    private static async Task<Bitmap?> LoadBackgroundImageAsync(string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return await Task.Run(
                () =>
                {
                    using var stream = File.OpenRead(path);
                    return new Bitmap(stream);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            return null;
        }
    }

    private async Task InitializeAsync()
    {
        await _marks.LoadAsync().ConfigureAwait(true);
        await RebuildAsync().ConfigureAwait(true);
    }

    private void ArmMark(RaidMarkKind? kind)
    {
        if (_armedMarkKind == kind)
        {
            return;
        }

        _armedMarkKind = kind;
        OnPropertyChanged(nameof(IsPlacingWaypoint));
        OnPropertyChanged(nameof(IsPlacingPing));
        OnPropertyChanged(nameof(IsPlacingMark));
    }

    private async Task PlaceMarkAsync(RaidMarkKind kind, string mapId, string? floorId, double x, double y)
    {
        await _marks.AddAsync(kind, mapId, floorId, x, y, label: null).ConfigureAwait(true);
    }

    private async Task SelectMapAsync(string mapId)
    {
        await _map.FollowRaidAsync(mapId).ConfigureAwait(true);
    }

    /// <summary>
    /// The map view model's properties that this cockpit either re-publishes or redraws for.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 22] A plain forward: the cockpit shows V1's own state, so when V1 says
    /// it changed, the cockpit says the same. The scene is rebuilt only for the properties that
    /// change what is drawn on the plan; a toggle's own label is not one of them.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string[]> MapEchoes = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [nameof(MapViewModel.FollowsPlayer)] = [nameof(FollowsPlayer)],
        [nameof(MapViewModel.RotationDegrees)] = [nameof(RotationLabel), nameof(IsRotated)],
        [nameof(MapViewModel.VisitedLabel)] = [nameof(VisitedLabel), nameof(ShowsVisited)],
        [nameof(MapViewModel.ShowsGroupNames)] = [nameof(ShowsGroupNames)],
        [nameof(MapViewModel.AutoSelectsFloor)] = [nameof(AutoSelectsFloor)],
        [nameof(MapViewModel.IsStacked)] = [nameof(IsStacked), nameof(HasFloorStack)],
        [nameof(MapViewModel.StackStatus)] = [nameof(StackStatus), nameof(HasStackStatus)],
        [nameof(MapViewModel.HasArtworkChoice)] = [nameof(HasArtworkChoice)],
        [nameof(MapViewModel.PrefersDrawing)] = [nameof(PrefersDrawing)],
        [nameof(MapViewModel.HideControlsWhenIdle)] = [nameof(HideControlsWhenIdle)],
        [nameof(MapViewModel.FrameAreaText)] = [nameof(FrameAreaText), nameof(CanFrameArea)],
        [nameof(MapViewModel.FloorVariantHint)] = [nameof(FloorVariantHint), nameof(HasFloorVariant)],
        [nameof(MapViewModel.StatusLine)] = [nameof(StatusLine), nameof(HasStatusLine)],
        [nameof(MapViewModel.Status)] = [nameof(StatusLine), nameof(HasStatusLine)],
        [nameof(MapViewModel.SpawnPanel)] = [nameof(SpawnPanel), nameof(HasSpawnPanel)],
        [nameof(MapViewModel.SpawnPanelDetail)] = [nameof(SpawnPanelDetail), nameof(HasSpawnPanelDetail)],
        [nameof(MapViewModel.SpawnThreats)] = [nameof(SpawnThreats), nameof(HasSpawnThreats)],
        [nameof(MapViewModel.ExtractPanel)] = [nameof(ExtractPanel), nameof(HasExtractPanel)],
        [nameof(MapViewModel.QuestPanel)] = [nameof(QuestPanel), nameof(HasQuestPanel)],
        [nameof(MapViewModel.LootPanel)] = [nameof(LootPanel), nameof(HasLootPanel)],
        [nameof(MapViewModel.GroupPanel)] = [nameof(GroupPanel), nameof(HasGroupPanel)],
        [nameof(MapViewModel.MarkList)] = [nameof(MarkList), nameof(HasMarkList), nameof(HasReachedMarks)],
        [nameof(MapViewModel.Floors)] = [nameof(CanStack)],
    };

    /// <summary>The map state that changes what is drawn on the plan, so the scene is rebuilt.</summary>
    private static readonly IReadOnlySet<string> MapRedraws = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(MapViewModel.PlayerMarkers),
        nameof(MapViewModel.PlayerTrail),
        nameof(MapViewModel.GroupMarkers),
        nameof(MapViewModel.GroupTrails),
        nameof(MapViewModel.VisitedTrails),
        nameof(MapViewModel.ShowsGroupNames),
        nameof(MapViewModel.SelectedFloor),
        nameof(MapViewModel.Overlays),
    };

    private void MapPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null)
        {
            return;
        }

        if (MapEchoes.TryGetValue(e.PropertyName, out var echoed))
        {
            foreach (var name in echoed)
            {
                OnPropertyChanged(name);
            }
        }

        if (e.PropertyName is nameof(MapViewModel.RenderModel))
        {
            OnPropertyChanged(nameof(SelectedMap));
            _ = RebuildAsync();
        }
        else if (e.PropertyName is nameof(MapViewModel.Locations))
        {
            // The catalog loads after construction, so the picker built from an empty list at
            // startup has to be rebuilt once it actually has maps to offer.
            RebuildMapPicker();
        }
        else if (e.PropertyName is nameof(MapViewModel.RotationDegrees))
        {
            // V1 turns its own surface; here the same quarter turn is the scene camera's
            // bearing, so markers and place names stay upright over a turned plan.
            Renderer?.SetBearing(_map.RotationDegrees);
        }
        else if (e.PropertyName is nameof(MapViewModel.PlayerMarkers))
        {
            OnPropertyChanged(nameof(HasPlayerMarker));
            OnPropertyChanged(nameof(HasPlayerTrail));
            _ = RebuildAsync();
        }
        else if (MapRedraws.Contains(e.PropertyName))
        {
            _ = RebuildAsync();
        }
    }

    /// <summary>
    /// V1 asks its view to move to the player when a screenshot places them; here the scene
    /// camera does it.
    /// </summary>
    /// <remarks>
    /// Deferred when there is no renderer yet (the first screenshot of a session can easily
    /// arrive before the map asset has finished decoding): the pending follow is honoured by the
    /// next rebuild that produces one, rather than being dropped.
    /// </remarks>
    private void PlayerFollowRequested(object? sender, EventArgs e) => FollowPlayer();

    private void FollowPlayer()
    {
        if (Renderer is null)
        {
            _followPending = true;
            return;
        }

        if (PlayerPlanPoint() is not { } point)
        {
            return;
        }

        _followPending = false;
        Renderer.FocusOn(point, FollowZoom);
    }

    /// <summary>Where the player is in plan coordinates, when a screenshot has placed them.</summary>
    private MapScenePoint? PlayerPlanPoint() =>
        _map.PlayerPosition is { } position && _map.RenderModel is { } model &&
        model.TryMapPosition(position.Position, out var point) &&
        double.IsFinite(point.X) && double.IsFinite(point.Y)
            ? new MapScenePoint(point.X, point.Y)
            : null;

    /// <summary>
    /// V1's "Frame area": put the part of the map the raid is actually happening in on screen.
    /// </summary>
    /// <remarks>
    /// V1 frames a rectangle by scrolling and scaling its viewport. This renderer's camera is a
    /// centre and a zoom rather than a rectangle, so the rough equivalent is to centre on the
    /// player at the follow zoom, which is the same question ("show me where this is happening")
    /// with the same answer in the common case. Framing the true bounds is listed as deferred.
    /// </remarks>
    private void FrameArea()
    {
        _map.RequestFit();
        FollowPlayer();
    }

    private void RebuildMapPicker()
    {
        MapPicker = [.. _map.Locations
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .Select(location => new RaidMapPickerItemViewModel(location.Id, location.Name, SelectMapAsync))];
        OnPropertyChanged(nameof(MapPicker));
        OnPropertyChanged(nameof(SelectedMap));
    }

    private void RaidPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RaidPageViewModel.TimeLeft):
                OnPropertyChanged(nameof(TimeLeft));
                OnPropertyChanged(nameof(RaidPhaseLabel));
                OnPropertyChanged(nameof(HasRaidPhaseDetail));
                break;
            case nameof(RaidPageViewModel.TimeLeftDetail):
                OnPropertyChanged(nameof(TimeLeftDetail));
                OnPropertyChanged(nameof(HasRaidPhaseDetail));
                break;
            case nameof(RaidPageViewModel.Extracts):
                OnPropertyChanged(nameof(Extracts));
                OnPropertyChanged(nameof(HasExtracts));
                break;
            case nameof(RaidPageViewModel.ExtractsNotMatched):
                OnPropertyChanged(nameof(ExtractsNotMatched));
                OnPropertyChanged(nameof(HasExtractsNotMatched));
                break;
            case nameof(RaidPageViewModel.Transits):
                OnPropertyChanged(nameof(Transits));
                OnPropertyChanged(nameof(HasTransits));
                break;
        }
    }

    private void RuntimeStateChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(RaidPhaseLabel));
        _ = RebuildAsync();
    }

    private void MarksChanged()
    {
        RefreshMarkRows();
        _ = RebuildAsync();
    }

    private void RefreshMarkRows()
    {
        var mapId = _map.RenderModel?.Location.Id;
        Marks = mapId is null
            ? []
            : [
                .. LabelMarksForMap(_marks.Marks, mapId)
                    .OrderByDescending(item => item.Mark.CreatedUtc)
                    .Select(item => new RaidMarkRowViewModel(item.Mark, item.Label, RenameMarkAsync, id => _marks.RemoveAsync(id))),
                .. GroupMarkRows(mapId),
            ];
        OnPropertyChanged(nameof(Marks));
        OnPropertyChanged(nameof(HasMarks));
    }

    /// <summary>
    /// V2 rough package 20: the group's marks for this map, each with the relay removal the Team
    /// workspace already uses, so "how do I get rid of this waypoint" has one answer wherever the
    /// waypoint came from. Empty when this cockpit was built without a group session.
    /// </summary>
    private IEnumerable<RaidMarkRowViewModel> GroupMarkRows(string mapId)
    {
        if (_groupSession is not { } session)
        {
            yield break;
        }

        var group = _stateStore.Current.Group;
        var number = 0;
        foreach (var waypoint in group.Waypoints.Where(item => string.Equals(item.MapId, mapId, StringComparison.OrdinalIgnoreCase)))
        {
            number++;
            yield return new(
                waypoint.Id,
                RaidMarkKind.Waypoint,
                string.IsNullOrWhiteSpace(waypoint.Label) ? number.ToString(CultureInfo.CurrentCulture) : waypoint.Label!,
                waypoint.By,
                id => session.RemoveMarkAsync(id, CancellationToken.None));
        }

        foreach (var ping in group.Pings.Where(item => string.Equals(item.MapId, mapId, StringComparison.OrdinalIgnoreCase)))
        {
            yield return new(
                ping.Id,
                RaidMarkKind.Ping,
                string.IsNullOrWhiteSpace(ping.Label) ? "Ping" : ping.Label!,
                ping.By,
                id => session.RemoveMarkAsync(id, CancellationToken.None));
        }
    }

    private Task RenameMarkAsync(Guid id, string? name) => _marks.RenameAsync(id, name);

    private async Task RebuildAsync()
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _rebuildCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        var cancellationToken = cancellation.Token;

        try
        {
            await RebuildCoreAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RebuildCoreAsync(CancellationToken cancellationToken)
    {
        RefreshMarkRows();
        var model = _map.RenderModel;
        if (model is null)
        {
            SetUnavailable("No map is loaded yet.");
            return;
        }

        var variant = model.Variant;
        if (variant.SvgPath is null)
        {
            SetUnavailable("This map has no reviewed 2D plan yet, so the V2 renderer cannot draw it.");
            return;
        }

        // Refetching on every rebuild (a runtime snapshot changes often) would re-hash a
        // multi-megabyte SVG on disk each time for no reason once the variant and floor have
        // not changed. Keyed on the floor too: a multi-floor SVG rasterizes a different upstream
        // layer per floor onto the same cache file, so the source hash alone cannot tell floors
        // apart the way V1's own floor switch relies on (see TarkovDevMapAssetCache.GetSvgAsync).
        var selectedFloor = model.SelectedFloor;
        var assetCacheKey = $"{variant.Key}::{selectedFloor?.Id ?? string.Empty}";
        if (_cachedAssetVariantKey != assetCacheKey || _cachedAsset is null)
        {
            var assetResult = await _assetCache.GetSvgAsync(variant, selectedFloor, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (assetResult.Asset is not { Availability: not MapAssetAvailability.Unavailable } fetched)
            {
                _cachedAssetVariantKey = null;
                _cachedAsset = null;
                ReplaceBackgroundImage(null);
                SetUnavailable(assetResult.Message ?? "The reviewed map asset is not available yet.");
                return;
            }

            // Decoded, and the cache markers updated, only once both steps succeed: if the
            // decode is cancelled by a newer rebuild landing first, leaving the markers behind
            // would make the next rebuild trust a bitmap that was never actually produced.
            var decoded = await LoadBackgroundImageAsync(fetched.RenderPath, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            _cachedAssetVariantKey = assetCacheKey;
            _cachedAsset = fetched;
            ReplaceBackgroundImage(decoded);
        }

        var cached = _cachedAsset!;

        var nowUtc = _timeProvider.GetUtcNow();
        var transformVersion = variant.Key;
        // The floor is part of the asset identity, not just its cache key: a multi-floor SVG
        // rasterizes a different upstream layer per floor onto the one cached file, so two
        // floors' artwork share every other field (source, licence, content hash) and would
        // otherwise look identical to the renderer's own change detection (see
        // MapSceneRendererViewModel.ResolveBackground), leaving a stale floor's picture on
        // screen after switching.
        var asset = new MapSceneAsset(
            new($"asset:{model.Location.Id}:{variant.Key}:{selectedFloor?.Id ?? "base"}"),
            MapSceneAssetKind.Background2D,
            cached.SourceUri,
            cached.LicenseUri,
            cached.ContentSha256,
            string.IsNullOrWhiteSpace(cached.Author) ? "Tarkov.dev community mapping" : cached.Author,
            variant.Key,
            "current",
            MapSceneAssetReviewStatus.Reviewed,
            cached.RetrievedUtc);

        var raidSnapshot = _stateStore.Current.Raid;
        // [V2 rough package 22] Place names, spawn areas and locked doors too, not only extracts
        // and objectives. The assembler has always adapted all five; the cockpit asked for two of
        // them, which is why V2 had no street names and why its own "Spawn areas" card was always
        // empty. Each one is still a scene layer with its own switch in the bottom strip.
        var legacyElements = model.OverlayElements
            .Where(element => element.Layer is MapOverlayKind.Extracts or MapOverlayKind.QuestObjectives
                or MapOverlayKind.Labels or MapOverlayKind.Spawns or MapOverlayKind.Keys)
            .Select(element => new MapSceneLegacyElement(
                element,
                new DataProvenance("map-catalog", nowUtc),
                element.Layer == MapOverlayKind.Extracts && MapViewModel.IsOfferedMarker(element.Label, raidSnapshot.ActiveExtracts)
                    ? MapSceneOfferState.Offered
                    : MapSceneOfferState.Unknown))
            .ToArray();

        var floorIds = model.Floors.Select(floor => floor.Id).ToArray();
        var lootLayer = _lootSource.Build(new HighValueLootRuntimeLayerRequest(
            model.Location.Id,
            transformVersion,
            PlanBounds,
            nowUtc,
            _lootFilter.Filter,
            floorIds));

        var (marksLayer, markObjects) = BuildMarksLayer(_marks.Marks, model.Location.Id, nowUtc);
        // [V2 rough package 22] You, your trail, the squad and where you have been before.
        var live = BuildLiveLayers(model, nowUtc);
        _objectStyles = live.Styles;
        // The renderer requires the scene to already declare the exact loot layer it is handed
        // beside it (see EnsureHighValueLootMatchesScene), so the loot layer and its objects are
        // merged in here rather than attached only through the constructor/Present overload.
        var additionalLayers = (marksLayer is { } definiteMarksLayer
            ? new[] { lootLayer.Layer, definiteMarksLayer }
            : [lootLayer.Layer]).Concat(live.Layers).ToArray();
        var additionalObjects = lootLayer.Objects.Concat(markObjects).Concat(live.Objects).ToArray();

        // The floor the plan is drawn on is V1's, because V1 is what fetches the artwork for it
        // and what an automatic floor change (AutoSelectsFloor) moves. The renderer's own
        // selection is pushed back into V1 by ViewChangeRequested, so the two only ever differ
        // for the moment between the two halves of one change.
        var modelFloorChanged = !string.Equals(_modelFloorId, model.SelectedFloor?.Id, StringComparison.OrdinalIgnoreCase);
        _modelFloorId = model.SelectedFloor?.Id;
        var requestedView = Renderer is { } current && string.Equals(current.Scene.LocationId, model.Location.Id, StringComparison.Ordinal)
            ? modelFloorChanged
                ? current.Scene.View with { SelectedFloorId = model.SelectedFloor?.Id }
                : current.Scene.View
            // A map opens the way V1 has it turned: which way round a map wants to be is a fact
            // about the map, remembered per map, not about this session.
            : new MapSceneViewState(MapSceneMode.Flat2D, model.SelectedFloor?.Id, new(50, 50, 1, Bearing(), 0), []);

        var request = new MapSceneBuildRequest(
            Interlocked.Increment(ref _revision),
            model,
            PlanBounds,
            transformVersion,
            requestedView,
            legacyElements,
            additionalLayers,
            additionalObjects,
            [asset]);
        var result = _assembler.Build(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Scene is not { } scene)
        {
            SetUnavailable(result.UnavailableReason ?? "The map scene is unavailable.");
            return;
        }

        if (Renderer is null)
        {
            var renderer = new MapSceneRendererViewModel(
                scene,
                _presentation,
                nextChangeId: Guid.NewGuid,
                reviewedAssetResolver: ResolveBackgroundImage,
                highValueLoot: lootLayer,
                highValueLootFilterState: _lootFilter,
                highValueLootCategories: null,
                // The Raid workspace's own right panel and bottom strip already show marks,
                // extracts, the raid clock and the layer switches; the renderer's own
                // search/layers/selection/loot-filter column would just be a second, narrower
                // copy of the same map beside it. Loot filtering stays reachable through
                // Renderer.HighValueLoot in this page's own right panel.
                showsDetailsPanel: false,
                // Fit is "contain, centred": the whole plan stays visible (an extract at the edge
                // is never cropped away) and the map card's own surface fills around it.
                fillsViewport: false,
                floorNameResolver: FloorName,
                styleResolver: StyleFor);
            renderer.ViewChangeRequested += ViewChangeRequested;
            renderer.HighValueLootFilterRequested += HighValueLootFilterRequested;
            Renderer = renderer;
            OnPropertyChanged(nameof(Renderer));
        }
        else
        {
            Renderer.Present(scene, lootLayer, _lootFilter, availableCategories: null);
        }

        OnPropertyChanged(nameof(HasRenderer));
        if (_followPending || _map.FollowsPlayer && Renderer.Scene.View.Camera.Zoom <= 1.0001)
        {
            // A screenshot that arrived before the artwork finished decoding still moves the map
            // to the player, once there is a map to move.
            FollowPlayer();
        }

        RefreshSceneLists(scene);
        SceneRebuilt?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// V2 rough package 17: raised after a scene rebuild presented, so the artwork and render
    /// model <see cref="CreateObjectivePreview"/> reads are current.
    /// </summary>
    public event EventHandler? SceneRebuilt;

    /// <summary>
    /// V2 rough package 17: a second, independent scene of the map this cockpit currently shows,
    /// carrying only the given quest objective markers, for the Plan workspace's centre map.
    /// </summary>
    /// <remarks>
    /// Its own renderer view model, not <see cref="Renderer"/>: the renderer keeps viewport and
    /// camera state, and two views sizing one renderer would fight over it. It reuses this
    /// cockpit's artwork, decoded bitmap and assembler, so Plan does not grow a second map
    /// pipeline. Null while this cockpit has no scene of its own.
    /// </remarks>
    internal MapSceneRendererViewModel? CreateObjectivePreview(
        IReadOnlyList<MapOverlayElement> objectives,
        MapSceneRendererViewModel? existing)
    {
        ArgumentNullException.ThrowIfNull(objectives);
        if (PreviewModel() is not { } model)
        {
            return null;
        }

        var nowUtc = _timeProvider.GetUtcNow();
        return BuildPreview(
            model,
            objectives.Select(element => new MapSceneLegacyElement(element, new DataProvenance("quest-catalog", nowUtc))).ToArray(),
            [],
            [],
            existing);
    }

    /// <summary>The render model a preview may be built from: only while this cockpit's own scene shows it.</summary>
    private MapRenderModel? PreviewModel() =>
        Renderer is { } current && _map.RenderModel is { } model &&
        string.Equals(current.Scene.LocationId, model.Location.Id, StringComparison.Ordinal)
            ? model
            : null;

    /// <summary>
    /// The one scene-building path behind both previews (Plan's objectives, Team's marks): its
    /// own renderer view model over this cockpit's artwork, assembler and plan bounds, keeping
    /// the existing preview's camera while it still shows the same map.
    /// </summary>
    private MapSceneRendererViewModel? BuildPreview(
        MapRenderModel model,
        IReadOnlyList<MapSceneLegacyElement> legacyElements,
        IReadOnlyList<MapSceneLayer> layers,
        IReadOnlyList<MapSceneObject> objects,
        MapSceneRendererViewModel? existing)
    {
        var sameMap = existing is not null &&
            string.Equals(existing.Scene.LocationId, model.Location.Id, StringComparison.Ordinal);
        var request = new MapSceneBuildRequest(
            (existing?.Scene.Revision ?? 0) + 1,
            model,
            PlanBounds,
            model.Variant.Key,
            sameMap
                ? existing!.Scene.View
                : new MapSceneViewState(MapSceneMode.Flat2D, model.SelectedFloor?.Id, new(50, 50, 1, 0, 0), []),
            legacyElements,
            layers,
            objects,
            Renderer!.Scene.Assets);
        if (_assembler.Build(request).Scene is not { } scene)
        {
            return null;
        }

        if (sameMap)
        {
            existing!.Present(scene);
            return existing;
        }

        return new MapSceneRendererViewModel(
            scene,
            _presentation,
            nextChangeId: Guid.NewGuid,
            reviewedAssetResolver: ResolveBackgroundImage,
            showsDetailsPanel: false,
            fillsViewport: false);
    }

    /// <summary>Internal for direct coverage (see the unit tests).</summary>
    internal static IReadOnlyList<RaidExtractRowViewModel> BuildExtractRows(IReadOnlyList<MapSceneObject> objects) => objects
        .Where(item => item.Kind is MapSceneObjectKind.Extract or MapSceneObjectKind.Transit)
        .GroupBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderByDescending(item => item.OfferState == MapSceneOfferState.Offered).First())
        .OrderBy(item => item.OfferState switch
        {
            MapSceneOfferState.Offered => 0,
            MapSceneOfferState.Unknown => 1,
            _ => 2,
        })
        .ThenBy(item => item.Kind)
        .ThenBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase)
        .Select(item => new RaidExtractRowViewModel(
            item.Label,
            item.Kind == MapSceneObjectKind.Transit ? "Transit" : item.Faction switch
            {
                MapFeatureFaction.Pmc => "PMC",
                MapFeatureFaction.Scav => "Scav",
                MapFeatureFaction.Shared => "PMC · Scav",
                _ => "",
            },
            item.OfferState))
        .ToArray();

    private void RefreshSceneLists(MapSceneSnapshot scene)
    {
        MapExtracts = BuildExtractRows(scene.Objects);
        SpawnAreas = scene.Objects
            .Where(item => item.Kind == MapSceneObjectKind.SpawnArea)
            .Select(item => item.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        OnPropertyChanged(nameof(MapExtracts));
        OnPropertyChanged(nameof(HasMapExtracts));
        OnPropertyChanged(nameof(SpawnAreas));
        OnPropertyChanged(nameof(HasSpawnAreas));
        OnPropertyChanged(nameof(MapTitle));
        OnPropertyChanged(nameof(MapSummary));
    }

    private void ViewChangeRequested(MapSceneViewChange change)
    {
        if (Renderer is null)
        {
            return;
        }

        var result = MapSceneViewReducer.Apply(Renderer.Scene, change);
        if (result.Status is MapSceneViewChangeStatus.Applied or MapSceneViewChangeStatus.Unchanged)
        {
            Renderer.Present(result.Scene);
        }

        // [V2 rough package 22] A floor change has to reach V1 too: V1 owns the floor the
        // artwork is rasterized for, so a renderer-only change would filter the markers to the
        // new floor while leaving the old floor's picture underneath them.
        if (change.Kind == MapSceneViewChangeKind.SelectFloor &&
            result.Status == MapSceneViewChangeStatus.Applied &&
            _map.Floors.FirstOrDefault(floor => string.Equals(floor.Id, change.FloorId, StringComparison.OrdinalIgnoreCase)) is { } selected &&
            !string.Equals(_map.SelectedFloor?.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
        {
            _ = _map.SelectFloorAsync(selected);
        }
    }

    /// <summary>The catalog's own name for a floor the scene only knows as an id.</summary>
    private string FloorName(string floorId) => _map.Floors
        .FirstOrDefault(floor => string.Equals(floor.Id, floorId, StringComparison.OrdinalIgnoreCase))?.Name ?? floorId;

    /// <summary>How this cockpit wants one object drawn; see <see cref="MapSceneObjectStyle"/>.</summary>
    private MapSceneObjectStyle? StyleFor(MapSceneObject item) =>
        _objectStyles.TryGetValue(item.Id, out var style) ? style : null;

    /// <summary>V1's remembered quarter turn for this map, as a scene camera bearing.</summary>
    private double Bearing() => (_map.RotationDegrees % 360 + 360) % 360;

    private void HighValueLootFilterRequested(HighValueLootFilterRequest request)
    {
        if (Renderer is null || _map.RenderModel is not { } model ||
            request.ExpectedRevision != Renderer.Scene.Revision ||
            !string.Equals(request.LocationId, Renderer.Scene.LocationId, StringComparison.Ordinal) ||
            !string.Equals(request.TransformVersion, Renderer.Scene.TransformVersion, StringComparison.Ordinal))
        {
            return;
        }

        _lootFilter = request.State;
        var result = _lootSource.Build(new HighValueLootRuntimeLayerRequest(
            model.Location.Id,
            request.TransformVersion,
            PlanBounds,
            _timeProvider.GetUtcNow(),
            request.State.Filter,
            model.Floors.Select(floor => floor.Id).ToArray()));
        var scene = ReplaceLootObjects(Renderer.Scene, result);
        Renderer.Present(scene, result, request.State, availableCategories: null);
    }

    private static MapSceneSnapshot ReplaceLootObjects(MapSceneSnapshot scene, HighValueLootLayerResult loot) => new(
        scene.Revision + 1,
        scene.LocationId,
        scene.VariantKey,
        scene.TransformVersion,
        scene.Bounds,
        scene.FloorIds,
        scene.Capabilities,
        scene.View,
        scene.Layers.Where(layer => layer.Id != HighValueLootLayerService.LayerId).Append(loot.Layer).ToArray(),
        scene.Objects.Where(item => item.LayerId != HighValueLootLayerService.LayerId).Concat(loot.Objects).ToArray(),
        scene.Assets);

    /// <summary>
    /// [V2 rough package 22] What a live-evidence build produced: the layers to declare, the
    /// objects to draw, and how this cockpit wants each of them drawn.
    /// </summary>
    internal sealed record LiveSceneLayers(
        IReadOnlyList<MapSceneLayer> Layers,
        IReadOnlyList<MapSceneObject> Objects,
        IReadOnlyDictionary<MapSceneObjectId, MapSceneObjectStyle> Styles);

    /// <summary>
    /// [V2 rough package 22] Everything the live layers are built from, read off the V1 map view
    /// model.
    /// </summary>
    /// <remarks>
    /// Passed in rather than read from <see cref="_map"/> inside the builder so the mapping from
    /// observed evidence to scene objects can be covered directly, without standing up a whole
    /// map page, a catalog and a runtime store to place one screenshot.
    /// </remarks>
    internal sealed record LiveSceneInputs(
        ScreenshotPosition? Player,
        IReadOnlyList<ScreenshotPosition> PlayerTrail,
        IReadOnlyList<GroupMemberView> Squad,
        Func<string?, bool> IsOnThisMap,
        Func<string, string> ColorFor,
        bool ShowsGroupNames,
        IReadOnlyList<RaidTrail> Visited,
        bool ShowsVisited);

    /// <summary>Colours, chosen here because they are presentation and the scene carries none.</summary>
    private const string PlayerColor = "#FF34D3E8";
    private const string VisitedColor = "#8056B8C6";

    /// <summary>A screenshot older than this is drawn faded, because the player has moved since.</summary>
    private static readonly TimeSpan PositionFreshFor = TimeSpan.FromMinutes(2);

    /// <summary>
    /// You, where you have walked this raid, your squad, and where past raids on this map put
    /// you — as scene layers the canonical renderer already knows how to draw.
    /// </summary>
    /// <remarks>
    /// All four were already in <see cref="MapViewModel"/> and drawn by the V1 map page; none of
    /// it is recomputed here. What is recomputed is the projection: V1's view models carry
    /// positions in its own zoomed canvas pixels, and this renderer works in the plan's percent
    /// box, so the world positions behind them are projected again through the same
    /// <c>TryMapPosition</c> V1 uses rather than being un-projected back out of pixels.
    ///
    /// Headings are turned by the artwork's own rotation for the same reason V1 turns them: the
    /// screenshot records a bearing in the world, and the map is drawn with the world turned.
    /// </remarks>
    private LiveSceneLayers BuildLiveLayers(MapRenderModel model, DateTimeOffset nowUtc) => BuildLiveLayers(
        new(
            _map.PlayerPosition,
            _map.PlayerTrailPositions,
            _map.GroupMembers,
            _map.IsOnOpenMap,
            _map.GroupColorFor,
            _map.ShowsGroupNames,
            _map.VisitedRaids,
            _map.ShowsVisited),
        model,
        nowUtc);

    internal static LiveSceneLayers BuildLiveLayers(LiveSceneInputs inputs, MapRenderModel model, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(model);
        var layers = new List<MapSceneLayer>(3);
        var objects = new List<MapSceneObject>();
        var styles = new Dictionary<MapSceneObjectId, MapSceneObjectStyle>();
        var artworkRotation = model.Variant.Transform?.RotationDegrees ?? 0;

        // --- you ------------------------------------------------------------------------
        var playerObjects = new List<MapSceneObject>(2);
        if (inputs.Player is { } position && TryPlan(model, position.Position, out var here))
        {
            var heading = Bearing(position.HeadingDegrees, artworkRotation);
            var stale = nowUtc - position.Timestamp.ToUniversalTime() > PositionFreshFor;
            playerObjects.Add(new(
                new(PlayerObjectId),
                PlayerLayerId,
                MapSceneObjectKind.LastKnownPosition,
                MapSceneTruthKind.LocalLastKnown,
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"You · {position.Timestamp.ToLocalTime():T} · facing {heading:F0}°"),
                stale ? "From an older screenshot — you have probably moved since." : null,
                MapSceneGeometry.At(here),
                [],
                new DataProvenance("screenshot", position.Timestamp.ToUniversalTime()),
                headingDegrees: heading));
            styles[new(PlayerObjectId)] = new(PlayerColor, Opacity: stale ? 0.65 : 1);
        }

        var trail = PlanPoints(model, inputs.PlayerTrail.Select(step => step.Position));
        if (trail.Count > 1)
        {
            playerObjects.Add(new(
                new(PlayerTrailObjectId),
                PlayerLayerId,
                MapSceneObjectKind.Route,
                MapSceneTruthKind.LocalLastKnown,
                "Your path this raid",
                null,
                new(MapSceneGeometryKind.Line, trail),
                [],
                new DataProvenance("screenshot", nowUtc)));
            styles[new(PlayerTrailObjectId)] = new(PlayerColor, LineThickness: 3);
        }

        if (playerObjects.Count > 0)
        {
            layers.Add(new(PlayerLayerId, "You", 70, true));
            objects.AddRange(playerObjects);
        }

        // --- squad ----------------------------------------------------------------------
        var squadObjects = new List<MapSceneObject>();
        foreach (var member in inputs.Squad)
        {
            if (!inputs.IsOnThisMap(member.MapId))
            {
                continue;
            }

            var color = inputs.ColorFor(member.Name);
            if (member.Position is { } memberPosition && TryPlan(model, memberPosition, out var at))
            {
                var id = new MapSceneObjectId($"squad:{member.Name}");
                var age = member.PositionAge ?? TimeSpan.MaxValue;
                squadObjects.Add(new(
                    id,
                    SquadLayerId,
                    MapSceneObjectKind.TeammateLastKnown,
                    MapSceneTruthKind.TeamSharedLastKnown,
                    member.Name,
                    Describe(age),
                    MapSceneGeometry.At(at),
                    [],
                    new DataProvenance("group-relay", nowUtc),
                    headingDegrees: member.HeadingDegrees is { } memberHeading
                        ? Bearing(memberHeading, artworkRotation)
                        : null));
                styles[id] = new(color, Opacity: age > PositionFreshFor || member.HasGoneQuiet ? 0.6 : 1);

                // V1's "Names": the squadmate's name written beside their marker. Here that is a
                // label object, so it goes through the same place-name text the plan already
                // draws rather than growing a second way to write on the map.
                if (inputs.ShowsGroupNames)
                {
                    var nameId = new MapSceneObjectId($"squad-name:{member.Name}");
                    squadObjects.Add(new(
                        nameId,
                        SquadLayerId,
                        MapSceneObjectKind.Label,
                        MapSceneTruthKind.TeamSharedLastKnown,
                        member.Name,
                        null,
                        MapSceneGeometry.At(new(at.X, at.Y + 1.4)),
                        [],
                        new DataProvenance("group-relay", nowUtc)));
                    styles[nameId] = new(color);
                }
            }

            var memberTrail = PlanPoints(
                model,
                member.Trail.Select(step => new WorldPosition(step.X, 0, step.Z)));
            if (memberTrail.Count > 1)
            {
                var trailId = new MapSceneObjectId($"squad-trail:{member.Name}");
                squadObjects.Add(new(
                    trailId,
                    SquadLayerId,
                    MapSceneObjectKind.Route,
                    MapSceneTruthKind.TeamSharedLastKnown,
                    $"{member.Name}'s path",
                    null,
                    new(MapSceneGeometryKind.Line, memberTrail),
                    [],
                    new DataProvenance("group-relay", nowUtc)));
                styles[trailId] = new(color, LineThickness: 2, Opacity: 0.8);
            }
        }

        if (squadObjects.Count > 0)
        {
            layers.Add(new(SquadLayerId, "Squad", 60, true));
            objects.AddRange(squadObjects);
        }

        // --- visited --------------------------------------------------------------------
        // Off unless asked for, exactly as in V1: it is the one layer that grows with how much
        // somebody has played, and on a map they know well it is a great deal of ink over the
        // things they opened the map to read. MapViewModel loads it on demand, so when the
        // toggle is off there is nothing here to draw anyway.
        var visitedObjects = new List<MapSceneObject>();
        var visited = inputs.Visited;
        for (var index = 0; index < visited.Count; index++)
        {
            var points = PlanPoints(model, visited[index].Positions.Select(step => step.Position));
            if (points.Count < 2)
            {
                continue;
            }

            var id = new MapSceneObjectId($"visited:{visited[index].RaidId}");
            visitedObjects.Add(new(
                id,
                VisitedLayerId,
                MapSceneObjectKind.Route,
                MapSceneTruthKind.LocalLastKnown,
                visited[index].StartedUtc is { } started
                    ? $"Raid on {started.ToLocalTime():d}"
                    : "An earlier raid",
                null,
                new(MapSceneGeometryKind.Line, points),
                [],
                new DataProvenance("raid-history", nowUtc)));
            // Oldest faintest, as V1 draws them: the list arrives newest first.
            styles[id] = new(
                VisitedColor,
                LineThickness: 1.5,
                Opacity: visited.Count <= 1 ? 0.55 : 0.55 - (0.35 * index / (visited.Count - 1.0)));
        }

        if (visitedObjects.Count > 0)
        {
            layers.Add(new(VisitedLayerId, "Visited", 20, inputs.ShowsVisited));
            objects.AddRange(visitedObjects);
        }

        return new(layers, objects, styles);
    }

    /// <summary>A world bearing read off a screenshot, turned into a bearing on the drawn plan.</summary>
    /// <remarks>Internal for direct coverage: pointing the cone the wrong way by whatever the
    /// artwork is rotated by is the bug this exists to prevent, and on some maps that is a
    /// quarter or a half turn.</remarks>
    internal static double Bearing(double headingDegrees, double artworkRotationDegrees) =>
        double.IsFinite(headingDegrees) ? ((headingDegrees - artworkRotationDegrees) % 360 + 360) % 360 : 0;

    private static string Describe(TimeSpan age) => age == TimeSpan.MaxValue
        ? "Position unknown"
        : age < TimeSpan.FromMinutes(1)
            ? string.Create(CultureInfo.CurrentCulture, $"From a screenshot {(int)age.TotalSeconds}s ago")
            : string.Create(CultureInfo.CurrentCulture, $"From a screenshot {(int)age.TotalMinutes}m ago");

    private static bool TryPlan(MapRenderModel model, WorldPosition position, out MapScenePoint point)
    {
        point = default;
        if (!model.TryMapPosition(position, out var mapped) ||
            !double.IsFinite(mapped.X) || !double.IsFinite(mapped.Y))
        {
            return false;
        }

        point = new(mapped.X, mapped.Y);
        return true;
    }

    /// <summary>
    /// A run of world positions as a plan line, dropping the ones this transform cannot place.
    /// </summary>
    /// <remarks>
    /// Points outside the plan's own bounds are dropped rather than clamped: the renderer will
    /// not draw a line that leaves the plan at all (see its bounds check), so one stray
    /// position from a bad transform would otherwise take the whole trail with it.
    /// </remarks>
    private static IReadOnlyList<MapScenePoint> PlanPoints(MapRenderModel model, IEnumerable<WorldPosition> positions)
    {
        var points = new List<MapScenePoint>();
        foreach (var position in positions)
        {
            if (TryPlan(model, position, out var point) && PlanBounds.Contains(point))
            {
                points.Add(point);
            }
        }

        return points;
    }

    /// <summary>Internal for direct coverage of the marks-to-scene mapping (see the unit tests).</summary>
    internal static (MapSceneLayer? Layer, IReadOnlyList<MapSceneObject> Objects) BuildMarksLayer(
        IReadOnlyList<RaidMark> marks,
        string mapId,
        DateTimeOffset nowUtc)
    {
        var labeled = LabelMarksForMap(marks, mapId);
        if (labeled.Count == 0)
        {
            return (null, []);
        }

        var layer = new MapSceneLayer(MarksLayerId, "My marks", 40, true);
        var objects = labeled
            .Select(item => new MapSceneObject(
                new($"{MarkIdPrefix}{item.Mark.Id}"),
                MarksLayerId,
                item.Mark.Kind == RaidMarkKind.Ping ? MapSceneObjectKind.Ping : MapSceneObjectKind.Waypoint,
                MapSceneTruthKind.UserAuthored,
                item.Label,
                null,
                MapSceneGeometry.At(new(item.Mark.State.X, item.Mark.State.Y)),
                item.Mark.State.FloorId is null ? [] : [item.Mark.State.FloorId],
                new DataProvenance("local-mark", nowUtc)))
            .ToArray();
        return (layer, objects);
    }

    /// <summary>
    /// V2 rough package 17 (team): a second, independent scene of the map this cockpit shows,
    /// carrying only the group's marks, for the Team workspace's centre map.
    /// </summary>
    /// <remarks>
    /// Built by the same BuildPreview as the Plan workspace's objective preview: its own renderer view model
    /// (a renderer owns its viewport and camera, so two views cannot share one) over this
    /// cockpit's artwork, assembler and plan bounds, so Team does not grow a second map
    /// pipeline. <paramref name="buildMarks"/> is handed the map's id, which relay map ids
    /// belong to it, and the world-to-plan projection, and returns the layer and objects to
    /// draw. Null while this cockpit has no scene of its own.
    /// </remarks>
    internal MapSceneRendererViewModel? CreateMarksPreview(
        Func<string, Func<string, bool>, Func<WorldPosition, MapScenePoint?>, (MapSceneLayer? Layer, IReadOnlyList<MapSceneObject> Objects)> buildMarks,
        MapSceneRendererViewModel? existing)
    {
        ArgumentNullException.ThrowIfNull(buildMarks);
        if (PreviewModel() is not { } model)
        {
            return null;
        }

        var compatible = TarkovCompanion.Application.Services.Quests.QuestMapProjectionService.CompatibleMapIds(model.Location, model.Variant);
        var (layer, objects) = buildMarks(
            model.Location.Id,
            mapId => compatible.Contains(mapId),
            position => model.TryMapPosition(position, out var point) && double.IsFinite(point.X) && double.IsFinite(point.Y)
                ? new MapScenePoint(point.X, point.Y)
                : null);
        return BuildPreview(model, [], layer is null ? [] : [layer], objects, existing);
    }

    /// <summary>
    /// Every mark on a map, in placement order, paired with the label its scene object and its
    /// row in the marks list must both show.
    /// </summary>
    /// <remarks>
    /// Built in one pass over one order, the same reason <c>MapViewModel.UpdateGroupMarks</c>
    /// numbers its own list inside the loop that draws it: a row numbered by a separate pass
    /// over a separately filtered or sorted collection can drift from the dot it names the
    /// moment one collection skips something the other kept. A waypoint with no custom name is
    /// numbered among the waypoints on this map only; a ping is always "Ping" and never carries
    /// a custom name — see <see cref="RaidMarkRowViewModel.CanRename"/>.
    /// </remarks>
    internal static IReadOnlyList<(RaidMark Mark, string Label)> LabelMarksForMap(
        IReadOnlyList<RaidMark> marks,
        string mapId)
    {
        var ordered = marks
            .Where(mark => string.Equals(mark.State.MapId, mapId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(mark => mark.CreatedUtc)
            .ThenBy(mark => mark.Id)
            .ToArray();
        var result = new List<(RaidMark, string)>(ordered.Length);
        var waypointNumber = 0;
        foreach (var mark in ordered)
        {
            if (mark.Kind == RaidMarkKind.Ping)
            {
                result.Add((mark, "Ping"));
                continue;
            }

            waypointNumber++;
            var label = string.IsNullOrWhiteSpace(mark.State.Label)
                ? waypointNumber.ToString(CultureInfo.InvariantCulture)
                : mark.State.Label!;
            result.Add((mark, label));
        }

        return result;
    }

    private void SetUnavailable(string reason)
    {
        if (Renderer is not null)
        {
            Renderer.ViewChangeRequested -= ViewChangeRequested;
            Renderer.HighValueLootFilterRequested -= HighValueLootFilterRequested;
            Renderer = null;
            OnPropertyChanged(nameof(Renderer));
            OnPropertyChanged(nameof(HasRenderer));
        }

        UnavailableReason = reason;
    }
}
