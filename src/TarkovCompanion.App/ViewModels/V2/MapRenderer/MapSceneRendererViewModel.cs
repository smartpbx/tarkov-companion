using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.ViewModels.V2.MapRenderer;

/// <summary>A host rebuild request bound to the canonical scene the user was filtering.</summary>
public sealed record HighValueLootFilterRequest
{
    public HighValueLootFilterRequest(
        Guid changeId,
        long expectedRevision,
        string locationId,
        string transformVersion,
        HighValueLootLayerFilterState state)
    {
        if (changeId == Guid.Empty)
        {
            throw new ArgumentException("A loot-filter change requires a non-empty ID.", nameof(changeId));
        }

        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(transformVersion);
        ChangeId = changeId;
        ExpectedRevision = expectedRevision;
        LocationId = locationId;
        TransformVersion = transformVersion;
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    public Guid ChangeId { get; }
    public long ExpectedRevision { get; }
    public string LocationId { get; }
    public string TransformVersion { get; }
    public HighValueLootLayerFilterState State { get; }
}

/// <summary>Presents a canonical map scene without privately applying its state transitions.</summary>
/// <remarks>
/// Desktop and paired clients emit the same revision-checked changes. Rendering may project,
/// bound, page, or cluster a scene for the current viewport, but it never rewrites scene truth.
/// This renderer currently draws one reviewed 2D plan. Unsupported floor-stack and 3D modes are
/// disabled instead of being represented by the same flat image under a different label.
/// </remarks>
public sealed class MapSceneRendererViewModel : BindableViewModel
{
    public const int MaximumPointMarkers = 280;
    public const int MaximumGeometryObjects = 300;

    /// <summary>[V2 rough package 22] Place names drawn at once; past this the map is ink.</summary>
    public const int MaximumPlaceNames = 400;
    public const int ListPageSize = 50;
    public const int MaximumListItems = ListPageSize;
    /// <summary>
    /// A marker's interactive hit box, in pixels (its touch/automation target, not its drawn
    /// box — that stays a 32px chip inside this button; see Views/V2/MapRenderer's
    /// v2-map-marker-chip style). Must match Views/V2/MapRenderer's v2-map-marker style.
    /// </summary>
    public const double MarkerExtent = 44;
    public const int MaximumLootPresetPreservedLayers = 64;

    private const int ClusterColumns = 20;
    private const int ClusterRows = 14;
    // [V2 rough package 39] How the floor stack is drawn. The gap between two plates is a
    // fraction of the drawn plan rather than V1's fixed 140 canvas units, because this card is
    // whatever size the window makes it and a fixed gap is a shove off the top of a small card
    // and invisible on a large one. Clamped so it stays a stack at both ends.
    private const double FloorSeparationFraction = 0.075;
    private const double MinimumFloorSeparation = 16;
    private const double MaximumFloorSeparation = 110;
    /// <summary>How solid a floor that is not being read is drawn; the read one is solid.</summary>
    private const double ContextFloorOpacity = 0.45;
    private const double MapInset = MarkerExtent / 2;
    private static readonly MapSceneLayerId HazardsLayerId = new("hazards");

    private readonly MapSceneRendererPresentation _presentation;
    private readonly Func<Guid> _nextChangeId;
    private readonly Func<MapSceneAsset, IImage?>? _reviewedAssetResolver;
    private readonly RendererPictureHold? _pictureHold;
    private readonly Func<string, string>? _floorNameResolver;
    private readonly Func<MapSceneObject, MapSceneObjectStyle?>? _styleResolver;
    private readonly Func<string, double?>? _floorElevationResolver;
    private MapSceneSnapshot _scene;
    private MapSceneObjectId? _selectedObjectId;
    private MapSceneObjectId? _hoveredRequirementObjectId;
    private string _rendererNotice = string.Empty;
    private MapSceneViewChange? _lastRequestedChange;
    private long? _pendingRevision;
    private double _canvasWidth = 1000;
    private double _canvasHeight = 700;
    private MapSceneProjection _projection;
    private string _searchText = string.Empty;
    private int _listPageIndex;
    private HashSet<MapSceneObjectId>? _clusterFilter;
    private string _clusterFilterLabel = string.Empty;
    private IReadOnlyList<MapSceneObject> _filteredListObjects = [];
    private string? _resolvedAssetKey;
    private IReadOnlyDictionary<MapSceneLayerId, bool>? _lootPresetTargets;
    private readonly IReadOnlySet<MapSceneLayerId> _lootPresetPreservedLayers;
    private string? _selectedLootSpawnId;
    private IReadOnlyList<string>? _lootCategories;
    private readonly bool _fillsViewport;
    // The drawn plan's true width:height, taken from the decoded artwork once it resolves. Scene
    // coordinates are the same normalized square for every map, so this is the only thing that
    // knows Streets is wide and Factory is not. NaN until (or unless) artwork resolves.
    private double _planAspect = double.NaN;
    private string _floorSourceNote = string.Empty;
    // V2 rough package 20: a drag used to move nothing until the pointer came up, then jump. The
    // plan now follows the pointer 1:1 through these two numbers, which only feed the canvas's
    // RenderTransform — no measure, no arrange, no marker rebuild per pointer delta. The camera
    // itself is committed to the canonical scene once, when the drag ends.
    private double _panOffsetX;
    private double _panOffsetY;
    private const int MinimumContentPoints = 8;
    private const double MinimumContentSpan = 0.4;
    private const double ContentMarginFraction = 0.04;

    /// <summary>
    /// [Issue 551] Whether the view is the fitted one: set by Fit and by a new map, cleared by
    /// anything the player does to the camera themselves. While it holds, a change to what the
    /// fit depends on (the card's size, the bearing, the plan) fits again rather than leaving a
    /// fit that was right for a card or a bearing that is gone.
    /// </summary>
    private bool _isFitted;
    private bool _refitting;
    private MapSceneCamera? _panStartCamera;
    private MapSceneCamera? _panTargetCamera;

    public MapSceneRendererViewModel(
        MapSceneSnapshot scene,
        MapSceneRendererPresentation presentation,
        Func<Guid>? nextChangeId = null,
        Func<MapSceneAsset, IImage?>? reviewedAssetResolver = null,
        HighValueLootLayerResult? highValueLoot = null,
        HighValueLootLayerFilterState? highValueLootFilterState = null,
        IReadOnlyList<string>? highValueLootCategories = null,
        IReadOnlyList<MapSceneLayerId>? highValueLootPresetPreservedLayers = null,
        bool showsDetailsPanel = true,
        bool fillsViewport = false,
        // [V2 rough package 22] Two host seams the Raid cockpit needs and nothing else does:
        // a human name for a floor id the scene only knows as a token, and a per-object style
        // (a squadmate's own colour, a fainter line for a path walked last week) that is a
        // presentation choice and so has no place in the scene contract. Both default to "no
        // opinion", which is what every other host wants.
        Func<string, string>? floorNameResolver = null,
        Func<MapSceneObject, MapSceneObjectStyle?>? styleResolver = null,
        // [V2 rough package 39] How high a floor sits in the building, so the stack can be drawn
        // in the order a person walks it rather than the order the catalog happens to list. The
        // scene knows floor ids; only the host holds the catalog's height bands. Null means "no
        // opinion", which leaves the floors in the order the scene gave them.
        Func<string, double?>? floorElevationResolver = null,
        // [#573] Draw potential loot spawns by value (MapLootRanking) under the place names, the
        // rest counted on badges. The Raid cockpit's choice; the renderer gallery keeps the
        // generic grid clusters and their list drill-down.
        bool ranksLootByValue = false,
        // [#775] A lease on each picture the resolver hands out, so its owner cannot free a
        // picture still on this renderer (RendererPictureHold). Null: the host owns nothing it frees.
        Func<IImage, IDisposable?>? pictureLease = null)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _pictureHold = pictureLease is null ? null : new(pictureLease);
        _ranksLootByValue = ranksLootByValue;
        _lootValues = MapLootRanking.ValuesOf(highValueLoot);
        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        _isFitted = IsPlainFit(scene);
        ShowsDetailsPanel = showsDetailsPanel;
        _fillsViewport = fillsViewport;
        _nextChangeId = nextChangeId ?? Guid.NewGuid;
        _reviewedAssetResolver = reviewedAssetResolver;
        _floorNameResolver = floorNameResolver;
        _styleResolver = styleResolver;
        _floorElevationResolver = floorElevationResolver;
        _lootPresetPreservedLayers = CreateLootPresetPreserveSet(scene, highValueLootPresetPreservedLayers);
        _projection = CreateProjection();

        ClearSelectionCommand = new DelegateCommand(ClearSelection);
        FocusNextObjectCommand = new DelegateCommand(() => MoveSelection(1));
        FocusPreviousObjectCommand = new DelegateCommand(() => MoveSelection(-1));
        FitPlanCommand = new DelegateCommand(FitPlan);
        ZoomInCommand = new DelegateCommand(() => RequestZoom(1));
        ZoomOutCommand = new DelegateCommand(() => RequestZoom(-1));
        PreviousPageCommand = new DelegateCommand(() => ChangePage(-1));
        NextPageCommand = new DelegateCommand(() => ChangePage(1));
        ClearClusterCommand = new DelegateCommand(ClearClusterFilter);
        HighValueLootPresetCommand = new DelegateCommand(ApplyHighValueLootPreset);
        FloorUpCommand = new DelegateCommand(() => StepFloor(1));
        FloorDownCommand = new DelegateCommand(() => StepFloor(-1));
        _lootCategories = highValueLootCategories;
        if (highValueLoot is not null)
        {
            var initialFilterState = highValueLootFilterState ?? HighValueLootLayerFilterState.Default;
            EnsureHighValueLootMatchesScene(highValueLoot, initialFilterState.Filter);
            HighValueLoot = new(
                highValueLoot,
                initialFilterState,
                highValueLootCategories,
                scene.FloorIds,
                IsLayerVisible(highValueLoot.Layer.Id),
                presentation,
                RequestHighValueLootFilter,
                SelectHighValueLootEntry,
                RequestHighValueLootRefresh);
            HighValueLoot.ProjectionChanged += HighValueLootProjectionChanged;
        }
        RebuildAll();
    }

    /// <summary>Raised for the owner to apply through the canonical reducer and publish back.</summary>
    public event Action<MapSceneViewChange>? ViewChangeRequested;

    /// <summary>The owner rebuilds the typed layer and canonical scene for this request.</summary>
    public event Action<HighValueLootFilterRequest>? HighValueLootFilterRequested;

    /// <summary>
    /// [Issue 563] The player asked to refresh loot-spawn data from the "no data yet" state,
    /// either from the preset button or the loot panel's own Refresh action. The owner (the Raid
    /// workspace) runs the actual import and rebuilds the scene; this view model owns no I/O.
    /// </summary>
    public event Action? HighValueLootRefreshRequested;

    /// <summary>False when a host (the Raid workspace) already shows search/layers/selection/loot
    /// filters in its own context panel, so this renderer's own details column would just repeat
    /// them beside a narrower map.</summary>
    public bool ShowsDetailsPanel { get; }
    public MapSceneSnapshot Scene => _scene;
    public IReadOnlyList<MapSceneRendererModeViewModel> Modes { get; private set; } = [];
    public IReadOnlyList<MapSceneRendererFloorViewModel> Floors { get; private set; } = [];
    /// <summary>The floor a compact selector shows as chosen; null only before the scene has any.</summary>
    public MapSceneRendererFloorViewModel? SelectedFloor => Floors.FirstOrDefault(floor => floor.IsSelected);
    public IReadOnlyList<MapSceneRendererLayerViewModel> Layers { get; private set; } = [];
    public IReadOnlyList<MapSceneRendererObjectViewModel> SpatialObjects { get; private set; } = [];
    /// <summary>[#453] One list for the life of the renderer, changed entry by entry: see <see cref="ReconciledList{T}"/>.</summary>
    public IReadOnlyList<MapSceneRendererObjectViewModel> PointMarkers => _pointMarkers;

    private readonly ReconciledList<MapSceneRendererObjectViewModel> _pointMarkers = [];
    public IReadOnlyList<MapSceneRendererObjectViewModel> ClusterMarkers { get; private set; } = [];

    /// <summary>
    /// [#573] Count badges for dense spots: three or more extracts, transits or quest objectives
    /// on one building are drawn as one badge while the plan is zoomed out, and pressing it zooms
    /// in on the spot. The marks themselves stay in <see cref="PointMarkers"/> and hide while
    /// their badge shows (MapSceneRendererObjectViewModel.IsShownOnPlan).
    /// </summary>
    public IReadOnlyList<MapSceneRendererObjectViewModel> StackMarkers { get; private set; } = [];

    /// <summary>[#573] Potential loot spawns, drawn under the place names; see <see cref="MapLootRanking"/>.</summary>
    /// <remarks>
    /// [#657] Only the spawns drawn at this zoom, kept in one list the view already holds. It
    /// used to be every ranked spawn on the map (626 on Streets) in a new array on each rebuild,
    /// and a rebuild comes with every squad position and screenshot: the view threw away and
    /// re-styled 626 buttons each time, most of them hidden, 1.4 s of every 2 s in a raid.
    /// </remarks>
    public IReadOnlyList<MapSceneRendererObjectViewModel> LootMarkers => _lootMarkers;

    private readonly ReconciledList<MapSceneRendererObjectViewModel> _lootMarkers = [];

    /// <summary>Every ranked spawn, most valuable first, drawn or not.</summary>
    private MapSceneRendererObjectViewModel[] _rankedLoot = [];

    /// <summary>The deepest zoom this map has been read at: what it revealed keeps its control.</summary>
    private double _lootZoomReached;

    /// <summary>[#573] "+N" per area for loot spawns not drawn yet at this zoom.</summary>
    public IReadOnlyList<MapSceneRendererObjectViewModel> LootBadges => _lootBadges;

    private readonly ReconciledList<MapSceneRendererObjectViewModel> _lootBadges = [];

    private readonly bool _ranksLootByValue;
    private IReadOnlyDictionary<MapSceneObjectId, (LootSpawnValueTier Tier, long Value)> _lootValues;
    public IReadOnlyList<MapSceneRendererGeometryViewModel> GeometryObjects { get; private set; } = [];

    /// <summary>
    /// [V2 rough package 22] Place names, drawn as text rather than as markers.
    /// </summary>
    /// <remarks>
    /// A label is a word written on the map, not a thing at a point: giving it a 44px marker
    /// button would make "Dorms" clickable furniture, and on Streets the hundred of them would
    /// exhaust <see cref="MaximumPointMarkers"/> and cluster the extracts away behind them.
    /// </remarks>
    public IReadOnlyList<MapSceneRendererLabelViewModel> LabelObjects => _labelObjects;

    private readonly ReconciledList<MapSceneRendererLabelViewModel> _labelObjects = [];

    public bool HasLabelObjects => LabelObjects.Count > 0;
    public IReadOnlyList<MapSceneRendererListItemViewModel> ListItems { get; private set; } = [];
    public MapSceneRendererObjectViewModel? SelectedObject { get; private set; }
    public HighValueLootEntryViewModel? SelectedLootEntry { get; private set; }
    public HighValueLootLayerViewModel? HighValueLoot { get; }
    public IImage? BackgroundImage { get; private set; }

    public MapSceneViewChange? LastRequestedChange
    {
        get => _lastRequestedChange;
        private set => SetProperty(ref _lastRequestedChange, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            value ??= string.Empty;
            if (!SetProperty(ref _searchText, value))
            {
                return;
            }

            _listPageIndex = 0;
            RebuildListItems();
            RaiseListChanged();
        }
    }

    public ICommand ClearSelectionCommand { get; }
    public ICommand FocusNextObjectCommand { get; }
    public ICommand FocusPreviousObjectCommand { get; }
    public ICommand FitPlanCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand ClearClusterCommand { get; }
    public ICommand HighValueLootPresetCommand { get; }
    /// <summary>Step one floor up the building; nothing when there is no floor above.</summary>
    public ICommand FloorUpCommand { get; }
    /// <summary>Step one floor down the building; nothing when there is no floor below.</summary>
    public ICommand FloorDownCommand { get; }

    public double CanvasWidth => _canvasWidth;
    public double CanvasHeight => _canvasHeight;
    public double MapLeft => _projection.MapLeft;
    public double MapTop => _projection.MapTop;
    public double MapWidth => _projection.MapWidth;
    public double MapHeight => _projection.MapHeight;

    /// <summary>
    /// The plan's own shape, width over height, or NaN when nothing can say what it is yet.
    /// </summary>
    /// <remarks>
    /// V2 rough package 32: a host laying itself out needs the shape the plan will be drawn at
    /// before it has decided how much room to give it, and MapWidth/MapHeight cannot answer that
    /// — they are the result of the room it was given. This is the same number the projection
    /// fits with, so a host's arithmetic and the draw agree.
    /// </remarks>
    public double PlanAspect => _projection.PlanAspect;
    public double MessageWidth => Math.Max(1, Math.Min(460, CanvasWidth - 24));
    public double EmptyMessageWidth => Math.Max(1, Math.Min(380, CanvasWidth - 24));
    public double StatusLeft => Math.Max(12, (CanvasWidth - MessageWidth) / 2);
    public double StatusTop => Math.Max(72, MapTop + 16);
    public double EmptyLeft => Math.Max(12, (CanvasWidth - EmptyMessageWidth) / 2);
    public double EmptyTop => Math.Max(72, (CanvasHeight - 100) / 2);
    public double CameraPreTranslateX => -_projection.Project(_scene.View.Camera.CenterX, _scene.View.Camera.CenterY).X;
    public double CameraPreTranslateY => -_projection.Project(_scene.View.Camera.CenterX, _scene.View.Camera.CenterY).Y;
    public double CameraPostTranslateX => (CanvasWidth / 2) + _panOffsetX;
    public double CameraPostTranslateY => (CanvasHeight / 2) + _panOffsetY;

    /// <summary>True while a drag is in flight, so a host knows the plan is being moved by hand.</summary>
    public bool IsPanning => _panStartCamera is not null;
    public double CameraZoom => _scene.View.Camera.Zoom;
    public double CameraRotationDegrees => -_scene.View.Camera.BearingDegrees;
    public string LocationLabel => _scene.LocationId;
    public string VariantLabel => _scene.VariantKey;
    public string ModeLabel => DescribeMode(_scene.View.Mode);
    public string ZoomOutLabel => Text("Map.Action.ZoomOut");
    public string ZoomInLabel => Text("Map.Action.ZoomIn");
    public string FitPlanLabel => Text("Map.Action.Fit");
    public string ClearSelectionLabel => Text("Map.Action.ClearSelection");
    public string PreviousPageLabel => Text("Map.Action.Previous");
    public string NextPageLabel => Text("Map.Action.Next");
    public string ClearClusterLabel => Text("Map.Action.ClearCluster");
    public string HighValueLootPresetLabel => Text("Map.Loot.Preset");
    public string PresentationLabel => Text("Map.Label.Presentation");
    public string FloorLabel => Text("Map.Label.Floor");
    public string MapPlanLabel => Text("Map.Label.Plan");
    public string LayersLabel => Text("Map.Label.Layers");
    public string DetailsLabel => Text("Map.Label.Details");
    public string SearchLabel => Text("Map.Label.Search");
    public string SearchPlaceholder => Text("Map.Label.SearchPlaceholder");
    public string RendererNotice => _rendererNotice;
    public bool HasRendererNotice => !string.IsNullOrWhiteSpace(RendererNotice);
    /// <summary>
    /// The map's floors drawn one above another, lowest first, when the stack is showing.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 39] The floor being read stays at offset zero and is drawn into exactly
    /// the projection's own plan rectangle, so every marker — which is projected into that same
    /// rectangle — lands on its own floor's artwork. The other floors are the same pictures,
    /// offset up or down the screen and quieted. Nothing is sheared or scaled: a plate that was
    /// squashed to look three-dimensional would no longer be the rectangle objects project into.
    /// </remarks>
    public IReadOnlyList<MapSceneRendererFloorLayerViewModel> FloorLayers { get; private set; } = [];
    /// <summary>Whether the shared scene is asking for the floors to be stacked.</summary>
    public bool IsStacked => _scene.View.Mode == MapSceneMode.FloorStack2D;
    public bool HasFloorStack => IsStacked && FloorLayers.Count > 1;
    /// <summary>The one flat picture, drawn only while the stack is not.</summary>
    public bool ShowsFlatBackground => HasBackgroundImage && !HasFloorStack;

    /// <summary>The layer whose switch shows and hides <see cref="TrafficHeatImage"/>.</summary>
    public static readonly MapSceneLayerId TrafficHeatLayerId = new("traffic-prior");

    /// <summary>
    /// [Issue 286] A host-made picture of a modelled traffic field, stretched over the plan rectangle.
    /// </summary>
    /// <remarks>
    /// A picture rather than scene objects because a field is a few thousand cells and the scene is
    /// a list of things with names: the layer's objects are its few hotspots, which is what a list
    /// or a screen reader can say, and this is what the eye reads. Drawn only on the flat plan — a
    /// floor stack offsets every plate, and one field cannot sit on all of them.
    /// </remarks>
    public IImage? TrafficHeatImage { get; private set; }

    public bool ShowsTrafficHeat => TrafficHeatImage is not null && !HasFloorStack &&
        _scene.Layers.Any(layer => layer.Id == TrafficHeatLayerId) && IsLayerVisible(TrafficHeatLayerId);

    public void SetTrafficHeat(IImage? image)
    {
        if (ReferenceEquals(TrafficHeatImage, image))
        {
            return;
        }

        TrafficHeatImage = image;
        OnPropertyChanged(nameof(TrafficHeatImage));
        OnPropertyChanged(nameof(ShowsTrafficHeat));
    }

    /// <summary>
    /// What the Layers button says: the word, and how many layers are on.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 46] The count is the whole reason a menu is allowed to replace a strip
    /// of visible switches. Folded away, the switches no longer say what is drawn; the count on
    /// the button does, so nothing has to be opened to find out.
    /// </remarks>
    public string LayersMenuLabel => _presentation.Format("Map.LayersMenu", Layers.Count(layer => layer.IsVisible));
    /// <summary>What the stack did, in one line: how many plates of how many floors.</summary>
    public string StackStatus { get; private set; } = string.Empty;
    public bool HasStackStatus => StackStatus.Length > 0;
    // [V2 rough package 39] More than one: a ladder with a single rung on a map drawn as one
    // storey is a control that asks a question with one answer.
    public bool HasFloorFilters => Floors.Count > 1;
    public bool HasFloors => Floors.Count > 0;
    /// <summary>Which floor of how many, for the ladder's own readout ("Floor 2 of 4").</summary>
    public string FloorPositionLabel => Floors.Count == 0 || SelectedFloor is null
        ? string.Empty
        : Format(
            "Map.Floor.Position",
            _presentation.Number(Floors.Count - FindIndex(Floors, floor => floor.IsSelected)),
            _presentation.Number(Floors.Count));
    /// <summary>
    /// Why this floor is the one on screen, beside the ladder that chooses it.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 46] Reported as vertical following not working: he went down to a
    /// basement and the map stayed where it was. The words already existed — package 39's
    /// FloorSource says whether following is on, whether it has a height yet, and whether that
    /// height matched a floor — but they were only in the status line at the other end of the
    /// card. A map stuck on the wrong floor looks the same as a map whose following is broken
    /// unless the answer is where the floors are chosen. The host sets it; a host that has no
    /// such notion leaves it empty and nothing is drawn.
    /// </remarks>
    public string FloorSourceNote
    {
        get => _floorSourceNote;
        set
        {
            var text = value ?? string.Empty;
            if (string.Equals(text, _floorSourceNote, StringComparison.Ordinal))
            {
                return;
            }

            _floorSourceNote = text;
            OnPropertyChanged(nameof(FloorSourceNote));
            OnPropertyChanged(nameof(HasFloorSourceNote));
        }
    }

    public bool HasFloorSourceNote => _floorSourceNote.Length > 0;

    private bool _followsFloor;
    private ICommand? _followFloorCommand;

    /// <summary>
    /// The switch that makes the plan follow the floor the player is standing on, in the floor menu.
    /// </summary>
    /// <remarks>
    /// It lived in the Raid page's bottom strip as "Floors", beside a "Stack" switch that did what the
    /// "Floor stack" mode in this control's own top bar does. Floors were controlled in two places, so
    /// everything about them is here now: the mode, the ladder, why this floor is on screen, and
    /// whether it follows you. The host owns what following means, so it hands over the state and the
    /// command; a host with no such notion leaves this off and nothing is drawn.
    /// </remarks>
    public bool HasFollowFloorSwitch => _followFloorCommand is not null;

    public bool FollowsFloor => _followsFloor;

    public ICommand? FollowFloorCommand => _followFloorCommand;

    public string FollowFloorLabel => Text("Map.Floor.Follow");

    public void SetFollowFloor(bool isOn, ICommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var had = _followFloorCommand is not null;
        _followFloorCommand = command;
        if (_followsFloor == isOn && had)
        {
            return;
        }

        _followsFloor = isOn;
        OnPropertyChanged(nameof(FollowsFloor));
        OnPropertyChanged(nameof(FollowFloorCommand));
        OnPropertyChanged(nameof(HasFollowFloorSwitch));
    }

    /// <summary>
    /// The floor ladder, folded into the one line that says where you are.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 46] Reported from a Customs raid: the block in the plan's top-left
    /// corner is about 500x130 pixels of chrome sitting on artwork. Five floor buttons, two
    /// arrows and two lines of text were most of it, for a control pressed a handful of times a
    /// raid. They are behind this now; the arrows beside it still change floor in one press, so
    /// nothing costs more than it did.
    /// </remarks>
    public string FloorSummaryLabel => Floors.Count == 0 || SelectedFloor is null
        ? string.Empty
        : _presentation.Format(
            "Map.Floor.Ladder",
            SelectedFloor.Name,
            _presentation.Number(Floors.Count - FindIndex(Floors, floor => floor.IsSelected)),
            _presentation.Number(Floors.Count));

    public bool CanGoUpAFloor => StepTarget(1) is not null;
    public bool CanGoDownAFloor => StepTarget(-1) is not null;
    public string FloorUpLabel => Text("Map.Action.FloorUp");
    public string FloorDownLabel => Text("Map.Action.FloorDown");
    public bool HasSpatialObjects => SpatialObjects.Count > 0 || GeometryObjects.Count > 0;
    public bool HasListItems => ListItems.Count > 0;
    public bool ShowsEmptyMap => !HasSpatialObjects;
    /// <summary>A host with its own fixed frame around the map (the Raid workspace) keeps this
    /// message out of the plan's centre; ShowsDetailsPanel doubles as that "full chrome" flag.</summary>
    public bool ShowsEmptyMapMessage => ShowsEmptyMap && ShowsDetailsPanel;
    public bool ShowsEmptyList => !HasListItems;
    public bool HasSelection => SelectedObject is not null || SelectedLootEntry is not null;
    public bool HasGenericSelection => SelectedObject is not null && SelectedLootEntry is null;
    public bool HasLootSelection => SelectedLootEntry is not null;
    public bool HasHighValueLoot => HighValueLoot is not null;
    public bool HasBackgroundImage => BackgroundImage is not null;
    public string EmptyMapMessage => Text("Map.Empty.Map");
    public string EmptyListMessage => Text("Map.Empty.List");
    public string ReviewedAssetLabel { get; private set; } = string.Empty;
    public string BackgroundStatus { get; private set; } = string.Empty;
    public bool HasBackgroundStatus => !string.IsNullOrWhiteSpace(BackgroundStatus);
    public string DenseSceneNotice { get; private set; } = string.Empty;
    /// <summary>The two-or-three-word form drawn over the plan; the full notice is its tooltip.</summary>
    public string DenseSceneChip { get; private set; } = string.Empty;
    public bool HasDenseSceneNotice => !string.IsNullOrWhiteSpace(DenseSceneNotice);

    /// <summary>Whether anything about the scene is worth a chip over the plan (package 46).</summary>
    /// <remarks>
    /// Narrower than <see cref="HasDenseSceneNotice"/>: a scene whose only notice is how many
    /// pages the list beside the map has draws nothing on the map, because that is not something
    /// about the map. The sentence is still the chip's tooltip wherever a chip is drawn, and the
    /// list's own pager says how many pages it has.
    /// </remarks>
    public bool HasDenseSceneChip => DenseSceneChip.Length > 0;
    public string ModeFallbackNotice => _scene.View.Mode switch
    {
        MapSceneMode.Flat2D => string.Empty,
        // [V2 rough package 39] The renderer draws this mode now, so it is not a fallback — and
        // when its artwork has not arrived, StackStatus says so in one line right beside this.
        // Two paragraphs about one missing picture is one too many.
        MapSceneMode.FloorStack2D => string.Empty,
        _ => Format("Map.Mode.Fallback", DescribeMode(_scene.View.Mode)),
    };
    public string ThreeDimensionalFallback => ModeFallbackNotice;
    public bool ShowsModeFallback => !string.IsNullOrWhiteSpace(ModeFallbackNotice);
    public bool ShowsThreeDimensionalFallback => ShowsModeFallback;
    public int FilteredListCount => _filteredListObjects.Count;
    public int ListPageCount => FilteredListCount == 0 ? 0 : (FilteredListCount + ListPageSize - 1) / ListPageSize;
    public int ListPageNumber => ListPageCount == 0 ? 0 : _listPageIndex + 1;
    public string ListPageLabel => Format("Map.List.Page", ListPageNumber, ListPageCount, FilteredListCount);
    public bool CanGoToPreviousPage => _listPageIndex > 0;
    public bool CanGoToNextPage => _listPageIndex + 1 < ListPageCount;
    public bool HasClusterFilter => _clusterFilter is { Count: > 0 };
    public string ClusterFilterLabel => _clusterFilterLabel;

    public void Present(
        MapSceneSnapshot scene,
        HighValueLootLayerResult highValueLoot,
        HighValueLootLayerFilterState filterState,
        IReadOnlyList<string>? availableCategories = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(highValueLoot);
        ArgumentNullException.ThrowIfNull(filterState);
        if (HighValueLoot is null)
        {
            throw new InvalidOperationException("This renderer was not created with a high-value loot layer.");
        }

        EnsureHighValueLootMatchesScene(scene, highValueLoot, filterState.Filter);
        _lootValues = MapLootRanking.ValuesOf(highValueLoot);
        Present(scene);
        _lootCategories = availableCategories ?? _lootCategories;
        HighValueLoot.Present(highValueLoot, filterState, _lootCategories, scene.FloorIds);
        RestoreLootSelection();
    }

    /// <summary>Replaces the display only after the canonical owner accepted or refreshed it.</summary>
    public void Present(MapSceneSnapshot scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var requestedRevision = _pendingRevision;
        var previous = _scene;
        var changedSceneIdentity = !string.Equals(previous.LocationId, scene.LocationId, StringComparison.Ordinal) ||
            !string.Equals(previous.VariantKey, scene.VariantKey, StringComparison.Ordinal);
        var boundsChanged = previous.Bounds != scene.Bounds;
        var objectDefinitionsChanged = !SameObjects(previous.Objects, scene.Objects) || StylesChanged();
        var layerDefinitionsChanged = !Equivalent(previous.Layers, scene.Layers);
        var layerVisibilityChanged = !Equivalent(previous.View.Layers, scene.View.Layers);
        var floorIdsChanged = !Equivalent(previous.FloorIds, scene.FloorIds, StringComparer.OrdinalIgnoreCase);
        var floorSelectionChanged = !string.Equals(
            previous.View.SelectedFloorId,
            scene.View.SelectedFloorId,
            StringComparison.OrdinalIgnoreCase);
        var modeChanged = previous.View.Mode != scene.View.Mode || previous.Capabilities != scene.Capabilities;
        var cameraChanged = previous.View.Camera != scene.View.Camera;
        var assetsChanged = !Equivalent(previous.Assets, scene.Assets);

        _scene = scene;
        _pendingRevision = null;
        var presetRejected = _lootPresetTargets is not null && requestedRevision == scene.Revision;
        if (presetRejected)
        {
            // A preset is a serialized, non-atomic series of ordinary revision-checked changes.
            // If a later step is rejected, earlier acknowledged steps remain canonical; abort
            // the remainder rather than looping or pretending those prior changes rolled back.
            _lootPresetTargets = null;
        }
        HighValueLoot?.SetLayerVisibility(
            IsLayerVisible(HighValueLootLayerService.LayerId),
            notifyProjection: false);
        if (changedSceneIdentity ||
            _selectedObjectId is { } selected && !VisibleObjects().Any(item => item.Id == selected))
        {
            _selectedObjectId = null;
            _selectedLootSpawnId = null;
        }

        if (changedSceneIdentity)
        {
            _lootZoomReached = 0;
            _clusterFilter = null;
            _clusterFilterLabel = string.Empty;
            _searchText = string.Empty;
            _listPageIndex = 0;
        }

        _rendererNotice = presetRejected ? Text("Map.Loot.PresetConflict") : string.Empty;
        if (changedSceneIdentity)
        {
            CancelPan();
        }

        // The rectangle the plan is drawn into is a function of the scene's bounds, the card, and the
        // stack (its mode, the floor being read, the floors that have artwork). It used to be rebuilt
        // for a change of bounds alone and never told the view: MapLeft/MapTop/MapWidth/MapHeight were
        // raised only when the card was resized or a decoded picture changed the plan's shape. So a
        // player who went from Customs to Factory kept Factory's picture stretched into Customs'
        // rectangle until something happened to resize the card. Every input that can move the
        // rectangle rebuilds it here, and a map or variant change always announces it.
        var projectionChanged = false;
        if (changedSceneIdentity || boundsChanged || modeChanged || floorSelectionChanged || floorIdsChanged || assetsChanged)
        {
            var previousProjection = _projection;
            _projection = CreateProjection();
            projectionChanged = changedSceneIdentity || boundsChanged || !SameFrame(previousProjection, _projection);
        }

        if (modeChanged)
        {
            BuildModes();
        }

        if (floorIdsChanged || floorSelectionChanged)
        {
            BuildFloors();
        }

        if (layerDefinitionsChanged || layerVisibilityChanged)
        {
            BuildLayers();
        }

        var visibleContentChanged = changedSceneIdentity || boundsChanged || objectDefinitionsChanged ||
            layerDefinitionsChanged || layerVisibilityChanged || floorIdsChanged || floorSelectionChanged ||
            projectionChanged;
        if (visibleContentChanged)
        {
            var visibleObjects = RebuildProjectedObjects();
            RebuildListItems();
            BuildDenseSceneNotice(visibleObjects);
        }
        else if (cameraChanged)
        {
            foreach (var marker in SpatialObjects)
            {
                marker.UpdateCamera(_scene.View.Camera);
            }

            foreach (var stack in StackMarkers.Concat(LootBadges))
            {
                stack.UpdateCamera(_scene.View.Camera);
            }

            foreach (var label in LabelObjects)
            {
                label.UpdateCamera(_scene.View.Camera);
            }

            ShowRevealedLoot();
        }

        if (changedSceneIdentity || assetsChanged || boundsChanged)
        {
            ResolveBackground(changedSceneIdentity || assetsChanged);
        }
        else
        {
            UpdateBackgroundStatus(SelectedBackgroundAsset());
        }

        RaisePresentChanged(
            modeChanged,
            floorIdsChanged || floorSelectionChanged,
            layerDefinitionsChanged || layerVisibilityChanged,
            visibleContentChanged,
            cameraChanged,
            changedSceneIdentity || assetsChanged || boundsChanged);
        if (projectionChanged)
        {
            RaiseProjectionChanged();
        }

        DispatchHighValueLootPresetChange();

        // [Issue 551] A host opens a map on the plain fit (the plan's middle at zoom one) because
        // it cannot know the card's size or where the map's features are. That is this view
        // model's to refine, and to keep right while the view is still the fitted one.
        if (changedSceneIdentity || boundsChanged)
        {
            _isFitted = IsPlainFit(scene);
        }

        if (changedSceneIdentity || boundsChanged || projectionChanged || objectDefinitionsChanged)
        {
            RefitIfFitted();
        }
    }

    /// <summary>The camera a host opens a map with: the middle of the plan at zoom one.</summary>
    private static bool IsPlainFit(MapSceneSnapshot scene)
    {
        var camera = scene.View.Camera;
        var bounds = scene.Bounds;
        return Math.Abs(camera.Zoom - 1) < 1e-9 &&
            Math.Abs(camera.CenterX - (bounds.MinimumX + (bounds.Width / 2))) < 1e-6 &&
            Math.Abs(camera.CenterY - (bounds.MinimumY + (bounds.Height / 2))) < 1e-6;
    }

    /// <summary>Whether two projections put the plan in the same rectangle at the same scale.</summary>
    private static bool SameFrame(MapSceneProjection left, MapSceneProjection right) =>
        left.IsUsable == right.IsUsable &&
        Math.Abs(left.MapLeft - right.MapLeft) < 1e-9 &&
        Math.Abs(left.MapTop - right.MapTop) < 1e-9 &&
        Math.Abs(left.MapWidth - right.MapWidth) < 1e-9 &&
        Math.Abs(left.MapHeight - right.MapHeight) < 1e-9 &&
        Math.Abs(left.ScaleX - right.ScaleX) < 1e-12 &&
        Math.Abs(left.ScaleY - right.ScaleY) < 1e-12;

    /// <summary>Updates only the projection; it does not create a new canonical camera state.</summary>
    public void SetViewportSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return;
        }

        if (Math.Abs(width - _canvasWidth) < 0.5 && Math.Abs(height - _canvasHeight) < 0.5)
        {
            return;
        }

        _canvasWidth = Math.Max(1, width);
        _canvasHeight = Math.Max(1, height);
        _projection = CreateProjection();
        RebuildProjectedObjects();
        UpdateBackgroundStatus(SelectedBackgroundAsset());
        RaiseProjectionChanged();
        RefitIfFitted();
    }

    public void RequestMode(MapSceneMode mode)
    {
        if (!CanRenderMode(mode))
        {
            SetRendererNotice(Format("Map.Mode.Unavailable", DescribeMode(mode), RendererUnavailableReason(mode)));
            return;
        }

        Request(new(MapSceneViewChangeKind.SetMode, Mode: mode));
    }

    public void SelectFloor(string? floorId)
    {
        if (floorId is not null && !Floors.Any(floor => string.Equals(floor.Id, floorId, StringComparison.OrdinalIgnoreCase)))
        {
            SetRendererNotice(Text("Map.Floor.Unavailable"));
            return;
        }

        Request(new(MapSceneViewChangeKind.SelectFloor, FloorId: floorId));
    }

    public void SetLayerVisibility(MapSceneLayerId layerId, bool isVisible)
    {
        if (!_scene.Layers.Any(layer => layer.Id == layerId))
        {
            SetRendererNotice(Text("Map.Layer.Unavailable"));
            return;
        }

        _lootPresetTargets = null;
        Request(new(MapSceneViewChangeKind.SetLayerVisibility, LayerId: layerId, IsVisible: isVisible));
    }

    public void SelectObject(MapSceneObjectId objectId)
    {
        if (!VisibleObjects().Any(item => item.Id == objectId) || _selectedObjectId == objectId)
        {
            return;
        }

        ApplySelection(objectId);
    }

    /// <summary>Temporarily reveals the switch chain for an extract under the pointer.</summary>
    public void HoverRequirementObject(MapSceneObjectId? objectId)
    {
        var next = objectId is { } id && _scene.Objects.FirstOrDefault(item => item.Id == id) is
            { Kind: MapSceneObjectKind.Extract, ExtractRequirements: { RequiresSwitch: true } }
                ? id
                : (MapSceneObjectId?)null;
        if (_hoveredRequirementObjectId == next)
        {
            return;
        }

        _hoveredRequirementObjectId = next;
        RebuildProjectedObjects();
        RebuildListItems();
        BuildDenseSceneNotice(VisibleObjects());
    }

    public bool TrySelectAt(double viewportX, double viewportY)
    {
        if (!TryHitObjectAt(viewportX, viewportY, out var objectId))
        {
            return false;
        }

        SelectObject(objectId);
        return true;
    }

    /// <summary>The object under a viewport position, without selecting it.</summary>
    /// <remarks>
    /// The hit-test <see cref="TrySelectAt"/> already does, exposed on its own so a host can ask
    /// "what did that gesture land on" for something other than selection — a right-click asking
    /// to remove a mark, for one. This view model does not need to know marks exist to answer it.
    /// </remarks>
    public bool TryHitObjectAt(double viewportX, double viewportY, out MapSceneObjectId objectId)
    {
        objectId = default;
        if (!_projection.TryUnproject(
                viewportX,
                viewportY,
                _scene.View.Camera,
                out var point,
                out var worldUnitsPerPixel))
        {
            return false;
        }

        var rendered = SpatialObjects
            .Where(item => !item.IsCluster && item.IsShownOnPlan && item.SceneObject is not null)
            .Select(item => item.SceneObject!)
            .Concat(GeometryObjects.Select(item => item.SceneObject))
            .ToArray();
        var hit = MapSceneHitTesting.HitTest(_scene, rendered, point, worldUnitsPerPixel * 24).FirstOrDefault();
        if (hit is null)
        {
            return false;
        }

        objectId = hit.Object.Id;
        return true;
    }

    /// <summary>The scene point under a viewport position, for a host placing something there.</summary>
    /// <remarks>
    /// The same unprojection <see cref="TrySelectAt"/> already does to hit-test, exposed on its
    /// own so a host — the raid cockpit placing a mark, for one — can ask "where is that" without
    /// this view model needing to know marks exist.
    /// </remarks>
    public bool TryScenePointAt(double viewportX, double viewportY, out MapScenePoint point) =>
        _projection.TryUnproject(viewportX, viewportY, _scene.View.Camera, out point, out _);

    /// <summary>[#286] Where a scene point is in the viewport now, for a popover pinned to it.</summary>
    public bool TryViewportPointAt(MapScenePoint point, out double viewportX, out double viewportY) =>
        _projection.TryProjectToViewport(point, _scene.View.Camera, out viewportX, out viewportY);

    public void ClearSelection()
    {
        if (_selectedObjectId is null && _selectedLootSpawnId is null)
        {
            return;
        }

        _selectedLootSpawnId = null;
        ApplySelection(null);
    }

    /// <summary>
    /// The furthest out the camera goes: the whole plan, fitted.
    /// </summary>
    /// <remarks>
    /// One, not a constant fraction of one, because the projection has already fitted the plan
    /// to the card before the camera's zoom is applied — so zoom 1 <em>is</em> the whole plan at
    /// whatever size the card happens to be. Anything below it only adds empty surface around a
    /// map that already fits.
    /// </remarks>
    /// <remarks>
    /// [Issue 551] One, while the plan lies the way the projection fitted it. Turned, the same
    /// rectangle can be taller than the card (Customs a quarter turn round is), and then the
    /// whole plan is further out than one; the limit follows it so the whole map can always be
    /// brought back on screen.
    /// </remarks>
    public double MinimumZoom => Math.Min(1, PlanFit()?.Zoom ?? 1);

    /// <summary>
    /// The furthest in the camera goes, from the artwork rather than from a constant.
    /// </summary>
    /// <remarks>
    /// Past the point where one artwork pixel covers more than about two screen pixels there is
    /// no more map to see, only a larger blur, so the limit is the drawn plan's own resolution
    /// with one doubling of headroom. A sharp plan on a small card therefore goes much further in
    /// than a coarse one on a wide card, which is the point. The floor keeps a plan whose artwork
    /// has not resolved yet — or one already drawn near its own resolution — usefully zoomable.
    /// </remarks>
    public double MaximumZoom
    {
        get
        {
            var native = BackgroundImage?.Size.Width ?? 0;
            var drawn = _projection.IsUsable ? _projection.MapWidth : 0;
            var resolution = native > 0 && drawn > 0 ? native / drawn * 2 : 8;
            return Math.Clamp(resolution, 4, 32);
        }
    }

    /// <summary>
    /// Keeps a camera on the plan that is actually drawn.
    /// </summary>
    /// <remarks>
    /// The clamp is on what the viewport can see, not on where its centre is. Clamping the
    /// centre to the plan's bounds — which is what this used to do — let the plan be dragged
    /// until only its corner was left on screen, and it silently moved any centre that started
    /// outside the bounds, which is the "it just moves right back" a player sees when a pan or a
    /// follow puts the camera somewhere the clamp then rejects.
    ///
    /// An axis whose visible span is wider than the plan itself is centred rather than clamped
    /// to an edge, so a plan smaller than the card sits in the middle of it instead of sticking
    /// to one side.
    /// </remarks>
    private MapSceneCamera Clamp(MapSceneCamera camera)
    {
        var zoom = Math.Clamp(camera.Zoom, MinimumZoom, MaximumZoom);
        var bounds = _scene.Bounds;
        if (!_projection.IsUsable || !double.IsFinite(camera.CenterX) || !double.IsFinite(camera.CenterY))
        {
            return new(
                bounds.MinimumX + (bounds.Width / 2),
                bounds.MinimumY + (bounds.Height / 2),
                zoom,
                camera.BearingDegrees,
                camera.PitchDegrees);
        }

        // The viewport, inverse-transformed back through the camera's rotation and zoom, as
        // half-extents in plan units: exactly the span TryUnproject would report for the
        // viewport's corners.
        var radians = camera.BearingDegrees * Math.PI / 180;
        var cosine = Math.Abs(Math.Cos(radians));
        var sine = Math.Abs(Math.Sin(radians));
        var halfX = ((cosine * CanvasWidth) + (sine * CanvasHeight)) / (2 * zoom * _projection.ScaleX);
        var halfY = ((sine * CanvasWidth) + (cosine * CanvasHeight)) / (2 * zoom * _projection.ScaleY);
        return new(
            Centre(camera.CenterX, bounds.MinimumX, bounds.MaximumX, halfX),
            Centre(camera.CenterY, bounds.MinimumY, bounds.MaximumY, halfY),
            zoom,
            camera.BearingDegrees,
            camera.PitchDegrees);
    }

    private static double Centre(double value, double minimum, double maximum, double halfExtent) =>
        halfExtent * 2 >= maximum - minimum
            ? minimum + ((maximum - minimum) / 2)
            : Math.Clamp(value, minimum + halfExtent, maximum - halfExtent);

    public void RequestZoom(double direction)
    {
        if (!double.IsFinite(direction) || direction == 0)
        {
            return;
        }

        var factor = direction > 0 ? 1.25 : 0.8;
        var camera = _scene.View.Camera;
        _isFitted = false;
        Request(new(
            MapSceneViewChangeKind.SetCamera,
            Camera: Clamp(new(
                camera.CenterX,
                camera.CenterY,
                camera.Zoom * factor,
                camera.BearingDegrees,
                camera.PitchDegrees))));
        CameraZoomedByPlayer?.Invoke(this, EventArgs.Empty);
    }

    public void RequestPan(double viewportDeltaX, double viewportDeltaY)
    {
        if (!double.IsFinite(viewportDeltaX) || !double.IsFinite(viewportDeltaY) ||
            Math.Abs(viewportDeltaX) < 0.5 && Math.Abs(viewportDeltaY) < 0.5)
        {
            return;
        }

        var camera = _scene.View.Camera;
        var radians = camera.BearingDegrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var unrotatedX = (cosine * viewportDeltaX) - (sine * viewportDeltaY);
        var unrotatedY = (sine * viewportDeltaX) + (cosine * viewportDeltaY);
        var worldDeltaX = unrotatedX / (_projection.ScaleX * camera.Zoom);
        var worldDeltaY = unrotatedY / (_projection.ScaleY * camera.Zoom);
        Request(new(
            MapSceneViewChangeKind.SetCamera,
            Camera: Clamp(new(
                camera.CenterX - worldDeltaX,
                camera.CenterY - worldDeltaY,
                camera.Zoom,
                camera.BearingDegrees,
                camera.PitchDegrees))));
    }

    /// <summary>Starts a drag from the camera as it stands. The scene is not touched.</summary>
    /// <summary>
    /// The player moved the camera themselves by finishing a drag.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 46] Reported as "when I zoom in and then try to pan, it snaps back to
    /// where it was". The clamp was not the cause: it already divides the viewport's half-extent
    /// by the camera's zoom, so zooming in allows strictly more pan, and a rebuild already reuses
    /// the current view rather than re-fitting. What snapped the map back is that V1 was still
    /// following the player. V1 turns its own following off when somebody pans V1's canvas
    /// (<c>ReportManualPan</c>), and nothing turned it off when they panned the V2 renderer, so
    /// the next screenshot re-centred the camera on the player a beat after the drag. At zoom 1
    /// that is nearly invisible, because the fit already shows the whole map; at zoom 2 or 3 it
    /// is exactly the snap he describes.
    ///
    /// Raised at the gesture boundary rather than from the camera change, because Follow
    /// moves the camera through the same reducer and must not be mistaken for the player doing
    /// it. Zoom has its own event because a following host can keep Follow and remember the new
    /// magnification. Fit does not raise either event.
    /// </remarks>
    public event EventHandler? CameraMovedByPlayer;

    /// <summary>The player changed magnification without necessarily asking to stop following.</summary>
    /// <remarks>
    /// [Issue 663] A following raid map treats the wheel as a new follow magnification. Keeping
    /// this separate from a drag lets its host preserve Follow for zoom while a pan still turns
    /// Follow off. A generic renderer has no Follow state of its own, so it only reports intent.
    /// </remarks>
    public event EventHandler? CameraZoomedByPlayer;

    public void BeginPan()
    {
        _panStartCamera = _scene.View.Camera;
        _panTargetCamera = null;
        SetPanOffset(0, 0);
    }

    /// <summary>
    /// Moves the plan under an in-flight drag, by the pointer's total offset from where it went
    /// down — not by the delta since the last event, so a dropped or coalesced move cannot make
    /// the plan drift away from the pointer.
    /// </summary>
    /// <remarks>
    /// The offset is derived from the camera the drag would actually commit, clamped to the
    /// plan's bounds, so what the player sees while dragging is exactly what they get when they
    /// let go: dragging past the edge stops rather than snapping back on release.
    /// </remarks>
    public void UpdatePan(double totalViewportDeltaX, double totalViewportDeltaY)
    {
        if (_panStartCamera is not { } start ||
            !double.IsFinite(totalViewportDeltaX) || !double.IsFinite(totalViewportDeltaY))
        {
            return;
        }

        var target = PanTargetCamera(start, totalViewportDeltaX, totalViewportDeltaY);
        _panTargetCamera = target;
        // How far the applied (clamped) camera move is, in projected pixels before the camera's
        // own zoom and rotation, then back through both to get the on-screen shift.
        var movedX = (start.CenterX - target.CenterX) * _projection.ScaleX;
        var movedY = (start.CenterY - target.CenterY) * _projection.ScaleY;
        var radians = start.BearingDegrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        SetPanOffset(
            start.Zoom * ((movedX * cosine) + (movedY * sine)),
            start.Zoom * ((-movedX * sine) + (movedY * cosine)));
    }

    /// <summary>Ends a drag, committing the camera it arrived at through the canonical reducer.</summary>
    public void CommitPan()
    {
        var target = _panTargetCamera;
        _panStartCamera = null;
        _panTargetCamera = null;
        if (target is { } camera)
        {
            _isFitted = false;
            // Requested before the offset is cleared: the host applies and presents this
            // synchronously, so the committed camera replaces the drag offset within the same
            // frame and the plan does not flash back to where the drag started.
            Request(new(MapSceneViewChangeKind.SetCamera, Camera: camera));
            CameraMovedByPlayer?.Invoke(this, EventArgs.Empty);
        }

        SetPanOffset(0, 0);
    }

    /// <summary>Abandons a drag (lost capture, a new scene) and puts the plan back.</summary>
    public void CancelPan()
    {
        _panStartCamera = null;
        _panTargetCamera = null;
        SetPanOffset(0, 0);
    }

    /// <summary>
    /// Zooms about a point in the viewport, so the place under the wheel (or the pinch centre)
    /// stays under it. Plain <see cref="RequestZoom(double)"/> zooms about the camera centre,
    /// which meant the feature the player was pointing at slid away as they zoomed in.
    /// </summary>
    public void RequestZoomAt(double direction, double viewportX, double viewportY)
    {
        if (!double.IsFinite(direction) || direction == 0 ||
            !double.IsFinite(viewportX) || !double.IsFinite(viewportY) || !_projection.IsUsable)
        {
            RequestZoom(direction);
            return;
        }

        var camera = _scene.View.Camera;
        var zoom = Math.Clamp(camera.Zoom * (direction > 0 ? 1.25 : 0.8), MinimumZoom, MaximumZoom);
        if (Math.Abs(zoom - camera.Zoom) < 1e-9)
        {
            return;
        }

        var radians = camera.BearingDegrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var offsetX = viewportX - (CanvasWidth / 2);
        var offsetY = viewportY - (CanvasHeight / 2);
        var planX = (cosine * offsetX) - (sine * offsetY);
        var planY = (sine * offsetX) + (cosine * offsetY);
        // The projected centre has to move by the pointer offset scaled by the change in 1/zoom
        // for the point under the pointer to stay put.
        var change = (1 / camera.Zoom) - (1 / zoom);
        _isFitted = false;
        var centre = _projection.Project(camera.CenterX, camera.CenterY);
        var moved = _projection.Unproject(centre.X + (planX * change), centre.Y + (planY * change));
        Request(new(
            MapSceneViewChangeKind.SetCamera,
            Camera: Clamp(new(moved.X, moved.Y, zoom, camera.BearingDegrees, camera.PitchDegrees))));
        CameraZoomedByPlayer?.Invoke(this, EventArgs.Empty);
    }

    private MapSceneCamera PanTargetCamera(MapSceneCamera start, double viewportDeltaX, double viewportDeltaY)
    {
        var radians = start.BearingDegrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var unrotatedX = (cosine * viewportDeltaX) - (sine * viewportDeltaY);
        var unrotatedY = (sine * viewportDeltaX) + (cosine * viewportDeltaY);
        // The same clamp the commit will use, so the plan under the pointer during the drag is
        // exactly where it is left when the pointer comes up: no snap back, in either direction.
        return Clamp(new(
            start.CenterX - (unrotatedX / (_projection.ScaleX * start.Zoom)),
            start.CenterY - (unrotatedY / (_projection.ScaleY * start.Zoom)),
            start.Zoom,
            start.BearingDegrees,
            start.PitchDegrees));
    }

    private void SetPanOffset(double x, double y)
    {
        if (Math.Abs(x - _panOffsetX) < 0.01 && Math.Abs(y - _panOffsetY) < 0.01)
        {
            return;
        }

        _panOffsetX = x;
        _panOffsetY = y;
        // Two notifications, both feeding a RenderTransform. Nothing here invalidates layout.
        OnPropertyChanged(nameof(CameraPostTranslateX));
        OnPropertyChanged(nameof(CameraPostTranslateY));
    }

    private void FitPlan()
    {
        _isFitted = true;
        // [V2 rough package 22] The bearing survives a fit. Turning the map is how somebody reads
        // it while playing, and V1's Fit never undid it; resetting it here meant every fit (and
        // the automatic one on a new map) silently put the map back the way round they had
        // rejected.
        Request(new(MapSceneViewChangeKind.SetCamera, Camera: FittedCamera(_scene.View.Camera.BearingDegrees)));
    }

    /// <summary>
    /// [Issue 551] The camera that fills the card with the map, turned to <paramref name="bearingDegrees"/>.
    /// </summary>
    /// <remarks>
    /// See <see cref="MapFitGeometry"/>. What is fitted is where the map's own fixed features are
    /// (extracts, transits, spawn areas, locks, hazards, place names) when there are enough of
    /// them, spread widely enough, to say where the map is; otherwise the plan's rectangle. A
    /// player, a ping or a waypoint never counts, so the fit does not shift as they move.
    /// </remarks>
    private MapSceneCamera FittedCamera(double bearingDegrees)
    {
        var bounds = _scene.Bounds;
        var fit = ContentFit(bearingDegrees) ?? PlanFit(bearingDegrees);
        if (fit is not { } found || !_projection.IsUsable)
        {
            return new(
                bounds.MinimumX + (bounds.Width / 2),
                bounds.MinimumY + (bounds.Height / 2),
                1,
                bearingDegrees,
                0);
        }

        var centre = _projection.Unproject(found.CentreX, found.CentreY);
        return new(
            centre.X,
            centre.Y,
            Math.Clamp(found.Zoom, Math.Min(1, PlanFit(bearingDegrees)?.Zoom ?? 1), Math.Max(1, MaximumZoom)),
            bearingDegrees,
            0);
    }

    /// <summary>The whole plan rectangle on the card at a bearing, with the markers' own inset.</summary>
    private MapFitGeometry.Fit? PlanFit(double? bearingDegrees = null)
    {
        if (!_projection.IsUsable)
        {
            return null;
        }

        var bounds = _scene.Bounds;
        var topLeft = _projection.Project(bounds.MinimumX, bounds.MinimumY);
        var bottomRight = _projection.Project(bounds.MaximumX, bounds.MaximumY);
        return MapFitGeometry.For(
            [(topLeft.X, topLeft.Y), (bottomRight.X, topLeft.Y), (bottomRight.X, bottomRight.Y), (topLeft.X, bottomRight.Y)],
            bearingDegrees ?? _scene.View.Camera.BearingDegrees,
            CanvasWidth,
            CanvasHeight,
            MapInset);
    }

    private MapFitGeometry.Fit? ContentFit(double bearingDegrees)
    {
        if (!_projection.IsUsable)
        {
            return null;
        }

        var bounds = _scene.Bounds;
        var points = new List<(double X, double Y)>();
        double minimumX = double.PositiveInfinity, minimumY = double.PositiveInfinity;
        double maximumX = double.NegativeInfinity, maximumY = double.NegativeInfinity;
        foreach (var item in _scene.Objects)
        {
            if (item.Kind is not (MapSceneObjectKind.Extract or MapSceneObjectKind.Transit or MapSceneObjectKind.SpawnArea
                or MapSceneObjectKind.Lock or MapSceneObjectKind.Hazard or MapSceneObjectKind.Label))
            {
                continue;
            }

            foreach (var point in item.Geometry.Points)
            {
                // Something the catalog places off the plan is not where the map is.
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                    point.X < bounds.MinimumX || point.X > bounds.MaximumX ||
                    point.Y < bounds.MinimumY || point.Y > bounds.MaximumY)
                {
                    continue;
                }

                var projected = _projection.Project(point);
                points.Add((projected.X, projected.Y));
                minimumX = Math.Min(minimumX, point.X);
                maximumX = Math.Max(maximumX, point.X);
                minimumY = Math.Min(minimumY, point.Y);
                maximumY = Math.Max(maximumY, point.Y);
            }
        }

        // A handful of features, or features bunched in one corner, do not say where the map is.
        if (points.Count < MinimumContentPoints ||
            maximumX - minimumX < bounds.Width * MinimumContentSpan ||
            maximumY - minimumY < bounds.Height * MinimumContentSpan)
        {
            return null;
        }

        // A marker is drawn around its point and a pin above it, so the points need more room
        // than a rectangle's edge does: the markers' inset, and about 4% of the card.
        return MapFitGeometry.For(
            points,
            bearingDegrees,
            CanvasWidth,
            CanvasHeight,
            MapInset + (ContentMarginFraction * Math.Min(CanvasWidth, CanvasHeight)));
    }

    /// <summary>
    /// Fits again when the view is still the fitted one and what it was fitted to has changed:
    /// the card's size, the plan, or where the map's features are.
    /// </summary>
    private void RefitIfFitted()
    {
        if (!_isFitted || _refitting || ViewChangeRequested is null)
        {
            return;
        }

        var wanted = FittedCamera(_scene.View.Camera.BearingDegrees);
        var camera = _scene.View.Camera;
        if (Math.Abs(wanted.CenterX - camera.CenterX) < 1e-6 && Math.Abs(wanted.CenterY - camera.CenterY) < 1e-6 &&
            Math.Abs(wanted.Zoom - camera.Zoom) < 1e-9)
        {
            return;
        }

        _refitting = true;
        try
        {
            Request(new(MapSceneViewChangeKind.SetCamera, Camera: wanted));
        }
        finally
        {
            _refitting = false;
        }
    }

    /// <summary>
    /// [V2 rough package 22] Puts a plan point in the middle of the viewport, optionally zooming
    /// in to it. This is what "Follow" does when a screenshot places the player.
    /// </summary>
    /// <remarks>
    /// <paramref name="minimumZoom"/> only ever zooms in, never out, for the same reason V1's
    /// CentreOnPlayer does: somebody who has deliberately zoomed further in to read a building
    /// should not be pulled back out by the next screenshot.
    /// </remarks>
    public void FocusOn(MapScenePoint point, double? minimumZoom = null)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            return;
        }

        var camera = _scene.View.Camera;
        var zoom = minimumZoom is { } wanted && double.IsFinite(wanted) && wanted > camera.Zoom
            ? Math.Clamp(wanted, MinimumZoom, MaximumZoom)
            : camera.Zoom;
        _isFitted = false;
        // Clamped like a pan, so following somebody standing near the edge of the map puts them
        // as close to the middle as the plan allows rather than leaving the camera on a centre
        // the next gesture would have to correct.
        Request(new(
            MapSceneViewChangeKind.SetCamera,
            Camera: Clamp(new(point.X, point.Y, zoom, camera.BearingDegrees, camera.PitchDegrees))));
    }

    /// <summary>
    /// [#604] Puts the camera exactly here, zooming out as well as in. What a paired tablet in
    /// Control drives: <see cref="FocusOn"/> only ever zooms in, so a tablet's zoom-out never
    /// reached the desk.
    /// </summary>
    public void ShowCamera(MapScenePoint point, double zoom)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(zoom) || zoom <= 0)
        {
            return;
        }

        var camera = _scene.View.Camera;
        _isFitted = false;
        Request(new(
            MapSceneViewChangeKind.SetCamera,
            Camera: Clamp(new(point.X, point.Y, Math.Clamp(zoom, MinimumZoom, MaximumZoom), camera.BearingDegrees, camera.PitchDegrees))));
    }

    /// <summary>[V2 rough package 22] Turns the whole plan, V1's "270°" control.</summary>
    public void SetBearing(double degrees)
    {
        if (!double.IsFinite(degrees))
        {
            return;
        }

        var bearing = (degrees % 360 + 360) % 360;
        var camera = _scene.View.Camera;
        if (Math.Abs(bearing - camera.BearingDegrees) < 0.001)
        {
            return;
        }

        // [Issue 551] A fitted map turned is fitted to the new way round, in the same change: a
        // tall map turned to lie along a wide card should grow to fill it, not keep the scale
        // that fitted it standing up.
        Request(new(
            MapSceneViewChangeKind.SetCamera,
            Camera: _isFitted
                ? FittedCamera(bearing)
                : new(camera.CenterX, camera.CenterY, camera.Zoom, bearing, camera.PitchDegrees)));
    }

    private void MoveSelection(int direction)
    {
        if (_filteredListObjects.Count == 0)
        {
            return;
        }

        var current = _selectedObjectId is { } selected
            ? FindIndex(_filteredListObjects, item => item.Id == selected)
            : -1;
        var next = ((current + direction) % _filteredListObjects.Count + _filteredListObjects.Count) %
            _filteredListObjects.Count;
        _listPageIndex = next / ListPageSize;
        RebuildListItems();
        ApplySelection(_filteredListObjects[next].Id);
        RaiseListChanged();
    }

    private void ChangePage(int direction)
    {
        var next = Math.Clamp(_listPageIndex + direction, 0, Math.Max(0, ListPageCount - 1));
        if (next == _listPageIndex)
        {
            return;
        }

        _listPageIndex = next;
        RebuildListItems();
        RaiseListChanged();
    }

    private void OpenCluster(IReadOnlyList<MapSceneObject> objects)
    {
        _clusterFilter = objects.Select(item => item.Id).ToHashSet();
        _clusterFilterLabel = Format("Map.Cluster.Filter", objects.Count);
        _listPageIndex = 0;
        RebuildListItems();
        RaiseListChanged();
    }

    private void ClearClusterFilter()
    {
        if (_clusterFilter is null)
        {
            return;
        }

        _clusterFilter = null;
        _clusterFilterLabel = string.Empty;
        _listPageIndex = 0;
        RebuildListItems();
        RaiseListChanged();
    }

    private void Request(MapSceneRendererChange change)
    {
        // A camera change is idempotent and last-write-wins, so it is never refused for an
        // in-flight change: refusing one mid-gesture is what made a wheel spin or a fast drag
        // stall and then jump, with "a change is already in flight" flashing over the plan.
        if (change.Kind != MapSceneViewChangeKind.SetCamera && _pendingRevision == _scene.Revision)
        {
            SetRendererNotice(Text("Map.Change.Pending"));
            return;
        }

        var changeId = _nextChangeId();
        if (changeId == Guid.Empty)
        {
            throw new InvalidOperationException("A map view change requires a non-empty change ID.");
        }

        var requested = new MapSceneViewChange(
            changeId,
            _scene.Revision,
            change.Kind,
            change.Mode,
            change.FloorId,
            change.LayerId,
            change.IsVisible,
            change.Camera);
        _pendingRevision = _scene.Revision;
        LastRequestedChange = requested;
        ViewChangeRequested?.Invoke(requested);
    }

    private void ApplyHighValueLootPreset()
    {
        if (HighValueLoot is null || !_scene.Layers.Any(layer => layer.Id == HighValueLootLayerService.LayerId))
        {
            SetRendererNotice(Text("Map.Loot.Unavailable"));
            return;
        }

        // [Issue 563] "i click the high value loot only button ... all the names of places
        // dissappear ... no loot shows up tho either": with no last-known-good snapshot at all,
        // the old preset still hid every other marker layer, leaving nothing to look at. There is
        // nothing to switch to here, so nothing is hidden; the one line says why and offers
        // Refresh instead.
        if (HighValueLoot.IsUnavailable)
        {
            SetRendererNotice(HighValueLoot.NoDataMessage);
            return;
        }

        var preserve = _lootPresetPreservedLayers
            .Where(id => _scene.Layers.Any(layer => layer.Id == id) && IsLayerVisible(id))
            .Concat(_scene.Layers
                .Where(layer => layer.Id == HazardsLayerId && IsLayerVisible(layer.Id))
                .Select(layer => layer.Id))
            .Concat(_selectedObjectId is { } selected
                ? _scene.Objects.Where(item => item.Id == selected).Select(item => item.LayerId)
                : [])
            .Distinct()
            .ToArray();
        _lootPresetTargets = HighValueLootLayerPreset.Create(_scene.Layers, preserve)
            .ToDictionary(state => state.LayerId, state => state.IsVisible);
        DispatchHighValueLootPresetChange();
    }

    private void DispatchHighValueLootPresetChange()
    {
        if (_lootPresetTargets is null || _pendingRevision == _scene.Revision)
        {
            return;
        }

        var next = _scene.Layers
            .OrderBy(layer => layer.ZIndex)
            .Select(layer => new
            {
                layer.Id,
                Current = IsLayerVisible(layer.Id),
                Target = _lootPresetTargets[layer.Id],
            })
            .FirstOrDefault(item => item.Current != item.Target);
        if (next is null)
        {
            _lootPresetTargets = null;
            return;
        }

        Request(new(
            MapSceneViewChangeKind.SetLayerVisibility,
            LayerId: next.Id,
            IsVisible: next.Target));
    }

    private void RequestHighValueLootFilter(HighValueLootLayerFilterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (HighValueLootFilterRequested is null)
        {
            SetRendererNotice(Text("Map.Loot.FilterUnavailable"));
            return;
        }

        var changeId = _nextChangeId();
        if (changeId == Guid.Empty)
        {
            throw new InvalidOperationException("A loot-filter change requires a non-empty change ID.");
        }

        HighValueLootFilterRequested.Invoke(new(
            changeId,
            _scene.Revision,
            _scene.LocationId,
            _scene.TransformVersion,
            state));
    }

    /// <summary>[Issue 563] Bridges the loot panel's Refresh action to the owner, the same way
    /// filter changes are bridged above. This view model has no network or store access.</summary>
    private void RequestHighValueLootRefresh()
    {
        if (HighValueLootRefreshRequested is null)
        {
            SetRendererNotice(Text("Map.Loot.FilterUnavailable"));
            return;
        }

        HighValueLootRefreshRequested.Invoke();
    }

    private static IReadOnlySet<MapSceneLayerId> CreateLootPresetPreserveSet(
        MapSceneSnapshot scene,
        IReadOnlyList<MapSceneLayerId>? requested)
    {
        var known = scene.Layers.Select(layer => layer.Id).ToHashSet();
        var preserved = new HashSet<MapSceneLayerId>();
        if (known.Contains(HazardsLayerId))
        {
            preserved.Add(HazardsLayerId);
        }

        if (requested is null)
        {
            return preserved;
        }

        using var enumerator = requested.GetEnumerator();
        var read = 0;
        while (read < MaximumLootPresetPreservedLayers && enumerator.MoveNext())
        {
            read++;
            if (!known.Contains(enumerator.Current))
            {
                throw new ArgumentException(
                    $"Loot-preset preserve layer '{enumerator.Current}' is not declared by the scene.",
                    nameof(requested));
            }

            preserved.Add(enumerator.Current);
        }

        if (read == MaximumLootPresetPreservedLayers && enumerator.MoveNext())
        {
            throw new ArgumentException(
                $"A loot preset cannot preserve more than {MaximumLootPresetPreservedLayers} supplied layers.",
                nameof(requested));
        }

        return preserved;
    }

    private void SelectHighValueLootEntry(HighValueLootEntry entry)
    {
        _selectedLootSpawnId = entry.Spawn.SpawnId;
        if (entry.SceneObjectId is { } objectId && VisibleObjects().Any(item => item.Id == objectId))
        {
            ApplySelection(objectId);
            return;
        }

        _selectedObjectId = null;
        SelectedObject = null;
        SelectedLootEntry = CreateLootEntry(entry);
        RaiseSelectionChanged();
    }

    private void HighValueLootProjectionChanged()
    {
        // [#677] A new loot result or filter changes what the layer's switch counts.
        BuildLayers();
        var visibleObjects = RebuildProjectedObjects();
        RebuildListItems();
        BuildDenseSceneNotice(visibleObjects);
        RestoreLootSelection();
        RaisePresentChanged(false, false, true, true, false, false);
    }

    private void RebuildAll()
    {
        _projection = CreateProjection();
        BuildModes();
        BuildFloors();
        BuildLayers();
        var visibleObjects = RebuildProjectedObjects();
        RebuildListItems();
        ResolveBackground(force: true);
        BuildDenseSceneNotice(visibleObjects);
    }

    private void BuildModes() => Modes = Enum.GetValues<MapSceneMode>()
        .Select(mode => new MapSceneRendererModeViewModel(
            mode,
            DescribeMode(mode),
            // [V2 rough package 39] The scene's own mode, now that more than one of them draws.
            mode == _scene.View.Mode,
            CanRenderMode(mode),
            RendererUnavailableReason(mode),
            () => RequestMode(mode),
            DescribeModeShort(mode)))
        .ToArray();

    // [V2 rough package 39] Top floor first, the way a lift's buttons and a building's section
    // drawing both read. Ordered by the host's own height bands where it has them; with no
    // opinion every floor scores the same and the scene's order survives untouched, which is
    // what every host but the Raid cockpit gives.
    private void BuildFloors() => Floors = OrderedFloorIds()
        .Reverse()
        .Select(floor => new MapSceneRendererFloorViewModel(
            floor,
            string.Equals(floor, _scene.View.SelectedFloorId, StringComparison.OrdinalIgnoreCase),
            () => SelectFloor(floor),
            _floorNameResolver?.Invoke(floor)))
        .ToArray();

    /// <summary>Every floor of this map, lowest first.</summary>
    private IReadOnlyList<string> OrderedFloorIds() => _scene.FloorIds
        .Select((id, index) => (Id: id, Index: index, Elevation: _floorElevationResolver?.Invoke(id)))
        .OrderBy(entry => entry.Elevation ?? double.NegativeInfinity)
        .ThenBy(entry => entry.Index)
        .Select(entry => entry.Id)
        .ToArray();

    /// <summary>The floor one step up (+1) or down (-1) the building, or null at the end.</summary>
    private string? StepTarget(int direction)
    {
        var ordered = OrderedFloorIds();
        var at = FindIndex(ordered, id => string.Equals(id, _scene.View.SelectedFloorId, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            return null;
        }

        var next = at + direction;
        return next >= 0 && next < ordered.Count ? ordered[next] : null;
    }

    private void StepFloor(int direction)
    {
        if (StepTarget(direction) is { } floorId)
        {
            SelectFloor(floorId);
        }
    }

    /// <summary>
    /// Draws the map's floors one above another, from the per-floor artwork the scene declares.
    /// </summary>
    /// <remarks>
    /// A floor whose picture the host cannot produce is left out rather than drawn blank, and
    /// <see cref="StackStatus"/> then says how many of how many arrived. A gap in the stack is
    /// honest; an empty plate at the right height is a floor that looks empty.
    /// </remarks>
    private void BuildFloorStack()
    {
        if (_scene.View.Mode != MapSceneMode.FloorStack2D)
        {
            FloorLayers = [];
            KeepHeldPictures();
            StackStatus = string.Empty;
            return;
        }

        var ordered = DrawableFloorIds();
        var artwork = _scene.Assets
            .Where(asset => asset.Kind == MapSceneAssetKind.Floor2D && asset.FloorId is not null)
            .GroupBy(asset => asset.FloorId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var selectedAt = FindIndex(ordered, id => string.Equals(id, _scene.View.SelectedFloorId, StringComparison.OrdinalIgnoreCase));
        var separation = FloorSeparation;
        var layers = new List<MapSceneRendererFloorLayerViewModel>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            var floorId = ordered[index];
            if (!artwork.TryGetValue(floorId, out var asset) ||
                HoldPicture(_reviewedAssetResolver?.Invoke(asset)) is not { } image)
            {
                continue;
            }

            var isSelected = index == selectedAt;
            layers.Add(new MapSceneRendererFloorLayerViewModel(
                floorId,
                _floorNameResolver?.Invoke(floorId) ?? MapRendererToken.Humanize(floorId),
                image,
                // Measured from the floor being read, not from the lowest one. That keeps the
                // read floor at zero, which is where the projection — and so every marker — is.
                (index - (selectedAt < 0 ? index : selectedAt)) * separation,
                isSelected ? 1 : ContextFloorOpacity,
                isSelected,
                _projection,
                () => SelectFloor(floorId)));
        }

        FloorLayers = layers;
        KeepHeldPictures();
        var floorCount = _scene.FloorIds.Count;
        StackStatus = layers.Count switch
        {
            0 => Text("Map.Stack.NoArtwork"),
            _ when layers.Count < floorCount =>
                Format("Map.Stack.Partial", _presentation.Number(layers.Count), _presentation.Number(floorCount)),
            _ => Format("Map.Stack.Floors", _presentation.Number(layers.Count)),
        };
    }

    private void BuildLayers()
    {
        // [V2 rough package 39] How many objects each layer holds, counted once for all of them
        // rather than once per layer over the whole object list. The loot layer is counted from
        // what its own filters currently leave visible, because that is what turning it on would
        // actually draw.
        var counts = new Dictionary<MapSceneLayerId, int>();
        foreach (var item in _scene.Objects)
        {
            if (HighValueLoot is not null &&
                item.LayerId == HighValueLootLayerService.LayerId &&
                !HighValueLoot.ShowableObjectIds.Contains(item.Id))
            {
                continue;
            }

            counts[item.LayerId] = counts.TryGetValue(item.LayerId, out var running) ? running + 1 : 1;
        }

        Layers = _scene.Layers
            .OrderBy(layer => layer.ZIndex)
            .ThenBy(layer => layer.Name, StringComparer.OrdinalIgnoreCase)
            .Select(layer => new MapSceneRendererLayerViewModel(
                layer,
                IsLayerVisible(layer.Id),
                _presentation,
                visible => SetLayerVisibility(layer.Id, visible),
                counts.TryGetValue(layer.Id, out var count) ? count : 0,
                layer.Id == HighValueLootLayerService.LayerId
                    ? HighValueLoot?.LayerMenuStatus
                    : null))
            .ToArray();
    }

    private IReadOnlyList<MapSceneObject> RebuildProjectedObjects()
    {
        var visibleObjects = VisibleObjects();
        GeometryObjects = _projection.IsUsable
            ? visibleObjects
                .Where(item => item.Geometry.Kind != MapSceneGeometryKind.Point &&
                    item.Geometry.Points.All(_scene.Bounds.Contains))
                .Take(MaximumGeometryObjects)
                .Select(item => new MapSceneRendererGeometryViewModel(item, _projection, _styleResolver?.Invoke(item)))
                .ToArray()
            : [];
        MapSceneRendererLabelViewModel[] labels = _projection.IsUsable
            ? visibleObjects
                .Where(item => item.Kind == MapSceneObjectKind.Label &&
                    item.Geometry.Kind == MapSceneGeometryKind.Point &&
                    _scene.Bounds.Contains(item.Geometry.Points[0]))
                .Take(MaximumPlaceNames)
                .Select(item => new MapSceneRendererLabelViewModel(item, _projection, _scene.View.Camera, _styleResolver?.Invoke(item)))
                .ToArray()
            : [];
        // [#453] What is drawn the same keeps its instance, so the view keeps its control.
        _labelObjects.Reconcile(ReconciledList<MapSceneRendererLabelViewModel>.Reuse(
            _labelObjects,
            labels,
            static label => label.SceneObject.Id.Value,
            static (old, fresh) => old.DrawsSameAs(fresh)));
        SpatialObjects = ReconciledList<MapSceneRendererObjectViewModel>.Reuse(
            SpatialObjects,
            BuildPointMarkers(visibleObjects),
            static marker => marker.Key,
            static (old, fresh) => old.DrawsSameAs(fresh));
        _pointMarkers.Reconcile([.. SpatialObjects.Where(item => !item.IsCluster && !item.IsRankedLoot)]);
        _rankedLoot = [.. SpatialObjects.Where(item => item.IsRankedLoot)];
        _lootBadges.Reconcile(ReconciledList<MapSceneRendererObjectViewModel>.Reuse(
            _lootBadges,
            BuildLootBadges(_rankedLoot),
            static badge => badge.Key,
            static (old, fresh) => old.DrawsSameBadgeAs(fresh)));
        ShowRevealedLoot();
        StackMarkers = BuildStackMarkers(PointMarkers);
        ClusterMarkers = SpatialObjects.Where(item => item.IsCluster).ToArray();
        SelectedObject = _selectedObjectId is { } selected
            ? CreateSelectedObject(selected)
            : null;
        if (_selectedLootSpawnId is null)
        {
            SelectedLootEntry = null;
        }
        // Built here rather than only on a scene change: the plates are positioned in the same
        // projected rectangle the markers are, so a resized card has to move both together.
        BuildFloorStack();
        return visibleObjects;
    }

    private IReadOnlyList<MapSceneRendererObjectViewModel> BuildPointMarkers(IReadOnlyList<MapSceneObject> visibleObjects)
    {
        if (!_projection.IsUsable)
        {
            return [];
        }

        var points = visibleObjects
            .Where(item => item.Kind != MapSceneObjectKind.Label)
            .Where(item => item.Geometry.Kind == MapSceneGeometryKind.Point && _scene.Bounds.Contains(item.Geometry.Points[0]))
            .ToArray();
        if (_ranksLootByValue && points.Any(IsRankableLoot))
        {
            var loot = points.Where(IsRankableLoot).ToArray();
            var rest = points.Where(item => !IsRankableLoot(item)).ToArray();
            return BuildPlacedMarkers(rest).Concat(BuildLootMarkers(loot)).ToArray();
        }

        return BuildPlacedMarkers(points);
    }

    private static bool IsRankableLoot(MapSceneObject item) =>
        item.Kind == MapSceneObjectKind.LootSpawn && item.Truth == MapSceneTruthKind.PotentialSpawn;

    private IReadOnlyList<MapSceneRendererObjectViewModel> BuildLootMarkers(IReadOnlyList<MapSceneObject> loot)
    {
        var ranked = MapLootRanking.Ranked(loot, _lootValues);
        return ranked
            .Select((item, rank) =>
            {
                var marker = MapSceneRendererObjectViewModel.ForObject(
                    item,
                    _projection,
                    _scene.View.Camera,
                    _presentation,
                    item.Id == _selectedObjectId,
                    () => SelectObject(item.Id),
                    _styleResolver?.Invoke(item),
                    canvasWidth: _canvasWidth,
                    canvasHeight: _canvasHeight);
                marker.RankAsLoot(
                    MapLootRanking.RevealZoom(rank),
                    _lootValues.TryGetValue(item.Id, out var value) ? value.Tier : LootSpawnValueTier.Unknown);
                return marker;
            })
            .ToArray();
    }

    /// <summary>
    /// Brings <see cref="LootMarkers"/> to the spawns revealed at the deepest zoom reached on this
    /// map, and the selected one. Revealed in rank order, so zooming in appends to the end. Zooming
    /// out keeps them (hidden by <c>IsShownOnPlan</c>), so zooming back in builds nothing again;
    /// a spawn nobody has zoomed in far enough to see never gets a control at all.
    /// </summary>
    private void ShowRevealedLoot()
    {
        _lootZoomReached = Math.Max(_lootZoomReached, _scene.View.Camera.Zoom);
        _lootMarkers.Reconcile([.. _rankedLoot.Where(marker => marker.LootRevealZoom <= _lootZoomReached || marker.IsShownOnPlan)]);
    }

    private IReadOnlyList<MapSceneRendererObjectViewModel> BuildLootBadges(IReadOnlyList<MapSceneRendererObjectViewModel> loot)
    {
        var badges = new List<MapSceneRendererObjectViewModel>();
        foreach (var cell in loot
                     .Where(marker => marker.LootRevealZoom > 0 && marker.SceneObject is not null)
                     .GroupBy(marker => (
                         Column: (int)Math.Floor(marker.AnchorLeft / MapLootRanking.BadgeCell),
                         Row: (int)Math.Floor(marker.AnchorTop / MapLootRanking.BadgeCell)))
                     .OrderBy(group => group.Key.Row)
                     .ThenBy(group => group.Key.Column))
        {
            var members = cell.ToArray();
            var objects = members.Select(member => member.SceneObject!).ToArray();
            var point = new MapScenePoint(
                objects.Average(item => item.Geometry.Points[0].X),
                objects.Average(item => item.Geometry.Points[0].Y));
            var badge = MapSceneRendererObjectViewModel.ForCluster(
                cell.Key.Column,
                cell.Key.Row + 10_000,
                objects,
                _projection,
                _scene.View.Camera,
                _presentation,
                () => FocusOn(point, Math.Max(_scene.View.Camera.Zoom * 2, StackZoom)));
            badge.CountHiddenLoot(members.Select(member => member.LootRevealZoom).ToArray());
            badges.Add(badge);
        }

        return badges;
    }

    private IReadOnlyList<MapSceneRendererObjectViewModel> BuildPlacedMarkers(IReadOnlyList<MapSceneObject> points)
    {
        if (points.Count <= MaximumPointMarkers)
        {
            // [Issue 508] Two pins (or a ping and a pin) on the exact same spot must both stay
            // legible, so a coincident group among the marks a player actually places and reads —
            // waypoints, quest objectives, pings — is nudged into a small ring around the shared
            // point before it is drawn. Every other kind (an extract, a loot spawn, …) draws the
            // older chip, which never reads this offset, so there is no reason to spend the pass
            // detecting collisions among them too.
            var offsets = ResolvePinOverlap(points);
            return points
                .Select((item, index) => MapSceneRendererObjectViewModel.ForObject(
                    item,
                    _projection,
                    _scene.View.Camera,
                    _presentation,
                    item.Id == _selectedObjectId,
                    () => SelectObject(item.Id),
                    _styleResolver?.Invoke(item),
                    offsets[index].DeltaX,
                    offsets[index].DeltaY,
                    _canvasWidth,
                    _canvasHeight,
                    SwitchStepGlyph(item)))
                .ToArray();
        }

        // [#797] Quest objectives are never part of an anonymous grid cell either: each keeps its
        // own lettered pin and only the rest of a crowded scene is counted.
        var objectives = points.Where(item => item.Kind == MapSceneObjectKind.QuestObjective).ToArray();
        var rest = points.Where(item => item.Kind != MapSceneObjectKind.QuestObjective).ToArray();
        var objectiveOffsets = ResolvePinOverlap(objectives);
        var objectiveMarkers = objectives
            .Select((item, index) => MapSceneRendererObjectViewModel.ForObject(
                item,
                _projection,
                _scene.View.Camera,
                _presentation,
                item.Id == _selectedObjectId,
                () => SelectObject(item.Id),
                _styleResolver?.Invoke(item),
                objectiveOffsets[index].DeltaX,
                objectiveOffsets[index].DeltaY,
                _canvasWidth,
                _canvasHeight));
        return objectiveMarkers.Concat(rest
            .GroupBy(item => ClusterCell(item.Geometry.Points[0]))
            .OrderBy(group => group.Key.Row)
            .ThenBy(group => group.Key.Column)
            .Select(group => BuildClusterMarker(group.Key.Column, group.Key.Row, group.ToArray()))
            .Take(Math.Max(0, MaximumPointMarkers - objectives.Length)))
            .ToArray();
    }

    /// <summary>
    /// The (dx, dy) each of <paramref name="points"/> should be drawn with, in the same order, so
    /// a waypoint, quest objective, ping, extract, transit, or active numbered switch landing on
    /// another one is still legible. Zero for every point that is not one of those kinds, or that
    /// has nothing else near it.
    /// </summary>
    /// <remarks>
    /// Issue 573: extracts joined this list alongside 508's original three. Several extracts and
    /// objectives stacked on one small building (the Resort on Shoreline) were reported reading as
    /// a single blob even after the objective's own letter and the extract's own border told them
    /// apart; fanning them the same ring a waypoint or objective already gets keeps the building's
    /// own name legible underneath.
    /// </remarks>
    private IReadOnlyList<(double DeltaX, double DeltaY)> ResolvePinOverlap(IReadOnlyList<MapSceneObject> points)
    {
        var result = new (double DeltaX, double DeltaY)[points.Count];
        var eligible = new List<int>();
        var anchors = new List<(double X, double Y)>();
        var numberedSwitches = new List<int>();
        var numberedSwitchAnchors = new List<(double X, double Y)>();
        for (var index = 0; index < points.Count; index++)
        {
            var icon = MapSceneRendererObjectViewModel.IconFor(points[index]);
            var isNumberedSwitch = points[index].Kind == MapSceneObjectKind.Switch &&
                SwitchStepGlyph(points[index]) is not null;
            if (icon is not (MapSceneMarkerIcon.Waypoint or MapSceneMarkerIcon.Objective or MapSceneMarkerIcon.Ping
                or MapSceneMarkerIcon.Extract or MapSceneMarkerIcon.Transit) && !isNumberedSwitch)
            {
                continue;
            }

            var projected = _projection.Project(points[index].Geometry.Points[0]);
            eligible.Add(index);
            anchors.Add((projected.X, projected.Y));
            if (isNumberedSwitch)
            {
                numberedSwitches.Add(index);
                numberedSwitchAnchors.Add((projected.X, projected.Y));
            }
        }

        if (eligible.Count < 2)
        {
            return result;
        }

        var resolved = MapMarkerOverlapLayout.Resolve(anchors, MapMarkerScale.For(_scene.View.Camera.Zoom));
        for (var slot = 0; slot < eligible.Count; slot++)
        {
            result[eligible[slot]] = resolved[slot];
        }

        // Elevator call and extract controls can share almost the same catalog point as the
        // selected extract. The ordinary 16-DIP ring still leaves their 20-DIP numbered chips
        // under its larger badge/name plate, so active switch pairs use a wider horizontal fan.
        // The anchor remains exact; only the drawn chip moves, as with every overlap nudge here.
        foreach (var group in MapMarkerOverlapLayout.Stacks(
                     numberedSwitchAnchors,
                     MapMarkerOverlapLayout.CollisionDistance,
                     minimumCount: 2))
        {
            if (group.Count == 2)
            {
                result[numberedSwitches[group[0]]] = (-30, 0);
                result[numberedSwitches[group[1]]] = (30, 0);
                continue;
            }

            for (var slot = 0; slot < group.Count; slot++)
            {
                var angle = (2 * Math.PI * slot / group.Count) - (Math.PI / 2);
                result[numberedSwitches[group[slot]]] = (30 * Math.Cos(angle), 30 * Math.Sin(angle));
            }
        }

        // The selected name plate is 184 DIPs wide. A numbered 20-DIP switch chip therefore
        // needs its centre at least 102 DIPs from the extract's centre to clear the plate; 108
        // leaves a visible gap. This second pass applies only while the extract is selected and
        // only to steps whose catalog point is actually beside it. A hover has no persistent name
        // to avoid, and a remote prerequisite keeps its exact map position.
        var selected = points.FirstOrDefault(item =>
            item.Id == _selectedObjectId &&
            item.Kind is MapSceneObjectKind.Extract or MapSceneObjectKind.Transit);
        if (selected is not null)
        {
            const double selectedNameStepScreenOffset = 108;
            const double additionalStepScreenSpacing = 24;
            const double selectedNameEdgeFlipMargin = 110;
            // PinOffset is inside the marker's scale transform. Compensate here so the clearance
            // above is 108 screen DIPs both at the 0.7 fit size and at full marker size.
            var markerScale = MapMarkerScale.For(_scene.View.Camera.Zoom);
            var selectedNameStepOffset = selectedNameStepScreenOffset / markerScale;
            var additionalStepSpacing = additionalStepScreenSpacing / markerScale;
            var selectedAnchor = _projection.Project(selected.Geometry.Points[0]);
            var nearby = numberedSwitchAnchors
                .Select((anchor, slot) => (anchor, slot))
                .Where(item => Math.Sqrt(
                    Math.Pow(item.anchor.X - selectedAnchor.X, 2) +
                    Math.Pow(item.anchor.Y - selectedAnchor.Y, 2)) < MapMarkerOverlapLayout.CollisionDistance)
                .Select(item => item.slot)
                .ToArray();
            var nameFlipsLeft = selectedAnchor.X > _canvasWidth - selectedNameEdgeFlipMargin;
            for (var slot = 0; slot < nearby.Length; slot++)
            {
                var direction = nameFlipsLeft ? 1 : slot % 2 == 0 ? -1 : 1;
                var lane = nameFlipsLeft ? slot : slot / 2;
                result[numberedSwitches[nearby[slot]]] =
                    (direction * (selectedNameStepOffset + (lane * additionalStepSpacing)), 0);
            }
        }

        return result;
    }

    /// <summary>The zoom below which a dense spot is drawn as one count badge.</summary>
    /// <remarks>
    /// Marks within <see cref="MapMarkerOverlapLayout.CollisionDistance"/> canvas DIPs of each other
    /// are fanned into a small ring; at twice the fitted zoom that ring has room to read, below it
    /// three or more of them only bury the building they stand on.
    /// </remarks>
    public const double StackZoom = 2;

    private IReadOnlyList<MapSceneRendererObjectViewModel> BuildStackMarkers(IReadOnlyList<MapSceneRendererObjectViewModel> markers)
    {
        // A reused marker (#453) may have been in a stack the last time; every one starts out of one.
        foreach (var marker in markers)
        {
            marker.JoinStack(0);
        }

        var eligible = markers
            .Where(marker => marker.SceneObject is not null &&
                marker.Icon is MapSceneMarkerIcon.Extract or MapSceneMarkerIcon.Transit or MapSceneMarkerIcon.Objective
                    or MapSceneMarkerIcon.Waypoint or MapSceneMarkerIcon.Ping)
            .ToArray();
        var groups = MapMarkerOverlapLayout.Stacks(
            eligible.Select(marker => (marker.AnchorLeft, marker.AnchorTop)).ToArray(),
            MapMarkerOverlapLayout.CollisionDistance,
            minimumCount: 3);
        var stacks = new List<MapSceneRendererObjectViewModel>();
        foreach (var group in groups)
        {
            var members = group.Select(index => eligible[index]).ToArray();
            // [#307] A spot the objective route stops at is never folded into a count: on Customs
            // the "5" and "7" boxes were stacks that had swallowed the route's numbered stops, and
            // a count drawn over the stop's badge hid it just the same.
            // [#797] Nor is one holding a quest objective. On Reserve a "3" box stood where three
            // objectives were, and the player lost which quest and where: an objective keeps its
            // lettered pin at every zoom, and ResolvePinOverlap fans pins that land together.
            if (members.Any(member => member.HasPinBadge || member.Icon == MapSceneMarkerIcon.Objective))
            {
                continue;
            }

            foreach (var member in members)
            {
                member.JoinStack(StackZoom);
            }

            var objects = members.Select(member => member.SceneObject!).ToArray();
            var point = new MapScenePoint(
                objects.Average(item => item.Geometry.Points[0].X),
                objects.Average(item => item.Geometry.Points[0].Y));
            var badge = MapSceneRendererObjectViewModel.ForCluster(
                stacks.Count,
                -1,
                objects,
                _projection,
                _scene.View.Camera,
                _presentation,
                () => FocusOn(point, StackZoom * 1.25));
            badge.ShowAsStackBelow(StackZoom);
            stacks.Add(badge);
        }

        return stacks;
    }

    private MapSceneRendererObjectViewModel BuildClusterMarker(
        int column,
        int row,
        IReadOnlyList<MapSceneObject> objects)
    {
        if (objects.Count == 1)
        {
            var item = objects[0];
            return MapSceneRendererObjectViewModel.ForObject(
                item,
                _projection,
                _scene.View.Camera,
                _presentation,
                item.Id == _selectedObjectId,
                () => SelectObject(item.Id),
                _styleResolver?.Invoke(item),
                canvasWidth: _canvasWidth,
                canvasHeight: _canvasHeight,
                markerGlyphOverride: SwitchStepGlyph(item));
        }

        return MapSceneRendererObjectViewModel.ForCluster(
            column,
            row,
            objects,
            _projection,
            _scene.View.Camera,
            _presentation,
            () => OpenCluster(objects));
    }

    private void RebuildListItems()
    {
        var search = SearchText.Trim();
        _filteredListObjects = VisibleObjects()
            .Where(item => _clusterFilter is null || _clusterFilter.Contains(item.Id))
            .Where(item => search.Length == 0 || MatchesSearch(item, search))
            .ToArray();
        var pages = ListPageCount;
        _listPageIndex = pages == 0 ? 0 : Math.Clamp(_listPageIndex, 0, pages - 1);
        ListItems = _filteredListObjects
            .Skip(_listPageIndex * ListPageSize)
            .Take(ListPageSize)
            .Select(item => new MapSceneRendererListItemViewModel(
                item,
                _presentation,
                item.Id == _selectedObjectId,
                () => SelectObject(item.Id)))
            .ToArray();
    }

    private bool MatchesSearch(MapSceneObject item, string search) =>
        Contains(item.Label, search) ||
        Contains(item.Detail, search) ||
        Contains(DescribeKind(item.Kind), search) ||
        Contains(DescribeTruth(item.Truth), search) ||
        Contains(DescribeFaction(item.Faction), search);

    private bool Contains(string? value, string search) => value is not null &&
        _presentation.Culture.CompareInfo.IndexOf(value, search, System.Globalization.CompareOptions.IgnoreCase) >= 0;

    private void ApplySelection(MapSceneObjectId? next)
    {
        var previousRequirements = ActiveSwitchSteps();
        var previous = _selectedObjectId;
        _selectedObjectId = next;
        foreach (var marker in SpatialObjects.Where(item => item.ObjectId == previous || item.ObjectId == next))
        {
            marker.SetSelected(marker.ObjectId == next);
        }

        ShowRevealedLoot();

        foreach (var item in ListItems.Where(item => item.Id == previous || item.Id == next))
        {
            item.SetSelected(item.Id == next);
        }

        SelectedObject = next is { } selected ? CreateSelectedObject(selected) : null;
        var lootEntry = next is { } objectId
            ? HighValueLootEntryFor(objectId)
            : null;
        if (lootEntry is not null)
        {
            _selectedLootSpawnId = lootEntry.Spawn.SpawnId;
        }
        else if (next is not null)
        {
            _selectedLootSpawnId = null;
        }

        SelectedLootEntry = lootEntry is null ? null : CreateLootEntry(lootEntry);
        if (!SameSwitchSteps(previousRequirements, ActiveSwitchSteps()))
        {
            RebuildProjectedObjects();
            RebuildListItems();
            BuildDenseSceneNotice(VisibleObjects());
        }

        RaiseSelectionChanged();
    }

    private MapSceneRendererObjectViewModel? CreateSelectedObject(MapSceneObjectId selected)
    {
        var item = VisibleObjects().FirstOrDefault(candidate => candidate.Id == selected);
        return item is null
            ? null
            : MapSceneRendererObjectViewModel.ForObject(
                item,
                _projection,
                _scene.View.Camera,
                _presentation,
                true,
                () => SelectObject(item.Id),
                _styleResolver?.Invoke(item),
                canvasWidth: _canvasWidth,
                canvasHeight: _canvasHeight,
                markerGlyphOverride: SwitchStepGlyph(item));
    }

    private (int Column, int Row) ClusterCell(MapScenePoint point)
    {
        var normalizedX = (point.X - _scene.Bounds.MinimumX) / _scene.Bounds.Width;
        var normalizedY = (point.Y - _scene.Bounds.MinimumY) / _scene.Bounds.Height;
        return (
            Math.Clamp((int)(normalizedX * ClusterColumns), 0, ClusterColumns - 1),
            Math.Clamp((int)(normalizedY * ClusterRows), 0, ClusterRows - 1));
    }

    private MapSceneAsset? SelectedBackgroundAsset() => _scene.Assets
        .FirstOrDefault(item => item.Kind == MapSceneAssetKind.Background2D);

    private void ResolveBackground(bool force)
    {
        var asset = SelectedBackgroundAsset();
        var key = asset is null ? null : $"{asset.Id.Value}:{asset.ContentSha256}";
        if (force || !string.Equals(key, _resolvedAssetKey, StringComparison.Ordinal))
        {
            _resolvedAssetKey = key;
            var resolved = asset is null || _reviewedAssetResolver is null
                ? null
                : _reviewedAssetResolver(asset);
            BackgroundImage = HoldPicture(resolved);
            if (resolved is not null && BackgroundImage is null)
            {
                // Already retired by its owner: ask again on the next present.
                _resolvedAssetKey = null;
            }

            KeepHeldPictures();
        }

        // What says how wide this map really is: its own plan rectangle where the host gave one,
        // and the decoded artwork where the bounds are only a square box (see AdoptPlanAspect).
        var reprojected = AdoptPlanAspect();
        ReviewedAssetLabel = asset is null
            ? string.Empty
            : Format("Map.Asset.Label", asset.Attribution, asset.MapVersion, asset.GameVersion);
        UpdateBackgroundStatus(asset);
        if (reprojected)
        {
            RaiseProjectionChanged();
        }
    }

    private IImage? HoldPicture(IImage? image) => _pictureHold is null ? image : _pictureHold.Hold(image);

    private void KeepHeldPictures() =>
        _pictureHold?.Keep([BackgroundImage, .. FloorLayers.Select(layer => layer.Image)]);

    /// <summary>How many pictures this renderer holds a lease on.</summary>
    internal int HeldPictureCount => _pictureHold?.Count ?? 0;

    /// <summary>
    /// [#775] Lets go of every picture, for a host that stops showing this renderer: a lease that
    /// is never ended keeps its picture alive for good.
    /// </summary>
    public void ReleasePictures()
    {
        if (_pictureHold is null || _pictureHold.Count == 0)
        {
            return;
        }

        BackgroundImage = null;
        FloorLayers = [];
        _resolvedAssetKey = null;
        OnPropertyChanged(nameof(BackgroundImage));
        OnPropertyChanged(nameof(HasBackgroundImage));
        OnPropertyChanged(nameof(ShowsFlatBackground));
        OnPropertyChanged(nameof(FloorLayers));
        _pictureHold.ReleaseAll();
    }

    private void UpdateBackgroundStatus(MapSceneAsset? asset) => BackgroundStatus = !_projection.IsUsable
        ? Text("Map.Background.Bounds")
        : asset is null
            ? Text("Map.Background.None")
            : BackgroundImage is null
                ? Text("Map.Background.NotCached")
                : string.Empty;

    private void BuildDenseSceneNotice(IReadOnlyList<MapSceneObject> visibleObjects)
    {
        // [#573] Loot drawn by value rank is never grouped by the point cap, so it is not counted
        // against it either ("286 grouped" was said of a Customs loot view that grouped nothing).
        var pointCount = visibleObjects.Count(item => item.Geometry.Kind == MapSceneGeometryKind.Point &&
            !(_ranksLootByValue && IsRankableLoot(item)));
        var geometryCount = visibleObjects.Count - pointCount;
        var outsideBounds = visibleObjects.Count(item => item.Geometry.Points.Any(point => !_scene.Bounds.Contains(point)));
        var messages = new List<string>(4);
        if (pointCount > MaximumPointMarkers)
        {
            messages.Add(Format("Map.Dense.Points", _presentation.Number(pointCount), _presentation.Number(SpatialObjects.Count)));
        }
        if (geometryCount > MaximumGeometryObjects)
        {
            messages.Add(Format("Map.Dense.Geometry", _presentation.Number(MaximumGeometryObjects), _presentation.Number(geometryCount)));
        }
        if (visibleObjects.Count > ListPageSize)
        {
            var pages = (visibleObjects.Count + ListPageSize - 1) / ListPageSize;
            messages.Add(Format("Map.Dense.List", _presentation.Number(visibleObjects.Count), _presentation.Number(pages)));
        }
        if (outsideBounds > 0)
        {
            messages.Add(Format("Map.Dense.Outside", _presentation.Number(outsideBounds)));
        }

        DenseSceneNotice = messages.Count == 0
            ? string.Empty
            : string.Join("; ", messages) + ". " + Text("Map.Dense.Suffix");
        // [V2 rough package 46] What actually draws over the plan, and only for the conditions
        // that change what is on it. "2 map notes" counted the sentences in the tooltip — a
        // number about the notice rather than about the map, which is why he asked what it
        // meant. Each chip now names its own condition, and list paging, which changes only how
        // the list beside the map is read, no longer puts anything on the plan at all; it stays
        // in the tooltip, and the list's own pager already says how many pages it has.
        var chips = new List<string>(3);
        if (pointCount > MaximumPointMarkers)
        {
            chips.Add(Format("Map.Dense.Chip.Grouped", _presentation.Number(pointCount)));
        }

        if (geometryCount > MaximumGeometryObjects)
        {
            chips.Add(Format("Map.Dense.Chip.Shapes", _presentation.Number(geometryCount - MaximumGeometryObjects)));
        }

        if (outsideBounds > 0)
        {
            chips.Add(Format("Map.Dense.Chip.Outside", _presentation.Number(outsideBounds)));
        }

        DenseSceneChip = string.Join(" · ", chips);
    }

    private bool CanRenderMode(MapSceneMode mode) => mode switch
    {
        MapSceneMode.Flat2D => _scene.Capabilities.Flat2D.IsAvailable,
        // [V2 rough package 39] Offered whenever the scene says the map has floors. Whether the
        // artwork for each one can actually be produced is the host's answer and arrives with
        // the next scene; a stack that came back empty says so through StackStatus rather than
        // refusing the press and leaving a dead control.
        MapSceneMode.FloorStack2D => _scene.Capabilities.FloorStack2D.IsAvailable,
        _ => false,
    };

    private string RendererUnavailableReason(MapSceneMode mode) => mode switch
    {
        MapSceneMode.Flat2D => _scene.Capabilities.Flat2D.UnavailableReason ?? string.Empty,
        MapSceneMode.FloorStack2D => Text("Map.Mode.FloorStackUnsupported"),
        MapSceneMode.Interior3D => Text("Map.Mode.InteriorUnsupported"),
        _ => string.Empty,
    };

    private bool IsLayerVisible(MapSceneLayerId layerId) => _scene.View.Layers
        .FirstOrDefault(state => state.LayerId == layerId)?.IsVisible ??
        _scene.Layers.Single(layer => layer.Id == layerId).IsVisibleByDefault;

    private IReadOnlyList<MapSceneObject> VisibleObjects()
    {
        IReadOnlyList<MapSceneObject> visible = _scene.VisibleObjects;
        var required = ActiveSwitchSteps();
        if (required.Count > 0)
        {
            var shown = visible.Select(item => item.Id).ToHashSet();
            var selectedFloor = _scene.View.SelectedFloorId;
            visible = visible.Concat(_scene.Objects.Where(item =>
                    item.Kind == MapSceneObjectKind.Switch &&
                    item.CatalogId is { } catalogId && required.ContainsKey(catalogId) &&
                    !shown.Contains(item.Id) &&
                    (selectedFloor is null || item.FloorIds.Count == 0 ||
                        item.FloorIds.Contains(selectedFloor, StringComparer.OrdinalIgnoreCase))))
                .OrderBy(item => _scene.Layers.Single(layer => layer.Id == item.LayerId).ZIndex)
                .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id.Value, StringComparer.Ordinal)
                .ToArray();
        }

        if (HighValueLoot is null)
        {
            return visible;
        }

        return visible
            .Where(item => item.LayerId != HighValueLootLayerService.LayerId ||
                           HighValueLoot.VisibleObjectIds.Contains(item.Id))
            .ToArray();
    }

    private IReadOnlyDictionary<string, int> ActiveSwitchSteps()
    {
        MapExtractRequirements? RequirementsFor(MapSceneObjectId? id) => id is { } definite
            ? _scene.Objects.FirstOrDefault(item => item.Id == definite)?.ExtractRequirements
            : null;

        var requirements = RequirementsFor(_selectedObjectId) ?? RequirementsFor(_hoveredRequirementObjectId);
        return requirements is null
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : requirements.SwitchChain
                .Select((item, index) => (item.Id, Step: index + 1))
                .ToDictionary(item => item.Id, item => item.Step, StringComparer.Ordinal);
    }

    private string? SwitchStepGlyph(MapSceneObject item) =>
        item.Kind == MapSceneObjectKind.Switch && item.CatalogId is { } id && ActiveSwitchSteps().TryGetValue(id, out var step)
            ? step.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private static bool SameSwitchSteps(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right) =>
        left.Count == right.Count && left.All(entry => right.TryGetValue(entry.Key, out var value) && value == entry.Value);

    private HighValueLootEntry? HighValueLootEntryFor(MapSceneObjectId objectId) => HighValueLoot is null
        ? null
        : HighValueLootResultEntries()
            .FirstOrDefault(entry => entry.SceneObjectId == objectId);

    private IReadOnlyList<HighValueLootEntry> HighValueLootResultEntries() => HighValueLoot?.AllEntries ?? [];

    private HighValueLootEntryViewModel CreateLootEntry(HighValueLootEntry entry) => new(
        entry,
        HighValueLoot?.FilterState.Filter ?? HighValueLootFilter.Default,
        _presentation,
        () => SelectHighValueLootEntry(entry));

    private void RestoreLootSelection()
    {
        if (_selectedLootSpawnId is null || HighValueLoot is null)
        {
            SelectedLootEntry = null;
            RaiseSelectionChanged();
            return;
        }

        var entry = HighValueLoot.AllEntries
            .FirstOrDefault(candidate => string.Equals(
                candidate.Spawn.SpawnId,
                _selectedLootSpawnId,
                StringComparison.Ordinal));
        if (entry is null)
        {
            _selectedLootSpawnId = null;
            _selectedObjectId = null;
            SelectedObject = null;
            SelectedLootEntry = null;
        }
        else
        {
            SelectedLootEntry = CreateLootEntry(entry);
        }

        RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedObject));
        OnPropertyChanged(nameof(SelectedLootEntry));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasGenericSelection));
        OnPropertyChanged(nameof(HasLootSelection));
    }

    private void EnsureHighValueLootMatchesScene(
        HighValueLootLayerResult result,
        HighValueLootFilter filter) =>
        EnsureHighValueLootMatchesScene(_scene, result, filter);

    private static void EnsureHighValueLootMatchesScene(
        MapSceneSnapshot scene,
        HighValueLootLayerResult result,
        HighValueLootFilter filter)
    {
        if (result.Layer.Id != HighValueLootLayerService.LayerId ||
            !scene.Layers.Any(layer => layer == result.Layer))
        {
            throw new ArgumentException(
                "The canonical scene must declare the exact high-value loot layer supplied beside it.",
                nameof(result));
        }

        if (!Equivalent(result.AppliedFilter, filter))
        {
            throw new ArgumentException(
                "The typed loot result must match the filter state presented beside it.",
                nameof(result));
        }

        if (!string.Equals(result.MapId, scene.LocationId, StringComparison.Ordinal) ||
            !string.Equals(result.TransformVersion, scene.TransformVersion, StringComparison.Ordinal))
        {
            // Positioned objects do not repeat map identity, and an unavailable or map-only result
            // can have no object to compare at all. Bind the whole typed projection explicitly or
            // a valid empty-object join could display another map's knowledge beside this plan.
            throw new ArgumentException(
                "The typed loot result must match the canonical scene map and transform.",
                nameof(result));
        }

        var sceneObjects = scene.Objects
            .Where(item => item.LayerId == result.Layer.Id)
            .OrderBy(item => item.Id.Value, StringComparer.Ordinal)
            .ToArray();
        var resultObjects = result.Objects
            .OrderBy(item => item.Id.Value, StringComparer.Ordinal)
            .ToArray();
        if (!sceneObjects.SequenceEqual(resultObjects))
        {
            throw new ArgumentException(
                "The typed loot result and canonical scene must contain the same loot objects.",
                nameof(result));
        }
    }

    private static bool Equivalent(HighValueLootFilter left, HighValueLootFilter right) =>
        left.ValueBasis == right.ValueBasis &&
        left.Thresholds == right.Thresholds &&
        left.MaximumPriceAge == right.MaximumPriceAge &&
        left.MaximumSourceAge == right.MaximumSourceAge &&
        left.MinimumConfidence.Equals(right.MinimumConfidence) &&
        left.IncludeProfileRelevant == right.IncludeProfileRelevant &&
        string.Equals(left.FloorId, right.FloorId, StringComparison.OrdinalIgnoreCase) &&
        left.ItemIds.SequenceEqual(right.ItemIds, StringComparer.OrdinalIgnoreCase) &&
        left.Categories.SequenceEqual(right.Categories, StringComparer.OrdinalIgnoreCase);

    private void SetRendererNotice(string value)
    {
        _rendererNotice = value;
        OnPropertyChanged(nameof(RendererNotice));
        OnPropertyChanged(nameof(HasRendererNotice));
    }

    internal string DescribeMode(MapSceneMode mode) => Text(mode switch
    {
        MapSceneMode.Flat2D => "Map.Mode.Flat",
        MapSceneMode.FloorStack2D => "Map.Mode.FloorStack",
        MapSceneMode.Interior3D => "Map.Mode.Interior",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    });

    internal string DescribeModeShort(MapSceneMode mode) => Text(mode switch
    {
        MapSceneMode.Flat2D => "Map.Mode.Flat.Short",
        MapSceneMode.FloorStack2D => "Map.Mode.FloorStack.Short",
        MapSceneMode.Interior3D => "Map.Mode.Interior.Short",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    });

    internal string DescribeKind(MapSceneObjectKind kind) => Text($"Map.Kind.{kind}");

    internal string DescribeTruth(MapSceneTruthKind truth) => Text(Enum.IsDefined(truth)
        ? $"Map.Truth.{truth}"
        : "Map.Truth.Unknown");

    internal string DescribeFaction(MapFeatureFaction faction) => Text(faction switch
    {
        MapFeatureFaction.Pmc => "Map.Faction.Pmc",
        MapFeatureFaction.Scav => "Map.Faction.Scav",
        MapFeatureFaction.Shared => "Map.Faction.Shared",
        _ => "Map.Faction.Unknown",
    });

    internal string DescribeOffer(MapSceneOfferState offerState) => Text(offerState switch
    {
        MapSceneOfferState.Offered => "Map.Offer.Offered",
        MapSceneOfferState.NotOffered => "Map.Offer.NotOffered",
        _ => "Map.Offer.Unknown",
    });

    internal static bool HasOfferStatus(MapSceneObjectKind kind) =>
        kind is MapSceneObjectKind.Extract or MapSceneObjectKind.Transit;

    internal string DescribeEvidence(DataProvenance provenance)
    {
        var confidence = provenance.Confidence is { } value
            ? Format("Map.Evidence.Confidence", _presentation.Percent(value.Value))
            : string.Empty;
        return Format("Map.Evidence", provenance.Source, _presentation.Instant(provenance.ObservedUtc), confidence);
    }

    internal string DescribeEstimate(MapSceneEstimateMetadata? estimate) => estimate is null
        ? string.Empty
        : Format(
            "Map.Estimate",
            estimate.ModelVersion,
            _presentation.Instant(estimate.ObservedFromUtc),
            _presentation.Instant(estimate.DataThroughUtc),
            _presentation.Instant(estimate.GeneratedUtc),
            estimate.Coverage,
            estimate.Calibration,
            estimate.TransformVersion);

    internal string DescribeAutomation(MapSceneObject item)
    {
        var offer = HasOfferStatus(item.Kind)
            ? Format("Map.Marker.OfferSuffix", DescribeOffer(item.OfferState))
            : string.Empty;
        return Format(
            "Map.Marker.Automation",
            item.Label,
            DescribeKind(item.Kind),
            DescribeTruth(item.Truth),
            DescribeFaction(item.Faction),
            offer);
    }

    private string Text(string key) => _presentation.Get(key);

    private string Format(string key, params object?[] arguments) => _presentation.Format(key, arguments);

    private void RaisePresentChanged(
        bool modes,
        bool floors,
        bool layers,
        bool visibleContent,
        bool camera,
        bool background)
    {
        OnPropertyChanged(nameof(Scene));
        OnPropertyChanged(nameof(RendererNotice));
        OnPropertyChanged(nameof(HasRendererNotice));
        OnPropertyChanged(nameof(LocationLabel));
        OnPropertyChanged(nameof(VariantLabel));
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(ModeFallbackNotice));
        OnPropertyChanged(nameof(ThreeDimensionalFallback));
        OnPropertyChanged(nameof(ShowsModeFallback));
        OnPropertyChanged(nameof(ShowsThreeDimensionalFallback));
        OnPropertyChanged(nameof(ShowsTrafficHeat));
        if (modes) OnPropertyChanged(nameof(Modes));
        if (floors)
        {
            OnPropertyChanged(nameof(Floors));
            OnPropertyChanged(nameof(HasFloorFilters));
            OnPropertyChanged(nameof(HasFloors));
            OnPropertyChanged(nameof(SelectedFloor));
            RaiseFloorStackChanged();
        }
        if (layers)
        {
            OnPropertyChanged(nameof(Layers));
            // [V2 rough package 46] The Layers button's count is the only thing saying what is
            // drawn once the switches are behind a menu, so it has to move when they do.
            OnPropertyChanged(nameof(LayersMenuLabel));
        }

        if (visibleContent)
        {
            OnPropertyChanged(nameof(SpatialObjects));
            OnPropertyChanged(nameof(PointMarkers));
            OnPropertyChanged(nameof(ClusterMarkers));
            OnPropertyChanged(nameof(StackMarkers));
            OnPropertyChanged(nameof(LootMarkers));
            OnPropertyChanged(nameof(LootBadges));
            OnPropertyChanged(nameof(GeometryObjects));
            OnPropertyChanged(nameof(LabelObjects));
            OnPropertyChanged(nameof(HasLabelObjects));
            OnPropertyChanged(nameof(SelectedObject));
            OnPropertyChanged(nameof(SelectedLootEntry));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasGenericSelection));
            OnPropertyChanged(nameof(HasLootSelection));
            OnPropertyChanged(nameof(HasSpatialObjects));
            OnPropertyChanged(nameof(ShowsEmptyMap));
            OnPropertyChanged(nameof(DenseSceneNotice));
            OnPropertyChanged(nameof(DenseSceneChip));
            OnPropertyChanged(nameof(HasDenseSceneChip));
            OnPropertyChanged(nameof(HasDenseSceneNotice));
            RaiseListChanged();
        }
        if (camera)
        {
            OnPropertyChanged(nameof(CameraPreTranslateX));
            OnPropertyChanged(nameof(CameraPreTranslateY));
            OnPropertyChanged(nameof(CameraZoom));
            OnPropertyChanged(nameof(CameraRotationDegrees));
        }
        if (background)
        {
            OnPropertyChanged(nameof(BackgroundImage));
            OnPropertyChanged(nameof(HasBackgroundImage));
            OnPropertyChanged(nameof(ShowsFlatBackground));
            OnPropertyChanged(nameof(ReviewedAssetLabel));
            OnPropertyChanged(nameof(BackgroundStatus));
            OnPropertyChanged(nameof(HasBackgroundStatus));
        }
    }

    private void RaiseListChanged()
    {
        foreach (var propertyName in new[]
                 {
                     nameof(ListItems), nameof(HasListItems), nameof(ShowsEmptyList), nameof(FilteredListCount),
                     nameof(ListPageCount), nameof(ListPageNumber), nameof(ListPageLabel),
                     nameof(CanGoToPreviousPage), nameof(CanGoToNextPage), nameof(HasClusterFilter),
                     nameof(ClusterFilterLabel),
                 })
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void RaiseFloorStackChanged()
    {
        foreach (var propertyName in new[]
                 {
                     nameof(FloorLayers), nameof(IsStacked), nameof(HasFloorStack), nameof(ShowsFlatBackground),
                     nameof(StackStatus), nameof(HasStackStatus), nameof(FloorPositionLabel),
                     nameof(FloorSummaryLabel),
                     nameof(CanGoUpAFloor), nameof(CanGoDownAFloor),
                 })
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void RaiseProjectionChanged()
    {
        RaiseFloorStackChanged();
        foreach (var propertyName in new[]
                 {
                     nameof(SpatialObjects), nameof(PointMarkers), nameof(ClusterMarkers), nameof(StackMarkers), nameof(LootMarkers), nameof(LootBadges), nameof(GeometryObjects),
                     nameof(LabelObjects), nameof(HasLabelObjects),
                     nameof(SelectedObject), nameof(HasSpatialObjects),
                     nameof(ShowsEmptyMap), nameof(CanvasWidth), nameof(CanvasHeight), nameof(MapLeft), nameof(MapTop),
                     // [V2 rough package 32] A host that lays itself out around the plan's shape
                     // has to hear when the decoded artwork changes what that shape is.
                     nameof(PlanAspect),
                     nameof(MapWidth), nameof(MapHeight), nameof(MessageWidth), nameof(EmptyMessageWidth), nameof(StatusLeft),
                     nameof(StatusTop), nameof(EmptyLeft), nameof(EmptyTop), nameof(CameraPreTranslateX),
                     nameof(CameraPreTranslateY), nameof(CameraPostTranslateX), nameof(CameraPostTranslateY),
                     nameof(BackgroundStatus), nameof(HasBackgroundStatus),
                 })
        {
            OnPropertyChanged(propertyName);
        }
    }

    private MapSceneProjection CreateProjection()
    {
        // [V2 rough package 39] A stacked map is drawn a little smaller so the floors above and
        // below the one being read have somewhere to be. Without this the plan already fills the
        // card and every other plate is clipped away at the card's edge, which is a stack nobody
        // can see. The read floor still lands in exactly the rectangle this returns.
        var (above, below) = StackHeadroom();
        return new(_scene.Bounds, CanvasWidth, CanvasHeight, MapInset, _fillsViewport, _planAspect, above, below);
    }

    /// <summary>How far apart two plates are drawn, in canvas pixels.</summary>
    /// <remarks>
    /// Measured against the card rather than against the plan, because the plan's own height is
    /// what the headroom this feeds is about to change.
    /// </remarks>
    private double FloorSeparation =>
        Math.Clamp(CanvasHeight * FloorSeparationFraction, MinimumFloorSeparation, MaximumFloorSeparation);

    /// <summary>How much room the plates above and below the read floor need.</summary>
    private (double Above, double Below) StackHeadroom()
    {
        if (_scene.View.Mode != MapSceneMode.FloorStack2D)
        {
            return (0, 0);
        }

        var drawable = DrawableFloorIds();
        if (drawable.Count < 2)
        {
            return (0, 0);
        }

        var at = Math.Max(0, FindIndex(drawable, id =>
            string.Equals(id, _scene.View.SelectedFloorId, StringComparison.OrdinalIgnoreCase)));
        var separation = FloorSeparation;
        // The same room above and below, because the camera centres the plan in the card: room
        // reserved on one side only is given straight back by the centring, and the top plate
        // goes off the card again. A little unused space under a ground floor is the price.
        var reach = Math.Max(drawable.Count - 1 - at, at) * separation;
        return (reach, reach);
    }

    /// <summary>The floors, lowest first, whose artwork this host can actually produce.</summary>
    private IReadOnlyList<string> DrawableFloorIds()
    {
        var artwork = _scene.Assets
            .Where(asset => asset.Kind == MapSceneAssetKind.Floor2D && asset.FloorId is not null)
            .Select(asset => asset.FloorId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return artwork.Count == 0
            ? []
            : [.. OrderedFloorIds().Where(artwork.Contains)];
    }

    /// <summary>
    /// Adopts the decoded artwork's shape, and reprojects when it differs from what is drawn.
    /// </summary>
    /// <remarks>
    /// Returns whether anything changed, so the one caller that already raises projection
    /// notifications does not raise a second, identical round on every background resolve.
    /// </remarks>
    private bool AdoptPlanAspect()
    {
        // Only when the scene's own bounds cannot say. A host that gives the plan's real
        // rectangle (the Raid cockpit does, from MapPlanProjection) has already described the
        // map's shape in the units every object on it is placed in, and that rectangle is the
        // one the artwork is stretched across; taking the shape from the decoded bitmap instead
        // would move the artwork off the markers whenever the two disagreed. A square box is the
        // synthetic case — the map gallery, a fixture scene — where the bitmap is all there is.
        var bounds = _scene.Bounds;
        var square = double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
            bounds.Width > 0 && bounds.Height > 0 && Math.Abs(bounds.Width - bounds.Height) < 1e-9;
        var size = square ? BackgroundImage?.Size : null;
        var aspect = size is { Width: > 0, Height: > 0 } bitmap ? bitmap.Width / bitmap.Height : double.NaN;
        var unchanged = double.IsNaN(aspect) && double.IsNaN(_planAspect) ||
            double.IsFinite(aspect) && double.IsFinite(_planAspect) && Math.Abs(aspect - _planAspect) < 0.0001;
        if (unchanged)
        {
            return false;
        }

        _planAspect = aspect;
        _projection = CreateProjection();
        RebuildProjectedObjects();
        return true;
    }

    private static bool Equivalent<T>(IReadOnlyList<T> left, IReadOnlyList<T> right) =>
        left.Count == right.Count && left.SequenceEqual(right);

    /// <summary>
    /// Whether two scenes carry objects that would be drawn identically.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Equivalent{T}"/>: scene objects hold their floor ids and points in arrays a
    /// record compares by reference, so a scene rebuilt from unchanged inputs always looked
    /// different and every present recreated every marker, label and line on the plan (measured:
    /// about 13 MB of allocation and a half-second of UI time per present on Customs).
    /// </remarks>
    private static bool SameObjects(IReadOnlyList<MapSceneObject> left, IReadOnlyList<MapSceneObject> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!left[index].HasSameDisplayAs(right[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the host now wants any drawn marker or line to look different than it was built to.
    /// </summary>
    /// <remarks>
    /// A style is asked for when a view model is made, not when it is drawn, so an object that is
    /// unchanged but whose owner has been given a new colour (a squadmate joining reassigns
    /// them all) would otherwise keep its old one for as long as it went on being reused.
    /// </remarks>
    private bool StylesChanged()
    {
        if (_styleResolver is null)
        {
            return false;
        }

        foreach (var marker in SpatialObjects)
        {
            if (marker.SceneObject is { } sceneObject && marker.Style != _styleResolver(sceneObject))
            {
                return true;
            }
        }

        foreach (var line in GeometryObjects)
        {
            if (line.Style != _styleResolver(line.SceneObject))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Equivalent(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right,
        StringComparer comparer) => left.Count == right.Count && left.SequenceEqual(right, comparer);

    private static int FindIndex<T>(IReadOnlyList<T> values, Func<T, bool> predicate)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (predicate(values[index])) return index;
        }

        return -1;
    }

    private sealed record MapSceneRendererChange(
        MapSceneViewChangeKind Kind,
        MapSceneMode? Mode = null,
        string? FloorId = null,
        MapSceneLayerId? LayerId = null,
        bool? IsVisible = null,
        MapSceneCamera? Camera = null);
}

public sealed class MapSceneRendererModeViewModel
{
    public MapSceneRendererModeViewModel(
        MapSceneMode mode,
        string label,
        bool isSelected,
        bool isAvailable,
        string unavailableReason,
        Action select,
        string? shortLabel = null)
    {
        Mode = mode;
        Label = label;
        ShortLabel = shortLabel ?? label;
        IsSelected = isSelected;
        IsAvailable = isAvailable;
        UnavailableReason = unavailableReason;
        SelectCommand = new DelegateCommand(select ?? throw new ArgumentNullException(nameof(select)));
    }

    public MapSceneMode Mode { get; }
    public string Label { get; }

    /// <summary>[#838] "2D", "Stack", "3D": what a compact strip shows, with <see cref="Label"/> as its tooltip.</summary>
    public string ShortLabel { get; }
    public bool IsSelected { get; }
    public bool IsAvailable { get; }
    public string UnavailableReason { get; }
    public string AutomationId => $"v2-map-mode-{Mode.ToString().ToLowerInvariant()}";
    public ICommand SelectCommand { get; }
}

public sealed class MapSceneRendererFloorViewModel
{
    private readonly string? _name;

    public MapSceneRendererFloorViewModel(string id, bool isSelected, Action select, string? name = null)
    {
        Id = id;
        IsSelected = isSelected;
        _name = string.IsNullOrWhiteSpace(name) ? null : name;
        SelectCommand = new DelegateCommand(select ?? throw new ArgumentNullException(nameof(select)));
    }

    public string Id { get; }
    /// <summary>The catalog's own floor name where the host knows one ("2nd floor"), and
    /// otherwise the id made readable, e.g. "ground" → "Ground".</summary>
    public string Name => _name ?? MapRendererToken.Humanize(Id);
    public bool IsSelected { get; }
    public string AutomationId => $"v2-map-floor-{MapRendererToken.From(Id)}";
    public ICommand SelectCommand { get; }
}

/// <summary>
/// One floor's artwork in the stacked view: the plan rectangle, where it sits, how solid it is.
/// </summary>
/// <remarks>
/// [V2 rough package 39] <see cref="Left"/>, <see cref="Top"/>, <see cref="Width"/> and
/// <see cref="Height"/> are the projection's own plan rectangle, identical on every floor —
/// which is the contract #413 established and this keeps: whatever rectangle the artwork draws
/// into is the rectangle objects project into. Only <see cref="Translate"/> differs, and it is
/// zero for the floor being read, so the markers sit on their own floor's picture.
/// </remarks>
public sealed class MapSceneRendererFloorLayerViewModel
{
    public MapSceneRendererFloorLayerViewModel(
        string floorId,
        string name,
        IImage image,
        double offset,
        double opacity,
        bool isSelected,
        MapSceneProjection projection,
        Action select)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(floorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(select);
        FloorId = floorId;
        Name = name;
        Image = image ?? throw new ArgumentNullException(nameof(image));
        Offset = offset;
        Opacity = opacity;
        IsSelected = isSelected;
        Left = projection.MapLeft;
        Top = projection.MapTop;
        Width = projection.MapWidth;
        Height = projection.MapHeight;
        SelectCommand = new DelegateCommand(select);
    }

    public string FloorId { get; }

    public string Name { get; }

    public IImage Image { get; }

    /// <summary>How far up the card this plate sits, measured from the floor being read.</summary>
    public double Offset { get; }

    /// <summary>Negative, because up the screen is a smaller Y.</summary>
    public double Translate => -Offset;

    /// <summary>
    /// Where this plate is drawn, as a transform rather than as Canvas.Left/Top.
    /// </summary>
    /// <remarks>
    /// Avalonia leaves the generated ContentPresenter at the Canvas origin, so an attached
    /// Canvas.Left on the templated control does nothing — the same reason every marker layer in
    /// this renderer positions itself with a transform inside its own template.
    /// </remarks>
    public double TranslateX => Left;

    public double TranslateY => Top + Translate;

    public double Opacity { get; }

    /// <summary>The name stays readable on a quieted plate; a stack you cannot label is mush.</summary>
    public double NameOpacity => IsSelected ? 1 : 0.8;

    public bool IsSelected { get; }

    /// <summary>Every sheet has an edge; the one being read has a thicker one.</summary>
    public Thickness BorderThickness => IsSelected ? new Thickness(2) : new Thickness(1);

    public double Left { get; }

    public double Top { get; }

    public double Width { get; }

    public double Height { get; }

    public string AutomationId => $"v2-map-floor-plate-{MapRendererToken.From(FloorId)}";

    public ICommand SelectCommand { get; }
}

public sealed class MapSceneRendererLayerViewModel
{
    public MapSceneRendererLayerViewModel(
        MapSceneLayer layer,
        bool isVisible,
        MapSceneRendererPresentation presentation,
        Action<bool> setVisible,
        // [V2 rough package 39] How many objects this layer would draw if it were on. Defaulted
        // so the hosts that build a layer row without a scene behind it (the gallery, a test)
        // keep working; every real scene passes the real count.
        int count = 0,
        string? status = null)
    {
        Layer = layer ?? throw new ArgumentNullException(nameof(layer));
        IsVisible = isVisible;
        Count = Math.Max(0, count);
        ToggleLabel = presentation.Format(isVisible ? "Map.Layer.Hide" : "Map.Layer.Show", layer.Name);
        // The switch itself carries the count, so "Extracts" reads "Extracts 12" and a player
        // can see what turning it on would give them without turning it on.
        var countedLabel = Count == 0
            ? presentation.Format("Map.Layer.Empty", layer.Name)
            : presentation.Format("Map.Layer.Count", layer.Name, presentation.Number(Count));
        Label = string.IsNullOrWhiteSpace(status) ? countedLabel : $"{countedLabel} · {status}";
        StateLabel = Count == 0
            ? presentation.Get("Map.Layer.NothingToShow")
            : presentation.Get(isVisible ? "Map.Layer.Visible" : "Map.Layer.Hidden");
        // A layer with nothing on it says so rather than switching the map to empty. The view
        // disables the switch too; this is the guard that does not depend on it doing so.
        ToggleCommand = new DelegateCommand(() =>
        {
            if (CanToggle)
            {
                setVisible(!IsVisible);
            }
        });
    }

    public MapSceneLayer Layer { get; }
    public string Name => Layer.Name;

    /// <summary>How many objects this layer holds, whether or not it is currently drawn.</summary>
    public int Count { get; }

    /// <summary>The layer's name with its count, which is what the switch shows.</summary>
    public string Label { get; }

    /// <summary>True when the layer would draw nothing, so the switch says so and stays off.</summary>
    public bool HasNothingToShow => Count == 0;

    /// <summary>Whether the switch does anything: every layer but an empty one that is already off.</summary>
    /// <remarks>
    /// The rule is "do not switch the map to empty", which is about turning a layer on. Applied
    /// to the switch as a whole it also locked on a layer that was already showing: with the
    /// loot preset applied and loot-spawn data unavailable, High-value loot was on, empty and
    /// impossible to turn off. That is a stuck control for a player, and for UI Automation it is
    /// an exception, because Toggle on a disabled control throws rather than doing nothing,
    /// which failed the Windows gallery's offline loot scenario with "no window".
    /// </remarks>
    public bool CanToggle => IsVisible || Count > 0;

    public bool IsVisible { get; }
    public string ToggleLabel { get; }
    public string StateLabel { get; }
    public string AutomationId => $"v2-map-layer-{MapRendererToken.From(Layer.Id.Value)}";
    public ICommand ToggleCommand { get; }
}

public sealed class MapSceneRendererObjectViewModel : BindableViewModel
{
    private bool _isSelected;
    private double _markerInverseZoom;
    private double _cameraZoom = 1;
    private double _stackZoom;
    private bool _isStackBadge;
    private readonly string _markerGlyph;
    private double[]? _hiddenLootZooms;
    private double _markerUprightDegrees;
    private double _coneDegrees;

    private MapSceneRendererObjectViewModel(
        MapSceneObject? sceneObject,
        string key,
        string label,
        string automationName,
        string? detail,
        string kindLabel,
        string truthLabel,
        string factionLabel,
        string offerLabel,
        bool hasOfferStatus,
        string evidenceLabel,
        string estimateLabel,
        bool isSelected,
        double anchorLeft,
        double anchorTop,
        double markerInverseZoom,
        double markerUprightDegrees,
        string markerGlyph,
        MapSceneMarkerIcon icon,
        string truthGlyph,
        string factionGlyph,
        string offerGlyph,
        bool isCluster,
        Action select,
        double? headingDegrees = null,
        double cameraBearingDegrees = 0,
        MapSceneObjectStyle? style = null,
        double pinOffsetX = 0,
        double pinOffsetY = 0,
        bool isNearRightEdge = false,
        bool isNearBottomEdge = false)
    {
        SceneObject = sceneObject;
        Key = key;
        Label = label;
        AutomationName = automationName;
        Detail = detail;
        KindLabel = kindLabel;
        TruthLabel = truthLabel;
        FactionLabel = factionLabel;
        OfferedLabel = offerLabel;
        HasOfferStatus = hasOfferStatus;
        EvidenceLabel = evidenceLabel;
        EstimateLabel = estimateLabel;
        _isSelected = isSelected;
        AnchorLeft = anchorLeft;
        AnchorTop = anchorTop;
        _cameraZoom = markerInverseZoom > 0 && double.IsFinite(markerInverseZoom) ? 1 / markerInverseZoom : 1;
        _markerUprightDegrees = markerUprightDegrees;
        _markerGlyph = markerGlyph;
        Icon = icon;
        TruthGlyph = truthGlyph;
        FactionGlyph = factionGlyph;
        OfferGlyph = offerGlyph;
        IsCluster = isCluster;
        HeadingDegrees = headingDegrees;
        _coneDegrees = ConeFor(headingDegrees, cameraBearingDegrees);
        Style = style;
        PinOffsetX = pinOffsetX;
        PinOffsetY = pinOffsetY;
        IsNearRightEdge = isNearRightEdge;
        IsNearBottomEdge = isNearBottomEdge;
        SelectCommand = new DelegateCommand(select ?? throw new ArgumentNullException(nameof(select)));
        _markerInverseZoom = MarkerScale / _cameraZoom;
    }

    /// <summary>
    /// Where the cone has to point on screen for a marker that is kept upright.
    /// </summary>
    /// <remarks>
    /// The plan itself is drawn turned by the camera's bearing, and the marker is turned back by
    /// the same amount so its icon stays the right way up. A facing recorded in plan degrees
    /// therefore has to be turned by the bearing here, or the cone points where the player was
    /// looking before the map was rotated.
    /// </remarks>
    /// <summary>
    /// [#453] Whether <paramref name="other"/> would draw exactly what this does, so this instance
    /// (and the control the view made for it) can stand in for it. Every constructor input counts.
    /// </summary>
    internal bool DrawsSameAs(MapSceneRendererObjectViewModel other) =>
        !IsCluster && !other.IsCluster &&
        SceneObject is { } mine && mine.HasSameDisplayAs(other.SceneObject) &&
        Key == other.Key && Label == other.Label && AutomationName == other.AutomationName &&
        Detail == other.Detail && KindLabel == other.KindLabel && TruthLabel == other.TruthLabel &&
        FactionLabel == other.FactionLabel && OfferedLabel == other.OfferedLabel &&
        HasOfferStatus == other.HasOfferStatus && EvidenceLabel == other.EvidenceLabel &&
        EstimateLabel == other.EstimateLabel && IsSelected == other.IsSelected &&
        AnchorLeft.Equals(other.AnchorLeft) && AnchorTop.Equals(other.AnchorTop) &&
        _markerInverseZoom.Equals(other._markerInverseZoom) && _markerUprightDegrees.Equals(other._markerUprightDegrees) &&
        MarkerGlyph == other.MarkerGlyph && Icon == other.Icon && TruthGlyph == other.TruthGlyph &&
        FactionGlyph == other.FactionGlyph && OfferGlyph == other.OfferGlyph &&
        Nullable.Equals(HeadingDegrees, other.HeadingDegrees) && _coneDegrees.Equals(other._coneDegrees) &&
        Nullable.Equals(Style, other.Style) && PinOffsetX.Equals(other.PinOffsetX) && PinOffsetY.Equals(other.PinOffsetY) &&
        IsNearRightEdge == other.IsNearRightEdge && IsNearBottomEdge == other.IsNearBottomEdge &&
        IsRankedLoot == other.IsRankedLoot && LootRevealZoom.Equals(other.LootRevealZoom) &&
        IsLootExceptional == other.IsLootExceptional && IsLootHigh == other.IsLootHigh;

    /// <summary>A "+N" loot badge over the same spawns, drawn in the same place at the same zoom.</summary>
    internal bool DrawsSameBadgeAs(MapSceneRendererObjectViewModel other) =>
        _hiddenLootZooms is { } mine && other._hiddenLootZooms is { } theirs &&
        Key == other.Key && Label == other.Label &&
        AnchorLeft.Equals(other.AnchorLeft) && AnchorTop.Equals(other.AnchorTop) &&
        _cameraZoom.Equals(other._cameraZoom) && _markerInverseZoom.Equals(other._markerInverseZoom) &&
        _markerUprightDegrees.Equals(other._markerUprightDegrees) &&
        mine.AsSpan().SequenceEqual(theirs);

    internal static double ConeFor(double? headingDegrees, double cameraBearingDegrees) =>
        headingDegrees is { } heading && double.IsFinite(heading)
            ? ((heading - cameraBearingDegrees) % 360 + 360) % 360
            : 0;

    public MapSceneObject? SceneObject { get; }
    public MapSceneObjectId? ObjectId => SceneObject?.Id;
    public string Key { get; }
    public string Label { get; }
    public string AutomationName { get; }
    public string? Detail { get; }
    public string KindLabel { get; }
    public string TruthLabel { get; }
    public string FactionLabel { get; }
    public string OfferedLabel { get; }
    public bool HasOfferStatus { get; }
    public string EvidenceLabel { get; }
    public string EstimateLabel { get; }
    public bool HasEstimate => !string.IsNullOrWhiteSpace(EstimateLabel);
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    /// <summary>
    /// What hovering the marker says: the name, plus for a ping or waypoint who sees it and how
    /// long it lasts (#289). Every other kind keeps its name alone, as before.
    /// </summary>
    public string HoverText => SceneObject?.Kind is MapSceneObjectKind.Ping or MapSceneObjectKind.Waypoint && HasDetail
        ? $"{Label} · {Detail}"
        : Label;
    public bool IsSelected => _isSelected;
    public bool IsCluster { get; }
    public bool IsOffered => SceneObject?.OfferState == MapSceneOfferState.Offered;
    public bool IsKnownNotOffered => SceneObject?.OfferState == MapSceneOfferState.NotOffered;
    public bool IsOfferUnknown => HasOfferStatus && SceneObject?.OfferState == MapSceneOfferState.Unknown;
    public bool IsHistorical => SceneObject?.Truth == MapSceneTruthKind.HistoricalEstimate;
    public bool IsLocalObserved => SceneObject?.Truth == MapSceneTruthKind.LocalLastKnown;
    public bool IsTeamObserved => SceneObject?.Truth == MapSceneTruthKind.TeamSharedLastKnown;
    public bool IsPotential => SceneObject?.Truth == MapSceneTruthKind.PotentialSpawn;
    public bool IsPmc => SceneObject?.Faction == MapFeatureFaction.Pmc;
    public bool IsScav => SceneObject?.Faction == MapFeatureFaction.Scav;
    public bool IsSharedFaction => SceneObject?.Faction == MapFeatureFaction.Shared;
    public double AnchorLeft { get; }
    public double AnchorTop { get; }
    public double MarkerInverseZoom => _markerInverseZoom;
    public double MarkerUprightDegrees => _markerUprightDegrees;

    /// <summary>The recorded facing in plan degrees, when this marker is somebody rather than a place.</summary>
    public double? HeadingDegrees { get; }

    public bool HasHeading => HeadingDegrees is not null;

    /// <summary>Where to point the facing cone on screen; see <see cref="ConeFor"/>.</summary>
    public double ConeDegrees => _coneDegrees;

    /// <summary>
    /// The V1 player/squadmate cone, drawn inside the 44px marker box.
    /// </summary>
    /// <remarks>
    /// The apex is at the centre of that box on purpose: it is the point the dot is drawn on and
    /// the point the heading turns the cone about. The two only agree while the Path is given the
    /// whole box — see MapPersonConeTests, and the note beside the Path in the view.
    /// </remarks>
    public const string PersonConeGeometry = "M 22,22 L 8,2 A 18,18 0 0 1 36,2 Z";

    public string ConeGeometry => PersonConeGeometry;

    /// <summary>A host-chosen style for this marker (a squadmate's own colour), where there is one.</summary>
    public MapSceneObjectStyle? Style { get; }

    public string? ColorHint => Style?.Color;

    public bool HasColorHint => ColorHint is not null;

    /// <summary>[Issue 573] A host-dimmed mark (a co-op extract at "Dim") draws faded; everything else is opaque.</summary>
    public double MarkerOpacity => Style?.Opacity ?? 1.0;

    /// <summary>
    /// [Issue 581] A squadmate's own colour, as a brush their dot and facing cone can be filled
    /// with — everything else on the map keeps its themed colour from the styles below, so this
    /// is null wherever the host gave no opinion.
    /// </summary>
    public IBrush? ColorHintBrush => ColorHint is { } hex && Color.TryParse(hex, out var color)
        ? new SolidColorBrush(color)
        : null;

    /// <summary>The number or letter on the mark; a loot count badge says how many it still holds.</summary>
    public string MarkerGlyph => _hiddenLootZooms is null ? _markerGlyph : $"+{HiddenLootCount}";

    /// <summary>Which drawn icon this marker shows. Never drawn when <see cref="HasMarkerNumber"/>.</summary>
    public MapSceneMarkerIcon Icon { get; }

    /// <summary>
    /// A numbered waypoint or plan step draws its number instead of an icon, so the pin on the
    /// plan and its row in the marks list are obviously the same thing.
    /// </summary>
    public bool HasMarkerNumber => MarkerGlyph.Length is > 0 and <= 3 && MarkerGlyph.All(char.IsAsciiDigit);
    public bool ShowsMarkerIcon => !HasMarkerNumber;
    public bool IsExtractIcon => ShowsMarkerIcon && Icon == MapSceneMarkerIcon.Extract;
    public bool IsTransitIcon => ShowsMarkerIcon && Icon == MapSceneMarkerIcon.Transit;
    /// <summary>
    /// [Issue 594] "Clicking an extract or transit doesn't tell me what one it is." The selected
    /// one keeps its name beside the icon, readable without hovering, in the same label style a
    /// place name uses; hovering (ToolTip.Tip, bound to Label) is what says it for every other one.
    /// </summary>
    public bool ShowsSelectedName => IsSelected && (IsExtractIcon || IsTransitIcon);
    /// <summary>
    /// Extract and transit selections keep their names beside the marker. Numbered switch steps
    /// keep only the always-visible number; their existing hover tooltip carries the name so two
    /// nearby controls cannot cover each other with wide persistent plates.
    /// </summary>
    public bool ShowsPersistentName => ShowsSelectedName;

    /// <summary>
    /// [Issue 594] "RUAF Roadblock" read as "RU…" near the card's right edge, clipped by
    /// PlanViewport. True within <see cref="EdgeFlipMargin"/> canvas pixels of the plan's own
    /// right/bottom edge, so the selected name label can flip to the other side of its icon
    /// instead of overflowing the card. Computed once, from the same projected point the icon
    /// itself is anchored to — see MapSceneRendererObjectViewModel.ForObject.
    /// </summary>
    public bool IsNearRightEdge { get; }

    public bool IsNearBottomEdge { get; }

    /// <summary>[Issue 594] Whether this is the kind of marker the Raid page's "Selected" card is for.</summary>
    public bool IsExtractOrTransit => IsExtractIcon || IsTransitIcon;
    public bool IsObjectiveIcon => ShowsMarkerIcon && Icon == MapSceneMarkerIcon.Objective;
    public bool IsWaypointIcon => ShowsMarkerIcon && Icon == MapSceneMarkerIcon.Waypoint;
    public bool IsPingIcon => ShowsMarkerIcon && Icon == MapSceneMarkerIcon.Ping;
    public bool IsSpawnIcon => ShowsMarkerIcon && Icon == MapSceneMarkerIcon.Spawn;
    public bool IsLootIcon => ShowsMarkerIcon && Icon == MapSceneMarkerIcon.Loot;
    public bool IsHazardIcon => ShowsMarkerIcon && Icon == MapSceneMarkerIcon.Hazard;
    public bool IsLockIcon => ShowsMarkerIcon && Icon == MapSceneMarkerIcon.Lock;
    public bool IsRouteIcon => ShowsMarkerIcon && Icon == MapSceneMarkerIcon.Route;
    public bool IsRiskIcon => ShowsMarkerIcon && Icon == MapSceneMarkerIcon.Risk;
    public bool IsPlayerIcon => ShowsMarkerIcon && Icon == MapSceneMarkerIcon.Player;
    public bool IsTeammateIcon => ShowsMarkerIcon && Icon == MapSceneMarkerIcon.Teammate;

    /// <summary>
    /// [Issue 581] A squadmate's own colour pairs with their initial on the dot itself, so which
    /// teammate is which never depends on colour alone (colour-vision modes).
    /// </summary>
    public string PersonInitial => IsTeammateIcon && SceneObject?.Label is { Length: > 0 } label
        ? label[..1].ToUpperInvariant()
        : string.Empty;
    public bool IsGenericIcon => ShowsMarkerIcon && Icon == MapSceneMarkerIcon.Generic;
    /// <summary>A person marker is a dot with a facing cone, not one of the drawn glyphs.</summary>
    public bool IsPersonIcon => IsPlayerIcon || IsTeammateIcon;
    public bool ShowsGlyphIcon => ShowsMarkerIcon && !IsPersonIcon;

    /// <summary>
    /// The older boxed chip: every kind except a person (a dot and cone), a pin (a waypoint or
    /// quest objective) and a ping (a pulse) — each of those three now draws its own visual.
    /// </summary>
    public bool ShowsLegacyChip => !IsPersonIcon && !IsPinMark && !IsPingMark;
    public string TruthGlyph { get; }
    public bool HasTruthGlyph => !string.IsNullOrWhiteSpace(TruthGlyph);
    public string FactionGlyph { get; }
    public bool HasFactionGlyph => !string.IsNullOrWhiteSpace(FactionGlyph);
    public string OfferGlyph { get; }
    public bool HasOfferGlyph => !string.IsNullOrWhiteSpace(OfferGlyph);

    // [Issue 508] A waypoint and a quest objective are pins now, not a chip that reads a number
    // when it has one and an icon when it does not. These are unconditional on the kind alone —
    // unlike IsWaypointIcon/IsObjectiveIcon above, which only ever apply to the *unnumbered* case
    // the old chip drew an icon for, and would be false for the common numbered waypoint.
    public bool IsWaypointMark => Icon == MapSceneMarkerIcon.Waypoint;
    public bool IsObjectiveMark => Icon == MapSceneMarkerIcon.Objective;
    public bool IsPinMark => IsWaypointMark || IsObjectiveMark;

    /// <summary>[#307] A route step number riding on this pin's corner; empty when there is none.</summary>
    public string PinBadge => IsPinMark ? Style?.Badge ?? string.Empty : string.Empty;
    public bool HasPinBadge => PinBadge.Length > 0;

    /// <summary>The route step badge's diameter in screen DIPs, whatever the zoom.</summary>
    internal const double PinBadgeExtent = 22;

    /// <summary>
    /// [#307] Cancels the mark's own zoom scale, so a step number reads at the same size on every
    /// stop. Scaled with its pin, the number on a stop of its own came out at about 7px at fit zoom
    /// on a 1080p screen, unreadable, while one on an objective's corner read at a different size.
    /// </summary>
    public double PinBadgeScale => 1 / MarkerScale;

    /// <summary>
    /// [#307] Where the step badge is centred inside the pin: on the head of a route stop's own
    /// pin, where its number would have been, or on the top-right corner of an objective's shield,
    /// so the objective's letter stays readable beside it.
    /// </summary>
    public Thickness PinBadgeMargin
    {
        get
        {
            // [#797] Beyond the shield's corner, not on it: the badge keeps its 22 screen DIPs
            // while the shield shrinks at fit zoom, and on Windows a badge centred on the corner
            // covered the whole letter. Its radius in pin units grows as the pin shrinks, so the
            // centre moves out with it, leaving only a sliver over the corner.
            var outward = PinBadgeExtent / 2 / MarkerScale * 0.6;
            var (x, y) = IsWaypointMark ? (PinWidth / 2, PinWidth / 2) : (PinWidth + outward, -outward);
            return new(x - (PinBadgeExtent / 2), y - (PinBadgeExtent / 2), 0, 0);
        }
    }

    /// <summary>A ping is a transient pulse, drawn at its own point, never a pin.</summary>
    public bool IsPingMark => Icon == MapSceneMarkerIcon.Ping;

    public bool IsSwitchMark => Icon == MapSceneMarkerIcon.Switch;

    public double ChipExtent => IsSwitchMark ? 20 : 26;

    /// <summary>The letter or number written on the pin's head; empty for an unnumbered, unnamed waypoint.</summary>
    public string PinLabel => MarkerGlyph;
    public bool HasPinLabel => PinLabel.Length > 0;

    /// <summary>The letter shows, or a completed objective's check does — never both at once.</summary>
    public bool ShowsPinLetter => HasPinLabel && !IsCompletedObjective && !(IsWaypointMark && HasPinBadge);

    /// <summary>
    /// Issue 379: an objective the catalog gives no place for, that the player put there
    /// themselves, is drawn with a visible "placed by you" distinction rather than as if it were
    /// the quest data's own.
    /// </summary>
    public bool IsUserPlacedMark => IsObjectiveMark && SceneObject?.Truth == MapSceneTruthKind.UserAuthored;

    /// <summary>A quest objective still on the map after its own step is done: dimmed, with a check.</summary>
    public bool IsCompletedObjective => IsObjectiveMark && (SceneObject?.IsCompleted ?? false);

    /// <summary>
    /// The pin's own top-left, in the 44x44 marker box's local coordinates, including the small
    /// nudge <see cref="MapMarkerOverlapLayout"/> gives a pin that would otherwise land exactly on
    /// another one. See <see cref="TarkovCompanion.App.Views.V2.MapRenderer.MapPinGeometry"/> for
    /// why this keeps the pin's tip on the anchor at every zoom and bearing.
    /// </summary>
    public double PinLeft => TarkovCompanion.App.Views.V2.MapRenderer.MapPinGeometry.TopLeftFor(MapSceneRendererViewModel.MarkerExtent, PinWidth, PinHeight).Left + PinOffsetX;
    public double PinTop => TarkovCompanion.App.Views.V2.MapRenderer.MapPinGeometry.TopLeftFor(MapSceneRendererViewModel.MarkerExtent, PinWidth, PinHeight).Top + PinOffsetY;

    /// <summary>
    /// [Issue 573] A quest objective's shield draws at <see cref="QuestPinShrink"/> of today's
    /// size by default — six of them used to bury a whole building at fit zoom — and back at full
    /// size once selected, so the one the player is looking at is still easy to find and to hit. A
    /// waypoint's round pin is unaffected: the report was about quest markers specifically.
    /// </summary>
    public double PinWidth => TarkovCompanion.App.Views.V2.MapRenderer.MapPinGeometry.Width * PinScale;
    public double PinHeight => TarkovCompanion.App.Views.V2.MapRenderer.MapPinGeometry.Height * PinScale;

    private double PinScale => IsObjectiveMark && !IsSelected
        ? IsOutlinedObjective ? SquadPinShrink : QuestPinShrink
        : 1.0;

    /// <summary>[#780] A squadmate's objective: drawn outlined in their colour, not filled.</summary>
    public bool IsOutlinedObjective => IsObjectiveMark && Style?.Outlined == true && ColorHintBrush is not null;

    /// <summary>[#780] The player's own objective: the filled shield (everything not outlined).</summary>
    public bool IsFilledObjective => IsObjectiveMark && !IsOutlinedObjective;

    /// <summary>[#780] The own letter shows on a filled shield; a squadmate's in their colour.</summary>
    public bool ShowsFilledPinLetter => ShowsPinLetter && !IsOutlinedObjective;

    public bool ShowsOutlinedPinLetter => ShowsPinLetter && IsOutlinedObjective;

    /// <summary>[#780] Half size: quieter than the player's own two-thirds pins.</summary>
    internal const double SquadPinShrink = 0.5;

    /// <summary>Two-thirds, the amount issue 573 asked a quest pin (and an extract badge) to shrink to.</summary>
    internal const double QuestPinShrink = 2.0 / 3.0;

    /// <summary>Where the letter sits inside the shield, scaled with <see cref="PinScale"/> so it stays centred at either size.</summary>
    public Thickness PinLabelMargin => new(0, 7 * PinScale, 0, 0);

    /// <summary>Where the completed check sits inside the shield, scaled the same way.</summary>
    public Thickness PinCheckMargin => new(0, 8 * PinScale, 0, 0);

    /// <summary>
    /// The letter's own size: kept a size above a strict two-thirds scale-down (which would make
    /// it 8pt) so the letter a shrunk shield carries is still legible at 1920x1080.
    /// </summary>
    public double PinLabelFontSize => PinScale < 1.0 ? 10 : 12;

    /// <summary>
    /// The overlap nudge <see cref="MapMarkerOverlapLayout"/> gave this marker, in the same box
    /// units as <see cref="PinLeft"/>/<see cref="PinTop"/>; (0, 0) for a marker nothing else lands
    /// on. Applied to the pulse (a ping) too, so a ping dropped on a pin still shows both.
    /// </summary>
    public double PinOffsetX { get; }
    public double PinOffsetY { get; }

    public string AutomationId => $"v2-map-object-{MapRendererToken.From(Key)}";
    public ICommand SelectCommand { get; }

    public void SetSelected(bool selected)
    {
        if (SetProperty(ref _isSelected, selected, nameof(IsSelected)))
        {
            OnPropertyChanged(nameof(ZOrder));
            OnPropertyChanged(nameof(ShowsSelectedName));
            OnPropertyChanged(nameof(ShowsPersistentName));
            OnPropertyChanged(nameof(IsShownOnPlan));
            // The selected mark is drawn full size whatever the zoom, so it and its name read.
            if (SetProperty(ref _markerInverseZoom, MarkerScale / _cameraZoom, nameof(MarkerInverseZoom)))
            {
                OnPropertyChanged(nameof(MarkerScale));
                OnPropertyChanged(nameof(HitExtent));
                OnPropertyChanged(nameof(HitCornerRadius));
                OnPropertyChanged(nameof(PinHeadHitMargin));
                OnPropertyChanged(nameof(PinBadgeScale));
                OnPropertyChanged(nameof(PinBadgeMargin));
            }
        }
    }

    /// <summary>
    /// [#573] Drawn smaller with the plan fitted and full size zoomed in (<see cref="MapMarkerScale"/>).
    /// A person and a count badge keep their size. Hand-authored waypoints and pings scale with
    /// every other mark so the player's own dense spot does not bury the map at fit zoom.
    /// </summary>
    public double MarkerScale => IsCluster || IsPersonIcon || _isSelected ? 1 : MapMarkerScale.For(_cameraZoom);

    /// <summary>The mark's hit box in its own DIPs, so it is never under 32 screen pixels however small it is drawn.</summary>
    public double HitExtent => MapMarkerScale.HitExtent(MarkerScale);

    /// <summary>Round, so the empty corners of the marker's box still do not take the pointer.</summary>
    public CornerRadius HitCornerRadius => new(HitExtent / 2);

    /// <summary>Centres a pin's round hit area on its head, which is drawn above the pin's tip.</summary>
    public Thickness PinHeadHitMargin => new(0, (PinWidth - HitExtent) / 2, 0, 0);

    /// <summary>
    /// Drawn on the plan now: a mark in a dense spot hides under its count badge while the plan is
    /// zoomed out (unless it is the selected one), and a badge shows only then.
    /// </summary>
    public bool IsShownOnPlan => _hiddenLootZooms is not null
        ? HiddenLootCount > 0
        : _isStackBadge
            ? _cameraZoom < _stackZoom
            : IsRankedLoot
                ? _cameraZoom >= LootRevealZoom || _isSelected
                : !(_stackZoom > 0 && _cameraZoom < _stackZoom && !_isSelected);

    /// <summary>[#573] A potential loot spawn drawn by value rank (MapLootRanking), under the place names.</summary>
    public bool IsRankedLoot { get; private set; }

    /// <summary>The zoom from which this spawn is drawn; 0 for the most valuable ones.</summary>
    public double LootRevealZoom { get; private set; }

    public bool IsLootExceptional { get; private set; }

    public bool IsLootHigh { get; private set; }

    /// <summary>How many of a badge's spawns are not drawn yet at this zoom.</summary>
    public int HiddenLootCount => _hiddenLootZooms?.Count(zoom => zoom > _cameraZoom) ?? 0;

    public bool IsLootBadge => _hiddenLootZooms is not null;

    internal void RankAsLoot(double revealZoom, LootSpawnValueTier tier)
    {
        IsRankedLoot = true;
        LootRevealZoom = revealZoom;
        IsLootExceptional = tier == LootSpawnValueTier.Exceptional;
        IsLootHigh = tier == LootSpawnValueTier.High;
    }

    internal void CountHiddenLoot(double[] revealZooms)
    {
        _hiddenLootZooms = revealZooms;
    }

    internal void JoinStack(double stackZoom)
    {
        _stackZoom = stackZoom;
        OnPropertyChanged(nameof(IsShownOnPlan));
    }

    internal void ShowAsStackBelow(double stackZoom)
    {
        _isStackBadge = true;
        _stackZoom = stackZoom;
        OnPropertyChanged(nameof(IsShownOnPlan));
    }

    /// <summary>
    /// Where the marker sits in the stack of markers: the selected one on top, so two objectives
    /// standing on the same helicopter do not leave the one that was picked underneath the other.
    /// </summary>
    public int ZOrder => IsSwitchMark && HasMarkerNumber ? 20 : _isSelected ? 10 : HasPinBadge ? 5 : 0;

    public void UpdateCamera(MapSceneCamera camera)
    {
        var wasShown = IsShownOnPlan;
        _cameraZoom = camera.Zoom;
        if (SetProperty(ref _markerInverseZoom, MarkerScale / camera.Zoom, nameof(MarkerInverseZoom)))
        {
            OnPropertyChanged(nameof(MarkerScale));
            OnPropertyChanged(nameof(HitExtent));
            OnPropertyChanged(nameof(HitCornerRadius));
            OnPropertyChanged(nameof(PinHeadHitMargin));
            OnPropertyChanged(nameof(PinBadgeScale));
            OnPropertyChanged(nameof(PinBadgeMargin));
        }

        if (wasShown != IsShownOnPlan)
        {
            OnPropertyChanged(nameof(IsShownOnPlan));
        }

        if (_hiddenLootZooms is not null)
        {
            OnPropertyChanged(nameof(HiddenLootCount));
            OnPropertyChanged(nameof(MarkerGlyph));
        }

        SetProperty(ref _markerUprightDegrees, camera.BearingDegrees, nameof(MarkerUprightDegrees));
        SetProperty(ref _coneDegrees, ConeFor(HeadingDegrees, camera.BearingDegrees), nameof(ConeDegrees));
    }

    /// <summary>[Issue 594] Canvas pixels from the right/bottom edge inside which the selected
    /// name label flips to the other side of its icon rather than overflow the card. Roughly the
    /// label's own half-width/height, so the flip happens before any part of it would reach the
    /// edge.</summary>
    private const double EdgeFlipMargin = 110;

    public static MapSceneRendererObjectViewModel ForObject(
        MapSceneObject sceneObject,
        MapSceneProjection projection,
        MapSceneCamera camera,
        MapSceneRendererPresentation presentation,
        bool isSelected,
        Action select,
        MapSceneObjectStyle? style = null,
        double pinOffsetX = 0,
        double pinOffsetY = 0,
        double canvasWidth = double.PositiveInfinity,
        double canvasHeight = double.PositiveInfinity,
        string? markerGlyphOverride = null)
    {
        var formatter = new MapSceneRendererSemanticText(presentation);
        var anchor = projection.Project(sceneObject.Geometry.Points[0]);
        // [Issue 594] "RUAF Roadblock" read as "RU…" near the card's right edge: the selected
        // name label used to always sit centred below its icon, so a marker close enough to the
        // card's own edge had it clipped by PlanViewport's own ClipToBounds. Flips it to the
        // other side of the icon instead, while it is still on-screen to flip away from.
        var nearRightEdge = anchor.X > canvasWidth - EdgeFlipMargin;
        var nearBottomEdge = anchor.Y > canvasHeight - EdgeFlipMargin;
        return new(
            sceneObject,
            sceneObject.Id.Value,
            sceneObject.Label,
            formatter.Automation(sceneObject),
            sceneObject.Detail,
            formatter.Kind(sceneObject.Kind),
            formatter.Truth(sceneObject.Truth),
            formatter.Faction(sceneObject.Faction),
            formatter.Offer(sceneObject.OfferState),
            MapSceneRendererViewModel.HasOfferStatus(sceneObject.Kind),
            formatter.Evidence(sceneObject.Provenance),
            formatter.Estimate(sceneObject.Estimate),
            isSelected,
            anchor.X - (MapSceneRendererViewModel.MarkerExtent / 2),
            anchor.Y - (MapSceneRendererViewModel.MarkerExtent / 2),
            1 / camera.Zoom,
            camera.BearingDegrees,
            markerGlyphOverride ?? MarkerFor(sceneObject),
            IconFor(sceneObject),
            // A pin (a waypoint or a quest objective) never carries the truth-glyph badge: its
            // shape and colour already say what it is, and the badge was never drawn anywhere —
            // see MapSceneRendererObjectViewModel.TruthGlyph's remaining callers, all tests.
            sceneObject.Kind is MapSceneObjectKind.QuestObjective or MapSceneObjectKind.Waypoint
                ? string.Empty
                : TruthGlyphFor(sceneObject.Truth),
            FactionGlyphFor(sceneObject),
            OfferGlyphFor(sceneObject),
            false,
            select,
            sceneObject.HeadingDegrees,
            camera.BearingDegrees,
            style,
            pinOffsetX,
            pinOffsetY,
            nearRightEdge,
            nearBottomEdge);
    }

    public static MapSceneRendererObjectViewModel ForCluster(
        int column,
        int row,
        IReadOnlyList<MapSceneObject> objects,
        MapSceneProjection projection,
        MapSceneCamera camera,
        MapSceneRendererPresentation presentation,
        Action select)
    {
        var point = new MapScenePoint(
            objects.Average(item => item.Geometry.Points[0].X),
            objects.Average(item => item.Geometry.Points[0].Y));
        var anchor = projection.Project(point);
        var count = objects.Count;
        var label = presentation.Format("Map.Cluster.Label", presentation.Number(count));
        return new(
            null,
            $"cluster-{column}-{row}",
            label,
            label,
            presentation.Get("Map.Cluster.Detail"),
            presentation.Get("Map.Cluster.Kind"),
            presentation.Get("Map.Cluster.Truth"),
            presentation.Get("Map.Cluster.Faction"),
            string.Empty,
            false,
            presentation.Get("Map.Cluster.Evidence"),
            string.Empty,
            false,
            anchor.X - (MapSceneRendererViewModel.MarkerExtent / 2),
            anchor.Y - (MapSceneRendererViewModel.MarkerExtent / 2),
            1 / camera.Zoom,
            camera.BearingDegrees,
            count > 99 ? "99+" : presentation.Number(count),
            MapSceneMarkerIcon.Cluster,
            string.Empty,
            string.Empty,
            string.Empty,
            true,
            select);
    }

    /// <summary>A label that is a 1–3 digit number: the numbered-marker convention Plan and Team share.</summary>
    private static bool HasStepNumberLabel(MapSceneObject item) =>
        item.Label.Length is > 0 and <= 3 && item.Label.All(char.IsAsciiDigit);

    /// <summary>
    /// [Issue 508] A quest objective's marker always draws its own label — a letter (A, B, C…)
    /// for one the catalog or the player placed, or a personal-plan route's own short step number
    /// where a caller supplied one (see <c>QuestObjectiveSceneBuilder</c>'s <c>numberFor</c>) —
    /// rather than a generic "◇" that discarded it. A waypoint is the only marker a plain number
    /// ever means now, so an objective's label can never be mistaken for one.
    /// </summary>
    private static string MarkerFor(MapSceneObject item) => item.Kind switch
    {
        MapSceneObjectKind.QuestObjective => item.Label.Length is > 0 and <= 4 ? item.Label : string.Empty,
        // V2 rough package 17 (team): a waypoint labelled with its number draws that number,
        // so the marker and its row in a marks list read as one thing. A custom-named waypoint
        // draws no text at all — its round pin head already says "waypoint", and a name is too
        // long to fit legibly inside it; the name itself is the marks list row and the tooltip.
        MapSceneObjectKind.Waypoint => HasStepNumberLabel(item) ? item.Label : string.Empty,
        _ => item.Truth switch
        {
            MapSceneTruthKind.HistoricalEstimate => "≈",
            MapSceneTruthKind.LocalLastKnown => "◎",
            MapSceneTruthKind.TeamSharedLastKnown => "◉",
            _ => item.Kind switch
            {
                MapSceneObjectKind.Extract => "⇱",
                MapSceneObjectKind.Transit => "↔",
                MapSceneObjectKind.Ping => "•",
                MapSceneObjectKind.Hazard => "!",
                MapSceneObjectKind.Lock => "⌑",
                MapSceneObjectKind.Switch => "",
                MapSceneObjectKind.LootSpawn or MapSceneObjectKind.LootContainer => "$",
                MapSceneObjectKind.Route => "↝",
                MapSceneObjectKind.LastKnownPosition => "◉",
                MapSceneObjectKind.TeammateLastKnown => "◍",
                MapSceneObjectKind.Risk => "△",
                _ => "●",
            },
        },
    };

    // V2 rough package 20: nothing unknown draws a "?" on the map any more. An unknown truth,
    // faction or offer state says nothing at all — the marker's own border colour already carries
    // "potential" and "offer unknown", and a literal question mark beside every extract read as a
    // rendering fault rather than as information.
    private static string TruthGlyphFor(MapSceneTruthKind truth) => truth switch
    {
        MapSceneTruthKind.HistoricalEstimate => "H",
        MapSceneTruthKind.LocalLastKnown => "L",
        MapSceneTruthKind.TeamSharedLastKnown => "T",
        MapSceneTruthKind.PersonalPlan => "P",
        MapSceneTruthKind.UserAuthored => "✎",
        _ => string.Empty,
    };

    private static string FactionGlyphFor(MapSceneObject item) =>
        item.Kind is MapSceneObjectKind.Extract or MapSceneObjectKind.Transit
            ? item.Faction switch
            {
                MapFeatureFaction.Pmc => "P",
                MapFeatureFaction.Scav => "S",
                MapFeatureFaction.Shared => "P/S",
                _ => string.Empty,
            }
            : string.Empty;

    private static string OfferGlyphFor(MapSceneObject item) =>
        MapSceneRendererViewModel.HasOfferStatus(item.Kind)
            ? item.OfferState switch
            {
                MapSceneOfferState.Offered => "✓",
                MapSceneOfferState.NotOffered => "×",
                _ => string.Empty,
            }
            : string.Empty;

    /// <summary>Which drawn icon a marker shows, in place of the old text glyph.</summary>
    internal static MapSceneMarkerIcon IconFor(MapSceneObject item) => item.Kind switch
    {
        MapSceneObjectKind.Extract => MapSceneMarkerIcon.Extract,
        MapSceneObjectKind.Transit => MapSceneMarkerIcon.Transit,
        MapSceneObjectKind.QuestObjective => MapSceneMarkerIcon.Objective,
        MapSceneObjectKind.Waypoint => MapSceneMarkerIcon.Waypoint,
        MapSceneObjectKind.Ping => MapSceneMarkerIcon.Ping,
        MapSceneObjectKind.Hazard => MapSceneMarkerIcon.Hazard,
        MapSceneObjectKind.Lock => MapSceneMarkerIcon.Lock,
        MapSceneObjectKind.Switch => MapSceneMarkerIcon.Switch,
        MapSceneObjectKind.LootSpawn or MapSceneObjectKind.LootContainer => MapSceneMarkerIcon.Loot,
        MapSceneObjectKind.SpawnArea => MapSceneMarkerIcon.Spawn,
        MapSceneObjectKind.Route => MapSceneMarkerIcon.Route,
        MapSceneObjectKind.Risk => MapSceneMarkerIcon.Risk,
        MapSceneObjectKind.LastKnownPosition => MapSceneMarkerIcon.Player,
        MapSceneObjectKind.TeammateLastKnown => MapSceneMarkerIcon.Teammate,
        _ => MapSceneMarkerIcon.Generic,
    };
}

public sealed class MapSceneRendererGeometryViewModel
{
    public MapSceneRendererGeometryViewModel(
        MapSceneObject sceneObject,
        MapSceneProjection projection,
        MapSceneObjectStyle? style = null)
    {
        SceneObject = sceneObject ?? throw new ArgumentNullException(nameof(sceneObject));
        Points = sceneObject.Geometry.Points.Select(projection.Project).ToArray();
        Style = style;
    }

    public MapSceneObject SceneObject { get; }
    public IReadOnlyList<MapSceneProjectedPoint> Points { get; }
    public MapSceneGeometryKind Kind => SceneObject.Geometry.Kind;
    public MapSceneTruthKind Truth => SceneObject.Truth;

    /// <summary>A host-chosen style for this line, where there is one.</summary>
    public MapSceneObjectStyle? Style { get; }

    public string? ColorHint => Style?.Color;

    public double? ThicknessHint => Style?.LineThickness;

    public double OpacityHint => Style?.Opacity ?? 1;

    public bool IsDashedHint => Style?.Dashed ?? false;
}

/// <summary>
/// [V2 rough package 22] What a host wants one scene object to look like.
/// </summary>
/// <remarks>
/// Colour, weight and opacity are presentation, and the scene contract deliberately carries
/// neither — two devices drawing the same scene are free to draw it their own way. A host that
/// does have an opinion (the Raid cockpit gives each squadmate their own colour and draws last
/// week's paths faintly) says so here instead of smuggling it into a label or an object kind.
/// <c>Dashed</c> and <c>Badge</c> are the objective route's (#307): the plan line is dashed so it
/// never reads as one of the solid extract routes, and a route stop that is already an objective's
/// pin puts its step number on that pin as a small badge instead of a second pin on top of it.
/// </remarks>
public readonly record struct MapSceneObjectStyle(
    string? Color = null,
    double? LineThickness = null,
    double? Opacity = null,
    bool Dashed = false,
    string? Badge = null,
    // [#780] A squadmate's objective: an outlined shield in their colour, smaller than the
    // player's own, so it reads as "theirs" and never competes with the player's filled pins.
    bool Outlined = false);

/// <summary>
/// [V2 rough package 22] One place name written on the plan, V1's <c>MapPlaceNameViewModel</c>.
/// </summary>
/// <remarks>
/// Text, not a marker: see <see cref="MapSceneRendererViewModel.LabelObjects"/>. It is laid out
/// centred on its point and, like a marker, is scaled back by the camera zoom and turned back by
/// the camera bearing so a rotated map still reads left to right.
/// </remarks>
public sealed class MapSceneRendererLabelViewModel : BindableViewModel
{
    /// <summary>Half the box a name is centred in, in unzoomed canvas pixels.</summary>
    private const double HalfWidth = 110;
    private const double HalfHeight = 9;

    private double _inverseZoom;
    private double _uprightDegrees;

    public MapSceneRendererLabelViewModel(
        MapSceneObject sceneObject,
        MapSceneProjection projection,
        MapSceneCamera camera,
        // [Issue 581] A squadmate's own name is written in their colour, the same as their marker
        // and trail; an ordinary place name gets no opinion and keeps its themed foreground.
        MapSceneObjectStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(sceneObject);
        ArgumentNullException.ThrowIfNull(projection);
        SceneObject = sceneObject;
        Style = style;
        var anchor = projection.Project(sceneObject.Geometry.Points[0]);
        AnchorLeft = anchor.X - HalfWidth;
        AnchorTop = anchor.Y - HalfHeight;
        _inverseZoom = 1 / camera.Zoom;
        _uprightDegrees = camera.BearingDegrees;
    }

    public MapSceneObject SceneObject { get; }

    private MapSceneObjectStyle? Style { get; }

    /// <summary>See <see cref="MapSceneRendererObjectViewModel.ColorHintBrush"/>: null keeps the label's themed foreground.</summary>
    public IBrush? ColorHintBrush => Style?.Color is { } hex && Color.TryParse(hex, out var color)
        ? new SolidColorBrush(color)
        : null;

    public bool HasColorHint => ColorHintBrush is not null;

    public string Text => SceneObject.Label;

    public double Width => HalfWidth * 2;

    public double AnchorLeft { get; }

    public double AnchorTop { get; }

    public double InverseZoom => _inverseZoom;

    public double UprightDegrees => _uprightDegrees;

    /// <summary>[#453] Whether <paramref name="other"/> would draw exactly what this does.</summary>
    internal bool DrawsSameAs(MapSceneRendererLabelViewModel other) =>
        SceneObject.HasSameDisplayAs(other.SceneObject) && Nullable.Equals(Style, other.Style) &&
        AnchorLeft.Equals(other.AnchorLeft) && AnchorTop.Equals(other.AnchorTop) &&
        _inverseZoom.Equals(other._inverseZoom) && _uprightDegrees.Equals(other._uprightDegrees);

    public void UpdateCamera(MapSceneCamera camera)
    {
        SetProperty(ref _inverseZoom, 1 / camera.Zoom, nameof(InverseZoom));
        SetProperty(ref _uprightDegrees, camera.BearingDegrees, nameof(UprightDegrees));
    }
}

public sealed class MapSceneRendererListItemViewModel : BindableViewModel
{
    private bool _isSelected;

    public MapSceneRendererListItemViewModel(
        MapSceneObject item,
        MapSceneRendererPresentation presentation,
        bool isSelected,
        Action select)
    {
        var formatter = new MapSceneRendererSemanticText(presentation);
        SceneObject = item ?? throw new ArgumentNullException(nameof(item));
        Id = item.Id;
        Label = item.Label;
        AutomationName = formatter.Automation(item);
        Detail = item.Detail;
        KindLabel = formatter.Kind(item.Kind);
        TruthLabel = formatter.Truth(item.Truth);
        FactionLabel = formatter.Faction(item.Faction);
        OfferState = item.OfferState;
        OfferedLabel = formatter.Offer(item.OfferState);
        HasOfferStatus = MapSceneRendererViewModel.HasOfferStatus(item.Kind);
        EvidenceLabel = formatter.Evidence(item.Provenance);
        _isSelected = isSelected;
        SelectCommand = new DelegateCommand(select ?? throw new ArgumentNullException(nameof(select)));
    }

    public MapSceneObject SceneObject { get; }
    public MapSceneObjectId Id { get; }
    public string Label { get; }
    public string AutomationName { get; }
    public string? Detail { get; }
    public string KindLabel { get; }
    public string TruthLabel { get; }
    public string FactionLabel { get; }
    public MapSceneOfferState OfferState { get; }
    public string OfferedLabel { get; }
    public bool HasOfferStatus { get; }
    public string EvidenceLabel { get; }
    public bool IsOffered => OfferState == MapSceneOfferState.Offered;
    public bool IsKnownNotOffered => OfferState == MapSceneOfferState.NotOffered;
    public bool IsOfferUnknown => HasOfferStatus && OfferState == MapSceneOfferState.Unknown;
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool IsSelected => _isSelected;
    public string AutomationId => $"v2-map-list-{MapRendererToken.From(Id.Value)}";
    public ICommand SelectCommand { get; }

    public void SetSelected(bool selected) => SetProperty(ref _isSelected, selected, nameof(IsSelected));
}

internal sealed class MapSceneRendererSemanticText(MapSceneRendererPresentation presentation)
{
    public string Kind(MapSceneObjectKind kind) => presentation.Get($"Map.Kind.{kind}");

    public string Truth(MapSceneTruthKind truth) => presentation.Get(Enum.IsDefined(truth)
        ? $"Map.Truth.{truth}"
        : "Map.Truth.Unknown");

    public string Faction(MapFeatureFaction faction) => presentation.Get(faction switch
    {
        MapFeatureFaction.Pmc => "Map.Faction.Pmc",
        MapFeatureFaction.Scav => "Map.Faction.Scav",
        MapFeatureFaction.Shared => "Map.Faction.Shared",
        _ => "Map.Faction.Unknown",
    });

    public string Offer(MapSceneOfferState state) => presentation.Get(state switch
    {
        MapSceneOfferState.Offered => "Map.Offer.Offered",
        MapSceneOfferState.NotOffered => "Map.Offer.NotOffered",
        _ => "Map.Offer.Unknown",
    });

    public string Evidence(DataProvenance provenance)
    {
        var confidence = provenance.Confidence is { } value
            ? presentation.Format("Map.Evidence.Confidence", presentation.Percent(value.Value))
            : string.Empty;
        return presentation.Format("Map.Evidence", provenance.Source, presentation.Instant(provenance.ObservedUtc), confidence);
    }

    public string Estimate(MapSceneEstimateMetadata? estimate) => estimate is null
        ? string.Empty
        : presentation.Format(
            "Map.Estimate",
            estimate.ModelVersion,
            presentation.Instant(estimate.ObservedFromUtc),
            presentation.Instant(estimate.DataThroughUtc),
            presentation.Instant(estimate.GeneratedUtc),
            estimate.Coverage,
            estimate.Calibration,
            estimate.TransformVersion);

    public string Automation(MapSceneObject item)
    {
        var offer = MapSceneRendererViewModel.HasOfferStatus(item.Kind)
            ? presentation.Format("Map.Marker.OfferSuffix", Offer(item.OfferState))
            : string.Empty;
        return presentation.Format(
            "Map.Marker.Automation",
            item.Label,
            Kind(item.Kind),
            Truth(item.Truth),
            Faction(item.Faction),
            offer);
    }
}

/// <summary>
/// V2 rough package 20: which drawn icon a map marker uses. The renderer used to put a text glyph
/// in the marker box ("⇱", "↔", "◇") with letter badges beside it, which on Clayton's Streets
/// screenshot read as "P/S ?" and "↔ ?" rather than as a map.
/// </summary>
public enum MapSceneMarkerIcon
{
    Generic,
    Extract,
    Transit,
    Objective,
    Waypoint,
    Ping,
    Spawn,
    Loot,
    Hazard,
    Lock,
    Switch,
    Route,
    Risk,
    Cluster,
    Player,
    Teammate,
}

public readonly record struct MapSceneProjectedPoint(double X, double Y);

public sealed class MapSceneProjection
{
    private readonly MapSceneBounds _bounds;
    private readonly double _canvasWidth;
    private readonly double _canvasHeight;

    /// <param name="planAspect">
    /// The drawn plan's true width-to-height ratio, from the decoded map artwork. Scene
    /// coordinates are a normalized percent box (see <see cref="MapSceneRendererViewModel"/>'s
    /// PlanBounds) that is the same 0-100 square for every map, so the bounds themselves cannot
    /// say what shape the map is. Without this, Streets — which is far wider than it is tall —
    /// was squashed into a square and every other map was distorted its own way. Non-finite or
    /// non-positive falls back to the bounds' own ratio, which is what a synthetic test scene
    /// and the map gallery want.
    /// </param>
    /// <param name="headroomAbove">
    /// [V2 rough package 39] Canvas pixels to keep clear above the plan, and
    /// <paramref name="headroomBelow"/> below it, for a stacked view whose other floors are
    /// drawn off the read floor. The plan is fitted into what is left and the read floor still
    /// lands in exactly this rectangle, so nothing about where an object projects changes — the
    /// map is simply drawn a little smaller to leave the stack somewhere to be.
    /// </param>
    public MapSceneProjection(
        MapSceneBounds bounds,
        double canvasWidth,
        double canvasHeight,
        double inset,
        bool fillCanvas = false,
        double planAspect = double.NaN,
        double headroomAbove = 0,
        double headroomBelow = 0)
    {
        _bounds = bounds;
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
        var boundsWidth = bounds.Width;
        var boundsHeight = bounds.Height;
        var finiteBounds = double.IsFinite(boundsWidth) && double.IsFinite(boundsHeight) &&
            boundsWidth > 0 && boundsHeight > 0;
        // Never let the stack eat the map: half the card is the most the plates may claim.
        var requested = Math.Max(0, headroomAbove) + Math.Max(0, headroomBelow);
        var allowed = Math.Max(0, canvasHeight * 0.45);
        var factor = requested > allowed && requested > 0 ? allowed / requested : 1;
        var above = Math.Max(0, headroomAbove) * factor;
        var below = Math.Max(0, headroomBelow) * factor;
        var availableWidth = Math.Max(1, canvasWidth - (inset * 2));
        var availableHeight = Math.Max(1, canvasHeight - (inset * 2) - above - below);
        // "Contain" (the default) never crops the plan, at the cost of letterboxing when the
        // viewport's aspect ratio does not match the plan's. A host with its own fixed frame
        // around the map instead asks to "cover": fill the viewport edge to edge, cropping the
        // plan's own overflow — PlanViewport already clips to its bounds.
        var aspect = double.IsFinite(planAspect) && planAspect > 0
            ? planAspect
            : finiteBounds ? boundsWidth / boundsHeight : double.NaN;
        IsUsable = finiteBounds && double.IsFinite(aspect) && aspect > 0;
        PlanAspect = IsUsable ? aspect : double.NaN;
        if (!IsUsable)
        {
            ScaleX = 1;
            ScaleY = 1;
            MapWidth = availableWidth;
            MapHeight = availableHeight;
            MapLeft = inset;
            MapTop = inset;
            return;
        }

        // One rectangle of the plan's true shape, fitted into the available rect and centred, so
        // the artwork is never stretched and never cropped by the fit itself.
        var byWidth = availableWidth;
        var byHeight = availableHeight * aspect;
        MapWidth = fillCanvas ? Math.Max(byWidth, byHeight) : Math.Min(byWidth, byHeight);
        MapHeight = MapWidth / aspect;
        MapLeft = (canvasWidth - MapWidth) / 2;
        MapTop = above + ((canvasHeight - above - below - MapHeight) / 2);
        // Separate axis scales: scene space is a percent box, so mapping it onto a rectangle of
        // the artwork's shape is exactly what puts a marker back over the feature it names.
        ScaleX = MapWidth / boundsWidth;
        ScaleY = MapHeight / boundsHeight;
    }

    public bool IsUsable { get; }

    /// <summary>The shape this projection fits, width over height, or NaN when it has none.</summary>
    public double PlanAspect { get; }
    public double ScaleX { get; }
    public double ScaleY { get; }

    /// <summary>One representative scale, for tolerances that are not per-axis.</summary>
    public double Scale => Math.Sqrt(ScaleX * ScaleY);
    public double MapLeft { get; }
    public double MapTop { get; }
    public double MapWidth { get; }
    public double MapHeight { get; }

    /// <summary>The scene point a projected (canvas) position stands for, ignoring the camera.</summary>
    public MapScenePoint Unproject(double projectedX, double projectedY) => IsUsable
        ? new(
            _bounds.MinimumX + ((projectedX - MapLeft) / ScaleX),
            _bounds.MinimumY + ((projectedY - MapTop) / ScaleY))
        : new(_bounds.MinimumX, _bounds.MinimumY);

    public MapSceneProjectedPoint Project(MapScenePoint point) => Project(point.X, point.Y);

    public MapSceneProjectedPoint Project(double x, double y) => new(
        IsUsable ? MapLeft + ((x - _bounds.MinimumX) * ScaleX) : _canvasWidth / 2,
        IsUsable ? MapTop + ((y - _bounds.MinimumY) * ScaleY) : _canvasHeight / 2);

    public bool TryUnproject(
        double viewportX,
        double viewportY,
        MapSceneCamera camera,
        out MapScenePoint point,
        out double worldUnitsPerPixel)
    {
        if (!IsUsable || !double.IsFinite(viewportX) || !double.IsFinite(viewportY))
        {
            point = default;
            worldUnitsPerPixel = 0;
            return false;
        }

        var screenX = (viewportX - (_canvasWidth / 2)) / camera.Zoom;
        var screenY = (viewportY - (_canvasHeight / 2)) / camera.Zoom;
        var radians = camera.BearingDegrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var baseX = (cosine * screenX) - (sine * screenY);
        var baseY = (sine * screenX) + (cosine * screenY);
        var cameraPoint = Project(camera.CenterX, camera.CenterY);
        point = new(
            _bounds.MinimumX + ((cameraPoint.X + baseX - MapLeft) / ScaleX),
            _bounds.MinimumY + ((cameraPoint.Y + baseY - MapTop) / ScaleY));
        // The more forgiving axis, so a hit tolerance stays at least the requested pixels wide on
        // a plan whose two axes no longer share a scale.
        worldUnitsPerPixel = 1 / (Math.Min(ScaleX, ScaleY) * camera.Zoom);
        return true;
    }

    /// <summary>[#286] The inverse of <see cref="TryUnproject"/>: where a scene point is in the viewport.</summary>
    public bool TryProjectToViewport(MapScenePoint point, MapSceneCamera camera, out double viewportX, out double viewportY)
    {
        viewportX = viewportY = 0;
        if (!IsUsable || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || camera.Zoom <= 0)
        {
            return false;
        }

        var projected = Project(point);
        var cameraPoint = Project(camera.CenterX, camera.CenterY);
        var baseX = projected.X - cameraPoint.X;
        var baseY = projected.Y - cameraPoint.Y;
        var radians = camera.BearingDegrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        viewportX = (((cosine * baseX) + (sine * baseY)) * camera.Zoom) + (_canvasWidth / 2);
        viewportY = (((-sine * baseX) + (cosine * baseY)) * camera.Zoom) + (_canvasHeight / 2);
        return true;
    }
}

internal static class MapRendererToken
{
    /// <summary>A raw floor id ("ground", "2nd-floor") as a short human label ("Ground", "2nd floor").</summary>
    public static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var spaced = value.Replace('-', ' ').Replace('_', ' ');
        return char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    public static string From(string value)
    {
        var normalized = new string(value.ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray());
        var suffix = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..8]
            .ToLowerInvariant();
        return $"{normalized}-{suffix}";
    }
}
