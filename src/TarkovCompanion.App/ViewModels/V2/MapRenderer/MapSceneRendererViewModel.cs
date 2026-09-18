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
    private readonly Func<string, string>? _floorNameResolver;
    private readonly Func<MapSceneObject, MapSceneObjectStyle?>? _styleResolver;
    private readonly Func<string, double?>? _floorElevationResolver;
    private MapSceneSnapshot _scene;
    private MapSceneObjectId? _selectedObjectId;
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
    // V2 rough package 20: a drag used to move nothing until the pointer came up, then jump. The
    // plan now follows the pointer 1:1 through these two numbers, which only feed the canvas's
    // RenderTransform — no measure, no arrange, no marker rebuild per pointer delta. The camera
    // itself is committed to the canonical scene once, when the drag ends.
    private double _panOffsetX;
    private double _panOffsetY;
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
        Func<string, double?>? floorElevationResolver = null)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
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
                SelectHighValueLootEntry);
            HighValueLoot.ProjectionChanged += HighValueLootProjectionChanged;
        }
        RebuildAll();
    }

    /// <summary>Raised for the owner to apply through the canonical reducer and publish back.</summary>
    public event Action<MapSceneViewChange>? ViewChangeRequested;

    /// <summary>The owner rebuilds the typed layer and canonical scene for this request.</summary>
    public event Action<HighValueLootFilterRequest>? HighValueLootFilterRequested;

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
    public IReadOnlyList<MapSceneRendererObjectViewModel> PointMarkers { get; private set; } = [];
    public IReadOnlyList<MapSceneRendererObjectViewModel> ClusterMarkers { get; private set; } = [];
    public IReadOnlyList<MapSceneRendererGeometryViewModel> GeometryObjects { get; private set; } = [];

    /// <summary>
    /// [V2 rough package 22] Place names, drawn as text rather than as markers.
    /// </summary>
    /// <remarks>
    /// A label is a word written on the map, not a thing at a point: giving it a 44px marker
    /// button would make "Dorms" clickable furniture, and on Streets the hundred of them would
    /// exhaust <see cref="MaximumPointMarkers"/> and cluster the extracts away behind them.
    /// </remarks>
    public IReadOnlyList<MapSceneRendererLabelViewModel> LabelObjects { get; private set; } = [];

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
        var objectDefinitionsChanged = !Equivalent(previous.Objects, scene.Objects);
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

        if (boundsChanged)
        {
            _projection = CreateProjection();
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
            layerDefinitionsChanged || layerVisibilityChanged || floorIdsChanged || floorSelectionChanged;
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

            foreach (var label in LabelObjects)
            {
                label.UpdateCamera(_scene.View.Camera);
            }
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
        DispatchHighValueLootPresetChange();
    }

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
            .Where(item => !item.IsCluster && item.SceneObject is not null)
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
    public double MinimumZoom => 1;

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
        Request(new(
            MapSceneViewChangeKind.SetCamera,
            Camera: Clamp(new(
                camera.CenterX,
                camera.CenterY,
                camera.Zoom * factor,
                camera.BearingDegrees,
                camera.PitchDegrees))));
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
            // Requested before the offset is cleared: the host applies and presents this
            // synchronously, so the committed camera replaces the drag offset within the same
            // frame and the plan does not flash back to where the drag started.
            Request(new(MapSceneViewChangeKind.SetCamera, Camera: camera));
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
        var centre = _projection.Project(camera.CenterX, camera.CenterY);
        var moved = _projection.Unproject(centre.X + (planX * change), centre.Y + (planY * change));
        Request(new(
            MapSceneViewChangeKind.SetCamera,
            Camera: Clamp(new(moved.X, moved.Y, zoom, camera.BearingDegrees, camera.PitchDegrees))));
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
        var bounds = _scene.Bounds;
        // [V2 rough package 22] The bearing survives a fit. Turning the map is how somebody reads
        // it while playing, and V1's Fit never undid it; resetting it here meant every fit (and
        // the automatic one on a new map) silently put the map back the way round they had
        // rejected.
        Request(new(
            MapSceneViewChangeKind.SetCamera,
            Camera: new(
                bounds.MinimumX + (bounds.Width / 2),
                bounds.MinimumY + (bounds.Height / 2),
                MinimumZoom,
                _scene.View.Camera.BearingDegrees,
                0)));
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
        // Clamped like a pan, so following somebody standing near the edge of the map puts them
        // as close to the middle as the plan allows rather than leaving the camera on a centre
        // the next gesture would have to correct.
        Request(new(
            MapSceneViewChangeKind.SetCamera,
            Camera: Clamp(new(point.X, point.Y, zoom, camera.BearingDegrees, camera.PitchDegrees))));
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

        Request(new(
            MapSceneViewChangeKind.SetCamera,
            Camera: new(camera.CenterX, camera.CenterY, camera.Zoom, bearing, camera.PitchDegrees)));
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
        var visibleObjects = RebuildProjectedObjects();
        RebuildListItems();
        BuildDenseSceneNotice(visibleObjects);
        RestoreLootSelection();
        RaisePresentChanged(false, false, false, true, false, false);
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
            () => RequestMode(mode)))
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
                _reviewedAssetResolver?.Invoke(asset) is not { } image)
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
                !HighValueLoot.VisibleObjectIds.Contains(item.Id))
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
                counts.TryGetValue(layer.Id, out var count) ? count : 0))
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
        LabelObjects = _projection.IsUsable
            ? visibleObjects
                .Where(item => item.Kind == MapSceneObjectKind.Label &&
                    item.Geometry.Kind == MapSceneGeometryKind.Point &&
                    _scene.Bounds.Contains(item.Geometry.Points[0]))
                .Take(MaximumPlaceNames)
                .Select(item => new MapSceneRendererLabelViewModel(item, _projection, _scene.View.Camera))
                .ToArray()
            : [];
        SpatialObjects = BuildPointMarkers(visibleObjects);
        PointMarkers = SpatialObjects.Where(item => !item.IsCluster).ToArray();
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
        if (points.Length <= MaximumPointMarkers)
        {
            return points
                .Select(item => MapSceneRendererObjectViewModel.ForObject(
                    item,
                    _projection,
                    _scene.View.Camera,
                    _presentation,
                    item.Id == _selectedObjectId,
                    () => SelectObject(item.Id),
                    _styleResolver?.Invoke(item)))
                .ToArray();
        }

        return points
            .GroupBy(item => ClusterCell(item.Geometry.Points[0]))
            .OrderBy(group => group.Key.Row)
            .ThenBy(group => group.Key.Column)
            .Select(group => BuildClusterMarker(group.Key.Column, group.Key.Row, group.ToArray()))
            .Take(MaximumPointMarkers)
            .ToArray();
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
                _styleResolver?.Invoke(item));
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
        var previous = _selectedObjectId;
        _selectedObjectId = next;
        foreach (var marker in SpatialObjects.Where(item => item.ObjectId == previous || item.ObjectId == next))
        {
            marker.SetSelected(marker.ObjectId == next);
        }

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
                _styleResolver?.Invoke(item));
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
            BackgroundImage = asset is null || _reviewedAssetResolver is null
                ? null
                : _reviewedAssetResolver(asset);
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

    private void UpdateBackgroundStatus(MapSceneAsset? asset) => BackgroundStatus = !_projection.IsUsable
        ? Text("Map.Background.Bounds")
        : asset is null
            ? Text("Map.Background.None")
            : BackgroundImage is null
                ? Text("Map.Background.NotCached")
                : string.Empty;

    private void BuildDenseSceneNotice(IReadOnlyList<MapSceneObject> visibleObjects)
    {
        var pointCount = visibleObjects.Count(item => item.Geometry.Kind == MapSceneGeometryKind.Point);
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
        // What actually draws over the plan: two or three words. The sentences above stay as its
        // tooltip and its accessible name, so nothing is lost, it just is not painted on the map.
        DenseSceneChip = messages.Count switch
        {
            0 => string.Empty,
            1 when outsideBounds > 0 => Format("Map.Dense.Chip.Outside", _presentation.Number(outsideBounds)),
            _ => Format("Map.Dense.Chip.Many", _presentation.Number(messages.Count)),
        };
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
        var visible = _scene.VisibleObjects;
        if (HighValueLoot is null)
        {
            return visible;
        }

        return visible
            .Where(item => item.LayerId != HighValueLootLayerService.LayerId ||
                           HighValueLoot.VisibleObjectIds.Contains(item.Id))
            .ToArray();
    }

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
        if (modes) OnPropertyChanged(nameof(Modes));
        if (floors)
        {
            OnPropertyChanged(nameof(Floors));
            OnPropertyChanged(nameof(HasFloorFilters));
            OnPropertyChanged(nameof(HasFloors));
            OnPropertyChanged(nameof(SelectedFloor));
            RaiseFloorStackChanged();
        }
        if (layers) OnPropertyChanged(nameof(Layers));
        if (visibleContent)
        {
            OnPropertyChanged(nameof(SpatialObjects));
            OnPropertyChanged(nameof(PointMarkers));
            OnPropertyChanged(nameof(ClusterMarkers));
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
                     nameof(SpatialObjects), nameof(PointMarkers), nameof(ClusterMarkers), nameof(GeometryObjects),
                     nameof(LabelObjects), nameof(HasLabelObjects),
                     nameof(SelectedObject), nameof(HasSpatialObjects),
                     nameof(ShowsEmptyMap), nameof(CanvasWidth), nameof(CanvasHeight), nameof(MapLeft), nameof(MapTop),
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
        Action select)
    {
        Mode = mode;
        Label = label;
        IsSelected = isSelected;
        IsAvailable = isAvailable;
        UnavailableReason = unavailableReason;
        SelectCommand = new DelegateCommand(select ?? throw new ArgumentNullException(nameof(select)));
    }

    public MapSceneMode Mode { get; }
    public string Label { get; }
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
        int count = 0)
    {
        Layer = layer ?? throw new ArgumentNullException(nameof(layer));
        IsVisible = isVisible;
        Count = Math.Max(0, count);
        ToggleLabel = presentation.Format(isVisible ? "Map.Layer.Hide" : "Map.Layer.Show", layer.Name);
        // The switch itself carries the count, so "Extracts" reads "Extracts 12" and a player
        // can see what turning it on would give them without turning it on.
        Label = Count == 0
            ? presentation.Format("Map.Layer.Empty", layer.Name)
            : presentation.Format("Map.Layer.Count", layer.Name, presentation.Number(Count));
        StateLabel = Count == 0
            ? presentation.Get("Map.Layer.NothingToShow")
            : presentation.Get(isVisible ? "Map.Layer.Visible" : "Map.Layer.Hidden");
        // A layer with nothing on it says so rather than switching the map to empty. The view
        // disables the switch too; this is the guard that does not depend on it doing so.
        ToggleCommand = new DelegateCommand(() =>
        {
            if (Count > 0)
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
        MapSceneObjectStyle? style = null)
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
        _markerInverseZoom = markerInverseZoom;
        _markerUprightDegrees = markerUprightDegrees;
        MarkerGlyph = markerGlyph;
        Icon = icon;
        TruthGlyph = truthGlyph;
        FactionGlyph = factionGlyph;
        OfferGlyph = offerGlyph;
        IsCluster = isCluster;
        HeadingDegrees = headingDegrees;
        _coneDegrees = ConeFor(headingDegrees, cameraBearingDegrees);
        Style = style;
        SelectCommand = new DelegateCommand(select ?? throw new ArgumentNullException(nameof(select)));
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

    /// <summary>The V1 player/squadmate cone, drawn inside the 44px marker box.</summary>
    public string ConeGeometry => "M 22,22 L 8,2 A 18,18 0 0 1 36,2 Z";

    /// <summary>A host-chosen style for this marker (a squadmate's own colour), where there is one.</summary>
    public MapSceneObjectStyle? Style { get; }

    public string? ColorHint => Style?.Color;

    public bool HasColorHint => ColorHint is not null;
    public string MarkerGlyph { get; }

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
    public bool IsGenericIcon => ShowsMarkerIcon && Icon == MapSceneMarkerIcon.Generic;
    /// <summary>A person marker is a dot with a facing cone, not one of the drawn glyphs.</summary>
    public bool IsPersonIcon => IsPlayerIcon || IsTeammateIcon;
    public bool ShowsGlyphIcon => ShowsMarkerIcon && !IsPersonIcon;
    public string TruthGlyph { get; }
    public bool HasTruthGlyph => !string.IsNullOrWhiteSpace(TruthGlyph);
    public string FactionGlyph { get; }
    public bool HasFactionGlyph => !string.IsNullOrWhiteSpace(FactionGlyph);
    public string OfferGlyph { get; }
    public bool HasOfferGlyph => !string.IsNullOrWhiteSpace(OfferGlyph);
    public string AutomationId => $"v2-map-object-{MapRendererToken.From(Key)}";
    public ICommand SelectCommand { get; }

    public void SetSelected(bool selected)
    {
        if (SetProperty(ref _isSelected, selected, nameof(IsSelected)))
        {
            OnPropertyChanged(nameof(ZOrder));
        }
    }

    /// <summary>
    /// Where the marker sits in the stack of markers: the selected one on top, so two objectives
    /// standing on the same helicopter do not leave the one that was picked underneath the other.
    /// </summary>
    public int ZOrder => _isSelected ? 10 : 0;

    public void UpdateCamera(MapSceneCamera camera)
    {
        SetProperty(ref _markerInverseZoom, 1 / camera.Zoom, nameof(MarkerInverseZoom));
        SetProperty(ref _markerUprightDegrees, camera.BearingDegrees, nameof(MarkerUprightDegrees));
        SetProperty(ref _coneDegrees, ConeFor(HeadingDegrees, camera.BearingDegrees), nameof(ConeDegrees));
    }

    public static MapSceneRendererObjectViewModel ForObject(
        MapSceneObject sceneObject,
        MapSceneProjection projection,
        MapSceneCamera camera,
        MapSceneRendererPresentation presentation,
        bool isSelected,
        Action select,
        MapSceneObjectStyle? style = null)
    {
        var formatter = new MapSceneRendererSemanticText(presentation);
        var anchor = projection.Project(sceneObject.Geometry.Points[0]);
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
            MarkerFor(sceneObject),
            IconFor(sceneObject),
            IsNumberedStep(sceneObject) ? string.Empty : TruthGlyphFor(sceneObject.Truth),
            FactionGlyphFor(sceneObject),
            OfferGlyphFor(sceneObject),
            false,
            select,
            sceneObject.HeadingDegrees,
            camera.BearingDegrees,
            style);
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

    /// <summary>
    /// V2 rough package 17: a personal-plan quest objective labelled with a short step number
    /// (the Plan workspace's numbered objectives) draws that number, so the marker and its row
    /// in the list read as one thing. Every other object keeps its kind glyph and truth badge.
    /// </summary>
    private static bool IsNumberedStep(MapSceneObject item) =>
        item.Kind == MapSceneObjectKind.QuestObjective &&
        item.Truth == MapSceneTruthKind.PersonalPlan &&
        HasStepNumberLabel(item);

    /// <summary>A label that is a 1–3 digit number: the numbered-marker convention Plan and Team share.</summary>
    private static bool HasStepNumberLabel(MapSceneObject item) =>
        item.Label.Length is > 0 and <= 3 && item.Label.All(char.IsAsciiDigit);

    private static string MarkerFor(MapSceneObject item) => IsNumberedStep(item) ? item.Label : item.Truth switch
    {
        MapSceneTruthKind.HistoricalEstimate => "≈",
        MapSceneTruthKind.LocalLastKnown => "◎",
        MapSceneTruthKind.TeamSharedLastKnown => "◉",
        _ => item.Kind switch
        {
            MapSceneObjectKind.Extract => "⇱",
            MapSceneObjectKind.Transit => "↔",
            MapSceneObjectKind.QuestObjective => "◇",
            // V2 rough package 17 (team): a waypoint labelled with its number draws that number,
            // so the marker and its row in a marks list read as one thing.
            MapSceneObjectKind.Waypoint => HasStepNumberLabel(item) ? item.Label : "◆",
            MapSceneObjectKind.Ping => "•",
            MapSceneObjectKind.Hazard => "!",
            MapSceneObjectKind.Lock => "⌑",
            MapSceneObjectKind.LootSpawn or MapSceneObjectKind.LootContainer => "$",
            MapSceneObjectKind.Route => "↝",
            MapSceneObjectKind.LastKnownPosition => "◉",
            MapSceneObjectKind.TeammateLastKnown => "◍",
            MapSceneObjectKind.Risk => "△",
            _ => "●",
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
}

/// <summary>
/// [V2 rough package 22] What a host wants one scene object to look like.
/// </summary>
/// <remarks>
/// Colour, weight and opacity are presentation, and the scene contract deliberately carries
/// neither — two devices drawing the same scene are free to draw it their own way. A host that
/// does have an opinion (the Raid cockpit gives each squadmate their own colour and draws last
/// week's paths faintly) says so here instead of smuggling it into a label or an object kind.
/// </remarks>
public readonly record struct MapSceneObjectStyle(
    string? Color = null,
    double? LineThickness = null,
    double? Opacity = null);

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
        MapSceneCamera camera)
    {
        ArgumentNullException.ThrowIfNull(sceneObject);
        ArgumentNullException.ThrowIfNull(projection);
        SceneObject = sceneObject;
        var anchor = projection.Project(sceneObject.Geometry.Points[0]);
        AnchorLeft = anchor.X - HalfWidth;
        AnchorTop = anchor.Y - HalfHeight;
        _inverseZoom = 1 / camera.Zoom;
        _uprightDegrees = camera.BearingDegrees;
    }

    public MapSceneObject SceneObject { get; }

    public string Text => SceneObject.Label;

    public double Width => HalfWidth * 2;

    public double AnchorLeft { get; }

    public double AnchorTop { get; }

    public double InverseZoom => _inverseZoom;

    public double UprightDegrees => _uprightDegrees;

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
