using System.Globalization;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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

    public Guid Id => _id;

    public RaidMarkKind Kind { get; }

    /// <summary>The number, custom name, or "Ping" — whichever the map dot beside this row shows.</summary>
    public string Label { get; }

    public string KindLabel => Kind == RaidMarkKind.Ping ? "Ping" : "Waypoint";

    /// <summary>A ping is "look here now": it is never told apart from another ping by name.</summary>
    public bool CanRename => Kind == RaidMarkKind.Waypoint;

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
    private static readonly MapSceneBounds PlanBounds = new(0, 0, 100, 100);
    private const string MarkIdPrefix = "mark:";

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
    /// <summary>The picker entry for the map currently shown, for a header selector that stays
    /// showing the current map name rather than resetting to a blank picker each time.</summary>
    public RaidMapPickerItemViewModel? SelectedMap =>
        MapPicker.FirstOrDefault(item => item.MapId == _map.RenderModel?.Location.Id);

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

    private void MapPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
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
            : [.. LabelMarksForMap(_marks.Marks, mapId)
                .OrderByDescending(item => item.Mark.CreatedUtc)
                .Select(item => new RaidMarkRowViewModel(item.Mark, item.Label, RenameMarkAsync, id => _marks.RemoveAsync(id)))];
        OnPropertyChanged(nameof(Marks));
        OnPropertyChanged(nameof(HasMarks));
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
                fillsViewport: false);
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
        RefreshSceneLists(scene);
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
    /// The same shape as the Plan workspace's objective preview: its own renderer view model
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
        if (Renderer is not { } current || _map.RenderModel is not { } model ||
            !string.Equals(current.Scene.LocationId, model.Location.Id, StringComparison.Ordinal))
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
            [],
            layer is null ? [] : [layer],
            objects,
            current.Scene.Assets);
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
