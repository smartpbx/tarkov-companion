using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.Maps.Scene;

public sealed class MapSceneHitTestingTests
{
    [Fact]
    public void Hit_testing_prefers_the_top_visible_layer()
    {
        var lower = new MapSceneLayer(new("lower"), "Lower", 10, true);
        var upper = new MapSceneLayer(new("upper"), "Upper", 20, true);
        var scene = Scene(
            [lower, upper],
            [Point(lower, "lower", 10, 10), Point(upper, "upper", 11, 10)],
            [new(lower.Id, true), new(upper.Id, true)]);

        var hits = MapSceneHitTesting.HitTest(scene, new(10, 10), 2);

        Assert.Equal(["upper", "lower"], hits.Select(hit => hit.Object.Label));
    }

    [Fact]
    public void A_point_inside_a_region_hits_with_zero_distance()
    {
        var layer = new MapSceneLayer(new("quest"), "Quest", 10, true);
        var region = new MapSceneObject(
            new("region"),
            layer.Id,
            MapSceneObjectKind.QuestObjective,
            MapSceneTruthKind.StaticReference,
            "Search area",
            null,
            new(MapSceneGeometryKind.Region, [new(0, 0), new(20, 0), new(20, 20), new(0, 20)]),
            [],
            Provenance());
        var scene = Scene([layer], [region], [new(layer.Id, true)]);

        var hit = Assert.Single(MapSceneHitTesting.HitTest(scene, new(10, 10), 0));

        Assert.Equal(0, hit.Distance);
    }

    [Fact]
    public void Hidden_layers_do_not_participate_in_hit_testing()
    {
        var layer = new MapSceneLayer(new("loot"), "Loot", 10, true);
        var scene = Scene([layer], [Point(layer, "GPU spawn", 10, 10)], [new(layer.Id, false)]);

        Assert.Empty(MapSceneHitTesting.HitTest(scene, new(10, 10), 5));
    }

    private static MapSceneSnapshot Scene(
        IReadOnlyList<MapSceneLayer> layers,
        IReadOnlyList<MapSceneObject> objects,
        IReadOnlyList<MapSceneLayerState> states) => new(
            1,
            "labs",
            "labs-plan",
            "transform-1",
            new(0, 0, 100, 100),
            [],
            new(
                MapSceneCapability.Available,
                MapSceneCapability.Unavailable("No floors."),
                MapSceneCapability.Unavailable("No interior.")),
            new(MapSceneMode.Flat2D, null, new(50, 50, 1, 0, 0), states),
            layers,
            objects,
            []);

    private static MapSceneObject Point(MapSceneLayer layer, string label, double x, double y) => new(
        new($"point:{label}"),
        layer.Id,
        MapSceneObjectKind.Custom,
        MapSceneTruthKind.StaticReference,
        label,
        null,
        MapSceneGeometry.At(new(x, y)),
        [],
        Provenance());

    private static DataProvenance Provenance() => new(
        "fixture",
        new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero),
        Confidence: Confidence.Certain);
}
