using System.Globalization;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [V2 rough package 46] A pan holds where the pointer left it, at any zoom.
/// </summary>
/// <remarks>
/// Reported from a raid: "it isn't choppy, it's when I zoom in and then try to pan, it snaps back
/// to where it was" — the same family as the report #413 answered in September.
///
/// Two of the three candidates were already sound and are pinned here so nobody spends the
/// evening on them again: the clamp divides the viewport's half-extent by the camera's zoom, so
/// zooming in allows strictly more pan rather than less; and a rebuild with the same map, variant
/// and floor reuses the current view instead of re-fitting.
///
/// The third was the cause, and it is not in the renderer at all: V1 was still following the
/// player, because nothing told V1 that somebody had panned the V2 map by hand. V1 turns its own
/// following off when its own canvas is panned; the V2 renderer now reports pans through
/// <see cref="MapSceneRendererViewModel.CameraMovedByPlayer"/> and zooms separately. The host can
/// therefore retain Follow for a zoom without retaining it for a drag.
/// </remarks>
public sealed class MapPanHoldsTests
{
    private static readonly MapSceneRendererPresentation Presentation =
        MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc);

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public void A_pan_holds_where_the_pointer_left_it(int zoomSteps)
    {
        var renderer = Renderer();
        ZoomIn(renderer, zoomSteps);
        var before = renderer.Scene.View.Camera;
        var zoom = before.Zoom;

        renderer.BeginPan();
        renderer.UpdatePan(-120, -80);
        renderer.CommitPan();

        var after = renderer.Scene.View.Camera;
        // Dragging left and up moves the camera right and down, by the pointer's offset divided
        // by the projection's scale and the zoom. The point is that it moved and stayed moved.
        Assert.True(after.CenterX > before.CenterX, $"at zoom {zoom} the camera did not move: {after.CenterX} vs {before.CenterX}");
        Assert.True(after.CenterY > before.CenterY, $"at zoom {zoom} the camera did not move: {after.CenterY} vs {before.CenterY}");
        Assert.Equal(zoom, after.Zoom, 3);

        // And the same scene presented again — the once-a-tick rebuild — leaves it exactly there.
        renderer.Present(renderer.Scene);
        Assert.Equal(after.CenterX, renderer.Scene.View.Camera.CenterX, 6);
        Assert.Equal(after.CenterY, renderer.Scene.View.Camera.CenterY, 6);
        Assert.Equal(after.Zoom, renderer.Scene.View.Camera.Zoom, 6);
    }

    [Fact]
    public void At_the_fit_there_is_nothing_to_pan_which_is_why_this_looked_fine_at_zoom_one()
    {
        // The whole plan is on screen at the fit, so the clamp centres it rather than letting an
        // edge be dragged inside the card. A drag there is a no-op by design — and it is why the
        // snap only showed once he had zoomed in, and why "it just moves right back" has been
        // reported twice about two different causes.
        var renderer = Renderer();
        var before = renderer.Scene.View.Camera;

        renderer.BeginPan();
        renderer.UpdatePan(-120, -80);
        renderer.CommitPan();

        Assert.Equal(before.CenterX, renderer.Scene.View.Camera.CenterX, 6);
        Assert.Equal(before.CenterY, renderer.Scene.View.Camera.CenterY, 6);
    }

    [Fact]
    public void Zooming_in_allows_more_pan_rather_than_less()
    {
        // The clamp is on what the viewport can see, so the more it is zoomed the further the
        // centre may travel before the edge of the plan reaches the edge of the card. If this
        // ever inverts, a zoomed pan is clamped back towards the fit and looks like a snap.
        static double Reach(int zoomSteps)
        {
            var renderer = Renderer();
            ZoomIn(renderer, zoomSteps);
            var before = renderer.Scene.View.Camera.CenterX;
            renderer.BeginPan();
            renderer.UpdatePan(-5000, 0);
            renderer.CommitPan();
            return renderer.Scene.View.Camera.CenterX - before;
        }

        var atFit = Reach(0);
        var zoomedIn = Reach(5);
        Assert.True(zoomedIn > atFit, $"zoomed in reached {zoomedIn} where the fit reached {atFit}.");
    }

    [Fact]
    public void A_pan_and_a_zoom_report_their_distinct_player_intents()
    {
        // A pan stops following; a zoom changes Follow's remembered magnification while Follow
        // is on and stops following otherwise. Fit is neither player intent.
        var renderer = Renderer();
        var moves = 0;
        var zooms = 0;
        renderer.CameraMovedByPlayer += (_, _) => moves++;
        renderer.CameraZoomedByPlayer += (_, _) => zooms++;

        renderer.BeginPan();
        renderer.UpdatePan(-90, -40);
        renderer.CommitPan();
        Assert.Equal(1, moves);
        Assert.Equal(0, zooms);

        renderer.RequestZoom(1);
        Assert.Equal(1, moves);
        Assert.Equal(1, zooms);

        renderer.RequestZoomAt(1, 100, 100);
        Assert.Equal(1, moves);
        Assert.Equal(2, zooms);

        renderer.FitPlanCommand.Execute(null);
        Assert.Equal(1, moves);
        Assert.Equal(2, zooms);

        // A drag that never moved the pointer commits nothing and is not a move.
        renderer.BeginPan();
        renderer.CancelPan();
        Assert.Equal(1, moves);
        Assert.Equal(2, zooms);
    }

    /// <summary>Zooms the way the wheel and the zoom button do, since that is the only way in.</summary>
    private static void ZoomIn(MapSceneRendererViewModel renderer, int steps)
    {
        for (var step = 0; step < steps; step++)
        {
            renderer.RequestZoom(1);
        }
    }

    private static MapSceneRendererViewModel Renderer()
    {
        var scene = new MapSceneSnapshot(
            1,
            "customs",
            "customs",
            "customs",
            new MapSceneBounds(0, 0, 400, 300),
            [],
            new(MapSceneCapability.Available, MapSceneCapability.Unavailable("no stack"), MapSceneCapability.Unavailable("no interior")),
            new(MapSceneMode.Flat2D, null, new(200, 150, 1, 0, 0), []),
            [],
            [],
            []);
        var renderer = new MapSceneRendererViewModel(scene, Presentation);
        // The host half of the loop, exactly as the cockpit wires it: a camera change goes
        // through the canonical reducer and comes back as the next scene. Without it the
        // renderer asks and nothing answers, which is not the behaviour under test.
        renderer.ViewChangeRequested += change =>
        {
            var result = MapSceneViewReducer.Apply(renderer.Scene, change);
            if (result.Status is MapSceneViewChangeStatus.Applied or MapSceneViewChangeStatus.Unchanged)
            {
                renderer.Present(result.Scene);
            }
        };
        renderer.SetViewportSize(1344, 865);
        return renderer;
    }
}
