using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Strategy;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
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
public sealed class RaidMarkRowViewModel(RaidMark mark, Func<Guid, Task> remove) : BindableViewModel
{
    public Guid Id { get; } = mark.Id;

    public string Label { get; } = string.IsNullOrWhiteSpace(mark.State.Label)
        ? (mark.Kind == RaidMarkKind.Ping ? "Ping" : "Waypoint")
        : mark.State.Label!;

    public string KindLabel { get; } = mark.Kind == RaidMarkKind.Ping ? "Ping" : "Waypoint";

    public ICommand RemoveCommand { get; } = new DelegateCommand(() => _ = remove(mark.Id));
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
    private static readonly MapSceneBounds PlanBounds = new(0, 0, 100, 100);

    private readonly MapViewModel _map;
    private readonly RaidPageViewModel _raid;
    private readonly IRuntimeStateStore _stateStore;
    private readonly MapSceneAssembler _assembler;
    private readonly IHighValueLootRuntimeSource _lootSource;
    private readonly IRaidMarkStore _marks;
    private readonly TarkovDevMapAssetCache _assetCache;
    private readonly TimeProvider _timeProvider;
    private readonly MapSceneRendererPresentation _presentation;

    private long _revision;
    private CancellationTokenSource? _rebuildCancellation;
    private HighValueLootLayerFilterState _lootFilter = HighValueLootLayerFilterState.Default;
    private RaidMarkKind? _armedMarkKind;
    private string _unavailableReason = "Loading the map…";
    private string? _cachedAssetVariantKey;
    private CachedMapAsset? _cachedAsset;

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
        TimeProvider timeProvider)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _raid = raid ?? throw new ArgumentNullException(nameof(raid));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _assembler = assembler ?? throw new ArgumentNullException(nameof(assembler));
        _lootSource = lootSource ?? throw new ArgumentNullException(nameof(lootSource));
        ArgumentNullException.ThrowIfNull(traffic);
        _marks = marks ?? throw new ArgumentNullException(nameof(marks));
        _assetCache = assetCache ?? throw new ArgumentNullException(nameof(assetCache));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _presentation = MapSceneRendererPresentation.English(CultureInfo.CurrentCulture, TimeZoneInfo.Local);

        RebuildMapPicker();

        PlaceWaypointCommand = new DelegateCommand(() => ArmMark(RaidMarkKind.Waypoint));
        PlacePingCommand = new DelegateCommand(() => ArmMark(RaidMarkKind.Ping));
        CancelPlacingCommand = new DelegateCommand(() => ArmMark(null));

        _map.PropertyChanged += MapPropertyChanged;
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

    public IReadOnlyList<RaidMarkRowViewModel> Marks { get; private set; } = [];

    public bool HasMarks => Marks.Count > 0;

    /// <summary>Registered but not evaluated: see the traffic remark on the constructor.</summary>
    public string TrafficLayerNotice =>
        "Historical traffic — estimate, no installed model yet";

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

    public void Dispose()
    {
        _map.PropertyChanged -= MapPropertyChanged;
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

    private void MapPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MapViewModel.RenderModel))
        {
            _ = RebuildAsync();
        }
        else if (e.PropertyName is nameof(MapViewModel.Locations))
        {
            // The catalog loads after construction, so the picker built from an empty list at
            // startup has to be rebuilt once it actually has maps to offer.
            RebuildMapPicker();
        }
    }

    private void RebuildMapPicker()
    {
        MapPicker = [.. _map.Locations
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .Select(location => new RaidMapPickerItemViewModel(location.Id, location.Name, SelectMapAsync))];
        OnPropertyChanged(nameof(MapPicker));
    }

    private void RaidPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RaidPageViewModel.TimeLeft):
                OnPropertyChanged(nameof(TimeLeft));
                break;
            case nameof(RaidPageViewModel.TimeLeftDetail):
                OnPropertyChanged(nameof(TimeLeftDetail));
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

    private void RuntimeStateChanged(object? sender, EventArgs e) => _ = RebuildAsync();

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
            : [.. _marks.Marks
                .Where(mark => string.Equals(mark.State.MapId, mapId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(mark => mark.CreatedUtc)
                .Select(mark => new RaidMarkRowViewModel(mark, id => _marks.RemoveAsync(id)))];
        OnPropertyChanged(nameof(Marks));
        OnPropertyChanged(nameof(HasMarks));
    }

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
        // multi-megabyte SVG on disk each time for no reason once the variant has not changed.
        if (_cachedAssetVariantKey != variant.Key || _cachedAsset is null)
        {
            var assetResult = await _assetCache.GetSvgAsync(variant, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (assetResult.Asset is not { Availability: not MapAssetAvailability.Unavailable } fetched)
            {
                _cachedAssetVariantKey = null;
                _cachedAsset = null;
                SetUnavailable(assetResult.Message ?? "The reviewed map asset is not available yet.");
                return;
            }

            _cachedAssetVariantKey = variant.Key;
            _cachedAsset = fetched;
        }

        var cached = _cachedAsset!;

        var nowUtc = _timeProvider.GetUtcNow();
        var transformVersion = variant.Key;
        var asset = new MapSceneAsset(
            new($"asset:{model.Location.Id}:{variant.Key}"),
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
        var legacyElements = model.OverlayElements
            .Where(element => element.Layer is MapOverlayKind.Extracts or MapOverlayKind.QuestObjectives)
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
        // The renderer requires the scene to already declare the exact loot layer it is handed
        // beside it (see EnsureHighValueLootMatchesScene), so the loot layer and its objects are
        // merged in here rather than attached only through the constructor/Present overload.
        var additionalLayers = marksLayer is { } definiteMarksLayer
            ? new[] { lootLayer.Layer, definiteMarksLayer }
            : new[] { lootLayer.Layer };
        var additionalObjects = lootLayer.Objects.Concat(markObjects).ToArray();

        var requestedView = Renderer is { } current && string.Equals(current.Scene.LocationId, model.Location.Id, StringComparison.Ordinal)
            ? current.Scene.View
            : new MapSceneViewState(MapSceneMode.Flat2D, model.SelectedFloor?.Id, new(50, 50, 1, 0, 0), []);

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
                reviewedAssetResolver: null,
                highValueLoot: lootLayer,
                highValueLootFilterState: _lootFilter,
                highValueLootCategories: null);
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
    }

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

    /// <summary>Internal for direct coverage of the marks-to-scene mapping (see the unit tests).</summary>
    internal static (MapSceneLayer? Layer, IReadOnlyList<MapSceneObject> Objects) BuildMarksLayer(
        IReadOnlyList<RaidMark> marks,
        string mapId,
        DateTimeOffset nowUtc)
    {
        var forMap = marks
            .Where(mark => string.Equals(mark.State.MapId, mapId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (forMap.Length == 0)
        {
            return (null, []);
        }

        var layer = new MapSceneLayer(MarksLayerId, "My marks", 40, true);
        var objects = forMap
            .Select(mark => new MapSceneObject(
                new($"mark:{mark.Id}"),
                MarksLayerId,
                mark.Kind == RaidMarkKind.Ping ? MapSceneObjectKind.Ping : MapSceneObjectKind.Waypoint,
                MapSceneTruthKind.UserAuthored,
                string.IsNullOrWhiteSpace(mark.State.Label)
                    ? (mark.Kind == RaidMarkKind.Ping ? "Ping" : "Waypoint")
                    : mark.State.Label!,
                null,
                MapSceneGeometry.At(new(mark.State.X, mark.State.Y)),
                mark.State.FloorId is null ? [] : [mark.State.FloorId],
                new DataProvenance("local-mark", nowUtc)))
            .ToArray();
        return (layer, objects);
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
