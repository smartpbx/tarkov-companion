using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.Maps.Scene;

public sealed class MapSceneAssemblerTests
{
    private static readonly DateTimeOffset RetrievedUtc =
        new(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Builds_one_renderer_neutral_scene_from_the_existing_map_model()
    {
        var model = Model([
            new(MapOverlayKind.Extracts, new(20, 30), "Crossroads", MinimumHeight: 0, MaximumHeight: 0),
            new(MapOverlayKind.Spawns, new(40, 50), "Potential PMC spawn", MinimumHeight: 12, MaximumHeight: 12),
        ]);

        var result = new MapSceneAssembler().Build(Request(model));

        var scene = Assert.IsType<MapSceneSnapshot>(result.Scene);
        Assert.Equal(MapSceneMode.Flat2D, scene.View.Mode);
        Assert.Equal(2, scene.Objects.Count);
        Assert.Equal(MapSceneTruthKind.StaticReference, scene.Objects[0].Truth);
        Assert.Equal(MapSceneTruthKind.PotentialSpawn, scene.Objects[1].Truth);
        Assert.Equal("lower", Assert.Single(scene.Objects[0].FloorIds));
        Assert.Equal("upper", Assert.Single(scene.Objects[1].FloorIds));
        Assert.False(scene.Capabilities.Interior3D.IsAvailable);
    }

    [Fact]
    public void Stable_object_ids_do_not_depend_on_catalog_order()
    {
        var first = new MapOverlayElement(MapOverlayKind.Extracts, new(20, 30), "Crossroads");
        var second = new MapOverlayElement(MapOverlayKind.Keys, new(40, 50), "Marked room");
        var assembler = new MapSceneAssembler();

        var forward = Assert.IsType<MapSceneSnapshot>(assembler.Build(Request(Model([first, second]))).Scene);
        var reverse = Assert.IsType<MapSceneSnapshot>(assembler.Build(Request(Model([second, first]))).Scene);

        Assert.Equal(
            forward.Objects.Select(item => item.Id.Value).Order(StringComparer.Ordinal),
            reverse.Objects.Select(item => item.Id.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Falls_back_to_flat_when_a_stale_client_requests_unavailable_3d()
    {
        var request = Request(Model([]));
        request = request with
        {
            RequestedView = request.RequestedView with { Mode = MapSceneMode.Interior3D },
        };

        var result = new MapSceneAssembler().Build(request);

        var scene = Assert.IsType<MapSceneSnapshot>(result.Scene);
        Assert.Equal(MapSceneMode.Flat2D, scene.View.Mode);
        Assert.False(scene.Capabilities.Interior3D.IsAvailable);
    }

    [Fact]
    public void Enables_3d_only_when_a_reviewed_interior_asset_is_present()
    {
        var request = Request(Model([]));
        request = request with
        {
            RequestedView = request.RequestedView with { Mode = MapSceneMode.Interior3D },
            Assets = [Asset(), Asset(MapSceneAssetKind.InteriorModel, "interior")],
        };

        var result = new MapSceneAssembler().Build(request);

        var scene = Assert.IsType<MapSceneSnapshot>(result.Scene);
        Assert.Equal(MapSceneMode.Interior3D, scene.View.Mode);
        Assert.True(scene.Capabilities.Interior3D.IsAvailable);
    }

    [Fact]
    public void Returns_an_honest_unavailable_result_without_a_reviewed_plan()
    {
        var request = Request(Model([])) with { Assets = [] };

        var result = new MapSceneAssembler().Build(request);

        Assert.False(result.IsAvailable);
        Assert.Contains("reviewed", result.UnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Does_not_guess_semantics_for_legacy_routes_marks_or_traffic()
    {
        var model = Model([
            new(MapOverlayKind.CompanionMarkers, new(10, 10), "Unknown marker"),
            new(MapOverlayKind.Routes, new(20, 20), "Unknown route"),
            new(MapOverlayKind.RiskAndTraffic, new(30, 30), "Unknown estimate"),
        ]);

        var scene = Assert.IsType<MapSceneSnapshot>(new MapSceneAssembler().Build(Request(model)).Scene);

        Assert.Empty(scene.Objects);
    }

    private static MapSceneBuildRequest Request(MapRenderModel model) => new(
        8,
        model,
        new(
            new("https://example.test/maps.json"),
            RetrievedUtc,
            new string('b', 64),
            MapCatalogAvailability.Current),
        new(0, 0, 100, 100),
        "maps-json:b",
        new(
            MapSceneMode.Flat2D,
            "lower",
            new(50, 50, 1, 0, 0),
            []),
        [],
        [],
        [Asset()]);

    private static MapSceneAsset Asset(
        MapSceneAssetKind kind = MapSceneAssetKind.Background2D,
        string suffix = "plan") => new(
            new($"asset:{suffix}"),
            kind,
            new($"https://example.test/{suffix}.asset"),
            new("https://example.test/licence"),
            new string('a', 64),
            "Example author",
            "map-1",
            "game-1",
            MapSceneAssetReviewStatus.Reviewed,
            RetrievedUtc);

    private static MapRenderModel Model(IReadOnlyList<MapOverlayElement> elements)
    {
        var location = new MapLocation("factory", null, "Factory", null, null, []);
        var floors = new[]
        {
            new MapFloorDefinition("lower", "Lower", null, null, true, [new(-10, 10, [])]),
            new MapFloorDefinition("upper", "Upper", null, null, false, [new(10, 30, [])]),
        };
        var variant = new MapVariant(
            location.Id,
            "factory-plan",
            MapProjectionKind.TwoDimensional,
            "2D",
            null,
            null,
            new("https://example.test/factory.svg"),
            null,
            256,
            null,
            null,
            new(new(0, 0), new(100, 100)),
            new(new(0, 0), new(100, 100)),
            new(1, 0, 1, 0, 0),
            null,
            null,
            null,
            "Example author",
            new("https://example.test/author"),
            [],
            floors,
            []);
        var overlays = Enum.GetValues<MapOverlayKind>()
            .Select((kind, index) => new MapOverlayLayer(kind, kind.ToString(), kind is not MapOverlayKind.Spawns, false))
            .ToArray();
        return new(
            location,
            variant,
            new(MapBackgroundKind.Svg, variant.SvgPath!, "/cache/factory.svg", MapAssetAvailability.Available, null),
            MapTransformAvailability.Valid,
            "Validated transform.",
            overlays,
            elements,
            floors,
            floors[0],
            "Example attribution",
            new("https://example.test/licence"));
    }
}
