using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.Maps.Scene;

public sealed class MapSceneViewReducerTests
{
    [Fact]
    public void Paired_change_updates_the_canonical_layer_state_and_revision()
    {
        var scene = Scene();
        var change = new MapSceneViewChange(
            Guid.NewGuid(),
            scene.Revision,
            MapSceneViewChangeKind.SetLayerVisibility,
            LayerId: new("loot"),
            IsVisible: true);

        var result = MapSceneViewReducer.Apply(scene, change);

        Assert.Equal(MapSceneViewChangeStatus.Applied, result.Status);
        Assert.Equal(scene.Revision + 1, result.Scene.Revision);
        Assert.Contains(result.Scene.View.Layers, state => state.LayerId == new MapSceneLayerId("loot") && state.IsVisible);
    }

    [Fact]
    public void Stale_tablet_change_cannot_overwrite_newer_desktop_state()
    {
        var scene = Scene();
        var change = new MapSceneViewChange(
            Guid.NewGuid(),
            scene.Revision - 1,
            MapSceneViewChangeKind.SetCamera,
            Camera: new(20, 20, 2, 0, 0));

        var result = MapSceneViewReducer.Apply(scene, change);

        Assert.Equal(MapSceneViewChangeStatus.StaleRevision, result.Status);
        Assert.Same(scene, result.Scene);
    }

    [Fact]
    public void Client_cannot_select_an_unavailable_presentation_mode()
    {
        var scene = Scene();
        var change = new MapSceneViewChange(
            Guid.NewGuid(),
            scene.Revision,
            MapSceneViewChangeKind.SetMode,
            Mode: MapSceneMode.Interior3D);

        var result = MapSceneViewReducer.Apply(scene, change);

        Assert.Equal(MapSceneViewChangeStatus.Rejected, result.Status);
        Assert.Equal("map_mode_unavailable", result.ErrorCode);
        Assert.Same(scene, result.Scene);
    }

    [Fact]
    public void Replaying_the_current_value_does_not_churn_the_revision()
    {
        var scene = Scene();
        var change = new MapSceneViewChange(
            Guid.NewGuid(),
            scene.Revision,
            MapSceneViewChangeKind.SetLayerVisibility,
            LayerId: new("loot"),
            IsVisible: false);

        var result = MapSceneViewReducer.Apply(scene, change);

        Assert.Equal(MapSceneViewChangeStatus.Unchanged, result.Status);
        Assert.Same(scene, result.Scene);
    }

    private static MapSceneSnapshot Scene()
    {
        var layer = new MapSceneLayer(new("loot"), "High-value loot", 20, false);
        return new(
            42,
            "lighthouse",
            "lighthouse-plan",
            "transform-1",
            new(0, 0, 100, 100),
            ["ground"],
            new(
                MapSceneCapability.Available,
                MapSceneCapability.Unavailable("No stack."),
                MapSceneCapability.Unavailable("No interior.")),
            new(MapSceneMode.Flat2D, "ground", new(50, 50, 1, 0, 0), [new(layer.Id, false)]),
            [layer],
            [],
            []);
    }
}
