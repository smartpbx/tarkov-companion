using System.Windows.Input;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.ViewModels.V2.MapRenderer;

/// <summary>
/// Presents the renderer-neutral map scene without owning its state transition.
/// </summary>
/// <remarks>
/// The desktop control deliberately emits the same revision-checked change a paired screen uses.
/// Applying it locally here would give the desktop a private version of the scene while a tablet
/// was still looking at the canonical one.
/// </remarks>
public sealed class MapSceneRendererViewModel : BindableViewModel
{
    private readonly Func<Guid> _nextChangeId;
    private MapSceneSnapshot _scene;
    private MapSceneObjectId? _selectedObjectId;
    private string _rendererNotice = string.Empty;
    private MapSceneViewChange? _lastRequestedChange;

    public MapSceneRendererViewModel(MapSceneSnapshot scene, Func<Guid>? nextChangeId = null)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _nextChangeId = nextChangeId ?? Guid.NewGuid;

        ClearSelectionCommand = new DelegateCommand(ClearSelection);
        FocusNextObjectCommand = new DelegateCommand(() => MoveSelection(1));
        FocusPreviousObjectCommand = new DelegateCommand(() => MoveSelection(-1));
        FitPlanCommand = new DelegateCommand(FitPlan);
        RebuildPresentation();
    }

    /// <summary>Raised for a host to send through the canonical scene reducer.</summary>
    public event Action<MapSceneViewChange>? ViewChangeRequested;

    public MapSceneSnapshot Scene => _scene;
    public IReadOnlyList<MapSceneRendererModeViewModel> Modes { get; private set; } = [];
    public IReadOnlyList<MapSceneRendererFloorViewModel> Floors { get; private set; } = [];
    public IReadOnlyList<MapSceneRendererLayerViewModel> Layers { get; private set; } = [];
    public IReadOnlyList<MapSceneRendererObjectViewModel> SpatialObjects { get; private set; } = [];
    public IReadOnlyList<MapSceneRendererListItemViewModel> ListItems { get; private set; } = [];
    public MapSceneRendererObjectViewModel? SelectedObject { get; private set; }
    public MapSceneViewChange? LastRequestedChange
    {
        get => _lastRequestedChange;
        private set => SetProperty(ref _lastRequestedChange, value);
    }

    public ICommand ClearSelectionCommand { get; }
    public ICommand FocusNextObjectCommand { get; }
    public ICommand FocusPreviousObjectCommand { get; }
    public ICommand FitPlanCommand { get; }

    public string LocationLabel => _scene.LocationId;
    public string VariantLabel => _scene.VariantKey;
    public string ModeLabel => DescribeMode(_scene.View.Mode);
    public string RendererNotice => _rendererNotice;
    public bool HasRendererNotice => !string.IsNullOrWhiteSpace(RendererNotice);
    public bool HasFloorStack => _scene.Capabilities.FloorStack2D.IsAvailable && Floors.Count > 0;
    public bool HasFloors => Floors.Count > 0;
    public bool HasSpatialObjects => SpatialObjects.Count > 0;
    public bool HasListItems => ListItems.Count > 0;
    public bool ShowsEmptyMap => !HasSpatialObjects;
    public bool ShowsEmptyList => !HasListItems;
    public bool HasSelection => SelectedObject is not null;
    public string EmptyMapMessage => "No visible map objects for this view.";
    public string EmptyListMessage => "No matching details for this view.";
    public string ThreeDimensionalFallback => _scene.View.Mode == MapSceneMode.Interior3D
        ? "3D is selected; this renderer is showing the reviewed 2D plan."
        : _scene.Capabilities.Interior3D.IsAvailable
            ? string.Empty
            : $"3D view unavailable. {_scene.Capabilities.Interior3D.UnavailableReason}";
    public bool ShowsThreeDimensionalFallback => !string.IsNullOrWhiteSpace(ThreeDimensionalFallback);

    /// <summary>Replaces the locally displayed immutable scene after its owner applies a change.</summary>
    public void Present(MapSceneSnapshot scene)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        if (_selectedObjectId is { } selected && !_scene.Objects.Any(item => item.Id == selected))
        {
            _selectedObjectId = null;
        }

        _rendererNotice = string.Empty;
        RebuildPresentation();
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

    public void SetLayerVisibility(MapSceneLayerId layerId, bool isVisible) =>
        Request(new(MapSceneViewChangeKind.SetLayerVisibility, LayerId: layerId, IsVisible: isVisible));

    public void SelectObject(MapSceneObjectId objectId)
    {
        if (!SpatialObjects.Any(item => item.Id == objectId) && !ListItems.Any(item => item.Id == objectId))
        {
            return;
        }

        _selectedObjectId = objectId;
        RebuildPresentation();
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
        LastRequestedChange = requested;
        ViewChangeRequested?.Invoke(requested);
    }

    private void RebuildPresentation()
    {
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
        SpatialObjects = _scene.VisibleObjects
            .Select(item => new MapSceneRendererObjectViewModel(item, _scene.Bounds, item.Id == _selectedObjectId, () => SelectObject(item.Id)))
            .ToArray();
        ListItems = _scene.ListEntries
            .Select(item => new MapSceneRendererListItemViewModel(item, item.Id == _selectedObjectId, () => SelectObject(item.Id)))
            .ToArray();
        SelectedObject = _selectedObjectId is { } selected
            ? SpatialObjects.FirstOrDefault(item => item.Id == selected) ?? CreateSelectedFromList(selected)
            : null;

        OnPropertyChanged(nameof(Scene));
        OnPropertyChanged(nameof(Modes));
        OnPropertyChanged(nameof(Floors));
        OnPropertyChanged(nameof(Layers));
        OnPropertyChanged(nameof(SpatialObjects));
        OnPropertyChanged(nameof(ListItems));
        OnPropertyChanged(nameof(SelectedObject));
        OnPropertyChanged(nameof(LocationLabel));
        OnPropertyChanged(nameof(VariantLabel));
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(HasFloorStack));
        OnPropertyChanged(nameof(HasFloors));
        OnPropertyChanged(nameof(HasSpatialObjects));
        OnPropertyChanged(nameof(HasListItems));
        OnPropertyChanged(nameof(ShowsEmptyMap));
        OnPropertyChanged(nameof(ShowsEmptyList));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ThreeDimensionalFallback));
        OnPropertyChanged(nameof(ShowsThreeDimensionalFallback));
    }

    private MapSceneRendererObjectViewModel? CreateSelectedFromList(MapSceneObjectId selected)
    {
        var item = _scene.Objects.FirstOrDefault(candidate => candidate.Id == selected);
        return item is null ? null : new(item, _scene.Bounds, true, () => SelectObject(item.Id));
    }

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

    private static string DescribeMode(MapSceneMode mode) => mode switch
    {
        MapSceneMode.Flat2D => "2D plan",
        MapSceneMode.FloorStack2D => "Floor stack",
        MapSceneMode.Interior3D => "3D interior",
        _ => mode.ToString(),
    };

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
    public string AutomationId => $"v2-map-floor-{Token(Id)}";
    public ICommand SelectCommand { get; }

    private static string Token(string value) => new(value.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray());
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
    public string AutomationId => $"v2-map-layer-{Token(Layer.Id.Value)}";
    public ICommand ToggleCommand { get; }

    private static string Token(string value) => new(value.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray());
}

public sealed class MapSceneRendererObjectViewModel
{
    private const double CanvasWidth = 1000;
    private const double CanvasHeight = 700;

    public MapSceneRendererObjectViewModel(MapSceneObject sceneObject, MapSceneBounds bounds, bool isSelected, Action select)
    {
        SceneObject = sceneObject ?? throw new ArgumentNullException(nameof(sceneObject));
        Id = sceneObject.Id;
        Label = sceneObject.Label;
        Detail = sceneObject.Detail;
        KindLabel = DescribeKind(sceneObject.Kind);
        TruthLabel = DescribeTruth(sceneObject.Truth);
        FactionLabel = DescribeFaction(sceneObject.Faction);
        OfferState = sceneObject.OfferState;
        OfferedLabel = DescribeOffer(sceneObject.OfferState);
        IsSelected = isSelected;
        var anchor = Average(sceneObject.Geometry.Points);
        AnchorX = Math.Round(Math.Clamp((anchor.X - bounds.MinimumX) / bounds.Width, 0, 1) * CanvasWidth, MidpointRounding.AwayFromZero);
        AnchorY = Math.Round(Math.Clamp((anchor.Y - bounds.MinimumY) / bounds.Height, 0, 1) * CanvasHeight, MidpointRounding.AwayFromZero);
        SelectCommand = new DelegateCommand(select ?? throw new ArgumentNullException(nameof(select)));
    }

    public MapSceneObject SceneObject { get; }
    public MapSceneObjectId Id { get; }
    public string Label { get; }
    public string? Detail { get; }
    public string KindLabel { get; }
    public string TruthLabel { get; }
    public string FactionLabel { get; }
    public MapSceneOfferState OfferState { get; }
    public string OfferedLabel { get; }
    public bool IsOffered => OfferState == MapSceneOfferState.Offered;
    public bool IsKnownNotOffered => OfferState == MapSceneOfferState.NotOffered;
    public bool IsOfferUnknown => OfferState == MapSceneOfferState.Unknown;
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool IsSelected { get; }
    public double AnchorX { get; }
    public double AnchorY { get; }
    public string MarkerGlyph => SceneObject.Kind switch
    {
        MapSceneObjectKind.Extract => "⇱",
        MapSceneObjectKind.Transit => "↔",
        MapSceneObjectKind.QuestObjective => "◇",
        MapSceneObjectKind.Waypoint => "◆",
        MapSceneObjectKind.Ping => "•",
        _ => "●",
    };
    public string AutomationId => $"v2-map-object-{Token(Id.Value)}";
    public ICommand SelectCommand { get; }

    private static MapScenePoint Average(IReadOnlyList<MapScenePoint> points) => new(
        points.Average(point => point.X),
        points.Average(point => point.Y));

    private static string DescribeKind(MapSceneObjectKind kind) => kind switch
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

    private static string DescribeTruth(MapSceneTruthKind truth) => truth switch
    {
        MapSceneTruthKind.StaticReference => "Reference",
        MapSceneTruthKind.PotentialSpawn => "Potential",
        MapSceneTruthKind.LocalLastKnown => "Last known",
        MapSceneTruthKind.TeamSharedLastKnown => "Team-shared last known",
        MapSceneTruthKind.HistoricalEstimate => "Historical estimate",
        MapSceneTruthKind.PersonalPlan => "Personal plan",
        MapSceneTruthKind.UserAuthored => "Map note",
        _ => "Unclassified",
    };

    private static string DescribeFaction(MapFeatureFaction faction) => faction switch
    {
        MapFeatureFaction.Pmc => "PMC",
        MapFeatureFaction.Scav => "Scav",
        MapFeatureFaction.Shared => "Shared",
        _ => "All sides",
    };

    private static string DescribeOffer(MapSceneOfferState offerState) => offerState switch
    {
        MapSceneOfferState.Offered => "Offered this raid",
        MapSceneOfferState.NotOffered => "Not offered this raid",
        _ => "Offer status unknown",
    };

    private static string Token(string value) => new(value.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray());
}

public sealed class MapSceneRendererListItemViewModel
{
    public MapSceneRendererListItemViewModel(MapSceneListEntry entry, bool isSelected, Action select)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        Id = entry.Id;
        Label = entry.Label;
        Detail = entry.Detail;
        KindLabel = entry.Kind.ToString();
        TruthLabel = entry.Truth == MapSceneTruthKind.HistoricalEstimate ? "Historical estimate" : entry.Truth.ToString();
        FactionLabel = entry.Faction switch
        {
            MapFeatureFaction.Pmc => "PMC",
            MapFeatureFaction.Scav => "Scav",
            MapFeatureFaction.Shared => "Shared",
            _ => "All sides",
        };
        OfferState = entry.OfferState;
        OfferedLabel = DescribeOffer(entry.OfferState);
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
    public bool IsOffered => OfferState == MapSceneOfferState.Offered;
    public bool IsKnownNotOffered => OfferState == MapSceneOfferState.NotOffered;
    public bool IsOfferUnknown => OfferState == MapSceneOfferState.Unknown;
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool IsSelected { get; }
    public string AutomationId => $"v2-map-list-{Token(Id.Value)}";
    public ICommand SelectCommand { get; }

    private static string Token(string value) => new(value.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray());

    private static string DescribeOffer(MapSceneOfferState offerState) => offerState switch
    {
        MapSceneOfferState.Offered => "Offered this raid",
        MapSceneOfferState.NotOffered => "Not offered this raid",
        _ => "Offer status unknown",
    };
}
