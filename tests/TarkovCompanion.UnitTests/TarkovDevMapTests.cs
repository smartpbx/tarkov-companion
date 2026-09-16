using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Infrastructure.Maps;
using SkiaSharp;

namespace TarkovCompanion.UnitTests;

public sealed class TarkovDevMapTests
{
    private static readonly Uri CatalogUri = new("https://raw.githubusercontent.com/the-hideout/tarkov-dev/main/src/data/maps.json");

    [Fact]
    public void CatalogParserCoversVariantsLayersLabelsAndAttributionWhileIgnoringUnknownFields()
    {
        var catalog = ParseFixture();

        var location = Assert.Single(catalog.Locations);
        Assert.Equal("synthetic-port", location.Id);
        Assert.Equal("synthetic-location-id", location.SourceId);
        Assert.Contains("test only", location.Name, StringComparison.OrdinalIgnoreCase);
        var variant = location.Variants[0];
        Assert.Equal(MapProjectionKind.Interactive, variant.Projection);
        Assert.Equal(new Uri("https://assets.tarkov.dev/maps/svg/SyntheticFixture.svg"), variant.SvgPath);
        Assert.Equal(new Uri("https://assets.tarkov.dev/maps/synthetic-fixture/main/{z}/{x}/{y}.png"), variant.TilePath);
        Assert.Equal(256, variant.TileSize);
        Assert.Equal(3, variant.Floors.Count);
        Assert.Equal("Upper_Floor", variant.Floors[1].SvgLayer);
        Assert.NotNull(variant.Floors[2].TilePath);
        var extentBounds = Assert.Single(variant.Floors[2].Extents).Bounds[0];
        Assert.True(extentBounds.Contains(0, 0));
        Assert.Equal("fixture tunnel", extentBounds.Description);
        Assert.Equal("Fixture Warehouse", Assert.Single(variant.Labels).Text);
        Assert.Equal("Synthetic Fixture Authors", variant.Author);
        Assert.Equal("synthetic-port-night", Assert.Single(variant.AlternateLocationIds));
        Assert.False(location.Variants[1].HasRuntimeAsset);
        Assert.Null(location.Variants[1].SvgPath);
        Assert.Null(location.Variants[1].TilePath);
        Assert.Single(location.Variants[1].Floors);
        Assert.Empty(location.Variants[1].Labels);
        Assert.Empty(location.Variants[1].AlternateLocationIds);
    }

    [Fact]
    public void CatalogTransformMatchesTarkovDevRotationScaleAndOffsetOrder()
    {
        var transform = ParseFixture().Locations[0].Variants[0].Transform;

        Assert.NotNull(transform);
        Assert.True(transform.TryProject(new WorldPosition(4, 0, 2), out var point));
        Assert.Equal(9, point.X, 10);
        Assert.Equal(19, point.Y, 10);
    }

    [Fact]
    public void TilePlanAndViewportStayWithinConfiguredBounds()
    {
        var variant = ParseFixture().Locations[0].Variants[0];

        var plan = MapTilePlanner.Plan(variant, 1, 16);
        var viewport = MapViewportState.Default.ZoomBy(10, 1, 5).PanBy(20, -15);

        Assert.True(plan.IsValid, plan.Error);
        Assert.InRange(plan.Tiles.Count, 1, 16);
        Assert.All(plan.Tiles, tile => Assert.Equal(256, tile.Size));
        Assert.Equal(5, viewport.Zoom);
        Assert.Equal(20, viewport.PanX);
        Assert.Equal(-15, viewport.PanY);
    }

    [Fact]
    public void TilePlanRejectsMissingOrUnsafeUpstreamZoomMetadata()
    {
        var variant = ParseFixture().Locations[0].Variants[0];

        var missingZoom = MapTilePlanner.Plan(variant with { MinimumZoom = null }, 1);
        var unsafeZoom = MapTilePlanner.Plan(variant with { MinimumZoom = -100, MaximumZoom = 100 }, 50);

        Assert.False(missingZoom.IsValid);
        Assert.Contains("metadata", missingZoom.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(unsafeZoom.IsValid);
        Assert.Contains("zoom", unsafeZoom.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatalogParserAcceptsQuotedNumbersAndSkipsUnparseableLocations()
    {
        // Upstream currently quotes three Customs label rotations ("6", "5", "-9"). Reading
        // those through JsonElement.TryGetDouble threw rather than returning false, and a
        // single throw discarded every location in the catalog.
        const string json = """
            [
              {
                "normalizedName": "quoted-rotation",
                "maps": [
                  {
                    "key": "quoted-rotation",
                    "projection": "interactive",
                    "svgPath": "https://assets.tarkov.dev/maps/svg/QuotedRotation.svg",
                    "tileSize": "512",
                    "labels": [{ "text": "Main Bridge", "position": [10, 20], "rotation": "-9" }]
                  }
                ]
              },
              {
                "normalizedName": "unparseable",
                "maps": [{ "projection": "interactive" }]
              }
            ]
            """;

        var catalog = TarkovDevMapCatalogParser.Parse(json, CatalogUri, DateTimeOffset.UnixEpoch);

        var location = Assert.Single(catalog.Locations);
        Assert.Equal("quoted-rotation", location.Id);
        var variant = Assert.Single(location.Variants);
        Assert.Equal(512, variant.TileSize);
        Assert.Equal(-9d, Assert.Single(variant.Labels).RotationDegrees);
        Assert.Contains("unparseable", Assert.Single(catalog.SkippedLocations), StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogParserRejectsValuesThatAreNotNumbersAtAll()
    {
        const string json = """
            [
              {
                "normalizedName": "bad-label",
                "maps": [
                  {
                    "key": "bad-label",
                    "projection": "interactive",
                    "svgPath": "https://assets.tarkov.dev/maps/svg/BadLabel.svg",
                    "labels": [{ "text": "Nowhere", "position": ["north", 20] }]
                  }
                ]
              }
            ]
            """;

        var exception = Assert.Throws<InvalidDataException>(() => TarkovDevMapCatalogParser.Parse(
            json,
            CatalogUri,
            DateTimeOffset.UnixEpoch));

        Assert.Contains("position", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatalogParserFailsClearlyForMissingRequiredIdentity()
    {
        var exception = Assert.Throws<InvalidDataException>(() => TarkovDevMapCatalogParser.Parse(
            """[{"maps":[]}]""",
            CatalogUri,
            DateTimeOffset.UnixEpoch));

        Assert.Contains("normalizedName", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VariantSelectionPersistsPerLocationAndFallsBackToInteractiveFirst()
    {
        using var directory = new TemporaryDirectory();
        var location = CreateLocationWithAssets();
        var store = new JsonFileMapVariantPreferenceStore(Path.Combine(directory.Path, "map-defaults.json"));
        var service = new MapVariantSelectionService(store);

        var fallback = await service.SelectAsync(location, CancellationToken.None);
        Assert.Equal("interactive", fallback?.Key);

        var chosen = await service.ChooseAsync(location, "two-dimensional", CancellationToken.None);
        var reloaded = await new MapVariantSelectionService(
                new JsonFileMapVariantPreferenceStore(Path.Combine(directory.Path, "map-defaults.json")))
            .SelectAsync(location, CancellationToken.None);

        Assert.Equal("two-dimensional", chosen.Key);
        Assert.Equal(chosen, reloaded);
    }

    [Fact]
    public async Task CorruptMapDefaultsFallBackAndCanBeReplacedSafely()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "map-defaults.json");
        await File.WriteAllTextAsync(settingsPath, "{ definitely not valid json");
        var location = CreateLocationWithAssets();
        var service = new MapVariantSelectionService(new JsonFileMapVariantPreferenceStore(settingsPath));

        var fallback = await service.SelectAsync(location, CancellationToken.None);
        var chosen = await service.ChooseAsync(location, "two-dimensional", CancellationToken.None);
        var reloaded = await new MapVariantSelectionService(new JsonFileMapVariantPreferenceStore(settingsPath))
            .SelectAsync(location, CancellationToken.None);

        Assert.Equal("interactive", fallback?.Key);
        Assert.Equal("two-dimensional", chosen.Key);
        Assert.Equal(chosen, reloaded);
    }

    /// <summary>
    /// Which layers a map opens with, and why those.
    /// </summary>
    /// <remarks>
    /// Reported as the map not marking quest objectives at all. It always could: the projection
    /// places every objective the catalog gives coordinates for, the map draws them, and the
    /// panel beside it lists them. The layer simply arrived hidden, behind a Layers expander
    /// that is collapsed by default, so none of it reached anybody.
    ///
    /// Spawns and locked doors stay off, and that is a judgement rather than an inconsistency:
    /// those draw every spawn and every door on the map whether or not they concern the player,
    /// while quest objectives are only ever the quests this player is actually on, because the
    /// projection filters to active and pinned.
    /// </remarks>
    [Fact]
    public void AMapOpensShowingWhatConcernsThisPlayerAndNotEverythingElse()
    {
        var location = ParseFixture().Locations[0];
        var overlays = new MapPresentationService()
            .Create(location, location.Variants[0], "/cache/map.svg")
            .Overlays;

        Assert.True(overlays.Single(layer => layer.Kind == MapOverlayKind.QuestObjectives).IsVisible);
        Assert.True(overlays.Single(layer => layer.Kind == MapOverlayKind.Extracts).IsVisible);
        Assert.False(overlays.Single(layer => layer.Kind == MapOverlayKind.Spawns).IsVisible);
        Assert.False(overlays.Single(layer => layer.Kind == MapOverlayKind.Keys).IsVisible);
    }

    [Fact]
    public void PresentationKeepsOverlayLayersIndependentAndShowsAttribution()
    {
        var location = ParseFixture().Locations[0];
        var variant = location.Variants[0];
        var service = new MapPresentationService();

        var model = service.Create(location, variant, "/cache/map.svg");
        var changed = model
            .SetLayerVisibility(MapOverlayKind.RiskAndTraffic, false)
            .HighlightLayer(MapOverlayKind.Extracts);

        Assert.False(changed.Overlays.Single(layer => layer.Kind == MapOverlayKind.RiskAndTraffic).IsVisible);
        Assert.True(changed.Overlays.Single(layer => layer.Kind == MapOverlayKind.Extracts).IsHighlighted);
        Assert.True(changed.Overlays.Single(layer => layer.Kind == MapOverlayKind.Labels).IsVisible);
        // On by default now. The whole quest pipeline exists to draw this layer, and it used to
        // arrive hidden behind a Layers expander that is itself collapsed — which is how a
        // feature that was built, bound and working was reported as not existing.
        Assert.True(changed.Overlays.Single(layer => layer.Kind == MapOverlayKind.QuestObjectives).IsVisible);
        Assert.Equal("Fixture Warehouse", Assert.Single(changed.VisibleOverlayElements).Label);
        Assert.Empty(changed.SetLayerVisibility(MapOverlayKind.Labels, false).VisibleOverlayElements);
        Assert.Empty(changed.SelectFloor("layer-1-underground").VisibleOverlayElements);
        Assert.Equal("Fixture Warehouse", Assert.Single(changed.SelectFloor("layer-0-upper-floor").VisibleOverlayElements).Label);
        Assert.Contains("Synthetic Fixture Authors", changed.AttributionText, StringComparison.Ordinal);
        Assert.Contains("CC BY-NC-SA 4.0", changed.AttributionText, StringComparison.Ordinal);
        Assert.Equal(MapTransformAvailability.Valid, changed.TransformAvailability);
        Assert.Equal("Upper Floor", service.SelectFloor(variant, new WorldPosition(0, 8, 0))?.Name);
        Assert.Equal("Underground", service.SelectFloor(variant, new WorldPosition(0, -10, 0))?.Name);
        Assert.Empty(service.Create(
            location,
            variant,
            assetAvailability: MapAssetAvailability.Unavailable).VisibleOverlayElements);
    }

    [Fact]
    public void PresentationRefusesInvalidOrMissingTransformsWithoutGuessing()
    {
        var location = ParseFixture().Locations[0];
        var invalid = location.Variants[0] with
        {
            Transform = new(0, 10, 0.25, 20, 90),
        };

        var model = new MapPresentationService().Create(location, invalid);

        Assert.Equal(MapTransformAvailability.Invalid, model.TransformAvailability);
        Assert.False(model.TryMapPosition(new WorldPosition(4, 0, 2), out _));
        Assert.Contains("hidden", model.TransformMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SvgCanvasMappingUsesTheSameFullExtentAsTheFillBackground()
    {
        var location = ParseFixture().Locations[0];
        var variant = location.Variants[0] with { TilePath = null };
        var render = new MapPresentationService().Create(location, variant);
        var mapper = MapCanvasCoordinateMapper.Create(render, 900, 620);
        var bounds = variant.SvgBounds!;
        var projected = new[]
        {
            new WorldPosition(bounds.First.X, 0, bounds.First.Y),
            new WorldPosition(bounds.First.X, 0, bounds.Second.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.First.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.Second.Y),
        }.Select(position =>
        {
            Assert.True(variant.Transform!.TryProject(position, out var point));
            return mapper!(point);
        }).ToArray();

        Assert.Equal(MapBackgroundKind.Svg, render.Background?.Kind);
        Assert.Equal(0, projected.Min(point => point.X), 6);
        Assert.Equal(900, projected.Max(point => point.X), 6);
        Assert.Equal(0, projected.Min(point => point.Y), 6);
        Assert.Equal(620, projected.Max(point => point.Y), 6);
    }

    [Fact]
    public async Task CatalogClientUsesVerifiedOfflineCacheWhenRefreshFails()
    {
        using var directory = new TemporaryDirectory();
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var handler = new QueueHttpMessageHandler(
            _ => JsonResponse(File.ReadAllText(FixturePath)),
            _ => throw new HttpRequestException("offline"));
        var client = new TarkovDevMapCatalogClient(
            new HttpClient(handler),
            new(CatalogUri, directory.Path, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), 1024 * 1024),
            time);

        var online = await client.GetAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(2));
        var offline = await client.GetAsync(CancellationToken.None);

        Assert.Equal(MapCatalogAvailability.Current, online.Availability);
        Assert.Equal(MapCatalogAvailability.OfflineCached, offline.Availability);
        Assert.NotNull(offline.Catalog);
        Assert.NotNull(online.SourceJson);
        Assert.Equal(online.SourceJson, offline.SourceJson);
        Assert.Equal(online.Catalog?.Provenance.ContentSha256, offline.Catalog.Provenance.ContentSha256);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task AssetCacheRetainsProvenanceAndFallsBackOfflineWithoutNetworkTests()
    {
        using var directory = new TemporaryDirectory();
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var bytes = Encoding.UTF8.GetBytes("synthetic png fixture bytes");
        var handler = new QueueHttpMessageHandler(
            _ => ImageResponse(bytes),
            _ => throw new HttpRequestException("offline"));
        var cache = new TarkovDevMapAssetCache(
            new HttpClient(handler),
            new(directory.Path, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), 1024),
            time);
        var source = new Uri("https://assets.tarkov.dev/maps/synthetic/1/2/3.png");
        var authorLink = new Uri("https://tarkov.dev");

        var online = await cache.GetAsync(source, "Fixture Author", authorLink, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(2));
        var offline = await cache.GetAsync(source, "Fixture Author", authorLink, CancellationToken.None);

        Assert.NotNull(online.Asset);
        Assert.True(File.Exists(online.Asset.LocalPath));
        Assert.Equal("Fixture Author", offline.Asset?.Author);
        Assert.Equal(TarkovDevMapAssetCache.LicenseIdentifier, offline.Asset?.LicenseIdentifier);
        Assert.Equal(MapAssetAvailability.CachedOffline, offline.Asset?.Availability);
        Assert.Equal(online.Asset.ContentSha256, offline.Asset?.ContentSha256);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task AssetCacheEvictsOldestEntriesAtItsConfiguredBound()
    {
        using var directory = new TemporaryDirectory();
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var handler = new QueueHttpMessageHandler(
            _ => ImageResponse([1]),
            _ => ImageResponse([2]),
            _ => ImageResponse([3]));
        var options = new MapAssetCacheOptions(
            directory.Path,
            TimeSpan.FromDays(1),
            TimeSpan.FromSeconds(1),
            1024)
        {
            MaximumCacheEntries = 2,
            MaximumCacheBytes = 1024 * 1024,
        };
        var cache = new TarkovDevMapAssetCache(new HttpClient(handler), options, time);

        var first = await cache.GetAsync(
            new Uri("https://assets.tarkov.dev/maps/synthetic/first.png"), null, null, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        var second = await cache.GetAsync(
            new Uri("https://assets.tarkov.dev/maps/synthetic/second.png"), null, null, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        var third = await cache.GetAsync(
            new Uri("https://assets.tarkov.dev/maps/synthetic/third.png"), null, null, CancellationToken.None);

        Assert.False(File.Exists(first.Asset!.LocalPath));
        Assert.True(File.Exists(second.Asset!.LocalPath));
        Assert.True(File.Exists(third.Asset!.LocalPath));
        Assert.Equal(2, Directory.EnumerateFiles(directory.Path, "*.metadata.json").Count());
    }

    [Fact]
    public async Task SvgAssetRetainsOriginalAndCreatesOnlyALocalRenderPreview()
    {
        using var directory = new TemporaryDirectory();
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 10">
              <rect width="20" height="10" fill="#123456" />
            </svg>
            """;
        var handler = new QueueHttpMessageHandler(_ => AssetResponse(Encoding.UTF8.GetBytes(svg), "image/svg+xml"));
        var cache = new TarkovDevMapAssetCache(
            new HttpClient(handler),
            new(directory.Path, TimeSpan.FromDays(1), TimeSpan.FromSeconds(1), 1024 * 1024));

        var result = await cache.GetAsync(
            new Uri("https://assets.tarkov.dev/maps/svg/SyntheticFixture.svg"),
            "Fixture Author",
            new Uri("https://tarkov.dev"),
            CancellationToken.None);

        Assert.NotNull(result.Asset);
        Assert.EndsWith(".svg", result.Asset.LocalPath, StringComparison.Ordinal);
        Assert.EndsWith(".preview.png", result.Asset.RenderPath, StringComparison.Ordinal);
        Assert.True(File.Exists(result.Asset.LocalPath));
        Assert.True(File.Exists(result.Asset.RenderPath));
        Assert.NotEqual(result.Asset.LocalPath, result.Asset.RenderPath);
    }

    /// <summary>
    /// A small drawing is rendered large, because it is a vector and can be.
    /// </summary>
    /// <remarks>
    /// Reported as "the factory map drawing is super low res and not useful". The scale was
    /// clamped at 1, which treated the SVG as though enlarging it would interpolate — so every
    /// map was rasterised at whatever its author had typed into the viewBox, and Factory's is
    /// 130.81831 by 141.23242. A hundred and thirty pixels, stretched across the map panel.
    ///
    /// This fixture is 20 by 10, which under the old rule produced a 20-pixel image.
    /// </remarks>
    [Fact]
    public async Task ASmallSvgIsRenderedAtFullResolutionRatherThanItsIntrinsicSize()
    {
        using var directory = new TemporaryDirectory();
        const string svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 20 10\">" +
            "<rect width=\"20\" height=\"10\" fill=\"#123456\" /></svg>";
        var handler = new QueueHttpMessageHandler(_ => AssetResponse(Encoding.UTF8.GetBytes(svg), "image/svg+xml"));
        var cache = new TarkovDevMapAssetCache(
            new HttpClient(handler),
            new(directory.Path, TimeSpan.FromDays(1), TimeSpan.FromSeconds(1), 1024 * 1024));

        var result = await cache.GetAsync(
            new Uri("https://assets.tarkov.dev/maps/svg/Tiny.svg"),
            "Fixture Author",
            new Uri("https://tarkov.dev"),
            CancellationToken.None);

        using var rendered = SKBitmap.Decode(result.Asset!.RenderPath);

        // 20 x 10, scaled to fill 4096 along its longer side.
        Assert.Equal(4096, rendered.Width);
        Assert.Equal(2048, rendered.Height);
    }

    [Fact]
    public async Task SvgFloorSelectionRendersOnlyTheExplicitUpstreamLayer()
    {
        using var directory = new TemporaryDirectory();
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 10">
              <g id="Ground_Level"><rect width="20" height="10" fill="#123456" /></g>
              <g id="Upper_Floor"><rect width="20" height="10" fill="#ABCDEF" /></g>
            </svg>
            """;
        var handler = new QueueHttpMessageHandler(_ => AssetResponse(Encoding.UTF8.GetBytes(svg), "image/svg+xml"));
        var cache = new TarkovDevMapAssetCache(
            new HttpClient(handler),
            new(directory.Path, TimeSpan.FromDays(1), TimeSpan.FromSeconds(1), 1024 * 1024));
        var variant = ParseFixture().Locations[0].Variants[0];

        var baseFloor = await cache.GetSvgAsync(variant, variant.Floors[0], CancellationToken.None);
        var basePreview = await File.ReadAllBytesAsync(baseFloor.Asset!.RenderPath);
        var upperFloor = await cache.GetSvgAsync(variant, variant.Floors[1], CancellationToken.None);
        var upperPreview = await File.ReadAllBytesAsync(upperFloor.Asset!.RenderPath);

        Assert.False(basePreview.SequenceEqual(upperPreview));
        Assert.Contains("Upper_Floor", upperFloor.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task SvgPreviewRejectsExternalResourceReferences()
    {
        using var directory = new TemporaryDirectory();
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 10">
              <image href="file:///private/example.png" />
            </svg>
            """;
        var handler = new QueueHttpMessageHandler(_ => AssetResponse(Encoding.UTF8.GetBytes(svg), "image/svg+xml"));
        var cache = new TarkovDevMapAssetCache(
            new HttpClient(handler),
            new(directory.Path, TimeSpan.FromDays(1), TimeSpan.FromSeconds(1), 1024 * 1024));

        var result = await cache.GetAsync(
            new Uri("https://assets.tarkov.dev/maps/svg/UnsafeFixture.svg"),
            null,
            null,
            CancellationToken.None);

        Assert.Null(result.Asset);
        Assert.Contains("external resource", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static TarkovDevMapCatalog ParseFixture() => TarkovDevMapCatalogParser.Parse(
        File.ReadAllText(FixturePath),
        CatalogUri,
        DateTimeOffset.UnixEpoch);

    private static string FixturePath => Fixture("fixtures/maps/tarkov-dev-catalog.synthetic.json");

    private static MapLocation CreateLocationWithAssets()
    {
        var variants = new[]
        {
            CreateVariant("two-dimensional", MapProjectionKind.TwoDimensional, new Uri("https://assets.tarkov.dev/two.svg")),
            CreateVariant("interactive", MapProjectionKind.Interactive, new Uri("https://assets.tarkov.dev/interactive.svg")),
        };
        return new("location", null, "Location", null, null, variants);
    }

    private static MapVariant CreateVariant(string key, MapProjectionKind projection, Uri svgPath) => new(
        "location",
        key,
        projection,
        projection.ToString(),
        null,
        null,
        svgPath,
        null,
        256,
        1,
        5,
        new(new(0, 0), new(1, 1)),
        null,
        new(1, 0, 1, 0, 0),
        null,
        null,
        null,
        "Author",
        new Uri("https://tarkov.dev"),
        [],
        [],
        []);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage ImageResponse(byte[] bytes) => AssetResponse(bytes, "image/png");

    private static HttpResponseMessage AssetResponse(byte[] bytes, string mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return response;
    }

    private static string Fixture(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate test fixture {relativePath}.");
    }

    private sealed class QueueHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _requestCount) - 1;
            return Task.FromResult(responses[index](request));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan amount) => utcNow += amount;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tarkov-map-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
