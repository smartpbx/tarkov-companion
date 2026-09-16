using System.Windows.Input;
using Avalonia.Media;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.ViewModels.V2.MapRenderer;

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
    public const int ListPageSize = 50;
    public const int MaximumListItems = ListPageSize;
    public const double MarkerExtent = 48;

    private const int ClusterColumns = 20;
    private const int ClusterRows = 14;
    private const double MapInset = MarkerExtent / 2;

    private readonly MapSceneRendererPresentation _presentation;
    private readonly Func<Guid> _nextChangeId;
    private readonly Func<MapSceneAsset, IImage?>? _reviewedAssetResolver;
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

    public MapSceneRendererViewModel(
        MapSceneSnapshot scene,
        MapSceneRendererPresentation presentation,
        Func<Guid>? nextChangeId = null,
        Func<MapSceneAsset, IImage?>? reviewedAssetResolver = null)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        _nextChangeId = nextChangeId ?? Guid.NewGuid;
        _reviewedAssetResolver = reviewedAssetResolver;
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
        RebuildAll();
    }

    /// <summary>Raised for the owner to apply through the canonical reducer and publish back.</summary>
    public event Action<MapSceneViewChange>? ViewChangeRequested;

    public MapSceneSnapshot Scene => _scene;
    public IReadOnlyList<MapSceneRendererModeViewModel> Modes { get; private set; } = [];
    public IReadOnlyList<MapSceneRendererFloorViewModel> Floors { get; private set; } = [];
    public IReadOnlyList<MapSceneRendererLayerViewModel> Layers { get; private set; } = [];
    public IReadOnlyList<MapSceneRendererObjectViewModel> SpatialObjects { get; private set; } = [];
    public IReadOnlyList<MapSceneRendererGeometryViewModel> GeometryObjects { get; private set; } = [];
    public IReadOnlyList<MapSceneRendererListItemViewModel> ListItems { get; private set; } = [];
    public MapSceneRendererObjectViewModel? SelectedObject { get; private set; }
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
    public double CameraPostTranslateX => CanvasWidth / 2;
    public double CameraPostTranslateY => CanvasHeight / 2;
    public double CameraZoom => _scene.View.Camera.Zoom;
    public double CameraRotationDegrees => -_scene.View.Camera.BearingDegrees;
    public string LocationLabel => _scene.LocationId;
    public string VariantLabel => _scene.VariantKey;
    public string ModeLabel => DescribeMode(MapSceneMode.Flat2D);
    public string ZoomOutLabel => Text("Map.Action.ZoomOut");
    public string ZoomInLabel => Text("Map.Action.ZoomIn");
    public string FitPlanLabel => Text("Map.Action.Fit");
    public string ClearSelectionLabel => Text("Map.Action.ClearSelection");
    public string PreviousPageLabel => Text("Map.Action.Previous");
    public string NextPageLabel => Text("Map.Action.Next");
    public string ClearClusterLabel => Text("Map.Action.ClearCluster");
    public string PresentationLabel => Text("Map.Label.Presentation");
    public string FloorLabel => Text("Map.Label.Floor");
    public string MapPlanLabel => Text("Map.Label.Plan");
    public string LayersLabel => Text("Map.Label.Layers");
    public string DetailsLabel => Text("Map.Label.Details");
    public string SearchLabel => Text("Map.Label.Search");
    public string SearchPlaceholder => Text("Map.Label.SearchPlaceholder");
    public string RendererNotice => _rendererNotice;
    public bool HasRendererNotice => !string.IsNullOrWhiteSpace(RendererNotice);
    public bool HasFloorStack => false;
    public bool HasFloorFilters => Floors.Count > 0;
    public bool HasFloors => HasFloorFilters;
    public bool HasSpatialObjects => SpatialObjects.Count > 0 || GeometryObjects.Count > 0;
    public bool HasListItems => ListItems.Count > 0;
    public bool ShowsEmptyMap => !HasSpatialObjects;
    public bool ShowsEmptyList => !HasListItems;
    public bool HasSelection => SelectedObject is not null;
    public bool HasBackgroundImage => BackgroundImage is not null;
    public string EmptyMapMessage => Text("Map.Empty.Map");
    public string EmptyListMessage => Text("Map.Empty.List");
    public string ReviewedAssetLabel { get; private set; } = string.Empty;
    public string BackgroundStatus { get; private set; } = string.Empty;
    public bool HasBackgroundStatus => !string.IsNullOrWhiteSpace(BackgroundStatus);
    public string DenseSceneNotice { get; private set; } = string.Empty;
    public bool HasDenseSceneNotice => !string.IsNullOrWhiteSpace(DenseSceneNotice);
    public string ModeFallbackNotice => _scene.View.Mode == MapSceneMode.Flat2D
        ? string.Empty
        : Format("Map.Mode.Fallback", DescribeMode(_scene.View.Mode));
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

    /// <summary>Replaces the display only after the canonical owner accepted or refreshed it.</summary>
    public void Present(MapSceneSnapshot scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
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
        if (changedSceneIdentity ||
            _selectedObjectId is { } selected && !_scene.VisibleObjects.Any(item => item.Id == selected))
        {
            _selectedObjectId = null;
        }

        if (changedSceneIdentity)
        {
            _clusterFilter = null;
            _clusterFilterLabel = string.Empty;
            _searchText = string.Empty;
            _listPageIndex = 0;
        }

        _rendererNotice = string.Empty;
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

        Request(new(MapSceneViewChangeKind.SetLayerVisibility, LayerId: layerId, IsVisible: isVisible));
    }

    public void SelectObject(MapSceneObjectId objectId)
    {
        if (!_scene.VisibleObjects.Any(item => item.Id == objectId) || _selectedObjectId == objectId)
        {
            return;
        }

        ApplySelection(objectId);
    }

    public bool TrySelectAt(double viewportX, double viewportY)
    {
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

        SelectObject(hit.Object.Id);
        return true;
    }

    public void ClearSelection()
    {
        if (_selectedObjectId is null)
        {
            return;
        }

        ApplySelection(null);
    }

    public void RequestZoom(double direction)
    {
        if (!double.IsFinite(direction) || direction == 0)
        {
            return;
        }

        var factor = direction > 0 ? 1.25 : 0.8;
        var camera = _scene.View.Camera;
        var zoom = Math.Clamp(camera.Zoom * factor, 0.25, 16);
        Request(new(
            MapSceneViewChangeKind.SetCamera,
            Camera: new(camera.CenterX, camera.CenterY, zoom, camera.BearingDegrees, camera.PitchDegrees)));
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
        var worldDeltaX = unrotatedX / (_projection.Scale * camera.Zoom);
        var worldDeltaY = unrotatedY / (_projection.Scale * camera.Zoom);
        var bounds = _scene.Bounds;
        Request(new(
            MapSceneViewChangeKind.SetCamera,
            Camera: new(
                Math.Clamp(camera.CenterX - worldDeltaX, bounds.MinimumX, bounds.MaximumX),
                Math.Clamp(camera.CenterY - worldDeltaY, bounds.MinimumY, bounds.MaximumY),
                camera.Zoom,
                camera.BearingDegrees,
                camera.PitchDegrees)));
    }

    private void FitPlan()
    {
        var bounds = _scene.Bounds;
        Request(new(
            MapSceneViewChangeKind.SetCamera,
            Camera: new(
                bounds.MinimumX + (bounds.Width / 2),
                bounds.MinimumY + (bounds.Height / 2),
                1,
                0,
                0)));
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
        if (_pendingRevision == _scene.Revision)
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
            mode == MapSceneMode.Flat2D,
            CanRenderMode(mode),
            RendererUnavailableReason(mode),
            () => RequestMode(mode)))
        .ToArray();

    private void BuildFloors() => Floors = _scene.FloorIds
        .Select(floor => new MapSceneRendererFloorViewModel(
            floor,
            string.Equals(floor, _scene.View.SelectedFloorId, StringComparison.OrdinalIgnoreCase),
            () => SelectFloor(floor)))
        .ToArray();

    private void BuildLayers() => Layers = _scene.Layers
        .OrderBy(layer => layer.ZIndex)
        .ThenBy(layer => layer.Name, StringComparer.OrdinalIgnoreCase)
        .Select(layer => new MapSceneRendererLayerViewModel(
            layer,
            IsLayerVisible(layer.Id),
            _presentation,
            visible => SetLayerVisibility(layer.Id, visible)))
        .ToArray();

    private IReadOnlyList<MapSceneObject> RebuildProjectedObjects()
    {
        var visibleObjects = _scene.VisibleObjects;
        GeometryObjects = _projection.IsUsable
            ? visibleObjects
                .Where(item => item.Geometry.Kind != MapSceneGeometryKind.Point &&
                    item.Geometry.Points.All(_scene.Bounds.Contains))
                .Take(MaximumGeometryObjects)
                .Select(item => new MapSceneRendererGeometryViewModel(item, _projection))
                .ToArray()
            : [];
        SpatialObjects = BuildPointMarkers(visibleObjects);
        SelectedObject = _selectedObjectId is { } selected
            ? CreateSelectedObject(selected)
            : null;
        return visibleObjects;
    }

    private IReadOnlyList<MapSceneRendererObjectViewModel> BuildPointMarkers(IReadOnlyList<MapSceneObject> visibleObjects)
    {
        if (!_projection.IsUsable)
        {
            return [];
        }

        var points = visibleObjects
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
                    () => SelectObject(item.Id)))
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
                () => SelectObject(item.Id));
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
        _filteredListObjects = _scene.VisibleObjects
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
        OnPropertyChanged(nameof(SelectedObject));
        OnPropertyChanged(nameof(HasSelection));
    }

    private MapSceneRendererObjectViewModel? CreateSelectedObject(MapSceneObjectId selected)
    {
        var item = _scene.VisibleObjects.FirstOrDefault(candidate => candidate.Id == selected);
        return item is null
            ? null
            : MapSceneRendererObjectViewModel.ForObject(
                item,
                _projection,
                _scene.View.Camera,
                _presentation,
                true,
                () => SelectObject(item.Id));
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

        ReviewedAssetLabel = asset is null
            ? string.Empty
            : Format("Map.Asset.Label", asset.Attribution, asset.MapVersion, asset.GameVersion);
        UpdateBackgroundStatus(asset);
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
    }

    private bool CanRenderMode(MapSceneMode mode) =>
        mode == MapSceneMode.Flat2D && _scene.Capabilities.Flat2D.IsAvailable;

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
        }
        if (layers) OnPropertyChanged(nameof(Layers));
        if (visibleContent)
        {
            OnPropertyChanged(nameof(SpatialObjects));
            OnPropertyChanged(nameof(GeometryObjects));
            OnPropertyChanged(nameof(SelectedObject));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasSpatialObjects));
            OnPropertyChanged(nameof(ShowsEmptyMap));
            OnPropertyChanged(nameof(DenseSceneNotice));
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

    private void RaiseProjectionChanged()
    {
        foreach (var propertyName in new[]
                 {
                     nameof(SpatialObjects), nameof(GeometryObjects), nameof(SelectedObject), nameof(HasSpatialObjects),
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

    private MapSceneProjection CreateProjection() => new(_scene.Bounds, CanvasWidth, CanvasHeight, MapInset);

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
    public MapSceneRendererFloorViewModel(string id, bool isSelected, Action select)
    {
        Id = id;
        IsSelected = isSelected;
        SelectCommand = new DelegateCommand(select ?? throw new ArgumentNullException(nameof(select)));
    }

    public string Id { get; }
    public bool IsSelected { get; }
    public string AutomationId => $"v2-map-floor-{MapRendererToken.From(Id)}";
    public ICommand SelectCommand { get; }
}

public sealed class MapSceneRendererLayerViewModel
{
    public MapSceneRendererLayerViewModel(
        MapSceneLayer layer,
        bool isVisible,
        MapSceneRendererPresentation presentation,
        Action<bool> setVisible)
    {
        Layer = layer ?? throw new ArgumentNullException(nameof(layer));
        IsVisible = isVisible;
        ToggleLabel = presentation.Format(isVisible ? "Map.Layer.Hide" : "Map.Layer.Show", layer.Name);
        StateLabel = presentation.Get(isVisible ? "Map.Layer.Visible" : "Map.Layer.Hidden");
        ToggleCommand = new DelegateCommand(() => setVisible(!IsVisible));
    }

    public MapSceneLayer Layer { get; }
    public string Name => Layer.Name;
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
        string truthGlyph,
        string factionGlyph,
        string offerGlyph,
        bool isCluster,
        Action select)
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
        TruthGlyph = truthGlyph;
        FactionGlyph = factionGlyph;
        OfferGlyph = offerGlyph;
        IsCluster = isCluster;
        SelectCommand = new DelegateCommand(select ?? throw new ArgumentNullException(nameof(select)));
    }

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
    public string MarkerGlyph { get; }
    public string TruthGlyph { get; }
    public bool HasTruthGlyph => !string.IsNullOrWhiteSpace(TruthGlyph);
    public string FactionGlyph { get; }
    public bool HasFactionGlyph => !string.IsNullOrWhiteSpace(FactionGlyph);
    public string OfferGlyph { get; }
    public bool HasOfferGlyph => !string.IsNullOrWhiteSpace(OfferGlyph);
    public string AutomationId => $"v2-map-object-{MapRendererToken.From(Key)}";
    public ICommand SelectCommand { get; }

    public void SetSelected(bool selected) => SetProperty(ref _isSelected, selected, nameof(IsSelected));

    public void UpdateCamera(MapSceneCamera camera)
    {
        SetProperty(ref _markerInverseZoom, 1 / camera.Zoom, nameof(MarkerInverseZoom));
        SetProperty(ref _markerUprightDegrees, camera.BearingDegrees, nameof(MarkerUprightDegrees));
    }

    public static MapSceneRendererObjectViewModel ForObject(
        MapSceneObject sceneObject,
        MapSceneProjection projection,
        MapSceneCamera camera,
        MapSceneRendererPresentation presentation,
        bool isSelected,
        Action select)
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
            TruthGlyphFor(sceneObject.Truth),
            FactionGlyphFor(sceneObject),
            OfferGlyphFor(sceneObject),
            false,
            select);
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
            string.Empty,
            string.Empty,
            string.Empty,
            true,
            select);
    }

    private static string MarkerFor(MapSceneObject item) => item.Truth switch
    {
        MapSceneTruthKind.HistoricalEstimate => "≈",
        MapSceneTruthKind.LocalLastKnown => "◎",
        MapSceneTruthKind.TeamSharedLastKnown => "◉",
        _ => item.Kind switch
        {
            MapSceneObjectKind.Extract => "⇱",
            MapSceneObjectKind.Transit => "↔",
            MapSceneObjectKind.QuestObjective => "◇",
            MapSceneObjectKind.Waypoint => "◆",
            MapSceneObjectKind.Ping => "•",
            MapSceneObjectKind.Hazard => "!",
            MapSceneObjectKind.Lock => "⌑",
            MapSceneObjectKind.LootSpawn or MapSceneObjectKind.LootContainer => "$",
            MapSceneObjectKind.Route => "↝",
            MapSceneObjectKind.Risk => "△",
            _ => "●",
        },
    };

    private static string TruthGlyphFor(MapSceneTruthKind truth) => truth switch
    {
        MapSceneTruthKind.HistoricalEstimate => "H",
        MapSceneTruthKind.LocalLastKnown => "L",
        MapSceneTruthKind.TeamSharedLastKnown => "T",
        MapSceneTruthKind.PotentialSpawn => "?",
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
                _ => "?",
            }
            : string.Empty;

    private static string OfferGlyphFor(MapSceneObject item) =>
        MapSceneRendererViewModel.HasOfferStatus(item.Kind)
            ? item.OfferState switch
            {
                MapSceneOfferState.Offered => "✓",
                MapSceneOfferState.NotOffered => "×",
                _ => "?",
            }
            : string.Empty;
}

public sealed class MapSceneRendererGeometryViewModel
{
    public MapSceneRendererGeometryViewModel(MapSceneObject sceneObject, MapSceneProjection projection)
    {
        SceneObject = sceneObject ?? throw new ArgumentNullException(nameof(sceneObject));
        Points = sceneObject.Geometry.Points.Select(projection.Project).ToArray();
    }

    public MapSceneObject SceneObject { get; }
    public IReadOnlyList<MapSceneProjectedPoint> Points { get; }
    public MapSceneGeometryKind Kind => SceneObject.Geometry.Kind;
    public MapSceneTruthKind Truth => SceneObject.Truth;
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

public readonly record struct MapSceneProjectedPoint(double X, double Y);

public sealed class MapSceneProjection
{
    private readonly MapSceneBounds _bounds;
    private readonly double _canvasWidth;
    private readonly double _canvasHeight;

    public MapSceneProjection(MapSceneBounds bounds, double canvasWidth, double canvasHeight, double inset)
    {
        _bounds = bounds;
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
        var boundsWidth = bounds.Width;
        var boundsHeight = bounds.Height;
        var finiteBounds = double.IsFinite(boundsWidth) && double.IsFinite(boundsHeight) &&
            boundsWidth > 0 && boundsHeight > 0;
        var tentativeScale = finiteBounds
            ? Math.Min(
                Math.Max(1, canvasWidth - (inset * 2)) / boundsWidth,
                Math.Max(1, canvasHeight - (inset * 2)) / boundsHeight)
            : double.NaN;
        IsUsable = double.IsFinite(tentativeScale) && tentativeScale > 0;
        if (!IsUsable)
        {
            Scale = 1;
            MapWidth = Math.Max(1, canvasWidth - (inset * 2));
            MapHeight = Math.Max(1, canvasHeight - (inset * 2));
            MapLeft = inset;
            MapTop = inset;
            return;
        }

        Scale = tentativeScale;
        MapWidth = boundsWidth * Scale;
        MapHeight = boundsHeight * Scale;
        MapLeft = (canvasWidth - MapWidth) / 2;
        MapTop = (canvasHeight - MapHeight) / 2;
    }

    public bool IsUsable { get; }
    public double Scale { get; }
    public double MapLeft { get; }
    public double MapTop { get; }
    public double MapWidth { get; }
    public double MapHeight { get; }

    public MapSceneProjectedPoint Project(MapScenePoint point) => Project(point.X, point.Y);

    public MapSceneProjectedPoint Project(double x, double y) => new(
        IsUsable ? MapLeft + ((x - _bounds.MinimumX) * Scale) : _canvasWidth / 2,
        IsUsable ? MapTop + ((y - _bounds.MinimumY) * Scale) : _canvasHeight / 2);

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
            _bounds.MinimumX + ((cameraPoint.X + baseX - MapLeft) / Scale),
            _bounds.MinimumY + ((cameraPoint.Y + baseY - MapTop) / Scale));
        worldUnitsPerPixel = 1 / (Scale * camera.Zoom);
        return true;
    }
}

internal static class MapRendererToken
{
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
