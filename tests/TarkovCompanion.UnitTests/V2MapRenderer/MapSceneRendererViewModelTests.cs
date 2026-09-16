using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

public sealed class MapSceneRendererViewModelTests
{
    [Fact]
    public void Renderer_emits_revision_checked_changes_without_mutating_its_scene()
    {
        var scene = Scene(revision: 41, supportsFloorStack: true);
        var changeIds = new Queue<Guid>([
            Guid.Parse("20000000-0000-0000-0000-000000000306"),
            Guid.Parse("20000000-0000-0000-0000-000000000307"),
        ]);
        var renderer = new MapSceneRendererViewModel(scene, changeIds.Dequeue);
        var published = new List<MapSceneViewChange>();
        renderer.ViewChangeRequested += published.Add;

        renderer.RequestMode(MapSceneMode.FloorStack2D);

        var modeChange = Assert.Single(published);
        Assert.Equal(scene.Revision, modeChange.ExpectedRevision);
        Assert.Equal(MapSceneViewChangeKind.SetMode, modeChange.Kind);
        Assert.Equal(MapSceneMode.FloorStack2D, modeChange.Mode);
        Assert.Same(scene, renderer.Scene);

        var updated = MapSceneViewReducer.Apply(scene, modeChange).Scene;
        renderer.Present(updated);
        renderer.Layers.Single(layer => layer.Layer.Id == new MapSceneLayerId("loot")).ToggleCommand.Execute(null);

        Assert.Equal(2, published.Count);
        var layerChange = published[1];
        Assert.Equal(updated.Revision, layerChange.ExpectedRevision);
        Assert.Equal(MapSceneViewChangeKind.SetLayerVisibility, layerChange.Kind);
        Assert.Equal("loot", layerChange.LayerId?.Value);
        Assert.True(layerChange.IsVisible.GetValueOrDefault());
    }

    [Fact]
    public void Renderer_uses_the_scene_list_for_floor_and_layer_filtered_details()
    {
        var scene = Scene(
            firstFloorObjects:
            [
                Object("crossroads", "Crossroads", "first", MapSceneOfferState.Offered),
                Object("dorms", "Dorms", "second", MapSceneOfferState.NotOffered),
            ]);
        var renderer = new MapSceneRendererViewModel(scene, () => Guid.Parse("20000000-0000-0000-0000-000000000308"));

        Assert.Equal(["Crossroads"], renderer.ListItems.Select(item => item.Label));
        Assert.Equal(["Crossroads"], renderer.SpatialObjects.Select(item => item.Label));

        renderer.SelectFloor("second");
        var floorChange = Assert.IsType<MapSceneViewChange>(renderer.LastRequestedChange);
        renderer.Present(MapSceneViewReducer.Apply(scene, floorChange).Scene);

        Assert.Equal(["Dorms"], renderer.ListItems.Select(item => item.Label));
        Assert.Equal(["Dorms"], renderer.SpatialObjects.Select(item => item.Label));
    }

    [Fact]
    public void Renderer_keeps_offered_not_offered_and_unknown_distinct()
    {
        var scene = Scene(firstFloorObjects:
        [
            Object("offered", "Road to Customs", "first", MapSceneOfferState.Offered),
            Object("not-offered", "Trailer Park", "first", MapSceneOfferState.NotOffered),
            Object("unknown", "Smuggler's Boat", "first", MapSceneOfferState.Unknown),
        ]);
        var renderer = new MapSceneRendererViewModel(scene);

        var offered = renderer.ListItems.Single(item => item.Label == "Road to Customs");
        var notOffered = renderer.ListItems.Single(item => item.Label == "Trailer Park");
        var unknown = renderer.ListItems.Single(item => item.Label == "Smuggler's Boat");

        Assert.True(offered.IsOffered);
        Assert.False(offered.IsKnownNotOffered);
        Assert.Equal("Offered this raid", offered.OfferedLabel);
        Assert.True(notOffered.IsKnownNotOffered);
        Assert.False(notOffered.IsOffered);
        Assert.Equal("Not offered this raid", notOffered.OfferedLabel);
        Assert.True(unknown.IsOfferUnknown);
        Assert.False(unknown.IsKnownNotOffered);
        Assert.Equal("Offer status unknown", unknown.OfferedLabel);
    }

    [Fact]
    public void Renderer_exposes_a_clear_2d_fallback_when_3d_is_not_available()
    {
        var renderer = new MapSceneRendererViewModel(Scene(interiorAvailable: false));

        Assert.True(renderer.ShowsThreeDimensionalFallback);
        Assert.Contains("3D view unavailable", renderer.ThreeDimensionalFallback);

        renderer.RequestMode(MapSceneMode.Interior3D);

        Assert.Null(renderer.LastRequestedChange);
        Assert.True(renderer.HasRendererNotice);
        Assert.Contains("unavailable", renderer.RendererNotice, StringComparison.OrdinalIgnoreCase);
    }

    private static MapSceneSnapshot Scene(
        long revision = 7,
        bool supportsFloorStack = false,
        bool interiorAvailable = false,
        IReadOnlyList<MapSceneObject>? firstFloorObjects = null)
    {
        var extracts = new MapSceneLayer(new("extracts"), "Extracts", 10, true);
        var loot = new MapSceneLayer(new("loot"), "Loot", 20, false);
        var objects = firstFloorObjects ?? [Object("crossroads", "Crossroads", "first", MapSceneOfferState.Unknown)];
        return new(
            revision,
            "customs",
            "customs-plan",
            "transform-1",
            new(0, 0, 100, 100),
            ["first", "second"],
            new(
                MapSceneCapability.Available,
                supportsFloorStack ? MapSceneCapability.Available : MapSceneCapability.Unavailable("No reviewed floor stack."),
                interiorAvailable ? MapSceneCapability.Available : MapSceneCapability.Unavailable("No reviewed interior model.")),
            new(MapSceneMode.Flat2D, "first", new(50, 50, 1, 0, 0), [new(extracts.Id, true), new(loot.Id, false)]),
            [extracts, loot],
            objects,
            [Asset()]);
    }

    private static MapSceneObject Object(string id, string label, string floor, MapSceneOfferState offerState) => new(
        new($"extract:{id}"),
        new("extracts"),
        MapSceneObjectKind.Extract,
        MapSceneTruthKind.StaticReference,
        label,
        $"{label} details",
        MapSceneGeometry.At(new(25, 50)),
        [floor],
        Provenance(),
        faction: MapFeatureFaction.Pmc,
        offerState: offerState);

    private static MapSceneAsset Asset() => new(
        new("asset:customs-plan"),
        MapSceneAssetKind.Background2D,
        new("https://example.test/maps/customs.svg"),
        new("https://example.test/licence"),
        new string('a', 64),
        "Example map author",
        "map-1",
        "game-1",
        MapSceneAssetReviewStatus.Reviewed,
        new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));

    private static DataProvenance Provenance() => new(
        "fixture",
        new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero),
        Confidence: Confidence.Certain);
}
