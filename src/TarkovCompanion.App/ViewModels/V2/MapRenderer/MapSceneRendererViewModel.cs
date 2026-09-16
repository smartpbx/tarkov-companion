using System.Globalization;
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
/// bound, or cluster a scene for the current viewport, but it never rewrites scene truth.
/// </remarks>
public sealed class MapSceneRendererViewModel : BindableViewModel
{
    public const int MaximumPointMarkers = 280;
    public const int MaximumGeometryObjects = 300;
    public const int MaximumListItems = 300;
    public const double MarkerExtent = 48;

    private const int ClusterColumns = 20;
    private const int ClusterRows = 14;
    private const double MinimumViewportWidth = 320;
    private const double MinimumViewportHeight = 280;
    private const double MapInset = MarkerExtent / 2;

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

    public MapSceneRendererViewModel(
        MapSceneSnapshot scene,
        Func<Guid>? nextChangeId = null,
        Func<MapSceneAsset, IImage?>? reviewedAssetResolver = null)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _nextChangeId = nextChangeId ?? Guid.NewGuid;
        _reviewedAssetResolver = reviewedAssetResolver;
        _projection = CreateProjection();

        ClearSelectionCommand = new DelegateCommand(ClearSelection);
        FocusNextObjectCommand = new DelegateCommand(() => MoveSelection(1));
        FocusPreviousObjectCommand = new DelegateCommand(() => MoveSelection(-1));
        FitPlanCommand = new DelegateCommand(FitPlan);
        ZoomInCommand = new DelegateCommand(() => RequestZoom(1));
        ZoomOutCommand = new DelegateCommand(() => RequestZoom(-1));
        RebuildPresentation();
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

    public ICommand ClearSelectionCommand { get; }
    public ICommand FocusNextObjectCommand { get; }
    public ICommand FocusPreviousObjectCommand { get; }
    public ICommand FitPlanCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }

    public double CanvasWidth => _canvasWidth;
    public double CanvasHeight => _canvasHeight;
    public double MapLeft => _projection.MapLeft;
    public double MapTop => _projection.MapTop;
    public double MapWidth => _projection.MapWidth;
    public double MapHeight => _projection.MapHeight;
    public double StatusLeft => Math.Max(12, (CanvasWidth - 460) / 2);
    public double StatusTop => Math.Max(96, MapTop + 16);
    public double EmptyLeft => Math.Max(12, (CanvasWidth - 380) / 2);
    public double EmptyTop => Math.Max(72, (CanvasHeight - 100) / 2);
    public double CameraPreTranslateX => -_projection.Project(_scene.View.Camera.CenterX, _scene.View.Camera.CenterY).X;
    public double CameraPreTranslateY => -_projection.Project(_scene.View.Camera.CenterX, _scene.View.Camera.CenterY).Y;
    public double CameraPostTranslateX => CanvasWidth / 2;
    public double CameraPostTranslateY => CanvasHeight / 2;
    public double CameraZoom => _scene.View.Camera.Zoom;
    public double CameraRotationDegrees => -_scene.View.Camera.BearingDegrees;
    public double MarkerInverseZoom => 1 / CameraZoom;
    public double MarkerUprightDegrees => _scene.View.Camera.BearingDegrees;
    public string LocationLabel => _scene.LocationId;
    public string VariantLabel => _scene.VariantKey;
    public string ModeLabel => DescribeMode(_scene.View.Mode);
    public string RendererNotice => _rendererNotice;
    public bool HasRendererNotice => !string.IsNullOrWhiteSpace(RendererNotice);
    public bool HasFloorStack => _scene.Capabilities.FloorStack2D.IsAvailable && Floors.Count > 0;
    public bool HasFloors => Floors.Count > 0;
    public bool HasSpatialObjects => SpatialObjects.Count > 0 || GeometryObjects.Count > 0;
    public bool HasListItems => ListItems.Count > 0;
    public bool ShowsEmptyMap => !HasSpatialObjects;
    public bool ShowsEmptyList => !HasListItems;
    public bool HasSelection => SelectedObject is not null;
    public bool HasBackgroundImage => BackgroundImage is not null;
    public string EmptyMapMessage => "No visible map objects for this view.";
    public string EmptyListMessage => "No matching details for this view.";
    public string ReviewedAssetLabel { get; private set; } = string.Empty;
    public string BackgroundStatus { get; private set; } = string.Empty;
    public bool HasBackgroundStatus => !string.IsNullOrWhiteSpace(BackgroundStatus);
    public string DenseSceneNotice { get; private set; } = string.Empty;
    public bool HasDenseSceneNotice => !string.IsNullOrWhiteSpace(DenseSceneNotice);
    public string ThreeDimensionalFallback => _scene.View.Mode == MapSceneMode.Interior3D
        ? "3D is selected; this renderer is showing the reviewed 2D plan."
        : _scene.Capabilities.Interior3D.IsAvailable
            ? string.Empty
            : $"3D view unavailable. {_scene.Capabilities.Interior3D.UnavailableReason}";
    public bool ShowsThreeDimensionalFallback => !string.IsNullOrWhiteSpace(ThreeDimensionalFallback);

    /// <summary>Replaces the display only after the canonical owner accepted or refreshed it.</summary>
    public void Present(MapSceneSnapshot scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var changedSceneIdentity = !string.Equals(_scene.LocationId, scene.LocationId, StringComparison.Ordinal) ||
            !string.Equals(_scene.VariantKey, scene.VariantKey, StringComparison.Ordinal);
        _scene = scene;
        _pendingRevision = null;
        if (changedSceneIdentity ||
            _selectedObjectId is { } selected && !_scene.Objects.Any(item => item.Id == selected))
        {
            _selectedObjectId = null;
        }

        _rendererNotice = string.Empty;
        RebuildPresentation();
    }

    /// <summary>Updates only the projection; it does not create a new canonical camera state.</summary>
    public void SetViewportSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height))
        {
            return;
        }

        width = Math.Max(MinimumViewportWidth, width);
        height = Math.Max(MinimumViewportHeight, height);
        if (Math.Abs(width - _canvasWidth) < 0.5 && Math.Abs(height - _canvasHeight) < 0.5)
        {
            return;
        }

        _canvasWidth = width;
        _canvasHeight = height;
        _projection = CreateProjection();
        RebuildProjectedObjects();
        RaiseProjectionChanged();
    }

    public void RequestMode(MapSceneMode mode)
    {
        if (!_scene.Capabilities.Supports(mode))
        {
            SetRendererNotice($"{DescribeMode(mode)} is unavailable. {UnavailableReason(mode)}");
            return;
        }

        Request(new(MapSceneViewChangeKind.SetMode, Mode: mode));
    }

    public void SelectFloor(string? floorId)
    {
        if (floorId is not null && !Floors.Any(floor => string.Equals(floor.Id, floorId, StringComparison.OrdinalIgnoreCase)))
        {
            SetRendererNotice("That floor is not available in this map.");
            return;
        }

        Request(new(MapSceneViewChangeKind.SelectFloor, FloorId: floorId));
    }

    public void SetLayerVisibility(MapSceneLayerId layerId, bool isVisible)
    {
        if (!_scene.Layers.Any(layer => layer.Id == layerId))
        {
            SetRendererNotice("That layer is no longer available.");
            return;
        }

        Request(new(MapSceneViewChangeKind.SetLayerVisibility, LayerId: layerId, IsVisible: isVisible));
    }

    public void SelectObject(MapSceneObjectId objectId)
    {
        if (!_scene.VisibleObjects.Any(item => item.Id == objectId))
        {
            return;
        }

        _selectedObjectId = objectId;
        RebuildPresentation();
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

        var hit = MapSceneHitTesting.HitTest(_scene, point, worldUnitsPerPixel * 24).FirstOrDefault();
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

        _selectedObjectId = null;
        RebuildPresentation();
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
        if (ListItems.Count == 0)
        {
            return;
        }

        var current = -1;
        if (_selectedObjectId is { } selected)
        {
            for (var index = 0; index < ListItems.Count; index++)
            {
                if (ListItems[index].Id == selected)
                {
                    current = index;
                    break;
                }
            }
        }

        var next = ((current + direction) % ListItems.Count + ListItems.Count) % ListItems.Count;
        SelectObject(ListItems[next].Id);
    }

    private void Request(MapSceneRendererChange change)
    {
        if (_pendingRevision == _scene.Revision)
        {
            SetRendererNotice("Waiting for the shared map to confirm the previous change.");
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

    private void RebuildPresentation()
    {
        _projection = CreateProjection();
        Modes = Enum.GetValues<MapSceneMode>()
            .Select(mode => new MapSceneRendererModeViewModel(
                mode,
                DescribeMode(mode),
                _scene.View.Mode == mode,
                _scene.Capabilities.Supports(mode),
                UnavailableReason(mode),
                () => RequestMode(mode)))
            .ToArray();
        Floors = _scene.FloorIds
            .Select(floor => new MapSceneRendererFloorViewModel(
                floor,
                string.Equals(floor, _scene.View.SelectedFloorId, StringComparison.OrdinalIgnoreCase),
                () => SelectFloor(floor)))
            .ToArray();
        Layers = _scene.Layers
            .OrderBy(layer => layer.ZIndex)
            .ThenBy(layer => layer.Name, StringComparer.OrdinalIgnoreCase)
            .Select(layer => new MapSceneRendererLayerViewModel(
                layer,
                IsLayerVisible(layer.Id),
                visible => SetLayerVisibility(layer.Id, visible)))
            .ToArray();

        var visibleObjects = RebuildProjectedObjects();
        ListItems = BuildListItems(_scene.ListEntries);
        ResolveBackground();
        BuildDenseSceneNotice(visibleObjects);
        RaisePresentationChanged();
    }

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
            : Array.Empty<MapSceneRendererGeometryViewModel>();
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
                    item.Id == _selectedObjectId,
                    () => SelectObject(item.Id)))
                .ToArray();
        }

        return points
            .GroupBy(item => ClusterCell(item.Geometry.Points[0]))
            .OrderBy(group => group.Key.Row)
            .ThenBy(group => group.Key.Column)
            .Select(group => group.Count() == 1
                ? MapSceneRendererObjectViewModel.ForObject(
                    group.First(),
                    _projection,
                    _scene.View.Camera,
                    group.First().Id == _selectedObjectId,
                    () => SelectObject(group.First().Id))
                : MapSceneRendererObjectViewModel.ForCluster(
                    group.Key.Column,
                    group.Key.Row,
                    group.ToArray(),
                    _projection,
                    _scene.View.Camera,
                    () => SetRendererNotice($"{group.Count()} nearby items are grouped here. Use layers or the details list to narrow them.")))
            .Take(MaximumPointMarkers)
            .ToArray();
    }

    private IReadOnlyList<MapSceneRendererListItemViewModel> BuildListItems(IReadOnlyList<MapSceneListEntry> entries)
    {
        var selected = _selectedObjectId;
        var bounded = entries.Take(MaximumListItems).ToList();
        if (selected is { } selectedId && bounded.All(item => item.Id != selectedId))
        {
            var selectedEntry = entries.FirstOrDefault(item => item.Id == selectedId);
            if (selectedEntry is not null && bounded.Count > 0)
            {
                bounded[^1] = selectedEntry;
            }
        }

        return bounded
            .Select(item => new MapSceneRendererListItemViewModel(
                item,
                item.Id == selected,
                () => SelectObject(item.Id)))
            .ToArray();
    }

    private MapSceneRendererObjectViewModel? CreateSelectedObject(MapSceneObjectId selected)
    {
        var item = _scene.VisibleObjects.FirstOrDefault(candidate => candidate.Id == selected);
        return item is null
            ? null
            : MapSceneRendererObjectViewModel.ForObject(item, _projection, _scene.View.Camera, true, () => SelectObject(item.Id));
    }

    private (int Column, int Row) ClusterCell(MapScenePoint point)
    {
        var normalizedX = (point.X - _scene.Bounds.MinimumX) / _scene.Bounds.Width;
        var normalizedY = (point.Y - _scene.Bounds.MinimumY) / _scene.Bounds.Height;
        return (
            Math.Clamp((int)(normalizedX * ClusterColumns), 0, ClusterColumns - 1),
            Math.Clamp((int)(normalizedY * ClusterRows), 0, ClusterRows - 1));
    }

    private void ResolveBackground()
    {
        var asset = _scene.Assets
            .Where(item => item.Kind == MapSceneAssetKind.Background2D)
            .Concat(_scene.Assets.Where(item => item.Kind == MapSceneAssetKind.Floor2D))
            .FirstOrDefault();
        BackgroundImage = asset is null || _reviewedAssetResolver is null
            ? null
            : _reviewedAssetResolver(asset);
        ReviewedAssetLabel = asset is null
            ? string.Empty
            : $"{asset.Attribution} · map {asset.MapVersion} · game {asset.GameVersion}";
        BackgroundStatus = !_projection.IsUsable
            ? "The reviewed scene bounds are too large to project safely. Spatial overlays are withheld."
            : asset is null
                ? "No reviewed 2D artwork is available. Spatial references are shown without a background."
                : BackgroundImage is null
                    ? "Reviewed artwork is not cached on this device. Spatial references remain available."
                    : string.Empty;
    }

    private void BuildDenseSceneNotice(IReadOnlyList<MapSceneObject> visibleObjects)
    {
        var pointCount = visibleObjects.Count(item => item.Geometry.Kind == MapSceneGeometryKind.Point);
        var geometryCount = visibleObjects.Count - pointCount;
        var outsideBounds = visibleObjects.Count(item => item.Geometry.Points.Any(point => !_scene.Bounds.Contains(point)));
        var listCount = _scene.ListEntries.Count;
        var messages = new List<string>(3);
        if (pointCount > MaximumPointMarkers)
        {
            messages.Add($"{pointCount.ToString("N0", CultureInfo.CurrentCulture)} points are grouped into {SpatialObjects.Count.ToString("N0", CultureInfo.CurrentCulture)} markers");
        }
        if (geometryCount > MaximumGeometryObjects)
        {
            messages.Add($"showing {MaximumGeometryObjects.ToString("N0", CultureInfo.CurrentCulture)} of {geometryCount.ToString("N0", CultureInfo.CurrentCulture)} shapes");
        }
        if (listCount > MaximumListItems)
        {
            messages.Add($"showing {MaximumListItems.ToString("N0", CultureInfo.CurrentCulture)} of {listCount.ToString("N0", CultureInfo.CurrentCulture)} details");
        }
        if (outsideBounds > 0)
        {
            messages.Add($"{outsideBounds.ToString("N0", CultureInfo.CurrentCulture)} features outside reviewed bounds are list-only");
        }

        DenseSceneNotice = messages.Count == 0
            ? string.Empty
            : string.Join("; ", messages) + ". Use layer filters to narrow the view.";
    }

    private void RaisePresentationChanged()
    {
        foreach (var propertyName in new[]
                 {
                     nameof(Scene), nameof(Modes), nameof(Floors), nameof(Layers), nameof(SpatialObjects),
                     nameof(GeometryObjects), nameof(ListItems), nameof(SelectedObject), nameof(BackgroundImage),
                     nameof(HasBackgroundImage), nameof(ReviewedAssetLabel), nameof(BackgroundStatus),
                     nameof(HasBackgroundStatus), nameof(DenseSceneNotice), nameof(HasDenseSceneNotice),
                     nameof(CanvasWidth), nameof(CanvasHeight), nameof(MapLeft), nameof(MapTop), nameof(MapWidth),
                     nameof(MapHeight), nameof(StatusLeft), nameof(StatusTop), nameof(EmptyLeft), nameof(EmptyTop),
                     nameof(CameraPreTranslateX), nameof(CameraPreTranslateY),
                     nameof(CameraPostTranslateX), nameof(CameraPostTranslateY), nameof(CameraZoom),
                     nameof(CameraRotationDegrees), nameof(MarkerInverseZoom), nameof(MarkerUprightDegrees),
                     nameof(LocationLabel), nameof(VariantLabel), nameof(ModeLabel), nameof(RendererNotice),
                     nameof(HasRendererNotice), nameof(HasFloorStack), nameof(HasFloors), nameof(HasSpatialObjects),
                     nameof(HasListItems), nameof(ShowsEmptyMap), nameof(ShowsEmptyList), nameof(HasSelection),
                     nameof(ThreeDimensionalFallback), nameof(ShowsThreeDimensionalFallback),
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
                     nameof(MapWidth), nameof(MapHeight), nameof(StatusLeft), nameof(StatusTop), nameof(EmptyLeft),
                     nameof(EmptyTop), nameof(CameraPreTranslateX), nameof(CameraPreTranslateY),
                     nameof(CameraPostTranslateX), nameof(CameraPostTranslateY),
                 })
        {
            OnPropertyChanged(propertyName);
        }
    }

    private MapSceneProjection CreateProjection() => new(_scene.Bounds, CanvasWidth, CanvasHeight, MapInset);

    private bool IsLayerVisible(MapSceneLayerId layerId) => _scene.View.Layers
        .FirstOrDefault(state => state.LayerId == layerId)?.IsVisible ??
        _scene.Layers.Single(layer => layer.Id == layerId).IsVisibleByDefault;

    private string UnavailableReason(MapSceneMode mode) => mode switch
    {
        MapSceneMode.Flat2D => _scene.Capabilities.Flat2D.UnavailableReason ?? string.Empty,
        MapSceneMode.FloorStack2D => _scene.Capabilities.FloorStack2D.UnavailableReason ?? string.Empty,
        MapSceneMode.Interior3D => _scene.Capabilities.Interior3D.UnavailableReason ?? string.Empty,
        _ => string.Empty,
    };

    private void SetRendererNotice(string value)
    {
        _rendererNotice = value;
        OnPropertyChanged(nameof(RendererNotice));
        OnPropertyChanged(nameof(HasRendererNotice));
    }

    internal static string DescribeMode(MapSceneMode mode) => mode switch
    {
        MapSceneMode.Flat2D => "2D plan",
        MapSceneMode.FloorStack2D => "Floor stack",
        MapSceneMode.Interior3D => "3D interior",
        _ => mode.ToString(),
    };

    internal static string DescribeKind(MapSceneObjectKind kind) => kind switch
    {
        MapSceneObjectKind.Extract => "Extract",
        MapSceneObjectKind.Transit => "Transit",
        MapSceneObjectKind.SpawnArea => "Spawn area",
        MapSceneObjectKind.LootSpawn => "Loot spawn",
        MapSceneObjectKind.LootContainer => "Loot container",
        MapSceneObjectKind.Hazard => "Hazard",
        MapSceneObjectKind.Lock => "Locked entry",
        MapSceneObjectKind.QuestObjective => "Quest objective",
        MapSceneObjectKind.Route => "Route",
        MapSceneObjectKind.Risk => "Risk area",
        MapSceneObjectKind.Traffic => "Traffic estimate",
        MapSceneObjectKind.LastKnownPosition => "Last known position",
        MapSceneObjectKind.TeammateLastKnown => "Teammate last known",
        MapSceneObjectKind.Ping => "Ping",
        MapSceneObjectKind.Waypoint => "Waypoint",
        MapSceneObjectKind.Label => "Map label",
        _ => "Map object",
    };

    internal static string DescribeTruth(MapSceneTruthKind truth) => truth switch
    {
        MapSceneTruthKind.StaticReference => "Reference",
        MapSceneTruthKind.PotentialSpawn => "Potential spawn",
        MapSceneTruthKind.LocalLastKnown => "Local last known",
        MapSceneTruthKind.TeamSharedLastKnown => "Team-shared last known",
        MapSceneTruthKind.HistoricalEstimate => "Historical estimate",
        MapSceneTruthKind.PersonalPlan => "Personal plan",
        MapSceneTruthKind.UserAuthored => "Map note",
        _ => "Unclassified",
    };

    internal static string DescribeFaction(MapFeatureFaction faction) => faction switch
    {
        MapFeatureFaction.Pmc => "PMC",
        MapFeatureFaction.Scav => "Scav",
        MapFeatureFaction.Shared => "PMC and Scav",
        _ => "Faction unknown",
    };

    internal static string DescribeOffer(MapSceneOfferState offerState) => offerState switch
    {
        MapSceneOfferState.Offered => "Offered this raid",
        MapSceneOfferState.NotOffered => "Not offered this raid",
        _ => "Offer status unknown",
    };

    internal static bool HasOfferStatus(MapSceneObjectKind kind) =>
        kind is MapSceneObjectKind.Extract or MapSceneObjectKind.Transit;

    internal static string DescribeEvidence(DataProvenance provenance)
    {
        var confidence = provenance.Confidence is { } value
            ? $" · {value.Value.ToString("P0", CultureInfo.CurrentCulture)} confidence"
            : string.Empty;
        return $"{provenance.Source} · observed {provenance.ObservedUtc.ToLocalTime():g}{confidence}";
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
    public MapSceneRendererLayerViewModel(MapSceneLayer layer, bool isVisible, Action<bool> setVisible)
    {
        Layer = layer ?? throw new ArgumentNullException(nameof(layer));
        IsVisible = isVisible;
        ToggleCommand = new DelegateCommand(() => setVisible(!IsVisible));
    }

    public MapSceneLayer Layer { get; }
    public string Name => Layer.Name;
    public bool IsVisible { get; }
    public string ToggleLabel => IsVisible ? $"Hide {Name}" : $"Show {Name}";
    public string StateLabel => IsVisible ? "Visible" : "Hidden";
    public string AutomationId => $"v2-map-layer-{MapRendererToken.From(Layer.Id.Value)}";
    public ICommand ToggleCommand { get; }
}

public sealed class MapSceneRendererObjectViewModel
{
    private MapSceneRendererObjectViewModel(
        MapSceneObject? sceneObject,
        string key,
        string label,
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
        bool isCluster,
        Action select)
    {
        SceneObject = sceneObject;
        Key = key;
        Label = label;
        Detail = detail;
        KindLabel = kindLabel;
        TruthLabel = truthLabel;
        FactionLabel = factionLabel;
        OfferedLabel = offerLabel;
        HasOfferStatus = hasOfferStatus;
        EvidenceLabel = evidenceLabel;
        EstimateLabel = estimateLabel;
        IsSelected = isSelected;
        AnchorLeft = anchorLeft;
        AnchorTop = anchorTop;
        MarkerInverseZoom = markerInverseZoom;
        MarkerUprightDegrees = markerUprightDegrees;
        MarkerGlyph = markerGlyph;
        IsCluster = isCluster;
        SelectCommand = new DelegateCommand(select ?? throw new ArgumentNullException(nameof(select)));
    }

    public MapSceneObject? SceneObject { get; }
    public MapSceneObjectId? ObjectId => SceneObject?.Id;
    public string Key { get; }
    public string Label { get; }
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
    public bool IsSelected { get; }
    public bool IsCluster { get; }
    public bool IsOffered => SceneObject?.OfferState == MapSceneOfferState.Offered;
    public bool IsKnownNotOffered => SceneObject?.OfferState == MapSceneOfferState.NotOffered;
    public bool IsOfferUnknown => HasOfferStatus && SceneObject?.OfferState == MapSceneOfferState.Unknown;
    public double AnchorLeft { get; }
    public double AnchorTop { get; }
    public double MarkerInverseZoom { get; }
    public double MarkerUprightDegrees { get; }
    public string MarkerGlyph { get; }
    public string AutomationId => $"v2-map-object-{MapRendererToken.From(Key)}";
    public ICommand SelectCommand { get; }

    public static MapSceneRendererObjectViewModel ForObject(
        MapSceneObject sceneObject,
        MapSceneProjection projection,
        MapSceneCamera camera,
        bool isSelected,
        Action select)
    {
        var anchor = projection.Project(sceneObject.Geometry.Points[0]);
        return new(
            sceneObject,
            sceneObject.Id.Value,
            sceneObject.Label,
            sceneObject.Detail,
            MapSceneRendererViewModel.DescribeKind(sceneObject.Kind),
            MapSceneRendererViewModel.DescribeTruth(sceneObject.Truth),
            MapSceneRendererViewModel.DescribeFaction(sceneObject.Faction),
            MapSceneRendererViewModel.DescribeOffer(sceneObject.OfferState),
            MapSceneRendererViewModel.HasOfferStatus(sceneObject.Kind),
            MapSceneRendererViewModel.DescribeEvidence(sceneObject.Provenance),
            DescribeEstimate(sceneObject.Estimate),
            isSelected,
            anchor.X - (MapSceneRendererViewModel.MarkerExtent / 2),
            anchor.Y - (MapSceneRendererViewModel.MarkerExtent / 2),
            1 / camera.Zoom,
            camera.BearingDegrees,
            MarkerFor(sceneObject.Kind),
            false,
            select);
    }

    public static MapSceneRendererObjectViewModel ForCluster(
        int column,
        int row,
        IReadOnlyList<MapSceneObject> objects,
        MapSceneProjection projection,
        MapSceneCamera camera,
        Action select)
    {
        var point = new MapScenePoint(
            objects.Average(item => item.Geometry.Points[0].X),
            objects.Average(item => item.Geometry.Points[0].Y));
        var anchor = projection.Project(point);
        var count = objects.Count;
        return new(
            null,
            $"cluster-{column}-{row}",
            $"{count.ToString("N0", CultureInfo.CurrentCulture)} nearby items",
            "An approximate cluster of sourced points. Narrow the visible layers for individual locations.",
            "Point cluster",
            "Multiple source records",
            "Mixed or unknown factions",
            string.Empty,
            false,
            "Open the details list for individual evidence.",
            string.Empty,
            false,
            anchor.X - (MapSceneRendererViewModel.MarkerExtent / 2),
            anchor.Y - (MapSceneRendererViewModel.MarkerExtent / 2),
            1 / camera.Zoom,
            camera.BearingDegrees,
            count > 99 ? "99+" : count.ToString(CultureInfo.CurrentCulture),
            true,
            select);
    }

    private static string MarkerFor(MapSceneObjectKind kind) => kind switch
    {
        MapSceneObjectKind.Extract => "⇱",
        MapSceneObjectKind.Transit => "↔",
        MapSceneObjectKind.QuestObjective => "◇",
        MapSceneObjectKind.Waypoint => "◆",
        MapSceneObjectKind.Ping => "•",
        MapSceneObjectKind.Hazard => "!",
        MapSceneObjectKind.Lock => "⌑",
        MapSceneObjectKind.LootSpawn or MapSceneObjectKind.LootContainer => "$",
        _ => "●",
    };

    private static string DescribeEstimate(MapSceneEstimateMetadata? estimate) => estimate is null
        ? string.Empty
        : $"Historical model {estimate.ModelVersion} · observed from {estimate.ObservedFromUtc.ToLocalTime():g} · " +
          $"data through {estimate.DataThroughUtc.ToLocalTime():g} · generated {estimate.GeneratedUtc.ToLocalTime():g} · " +
          $"{estimate.Coverage} · {estimate.Calibration} · transform {estimate.TransformVersion}";
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

public sealed class MapSceneRendererListItemViewModel
{
    public MapSceneRendererListItemViewModel(MapSceneListEntry entry, bool isSelected, Action select)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        Id = entry.Id;
        Label = entry.Label;
        Detail = entry.Detail;
        KindLabel = MapSceneRendererViewModel.DescribeKind(entry.Kind);
        TruthLabel = MapSceneRendererViewModel.DescribeTruth(entry.Truth);
        FactionLabel = MapSceneRendererViewModel.DescribeFaction(entry.Faction);
        OfferState = entry.OfferState;
        OfferedLabel = MapSceneRendererViewModel.DescribeOffer(entry.OfferState);
        HasOfferStatus = MapSceneRendererViewModel.HasOfferStatus(entry.Kind);
        EvidenceLabel = MapSceneRendererViewModel.DescribeEvidence(entry.Provenance);
        IsSelected = isSelected;
        SelectCommand = new DelegateCommand(select ?? throw new ArgumentNullException(nameof(select)));
    }

    public MapSceneListEntry Entry { get; }
    public MapSceneObjectId Id { get; }
    public string Label { get; }
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
    public bool IsSelected { get; }
    public string AutomationId => $"v2-map-list-{MapRendererToken.From(Id.Value)}";
    public ICommand SelectCommand { get; }
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
        var suffix = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..8]
            .ToLowerInvariant();
        return $"{normalized}-{suffix}";
    }
}
