using System.Globalization;
using Avalonia;
using Avalonia.Media;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [V2 rough package 23] Everything drawn on a map lands on the artwork.
/// </summary>
/// <remarks>
/// #393 checked this proportionally: a scene point at (25, 75) in a 0–100 box lands a quarter
/// across and three quarters down the drawn rectangle. That held while the fault was upstream of
/// it — the cockpit declared a 0–100 box as the plan's bounds and then filled the scene with
/// Leaflet map units from the variant's own transform, so the box and the coordinates in it
/// described different rectangles and Customs' extracts piled up against the plan's edges
/// instead of sitting on its exits.
///
/// These tests therefore start from world coordinates and a catalog transform, run the real
/// adapter (<see cref="RaidCockpitViewModel.PlanBoundsFor"/>, the assembler, the renderer) and
/// check the marker against where the artwork itself puts that world position — computed here
/// from the variant's bounds corners, independently of the code under test. They are run on a
/// wide map and a tall one, at two card sizes, turned and unturned, because the fault was
/// invisible on any map whose projected bounds happened to resemble a 0–100 square.
///
/// The world coordinates are fixture values: this repository carries no real tarkov.dev map
/// catalog, so the transforms are shaped like the catalog's (a non-unit scale, an offset, a
/// coordinate rotation, bounds well away from 0–100) rather than quoted from it.
/// </remarks>
public sealed class MapMarkerLandingTests
{
    private static readonly MapSceneRendererPresentation Presentation =
        MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc);

    private static readonly DateTimeOffset NowUtc = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Where three known Customs features are, in world coordinates.</summary>
    private static readonly (string Name, WorldPosition World)[] CustomsFeatures =
    [
        ("Old Gas Station", new(-120, 0, -60)),
        ("Crossroads", new(-340, 0, 90)),
        ("RUAF Roadblock", new(480, 0, -250)),
    ];

    private static readonly (string Name, WorldPosition World)[] LighthouseFeatures =
    [
        ("Northern Road", new(-760, 0, 180)),
        ("Path to Lighthouse", new(240, 0, -95)),
        ("V-Ex", new(700, 0, 220)),
    ];

    public static TheoryData<int, int, double> Cards => new()
    {
        { 1920, 1080, 0 },
        { 1920, 1080, 90 },
        { 3840, 1080, 0 },
        { 3840, 1080, 270 },
    };

    [Theory]
    [MemberData(nameof(Cards))]
    public void Customs_extracts_land_on_the_artwork(int width, int height, double bearingDegrees)
    {
        AssertFeaturesLand(Customs(), CustomsFeatures, width, height, bearingDegrees);
    }

    [Theory]
    [MemberData(nameof(Cards))]
    public void Lighthouse_extracts_land_on_the_artwork(int width, int height, double bearingDegrees)
    {
        AssertFeaturesLand(Lighthouse(), LighthouseFeatures, width, height, bearingDegrees);
    }

    [Fact]
    public void The_plan_rectangle_keeps_each_maps_own_shape()
    {
        // The scene's bounds are the artwork's rectangle, so they say what shape the map is —
        // which is the whole reason a wide map is no longer squashed into a square. Customs is
        // wider than it is tall and Lighthouse is the other way round.
        var customs = RaidCockpitViewModel.PlanBoundsFor(Customs());
        var lighthouse = RaidCockpitViewModel.PlanBoundsFor(Lighthouse());

        Assert.True(customs.Width > customs.Height);
        Assert.True(lighthouse.Height > lighthouse.Width);
        // And neither is the 0-100 box that used to stand in for both.
        Assert.NotEqual(new MapSceneBounds(0, 0, 100, 100), customs);
        Assert.NotEqual(new MapSceneBounds(0, 0, 100, 100), lighthouse);
    }

    [Fact]
    public void Nothing_on_a_placeable_map_is_reported_as_off_plan()
    {
        // The "24 off-plan" chip counts objects whose geometry leaves the plan. Every feature
        // here is inside the map's own reviewed bounds, so the honest count is none: the chip
        // was counting the mismatch between the coordinates and the box, not the map.
        var renderer = Render(Customs(), CustomsFeatures, 1920, 1080, bearingDegrees: 0);

        Assert.DoesNotContain("off-plan", renderer.DenseSceneChip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outside", renderer.DenseSceneNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_player_their_trail_and_a_squadmate_land_on_the_artwork_too()
    {
        // If the extracts are wrong they are all wrong, because everything on the plan is
        // projected the same way. The live layers go through the cockpit's own builder.
        var model = Customs();
        var player = CustomsFeatures[0].World;
        var mate = CustomsFeatures[2].World;
        var live = RaidCockpitViewModel.BuildLiveLayers(
            new(
                new ScreenshotPosition(NowUtc.AddSeconds(-5), player, default, 90, null, null, "demo.png"),
                [
                    new ScreenshotPosition(NowUtc.AddSeconds(-60), CustomsFeatures[1].World, default, 0, null, null, "a.png"),
                    new ScreenshotPosition(NowUtc.AddSeconds(-5), player, default, 90, null, null, "b.png"),
                ],
                [
                    new GroupMemberView(
                        "Geo", model.Location.Id, RaidLifecycleState.InRaid, "PMC", mate, 0,
                        TimeSpan.FromSeconds(5), [], []),
                ],
                _ => true,
                _ => "#FF00FF00",
                false,
                [],
                false),
            model,
            NowUtc);

        var renderer = Render(model, [], 1920, 1080, bearingDegrees: 0, live.Layers, live.Objects);
        var expectedPlayer = OnArtwork(model, renderer, player);
        var marker = Assert.Single(renderer.SpatialObjects, item =>
            item.SceneObject?.Kind == MapSceneObjectKind.LastKnownPosition);
        AssertAnchoredAt(renderer, marker, expectedPlayer);

        var mateMarker = Assert.Single(renderer.SpatialObjects, item =>
            item.SceneObject?.Kind == MapSceneObjectKind.TeammateLastKnown);
        AssertAnchoredAt(renderer, mateMarker, OnArtwork(model, renderer, mate));

        // The trail is drawn as geometry rather than a marker; its last point is where the
        // player is standing, so it has to agree with the player's own dot.
        var trail = Assert.Single(renderer.GeometryObjects, item => item.SceneObject.Kind == MapSceneObjectKind.Route);
        var last = trail.Points[^1];
        Assert.Equal(expectedPlayer.X, last.X, 1);
        Assert.Equal(expectedPlayer.Y, last.Y, 1);
    }

    private static void AssertFeaturesLand(
        MapRenderModel model,
        IReadOnlyList<(string Name, WorldPosition World)> features,
        int width,
        int height,
        double bearingDegrees)
    {
        var renderer = Render(model, features, width, height, bearingDegrees);

        foreach (var (name, world) in features)
        {
            var marker = Assert.Single(renderer.SpatialObjects, item => item.Label == name);
            AssertAnchoredAt(renderer, marker, OnArtwork(model, renderer, world));
        }

        // And none of them is piled against an edge of the plan, which is what a mismatched
        // projection looks like on screen.
        foreach (var marker in renderer.SpatialObjects)
        {
            var centreX = marker.AnchorLeft + (MapSceneRendererViewModel.MarkerExtent / 2);
            var centreY = marker.AnchorTop + (MapSceneRendererViewModel.MarkerExtent / 2);
            Assert.InRange(centreX, renderer.MapLeft, renderer.MapLeft + renderer.MapWidth);
            Assert.InRange(centreY, renderer.MapTop, renderer.MapTop + renderer.MapHeight);
        }
    }

    private static void AssertAnchoredAt(
        MapSceneRendererViewModel renderer,
        MapSceneRendererObjectViewModel marker,
        (double X, double Y) expected)
    {
        var centreX = marker.AnchorLeft + (MapSceneRendererViewModel.MarkerExtent / 2);
        var centreY = marker.AnchorTop + (MapSceneRendererViewModel.MarkerExtent / 2);
        Assert.Equal(expected.X, centreX, 1);
        Assert.Equal(expected.Y, centreY, 1);
    }

    /// <summary>
    /// Where the artwork puts a world position, worked out from the variant alone.
    /// </summary>
    /// <remarks>
    /// Deliberately not through <see cref="MapPlanProjection"/>: the drawn plan is the variant's
    /// projected bounds stretched across MapLeft/MapTop/MapWidth/MapHeight (see
    /// MapSceneRendererView.axaml), so this rederives that from the catalog transform and asks
    /// the renderer to agree with it.
    /// </remarks>
    private static (double X, double Y) OnArtwork(
        MapRenderModel model,
        MapSceneRendererViewModel renderer,
        WorldPosition world)
    {
        var bounds = model.Variant.SvgBounds ?? model.Variant.Bounds!;
        var transform = model.Variant.Transform!;
        var corners = new[]
        {
            new WorldPosition(bounds.First.X, 0, bounds.First.Y),
            new WorldPosition(bounds.First.X, 0, bounds.Second.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.First.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.Second.Y),
        };
        var projected = corners.Select(corner =>
        {
            transform.TryProject(corner, out var point);
            return point;
        }).ToArray();
        var minimumX = projected.Min(point => point.X);
        var maximumX = projected.Max(point => point.X);
        var minimumY = projected.Min(point => point.Y);
        var maximumY = projected.Max(point => point.Y);
        Assert.True(transform.TryProject(world, out var here));
        return (
            renderer.MapLeft + ((here.X - minimumX) / (maximumX - minimumX) * renderer.MapWidth),
            renderer.MapTop + ((here.Y - minimumY) / (maximumY - minimumY) * renderer.MapHeight));
    }

    private static MapSceneRendererViewModel Render(
        MapRenderModel model,
        IReadOnlyList<(string Name, WorldPosition World)> features,
        int width,
        int height,
        double bearingDegrees,
        IReadOnlyList<MapSceneLayer>? extraLayers = null,
        IReadOnlyList<MapSceneObject>? extraObjects = null)
    {
        var elements = features
            .Select(feature =>
            {
                Assert.True(model.Variant.Transform!.TryProject(feature.World, out var point));
                return new MapSceneLegacyElement(
                    new MapOverlayElement(MapOverlayKind.Extracts, point, feature.Name),
                    new DataProvenance("fixture", NowUtc));
            })
            .ToArray();
        var bounds = RaidCockpitViewModel.PlanBoundsFor(model);
        var centre = new MapSceneCamera(
            bounds.MinimumX + (bounds.Width / 2),
            bounds.MinimumY + (bounds.Height / 2),
            1,
            bearingDegrees,
            0);
        var result = new MapSceneAssembler().Build(new(
            1,
            model,
            bounds,
            model.Variant.Key,
            new(MapSceneMode.Flat2D, model.SelectedFloor?.Id, centre, []),
            elements,
            extraLayers ?? [],
            extraObjects ?? [],
            [Asset()]));
        var scene = Assert.IsType<MapSceneSnapshot>(result.Scene);
        var renderer = new MapSceneRendererViewModel(
            scene,
            Presentation,
            reviewedAssetResolver: _ => new TestArtwork(new(2048, 2048 / bounds.Width * bounds.Height)));
        renderer.SetViewportSize(width, height);
        return renderer;
    }

    /// <summary>
    /// A wide map with a rotated transform and bounds nowhere near a 0-100 box.
    /// </summary>
    private static MapRenderModel Customs() => Model(
        "customs",
        "Customs",
        new MapCatalogBounds(new(-390, -380), new(570, 180)),
        new MapCatalogTransform(0.0619, 89.5, 0.0619, 128, 180));

    /// <summary>A tall map, turned a quarter of the way round by its own transform.</summary>
    private static MapRenderModel Lighthouse() => Model(
        "lighthouse",
        "Lighthouse",
        new MapCatalogBounds(new(-900, -300), new(900, 300)),
        new MapCatalogTransform(0.05, 60, 0.05, -20, 90));

    private static MapRenderModel Model(
        string id,
        string name,
        MapCatalogBounds bounds,
        MapCatalogTransform transform)
    {
        var location = new MapLocation(id, null, name, null, null, []);
        var floors = new[] { new MapFloorDefinition("ground", "Ground", null, null, true, []) };
        var variant = new MapVariant(
            location.Id,
            $"{id}-interactive",
            MapProjectionKind.Interactive,
            "2D",
            null,
            null,
            new($"https://example.test/{id}.svg"),
            null,
            256,
            1,
            5,
            bounds,
            bounds,
            transform,
            null,
            null,
            null,
            "Example author",
            new("https://example.test/author"),
            [],
            floors,
            []);
        var overlays = Enum.GetValues<MapOverlayKind>()
            .Select(kind => new MapOverlayLayer(kind, kind.ToString(), true, false))
            .ToArray();
        return new(
            location,
            variant,
            new(MapBackgroundKind.Svg, variant.SvgPath!, $"/cache/{id}.svg", MapAssetAvailability.Available, null),
            MapTransformAvailability.Valid,
            "Validated transform.",
            overlays,
            [],
            floors,
            floors[0],
            "Example attribution",
            new("https://example.test/licence"));
    }

    private static MapSceneAsset Asset() => new(
        new("asset:fixture"),
        MapSceneAssetKind.Background2D,
        new("https://example.test/maps/plan.svg"),
        new("https://example.test/licence"),
        new string('b', 64),
        "Example map author",
        "map-1",
        "game-1",
        MapSceneAssetReviewStatus.Reviewed,
        NowUtc);

    private sealed class TestArtwork(Size size) : IImage
    {
        public Size Size { get; } = size;

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }
    }
}
