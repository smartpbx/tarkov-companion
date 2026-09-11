using Avalonia;
using Avalonia.Collections;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.App.ViewModels.Quests;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Infrastructure.Maps;

namespace TarkovCompanion.App.ViewModels.Maps;

public sealed record MapTileViewModel(string LocalPath, Bitmap Image, double Left, double Top, int Size);

public sealed record MapOverlayViewModel(MapOverlayKind Kind, string Name, bool IsVisible, bool IsHighlighted)
{
    public string HighlightLabel => IsHighlighted ? "Highlighted" : "Highlight";
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

public sealed record QuestMapPointViewModel(
    string Label,
    double CenterX,
    double CenterY,
    bool IsPinned,
    bool IsHighlighted)
{
    public double Size => IsHighlighted ? IsPinned ? 24 : 22 : IsPinned ? 18 : 14;

    public double Left => CenterX - (Size / 2);

    public double Top => CenterY - (Size / 2);

    public double CornerRadius => Size / 2;

    public double BorderThickness => IsHighlighted ? IsPinned ? 4 : 3 : IsPinned ? 3 : 2;

    public string FillColor => IsHighlighted ? "#FFF0B44D" : IsPinned ? "#FFC6A15B" : "#E656B8C6";

    public string BorderColor => IsPinned ? "#FFFFFFFF" : "#FFE6EDF2";
}

public sealed record QuestMapRegionViewModel(
    string Label,
    AvaloniaList<Point> Points,
    bool IsPinned,
    bool IsHighlighted)
{
    public string FillColor => IsHighlighted ? "#70F0B44D" : IsPinned ? "#50C6A15B" : "#3056B8C6";

    public string StrokeColor => IsHighlighted ? "#FFF0B44D" : IsPinned ? "#FFFFFFFF" : "#E6C6A15B";

    public double StrokeThickness => IsHighlighted ? IsPinned ? 5 : 4 : IsPinned ? 3 : 2;
}

public static class MapCanvasCoordinateMapper
{
    /// <summary>
    /// How many upstream PNG tiles one view may stitch together.
    /// </summary>
    /// <remarks>
    /// This was pinned at 16 here while the planner's own default is 64, so any map whose
    /// upstream bounds need more than a four-by-four grid rendered nothing at all. Customs
    /// needs twenty.
    /// </remarks>
    public const int MaximumTilesPerView = 64;

    public static Func<MapPoint, Point>? Create(
        MapRenderModel renderModel,
        double canvasWidth,
        double canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(renderModel);
        var variant = renderModel.Variant;
        if (variant.Transform is null ||
            !double.IsFinite(canvasWidth) || canvasWidth <= 0 ||
            !double.IsFinite(canvasHeight) || canvasHeight <= 0)
        {
            return null;
        }

        if (renderModel.Background?.Kind == MapBackgroundKind.TileTemplate && variant.MinimumZoom is { } zoom)
        {
            var plan = MapTilePlanner.Plan(variant, zoom, MaximumTilesPerView);
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
            ((point.X - minimumX) / (maximumX - minimumX)) * canvasWidth,
            ((point.Y - minimumY) / (maximumY - minimumY)) * canvasHeight);
    }
}

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
    private Bitmap? _backgroundImage;
    private string _status = "Loading the tarkov.dev map catalog…";
    private string _questLayerStatus = "Quest layer is off. Enable it to show static active or pinned objectives.";
    private long _questRefreshGeneration;
    private double _canvasWidth = 900;
    private double _canvasHeight = 620;
    private double _zoomScale = 1;
    private bool _isAutoFit = true;
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
            var replaced = _tiles;
            Set(ref _tiles, value);
            ContentBounds = MeasureContent(value);
            OnPropertyChanged(nameof(HasTiles));
            OnPropertyChanged(nameof(ShowsPlaceholder));
            ReleaseLater(replaced.Select(tile => tile.Image));
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

    /// <summary>
    /// The decoded map artwork.
    /// </summary>
    /// <remarks>
    /// This has to be a decoded image rather than a path. Image.Source is an IImage, the
    /// project compiles bindings, and there is no converter, so binding a file path here
    /// silently rendered nothing at all: every downloaded tile and every rasterized SVG
    /// reached the cache on disk and none of them ever reached the screen.
    /// </remarks>
    public Bitmap? BackgroundImage
    {
        get => _backgroundImage;
        private set
        {
            var replaced = _backgroundImage;
            Set(ref _backgroundImage, value);
            OnPropertyChanged(nameof(HasBackgroundImage));
            OnPropertyChanged(nameof(ShowsPlaceholder));
            if (!ReferenceEquals(replaced, value) && replaced is not null)
            {
                ReleaseLater([replaced]);
            }
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
            if (Set(ref _canvasWidth, value))
            {
                OnPropertyChanged(nameof(ViewportWidth));
                RequestFit();
            }
        }
    }

    public double CanvasHeight
    {
        get => _canvasHeight;
        private set
        {
            if (Set(ref _canvasHeight, value))
            {
                OnPropertyChanged(nameof(ViewportHeight));
                RequestFit();
            }
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

    public bool HasBackgroundImage => BackgroundImage is not null;

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
            Status = DescribeCatalog(result, Locations.Count);
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

    /// <summary>
    /// Describes what the catalog load actually produced.
    /// </summary>
    /// <remarks>
    /// A catalog that parses but yields no usable location previously reported the upstream
    /// success message, leaving the user with a blank canvas and text saying it worked.
    /// </remarks>
    private static string DescribeCatalog(MapCatalogLoadResult result, int usableLocations)
    {
        var skipped = result.Catalog?.SkippedLocations ?? [];
        if (usableLocations == 0)
        {
            return skipped.Count == 0
                ? "The tarkov.dev map catalog loaded but contains no map with a usable image."
                : $"The tarkov.dev map catalog loaded but no map has a usable image. Skipped {skipped.Count} location(s): {string.Join("; ", skipped)}";
        }

        var loaded = result.Message ?? "Map catalog loaded.";
        return skipped.Count == 0
            ? loaded
            : $"{loaded} Skipped {skipped.Count} upstream location(s): {string.Join("; ", skipped)}";
    }

    /// <summary>
    /// Switches the map to the location the player is currently in.
    /// </summary>
    /// <remarks>
    /// Raid evidence names a location the same way the tarkov.dev catalog does, so the map
    /// can follow the player into a raid without anyone touching the companion. A location
    /// the catalog does not carry is ignored rather than clearing the current view.
    /// </remarks>
    /// <summary>Shows a failed interaction in the map status rather than crashing.</summary>
    public void ReportInteractionFailure(string detail) =>
        Status = $"That map action failed: {detail}";

    public async Task FollowRaidAsync(string mapId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        if (SelectedLocation is not null &&
            string.Equals(SelectedLocation.Id, mapId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var location = Locations.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, mapId, StringComparison.OrdinalIgnoreCase));
        if (location is null)
        {
            return;
        }

        await SelectLocationAsync(location).ConfigureAwait(true);
        Status = $"Following the current raid on {location.Name}.";
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
                BackgroundImage = null;
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
                BackgroundImage = null;
                await LoadSvgFloorAsync(variant, floor, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                Tiles = [];
                BackgroundImage = null;
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
            BackgroundImage = null;
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

    /// <summary>
    /// Raised when the map should be scaled to the panel and centred again.
    /// </summary>
    /// <remarks>
    /// Only the view knows how much room the panel actually has, so the view model asks
    /// rather than computes. A newly loaded map raises this so the player sees the whole
    /// thing at once instead of the empty top-left corner of a tile grid.
    /// </remarks>
    public event EventHandler? FitRequested;

    /// <summary>Whether the view should keep refitting as the panel resizes.</summary>
    public bool IsAutoFit
    {
        get => _isAutoFit;
        private set => Set(ref _isAutoFit, value);
    }

    public void ChangeZoom(double wheelDelta)
    {
        var factor = wheelDelta > 0 ? 1.2 : 1 / 1.2;
        SetZoom(ZoomScale * factor);
    }

    /// <summary>Zooms deliberately, which turns off automatic fitting.</summary>
    public void SetZoom(double scale)
    {
        IsAutoFit = false;
        ZoomScale = ClampZoom(scale);
    }

    /// <summary>Asks the view to scale the whole map into the panel and centre it.</summary>
    public void RequestFit()
    {
        IsAutoFit = true;
        FitRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Scales the map to the room the panel reports.</summary>
    /// <summary>
    /// The part of the canvas that actually has artwork on it.
    /// </summary>
    /// <remarks>
    /// A tile plan covers the upstream bounds, and upstream bounds routinely extend past the
    /// drawn map: on Customs four of twenty tiles have no image at all. Fitting the whole
    /// plan therefore scaled the map down to make room for blank space and parked it in a
    /// corner. Fitting the tiles that actually loaded is what a player means by "fit".
    /// </remarks>
    public Rect ContentBounds { get; private set; }

    public void ApplyFit(double availableWidth, double availableHeight)
    {
        if (CanvasWidth <= 0 || CanvasHeight <= 0 ||
            !double.IsFinite(availableWidth) || availableWidth <= 0 ||
            !double.IsFinite(availableHeight) || availableHeight <= 0)
        {
            return;
        }

        var content = ContentBounds;
        var width = content.Width > 0 ? content.Width : CanvasWidth;
        var height = content.Height > 0 ? content.Height : CanvasHeight;
        ZoomScale = ClampZoom(Math.Min(availableWidth / width, availableHeight / height));
    }

    // A tile grid can be several times the panel's size, so the lower bound has to allow a
    // genuine fit. The previous floor of 0.5 could not show a whole map at once.
    private static double ClampZoom(double scale) => Math.Clamp(scale, 0.05, 8);

    /// <summary>Measures the rectangle the loaded tiles occupy, in canvas coordinates.</summary>
    private static Rect MeasureContent(IReadOnlyList<MapTileViewModel> tiles)
    {
        if (tiles.Count == 0)
        {
            return default;
        }

        var left = tiles.Min(tile => tile.Left);
        var top = tiles.Min(tile => tile.Top);
        var right = tiles.Max(tile => tile.Left + tile.Size);
        var bottom = tiles.Max(tile => tile.Top + tile.Size);
        return new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
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

        _backgroundImage?.Dispose();
        _backgroundImage = null;
        foreach (var tile in _tiles)
        {
            tile.Image.Dispose();
        }

        _tiles = [];
    }

    /// <summary>Decodes a cached image file off the UI thread.</summary>
    private static async Task<Bitmap?> LoadBitmapAsync(string? path, CancellationToken cancellationToken)
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

    /// <summary>
    /// Disposes replaced artwork once the current render pass has finished with it.
    /// </summary>
    private static void ReleaseLater(IEnumerable<Bitmap> images)
    {
        var retained = images.ToArray();
        if (retained.Length == 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                foreach (var image in retained)
                {
                    image.Dispose();
                }
            },
            DispatcherPriority.Background);
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
            BackgroundImage = null;
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
                BackgroundImage = await LoadBitmapAsync(cached.Asset?.RenderPath, cancellationToken).ConfigureAwait(true);
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
        BackgroundImage = await LoadBitmapAsync(cached.Asset?.RenderPath, cancellationToken).ConfigureAwait(true);
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
            var mapIds = QuestMapProjectionService.CompatibleMapIds(location, variant)
                .Order(StringComparer.OrdinalIgnoreCase)
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
                QuestItemRequirementFormatter.DescribeForMap(
                    objective.ItemTargets,
                    objective.FoundInRaidRequired),
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
        var plan = MapTilePlanner.Plan(variant, zoom, MapCanvasCoordinateMapper.MaximumTilesPerView);
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
                    var decoded = await LoadBitmapAsync(result.Asset.LocalPath, cancellationToken)
                        .ConfigureAwait(false);
                    if (decoded is null)
                    {
                        return;
                    }

                    if (result.Asset.Availability == MapAssetAvailability.CachedOffline)
                    {
                        Interlocked.Increment(ref offlineCount);
                    }

                    lock (loaded)
                    {
                        loaded.Add(new(result.Asset.LocalPath, decoded, tile.Left, tile.Top, tile.Size));
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
        return _renderModel is null
            ? null
            : MapCanvasCoordinateMapper.Create(_renderModel, CanvasWidth, CanvasHeight);
    }

    private void UpdateQuestGeometry()
    {
        var questLayer = _renderModel?.Overlays
            .SingleOrDefault(layer => layer.Kind == MapOverlayKind.QuestObjectives);
        var questLayerVisible = questLayer?.IsVisible == true;
        var questLayerHighlighted = questLayer?.IsHighlighted == true;
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
                    objective.IsPinned,
                    questLayerHighlighted);
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
                    objective.IsPinned,
                    questLayerHighlighted);
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

    /// <summary>Assigns a backing field and reports whether the value actually changed.</summary>
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}
