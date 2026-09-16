using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.Maps.Scene;

public sealed class MapSceneContractsTests
{
    [Fact]
    public void Historical_estimates_cannot_lose_their_model_and_time_window()
    {
        var action = () => Object(
            new("traffic"),
            MapSceneTruthKind.HistoricalEstimate,
            estimate: null);

        var exception = Assert.Throws<ArgumentException>(action);
        Assert.Contains("estimate metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scene_rejects_an_unreviewed_asset_instead_of_rendering_it_as_fact()
    {
        var asset = Asset(MapSceneAssetReviewStatus.Unavailable);

        var action = () => Scene(assets: [asset]);

        var exception = Assert.Throws<ArgumentException>(action);
        Assert.Contains("unreviewed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Visible_list_uses_the_same_layer_and_floor_state_as_the_map()
    {
        var extracts = new MapSceneLayer(new("extracts"), "Extracts", 10, true);
        var loot = new MapSceneLayer(new("loot"), "Loot", 20, true);
        var firstFloor = Object(extracts.Id, floors: ["first"], label: "First-floor exit");
        var secondFloor = Object(extracts.Id, floors: ["second"], label: "Second-floor exit");
        var hiddenLoot = Object(loot.Id, floors: ["first"], label: "Potential loot");
        var view = new MapSceneViewState(
            MapSceneMode.Flat2D,
            "first",
            new(50, 50, 1, 0, 0),
            [new(extracts.Id, true), new(loot.Id, false)]);

        var scene = new MapSceneSnapshot(
            7,
            "factory",
            "factory-plan",
            "catalog-sha:abc",
            new(0, 0, 100, 100),
            ["first", "second"],
            Capabilities(),
            view,
            [extracts, loot],
            [firstFloor, secondFloor, hiddenLoot],
            [Asset()]);

        var entry = Assert.Single(scene.ListEntries);
        Assert.Equal("First-floor exit", entry.Label);
    }

    [Fact]
    public void Scene_rejects_a_mode_that_has_no_reviewed_content()
    {
        var scene = Scene();
        var unavailable = scene.Capabilities with
        {
            Interior3D = MapSceneCapability.Unavailable("No reviewed model."),
        };
        var view = scene.View with { Mode = MapSceneMode.Interior3D };

        var action = () => new MapSceneSnapshot(
            scene.Revision,
            scene.LocationId,
            scene.VariantKey,
            scene.TransformVersion,
            scene.Bounds,
            scene.FloorIds,
            unavailable,
            view,
            scene.Layers,
            scene.Objects,
            scene.Assets);

        Assert.Throws<ArgumentException>(action);
    }

    private static MapSceneSnapshot Scene(IReadOnlyList<MapSceneAsset>? assets = null)
    {
        var layer = new MapSceneLayer(new("extracts"), "Extracts", 1, true);
        return new(
            1,
            "customs",
            "customs-plan",
            "catalog-sha:abc",
            new(0, 0, 100, 100),
            [],
            Capabilities(),
            new(MapSceneMode.Flat2D, null, new(50, 50, 1, 0, 0), [new(layer.Id, true)]),
            [layer],
            [Object(layer.Id)],
            assets ?? [Asset()]);
    }

    private static MapSceneCapabilities Capabilities() => new(
        MapSceneCapability.Available,
        MapSceneCapability.Unavailable("No floors."),
        MapSceneCapability.Unavailable("No interior."));

    private static MapSceneObject Object(
        MapSceneLayerId layer,
        MapSceneTruthKind truth = MapSceneTruthKind.StaticReference,
        MapSceneEstimateMetadata? estimate = null,
        IReadOnlyList<string>? floors = null,
        string label = "Crossroads") => new(
            new($"object:{label}"),
            layer,
            MapSceneObjectKind.Extract,
            truth,
            label,
            null,
            MapSceneGeometry.At(new(10, 20)),
            floors ?? [],
            Provenance(),
            estimate);

    private static MapSceneAsset Asset(MapSceneAssetReviewStatus status = MapSceneAssetReviewStatus.Reviewed) => new(
        new("asset:plan"),
        MapSceneAssetKind.Background2D,
        new("https://example.test/maps/customs.svg"),
        new("https://example.test/licence"),
        new string('a', 64),
        "Example map author",
        "map-1",
        "game-1",
        status,
        new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));

    private static DataProvenance Provenance() => new(
        "fixture",
        new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero),
        Confidence: Confidence.Certain);
}
