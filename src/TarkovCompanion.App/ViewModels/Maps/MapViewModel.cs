using Avalonia;
using Avalonia.Collections;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Infrastructure.Maps;

namespace TarkovCompanion.App.ViewModels.Maps;

public sealed record MapTileViewModel(string LocalPath, double Left, double Top, int Size);

public sealed record MapOverlayViewModel(MapOverlayKind Kind, string Name, bool IsVisible, bool IsHighlighted)
{
    public string HighlightLabel => IsHighlighted ? "Highlighted" : "Highlight";

    public bool CanHighlight => Kind != MapOverlayKind.QuestObjectives;
}

public sealed record MapOverlayElementViewModel(
    string Label,
    double Left,
    double Top,
    double RotationDegrees,
    double FontSize,
    bool IsHighlighted)
{
    public string BorderColor => IsHighlighted ? "#FFC6A15B" : "#8056B8C6";
}

public sealed record QuestMapPointViewModel(string Label, double Left, double Top, bool IsPinned);

public sealed record QuestMapRegionViewModel(string Label, AvaloniaList<Point> Points, bool IsPinned);

public sealed record QuestMapAssociationViewModel(
    string Title,
    string Detail,
    string Evidence,
    string Items,
    bool HasExactGeometry,
    bool IsUnsupported,
    bool IsFloorFiltered);

public sealed class MapViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly HttpClient? _ownedHttpClient;
    private readonly TarkovDevMapCatalogClient _catalogClient;
    private readonly TarkovDevMapAssetCache _assetCache;
    private readonly MapVariantSelectionService _selectionService;
    private readonly IPlayerProfileService? _profileService;
    private readonly IQuestReadService? _questReadService;
    private readonly QuestMapProjectionService? _questProjectionService;
    private readonly MapPresentationService _presentationService = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _selectionLoad;
    private IReadOnlyList<MapLocation> _locations = [];
    private IReadOnlyList<MapVariant> _variants = [];
    private IReadOnlyList<MapFloorDefinition> _floors = [];
    private IReadOnlyList<MapOverlayViewModel> _overlays = [];
    private IReadOnlyList<MapOverlayElementViewModel> _overlayElements = [];
    private IReadOnlyList<MapTileViewModel> _tiles = [];
    private IReadOnlyList<QuestMapPointViewModel> _questPoints = [];
    private IReadOnlyList<QuestMapRegionViewModel> _questRegions = [];
    private IReadOnlyList<QuestMapAssociationViewModel> _questAssociations = [];
    private MapLocation? _selectedLocation;
    private MapVariant? _selectedVariant;
    private MapFloorDefinition? _selectedFloor;
    private MapRenderModel? _renderModel;
    private MapCatalogProvenance? _mapCatalogProvenance;
    private QuestMapProjectionReadModel? _questProjection;
    private string? _backgroundImagePath;
    private string _status = "Loading the tarkov.dev map catalog…";
    private string _questLayerStatus = "Quest layer is off. Enable it to show static active or pinned objectives.";
    private long _questRefreshGeneration;
    private double _canvasWidth = 900;
    private double _canvasHeight = 620;
    private double _zoomScale = 1;
    private bool _disposed;

    public MapViewModel(
        TarkovDevMapCatalogClient catalogClient,
        TarkovDevMapAssetCache assetCache,
        MapVariantSelectionService selectionService,
        IPlayerProfileService profileService,
        IQuestReadService questReadService,
        QuestMapProjectionService questProjectionService)
        : this(
            null,
            catalogClient,
            assetCache,
            selectionService,
            profileService,
            questReadService,
            questProjectionService)
    {
    }

    private MapViewModel(
        HttpClient? ownedHttpClient,
        TarkovDevMapCatalogClient catalogClient,
        TarkovDevMapAssetCache assetCache,
        MapVariantSelectionService selectionService,
        IPlayerProfileService? profileService,
        IQuestReadService? questReadService,
        QuestMapProjectionService? questProjectionService)
    {
        _ownedHttpClient = ownedHttpClient;
        _catalogClient = catalogClient;
        _assetCache = assetCache;
        _selectionService = selectionService;
        _profileService = profileService;
        _questReadService = questReadService;
        _questProjectionService = questProjectionService;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<MapLocation> Locations
    {
        get => _locations;
        private set => Set(ref _locations, value);
    }

    public IReadOnlyList<MapVariant> Variants
    {
        get => _variants;
        private set => Set(ref _variants, value);
    }

    public IReadOnlyList<MapFloorDefinition> Floors
    {
        get => _floors;
        private set => Set(ref _floors, value);
    }

    public IReadOnlyList<MapOverlayViewModel> Overlays
    {
        get => _overlays;
        private set => Set(ref _overlays, value);
    }

    public IReadOnlyList<MapOverlayElementViewModel> OverlayElements
    {
        get => _overlayElements;
        private set => Set(ref _overlayElements, value);
    }

    public IReadOnlyList<MapTileViewModel> Tiles
    {
        get => _tiles;
        private set
        {
            Set(ref _tiles, value);
            OnPropertyChanged(nameof(HasTiles));
            OnPropertyChanged(nameof(ShowsPlaceholder));
        }
    }

    public IReadOnlyList<QuestMapPointViewModel> QuestPoints
    {
        get => _questPoints;
        private set => Set(ref _questPoints, value);
    }

    public IReadOnlyList<QuestMapRegionViewModel> QuestRegions
    {
        get => _questRegions;
        private set => Set(ref _questRegions, value);
    }

    public IReadOnlyList<QuestMapAssociationViewModel> QuestAssociations
    {
        get => _questAssociations;
        private set => Set(ref _questAssociations, value);
    }

    public MapLocation? SelectedLocation
    {
        get => _selectedLocation;
        private set => Set(ref _selectedLocation, value);
    }

    public MapVariant? SelectedVariant
    {
        get => _selectedVariant;
        private set => Set(ref _selectedVariant, value);
    }

    public MapFloorDefinition? SelectedFloor
    {
        get => _selectedFloor;
        private set => Set(ref _selectedFloor, value);
    }

    public string? BackgroundImagePath
    {
        get => _backgroundImagePath;
        private set
        {
            Set(ref _backgroundImagePath, value);
            OnPropertyChanged(nameof(HasBackgroundImage));
            OnPropertyChanged(nameof(ShowsPlaceholder));
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string QuestLayerStatus
    {
        get => _questLayerStatus;
        private set => Set(ref _questLayerStatus, value);
    }

    public double CanvasWidth
    {
        get => _canvasWidth;
        private set
        {
            Set(ref _canvasWidth, value);
            OnPropertyChanged(nameof(ViewportWidth));
        }
    }

    public double CanvasHeight
    {
        get => _canvasHeight;
        private set
        {
            Set(ref _canvasHeight, value);
            OnPropertyChanged(nameof(ViewportHeight));
        }
    }

    public double ZoomScale
    {
        get => _zoomScale;
        private set
        {
            Set(ref _zoomScale, value);
            OnPropertyChanged(nameof(ViewportWidth));
            OnPropertyChanged(nameof(ViewportHeight));
        }
    }

    public double ViewportWidth => CanvasWidth * ZoomScale;

    public double ViewportHeight => CanvasHeight * ZoomScale;

    public string AttributionText => _renderModel?.AttributionText ?? "Map artwork is not loaded.";

    public Uri AttributionUri => _renderModel?.Variant.AuthorLink ?? new Uri("https://tarkov.dev");

    public Uri LicenseUri => MapPresentationService.LicenseUri;

    public string TransformStatus => _renderModel?.TransformMessage ?? "No map transform is loaded; position markers are hidden.";

    public bool HasTiles => Tiles.Count > 0;

    public bool HasBackgroundImage => !string.IsNullOrWhiteSpace(BackgroundImagePath);

    public bool ShowsPlaceholder => !HasTiles && !HasBackgroundImage;

    public static MapViewModel CreateDefault()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TarkovCompanion",
            "Maps");
        var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var catalogClient = new TarkovDevMapCatalogClient(
            httpClient,
            TarkovDevMapCatalogClientOptions.CreateDefault(Path.Combine(root, "Catalog")));
        var assetCache = new TarkovDevMapAssetCache(
            httpClient,
            MapAssetCacheOptions.CreateDefault(Path.Combine(root, "Assets")));
        var preferences = new JsonFileMapVariantPreferenceStore(Path.Combine(root, "map-defaults.json"));
        return new(httpClient, catalogClient, assetCache, new(preferences), null, null, null);
    }

    public async Task InitializeAsync()
    {
        try
        {
            var result = await _catalogClient.GetAsync(_lifetime.Token).ConfigureAwait(true);
            if (result.Catalog is null)
            {
                Status = result.Message ?? "Map catalog unavailable.";
                return;
            }

            _mapCatalogProvenance = result.Catalog.Provenance;

            Locations = result.Catalog.Locations
                .Where(location => location.Variants.Any(variant => variant.HasRuntimeAsset))
                .ToArray();
            var initial = Locations.FirstOrDefault(location =>
                    string.Equals(location.Id, "customs", StringComparison.OrdinalIgnoreCase))
                ?? Locations.FirstOrDefault();
            Status = result.Message ?? "Map catalog loaded.";
            if (initial is not null)
            {
                await SelectLocationAsync(initial).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"Map catalog invalid or unavailable: {exception.Message}";
        }
    }

    public async Task SelectLocationAsync(MapLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        SelectedLocation = location;
        Variants = location.Variants.Where(variant => variant.HasRuntimeAsset).ToArray();
        var selected = await _selectionService.SelectAsync(location, _lifetime.Token).ConfigureAwait(true);
        if (selected is not null)
        {
            await LoadVariantAsync(selected, persist: false).ConfigureAwait(true);
        }
    }

    public Task SelectVariantAsync(MapVariant variant) => LoadVariantAsync(variant, persist: true);

    public async Task SelectFloorAsync(MapFloorDefinition floor)
    {
        ArgumentNullException.ThrowIfNull(floor);
        if (SelectedLocation is not { } location || SelectedVariant is not { } variant ||
            !Floors.Any(candidate => string.Equals(candidate.Id, floor.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _selectionLoad?.Cancel();
        _selectionLoad?.Dispose();
        _selectionLoad = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var cancellationToken = _selectionLoad.Token;
        try
        {
            SelectedFloor = floor;
            _renderModel = (_renderModel ?? _presentationService.Create(location, variant)).SelectFloor(floor.Id);
            if (floor.TilePath is not null)
            {
                Tiles = [];
                BackgroundImagePath = null;
                _renderModel = _renderModel with
                {
                    Background = new(
                        MapBackgroundKind.TileTemplate,
                        floor.TilePath,
                        null,
                        MapAssetAvailability.Available,
                        $"Loading upstream floor '{floor.Name}'."),
                };
                await LoadTilesAsync(variant with { TilePath = floor.TilePath }, cancellationToken).ConfigureAwait(true);
            }
            else if (variant.SvgPath is not null &&
                (string.Equals(floor.Id, "base", StringComparison.Ordinal) || floor.SvgLayer is not null))
            {
                Tiles = [];
                BackgroundImagePath = null;
                await LoadSvgFloorAsync(variant, floor, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                Tiles = [];
                BackgroundImagePath = null;
                _renderModel = _renderModel with { Background = null };
                Status = $"Floor '{floor.Name}' has no explicit upstream SVG layer or PNG tile asset.";
                UpdateOverlayElements();
                NotifyPresentationProperties();
            }

            await RefreshQuestLayerAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Tiles = [];
            BackgroundImagePath = null;
            Status = $"Map floor unavailable: {exception.Message}";
            NotifyPresentationProperties();
        }
    }

    public void ToggleOverlay(MapOverlayKind kind, bool isVisible)
    {
        if (_renderModel is null)
        {
            return;
        }

        _renderModel = _renderModel.SetLayerVisibility(kind, isVisible);
        UpdateOverlays();
        if (kind == MapOverlayKind.QuestObjectives)
        {
            _ = RefreshQuestLayerAsync();
        }
    }

    public void HighlightOverlay(MapOverlayKind? kind)
    {
        if (_renderModel is null)
        {
            return;
        }

        _renderModel = _renderModel.HighlightLayer(kind);
        UpdateOverlays();
    }

    public void ChangeZoom(double wheelDelta)
    {
        var factor = wheelDelta > 0 ? 1.2 : 1 / 1.2;
        ZoomScale = Math.Clamp(ZoomScale * factor, 0.5, 6);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _selectionLoad?.Cancel();
        _selectionLoad?.Dispose();
        _lifetime.Dispose();
        _ownedHttpClient?.Dispose();
    }

    private async Task LoadVariantAsync(MapVariant variant, bool persist)
    {
        if (SelectedLocation is not { } location ||
            !string.Equals(location.Id, variant.LocationId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _selectionLoad?.Cancel();
        _selectionLoad?.Dispose();
        _selectionLoad = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var cancellationToken = _selectionLoad.Token;
        try
        {
            if (persist)
            {
                await _selectionService.ChooseAsync(location, variant.Key, cancellationToken).ConfigureAwait(true);
            }

            SelectedVariant = variant;
            Floors = variant.Floors;
            SelectedFloor = variant.Floors.FirstOrDefault(floor => floor.IsVisibleByDefault) ?? variant.Floors.FirstOrDefault();
            Tiles = [];
            BackgroundImagePath = null;
            CanvasWidth = 900;
            CanvasHeight = 620;
            ZoomScale = 1;
            Status = $"Loading {location.Name} · {variant.DisplayName}…";

            if (variant.TilePath is not null)
            {
                _renderModel = _presentationService.Create(location, variant);
                await LoadTilesAsync(variant, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                var cached = variant.SvgPath is null
                    ? new MapAssetCacheResult(null, "No upstream artwork is configured for this variant.")
                    : await _assetCache.GetSvgAsync(variant, SelectedFloor, cancellationToken).ConfigureAwait(true);
                var availability = cached.Asset?.Availability ?? MapAssetAvailability.Unavailable;
                _renderModel = _presentationService.Create(
                    location,
                    variant,
                    cached.Asset?.LocalPath,
                    availability,
                    cached.Message);
                BackgroundImagePath = cached.Asset?.RenderPath;
                Status = cached.Asset is not null
                    ? $"{cached.Message} SVG rendered from the retained original; floor groups remain separate from companion overlays."
                    : cached.Message ?? "Map artwork unavailable.";
            }

            UpdateOverlays();
            NotifyPresentationProperties();
            await RefreshQuestLayerAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"Map unavailable: {exception.Message}";
        }
    }

    private async Task LoadSvgFloorAsync(
        MapVariant variant,
        MapFloorDefinition floor,
        CancellationToken cancellationToken)
    {
        var cached = await _assetCache.GetSvgAsync(variant, floor, cancellationToken).ConfigureAwait(true);
        var availability = cached.Asset?.Availability ?? MapAssetAvailability.Unavailable;
        _renderModel = _renderModel is null
            ? null
            : _renderModel with
            {
                Background = variant.SvgPath is null
                    ? null
                    : new(
                        MapBackgroundKind.Svg,
                        variant.SvgPath,
                        cached.Asset?.LocalPath,
                        availability,
                        cached.Message),
                SelectedFloor = floor,
            };
        BackgroundImagePath = cached.Asset?.RenderPath;
        Status = cached.Asset is not null
            ? $"{cached.Message} SVG floor rendered from the retained original."
            : cached.Message ?? $"Floor '{floor.Name}' is unavailable.";
        UpdateOverlayElements();
        NotifyPresentationProperties();
    }

    public Task RefreshQuestLayerAsync() => RefreshQuestLayerAsync(CancellationToken.None);

    private async Task RefreshQuestLayerAsync(CancellationToken cancellationToken)
    {
        var refreshGeneration = Interlocked.Increment(ref _questRefreshGeneration);
        if (_renderModel?.Overlays.SingleOrDefault(layer => layer.Kind == MapOverlayKind.QuestObjectives)?.IsVisible != true)
        {
            ClearQuestLayer("Quest layer is off. Enable it to show static active or pinned objectives.");
            return;
        }

        if (_profileService is null || _questReadService is null || _questProjectionService is null)
        {
            ClearQuestLayer("Quest services are unavailable; no quest geometry is shown.");
            return;
        }

        if (SelectedLocation is not { } location ||
            SelectedVariant is not { } variant ||
            _mapCatalogProvenance is null)
        {
            ClearQuestLayer("Select a tarkov.dev map variant to load quest associations.");
            return;
        }

        try
        {
            var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(true);
            var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
            var mapIds = new[] { location.Id, location.SourceId }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var query = await _questReadService
                .GetActiveMapObjectivesAsync(scope, mapIds, cancellationToken)
                .ConfigureAwait(true);
            if (refreshGeneration != Volatile.Read(ref _questRefreshGeneration) ||
                !ReferenceEquals(SelectedLocation, location) ||
                !ReferenceEquals(SelectedVariant, variant))
            {
                return;
            }

            var projection = _questProjectionService.Project(
                query,
                location,
                variant,
                SelectedFloor,
                _mapCatalogProvenance);
            _questProjection = projection;
            QuestAssociations = _questProjection.Objectives.Select(objective => new QuestMapAssociationViewModel(
                $"{objective.TaskName} · {objective.ObjectiveKind}",
                objective.Availability,
                $"{objective.Attribution} · quest catalog {FormatUtc(objective.QuestCatalogProvenance.ValidatedUtc)} · map catalog {FormatUtc(objective.MapCatalogProvenance.RetrievedUtc)}",
                DescribeItems(objective.ItemTargets),
                objective.HasExactGeometry,
                objective.IsUnsupported,
                objective.IsFloorFiltered)).ToArray();
            UpdateQuestGeometry();
            var exactCount = _questProjection.Objectives.Count(objective => objective.HasExactGeometry);
            var associationCount = _questProjection.Objectives.Count - exactCount;
            QuestLayerStatus = _questProjection.UnavailableReason ?? (_renderModel?.CanRender == true
                ? $"Static quest layer · exact {exactCount} · association only {associationCount} · mode {scope.GameMode} · generation {scope.Generation}"
                : $"Map artwork unavailable · {exactCount} exact source geometry item(s) hidden · association only {associationCount} · mode {scope.GameMode} · generation {scope.Generation}");
            if (_questProjection.OrphanedProgress.Count > 0)
            {
                QuestLayerStatus += $" · {_questProjection.OrphanedProgress.Count} orphaned local record(s)";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (refreshGeneration == Volatile.Read(ref _questRefreshGeneration))
            {
                ClearQuestLayer($"Quest layer unavailable: {exception.Message}");
            }
        }
    }

    private async Task LoadTilesAsync(MapVariant variant, CancellationToken cancellationToken)
    {
        var zoom = variant.MinimumZoom ?? 0;
        var plan = MapTilePlanner.Plan(variant, zoom, 16);
        if (!plan.IsValid)
        {
            Tiles = [];
            Status = plan.Error ?? "PNG tile plan unavailable.";
            return;
        }

        var loaded = new List<MapTileViewModel>();
        var offlineCount = 0;
        using var concurrency = new SemaphoreSlim(4, 4);
        var tasks = plan.Tiles.Select(async tile =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await _assetCache
                    .GetTileAsync(variant, tile.Zoom, tile.X, tile.Y, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Asset is not null)
                {
                    if (result.Asset.Availability == MapAssetAvailability.CachedOffline)
                    {
                        Interlocked.Increment(ref offlineCount);
                    }

                    lock (loaded)
                    {
                        loaded.Add(new(result.Asset.LocalPath, tile.Left, tile.Top, tile.Size));
                    }
                }
            }
            finally
            {
                concurrency.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(true);
        Tiles = loaded.OrderBy(tile => tile.Top).ThenBy(tile => tile.Left).ToArray();
        CanvasWidth = plan.Width;
        CanvasHeight = plan.Height;
        var availability = Tiles.Count == 0
            ? MapAssetAvailability.Unavailable
            : offlineCount > 0
                ? MapAssetAvailability.CachedOffline
                : MapAssetAvailability.Available;
        Status = availability switch
        {
            MapAssetAvailability.CachedOffline => $"Offline: loaded {Tiles.Count} cached tarkov.dev PNG tiles at zoom {zoom}.",
            MapAssetAvailability.Available when Tiles.Count == plan.Tiles.Count => $"Loaded {Tiles.Count} cached tarkov.dev PNG tiles at zoom {zoom}.",
            MapAssetAvailability.Available => $"Loaded {Tiles.Count} of {plan.Tiles.Count} PNG tiles; unavailable tiles remain blank.",
            _ => "PNG map tiles are unavailable and no cached tiles could be used.",
        };
        if (_renderModel?.Background is { } background)
        {
            _renderModel = _renderModel with
            {
                Background = background with { Availability = availability, Message = Status },
            };
        }

        UpdateOverlayElements();
        NotifyPresentationProperties();
    }

    private void UpdateOverlays()
    {
        Overlays = _renderModel?.Overlays
            .Select(layer => new MapOverlayViewModel(layer.Kind, layer.Name, layer.IsVisible, layer.IsHighlighted))
            .ToArray() ?? [];
        UpdateOverlayElements();
    }

    private void UpdateOverlayElements()
    {
        var mapper = CreateCanvasMapper();
        if (mapper is null)
        {
            OverlayElements = [];
            UpdateQuestGeometry();
            return;
        }

        OverlayElements = _renderModel?.VisibleOverlayElements.Select(element =>
        {
            var layer = _renderModel.Overlays.Single(item => item.Kind == element.Layer);
            var canvasPoint = mapper(element.Position);
            return new MapOverlayElementViewModel(
                element.Label,
                canvasPoint.X,
                canvasPoint.Y,
                element.RotationDegrees,
                Math.Clamp(element.SizePercent / 7, 9, 18),
                layer.IsHighlighted);
        }).ToArray() ?? [];
        UpdateQuestGeometry();
    }

    private Func<MapPoint, Point>? CreateCanvasMapper()
    {
        if (_renderModel is null || SelectedVariant is not { } variant || variant.Transform is null)
        {
            return null;
        }

        if (_renderModel.Background?.Kind == MapBackgroundKind.TileTemplate && variant.MinimumZoom is { } zoom)
        {
            var plan = MapTilePlanner.Plan(variant, zoom, 16);
            if (plan.IsValid)
            {
                var scale = Math.Pow(2, zoom);
                return point => new(
                    (point.X * scale) - plan.OriginPixelX,
                    (point.Y * scale) - plan.OriginPixelY);
            }
        }

        if (variant.Bounds?.IsValid != true)
        {
            return null;
        }

        var bounds = variant.SvgBounds ?? variant.Bounds;
        var corners = new[]
        {
            new WorldPosition(bounds.First.X, 0, bounds.First.Y),
            new WorldPosition(bounds.First.X, 0, bounds.Second.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.First.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.Second.Y),
        };
        var projected = corners.Select(corner =>
        {
            variant.Transform.TryProject(corner, out var point);
            return point;
        }).ToArray();
        var minimumX = projected.Min(point => point.X);
        var maximumX = projected.Max(point => point.X);
        var minimumY = projected.Min(point => point.Y);
        var maximumY = projected.Max(point => point.Y);
        if (maximumX <= minimumX || maximumY <= minimumY)
        {
            return null;
        }

        return point => new(
            ((point.X - minimumX) / (maximumX - minimumX)) * CanvasWidth,
            ((point.Y - minimumY) / (maximumY - minimumY)) * CanvasHeight);
    }

    private void UpdateQuestGeometry()
    {
        var questLayerVisible = _renderModel?.Overlays
            .SingleOrDefault(layer => layer.Kind == MapOverlayKind.QuestObjectives)?.IsVisible == true;
        var mapper = CreateCanvasMapper();
        if (!questLayerVisible || _renderModel?.CanRender != true || _questProjection is null || mapper is null)
        {
            QuestPoints = [];
            QuestRegions = [];
            return;
        }

        QuestPoints = _questProjection.Objectives
            .Where(objective => objective.HasExactGeometry && objective.GeometryKind == QuestMapGeometryKind.Point)
            .Select(objective =>
            {
                var point = mapper(objective.Points[0]);
                return new QuestMapPointViewModel(
                    $"{objective.TaskName} · {objective.Description}",
                    point.X,
                    point.Y,
                    objective.IsPinned);
            })
            .ToArray();
        QuestRegions = _questProjection.Objectives
            .Where(objective => objective.HasExactGeometry && objective.GeometryKind == QuestMapGeometryKind.Region)
            .Select(objective =>
            {
                var points = new AvaloniaList<Point>();
                points.AddRange(objective.Points.Select(mapper));
                return new QuestMapRegionViewModel(
                    $"{objective.TaskName} · {objective.Description}",
                    points,
                    objective.IsPinned);
            })
            .ToArray();
    }

    private void ClearQuestLayer(string status)
    {
        _questProjection = null;
        QuestPoints = [];
        QuestRegions = [];
        QuestAssociations = [];
        QuestLayerStatus = status;
    }

    private static string DescribeItems(IReadOnlyList<QuestObjectiveItemTarget> targets)
    {
        if (targets.Count == 0)
        {
            return "No item requirement";
        }

        var itemIds = targets
            .Where(target => target.SourceField != "requiredKeys")
            .Select(target => target.ItemId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var requiredKeys = targets
            .Where(target => target.SourceField == "requiredKeys")
            .GroupBy(target => target.AlternativeGroup)
            .OrderBy(group => group.Key)
            .Select(group => string.Join(" or ", group.Select(target => target.ItemId).Order(StringComparer.Ordinal)))
            .ToArray();
        var fir = targets.Any(target => target.SourceField != "requiredKeys" && target.FoundInRaidRequired == true)
            ? " · found in raid"
            : string.Empty;
        var itemText = itemIds.Length == 0 ? null : $"Items: {string.Join(" or ", itemIds)}{fir}";
        var keyText = requiredKeys.Length == 0 ? null : $"Required keys: {string.Join("; ", requiredKeys)}";
        return string.Join(" · ", new[] { itemText, keyText }.OfType<string>());
    }

    private static string FormatUtc(DateTimeOffset timestamp) => timestamp.ToUniversalTime().ToString("u");

    private void NotifyPresentationProperties()
    {
        OnPropertyChanged(nameof(AttributionText));
        OnPropertyChanged(nameof(AttributionUri));
        OnPropertyChanged(nameof(LicenseUri));
        OnPropertyChanged(nameof(TransformStatus));
        OnPropertyChanged(nameof(HasTiles));
        OnPropertyChanged(nameof(HasBackgroundImage));
        OnPropertyChanged(nameof(ShowsPlaceholder));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}
