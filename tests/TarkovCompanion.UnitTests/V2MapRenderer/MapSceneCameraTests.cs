using System.Globalization;
using Avalonia;
using Avalonia.Media;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [V2 rough package 23] A camera that does not fight the user.
/// </summary>
/// <remarks>
/// Reported from a live raid: the map opened zoomed into a corner, and moving it back to the
/// middle "just moves right back". Both came from clamping the camera's <em>centre</em> to the
/// plan's bounds. That let the plan be dragged until only a corner of it was on screen, and it
/// silently moved any centre that started outside the bounds — which a follow or a stale camera
/// easily produces — so every gesture was corrected to somewhere the player had not asked for.
///
/// The clamp is now on what the viewport can see, against the rectangle the artwork is actually
/// drawn into, and it is the same clamp during a drag as on the drag's commit.
/// </remarks>
public sealed class MapSceneCameraTests
{
    private static readonly MapSceneRendererPresentation Presentation =
        MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc);

    /// <summary>A wide plan, so the fit letterboxes vertically and pans horizontally.</summary>
    private static readonly MapSceneBounds WideBounds = new(54, 104, 114, 139);

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 1080)]
    [InlineData(640, 900)]
    public void A_map_opens_with_the_whole_plan_on_screen(int width, int height)
    {
        var renderer = Renderer(Scene(WideBounds));
        renderer.SetViewportSize(width, height);

        AssertWholePlanVisible(renderer);
        Assert.Equal(1, renderer.CameraZoom);
    }

    [Fact]
    public void A_new_map_is_fitted_again_rather_than_keeping_the_last_maps_camera()
    {
        // The cockpit builds a fresh view state whenever the plan rectangle changes, so a map
        // whose bounds are somewhere else entirely cannot inherit a camera pointing off it.
        var renderer = Renderer(Scene(WideBounds));
        renderer.SetViewportSize(1920, 1080);
        ZoomTo(renderer, 3);

        var next = new MapSceneBounds(-40, -900, 60, -120);
        renderer.Present(Scene(next, revision: renderer.Scene.Revision + 1, locationId: "lighthouse"));

        AssertWholePlanVisible(renderer);
    }

    [Fact]
    public void A_plan_smaller_than_the_card_sits_in_the_middle_of_it_and_stays_there()
    {
        var renderer = Renderer(Scene(WideBounds));
        renderer.SetViewportSize(1920, 1080);
        var published = new List<MapSceneViewChange>();
        renderer.ViewChangeRequested += published.Add;

        // At the fit the plan is inside the card on both axes, so there is nothing to pan to:
        // a drag leaves it centred rather than dragging it off one edge.
        renderer.BeginPan();
        renderer.UpdatePan(600, 400);
        Assert.Equal(renderer.CanvasWidth / 2, renderer.CameraPostTranslateX, 3);
        Assert.Equal(renderer.CanvasHeight / 2, renderer.CameraPostTranslateY, 3);
        renderer.CommitPan();

        var camera = Assert.Single(published).Camera!.Value;
        Assert.Equal(WideBounds.MinimumX + (WideBounds.Width / 2), camera.CenterX, 6);
        Assert.Equal(WideBounds.MinimumY + (WideBounds.Height / 2), camera.CenterY, 6);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    public void Panning_and_back_returns_to_exactly_where_it_started(int towardsX, int towardsY)
    {
        var renderer = Renderer(Scene(WideBounds));
        renderer.SetViewportSize(1920, 1080);
        ZoomTo(renderer, 4);
        var start = renderer.Scene.View.Camera;

        Drag(renderer, towardsX * 600, towardsY * 600);
        Assert.NotEqual(start, renderer.Scene.View.Camera);

        Drag(renderer, towardsX * -600, towardsY * -600);

        Assert.Equal(start.CenterX, renderer.Scene.View.Camera.CenterX, 6);
        Assert.Equal(start.CenterY, renderer.Scene.View.Camera.CenterY, 6);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    public void Dragging_past_an_edge_stops_with_that_edge_against_the_card(int towardsX, int towardsY)
    {
        // Dragged far past what the plan has to offer. The plan stops with its own edge on the
        // card's edge — it does not keep going into the surface behind it, and it does not come
        // back to somewhere else when the pointer is released.
        var renderer = Renderer(Scene(WideBounds));
        renderer.SetViewportSize(1920, 1080);
        ZoomTo(renderer, 4);

        Drag(renderer, towardsX * 6000, towardsY * 6000);

        var topLeft = OnScreen(renderer, renderer.MapLeft, renderer.MapTop);
        var bottomRight = OnScreen(
            renderer,
            renderer.MapLeft + renderer.MapWidth,
            renderer.MapTop + renderer.MapHeight);
        switch (towardsX, towardsY)
        {
            case (1, 0): Assert.Equal(0, topLeft.X, 3); break;
            case (-1, 0): Assert.Equal(renderer.CanvasWidth, bottomRight.X, 3); break;
            case (0, 1): Assert.Equal(0, topLeft.Y, 3); break;
            default: Assert.Equal(renderer.CanvasHeight, bottomRight.Y, 3); break;
        }
    }

    [Fact]
    public void The_plan_never_leaves_the_card_however_hard_it_is_dragged()
    {
        var renderer = Renderer(Scene(WideBounds));
        renderer.SetViewportSize(1920, 1080);
        ZoomTo(renderer, 4);

        Drag(renderer, -9000, -9000);

        // The far corner of the plan is still on screen: the clamp stops at the plan's edge
        // rather than letting the map slide away into the surface behind it.
        var corner = OnScreen(renderer, renderer.MapLeft + renderer.MapWidth, renderer.MapTop + renderer.MapHeight);
        Assert.InRange(corner.X, 0, renderer.CanvasWidth + 1);
        Assert.InRange(corner.Y, 0, renderer.CanvasHeight + 1);
    }

    [Fact]
    public void What_a_drag_shows_is_what_it_commits()
    {
        // The snap-back: the live drag used one clamp and the commit another, so letting go moved
        // the plan somewhere other than where the pointer left it.
        var renderer = Renderer(Scene(WideBounds));
        renderer.SetViewportSize(1920, 1080);
        ZoomTo(renderer, 4);
        var published = new List<MapSceneViewChange>();
        renderer.ViewChangeRequested += published.Add;

        renderer.BeginPan();
        renderer.UpdatePan(5000, 120);
        // Dragged well past the edge, so the clamp is what decides where the plan stopped.
        var shown = OnScreen(renderer, renderer.MapLeft, renderer.MapTop);
        renderer.CommitPan();
        renderer.Present(MapSceneViewReducer.Apply(renderer.Scene, Assert.Single(published)).Scene);

        var settled = OnScreen(renderer, renderer.MapLeft, renderer.MapTop);
        Assert.Equal(shown.X, settled.X, 3);
        Assert.Equal(shown.Y, settled.Y, 3);
    }

    [Fact]
    public void Zoom_stops_at_the_fit_going_out_and_at_the_artworks_own_resolution_going_in()
    {
        var renderer = Renderer(Scene(WideBounds), artwork: new Size(4096, 2389));
        renderer.SetViewportSize(1920, 1080);

        // Out: the whole plan is already on screen, so there is nothing further out to go.
        Assert.Equal(1, renderer.MinimumZoom);
        renderer.RequestZoom(-1);
        renderer.Present(MapSceneViewReducer.Apply(renderer.Scene, renderer.LastRequestedChange!).Scene);
        Assert.Equal(1, renderer.CameraZoom);

        // In: from the artwork, not a constant. 4096 native pixels across a plan drawn about
        // 1876 wide is a shade over two, doubled for headroom.
        Assert.Equal(4096 / renderer.MapWidth * 2, renderer.MaximumZoom, 6);
        for (var step = 0; step < 20 && renderer.CameraZoom < renderer.MaximumZoom; step++)
        {
            renderer.RequestZoom(1);
            var applied = MapSceneViewReducer.Apply(renderer.Scene, renderer.LastRequestedChange!);
            Assert.Equal(MapSceneViewChangeStatus.Applied, applied.Status);
            renderer.Present(applied.Scene);
        }

        Assert.Equal(renderer.MaximumZoom, renderer.CameraZoom, 6);
    }

    [Fact]
    public void A_wider_card_shows_the_same_plan_without_changing_the_zoom()
    {
        // "Fit" is a property of the projection, not of the camera, so widening the card keeps
        // the fit rather than leaving the plan at the size it had on the narrower one.
        var renderer = Renderer(Scene(WideBounds));
        renderer.SetViewportSize(640, 900);
        var narrow = renderer.MapWidth;

        renderer.SetViewportSize(1920, 1080);

        Assert.Equal(1, renderer.CameraZoom);
        Assert.True(renderer.MapWidth > narrow);
        AssertWholePlanVisible(renderer);
    }

    private static void AssertWholePlanVisible(MapSceneRendererViewModel renderer)
    {
        var topLeft = OnScreen(renderer, renderer.MapLeft, renderer.MapTop);
        var bottomRight = OnScreen(renderer, renderer.MapLeft + renderer.MapWidth, renderer.MapTop + renderer.MapHeight);
        Assert.InRange(topLeft.X, 0, renderer.CanvasWidth);
        Assert.InRange(topLeft.Y, 0, renderer.CanvasHeight);
        Assert.InRange(bottomRight.X, 0, renderer.CanvasWidth);
        Assert.InRange(bottomRight.Y, 0, renderer.CanvasHeight);
    }

    /// <summary>One whole drag: down, moved, up, and the camera it commits applied.</summary>
    private static void Drag(MapSceneRendererViewModel renderer, double deltaX, double deltaY)
    {
        MapSceneViewChange? change = null;
        void Capture(MapSceneViewChange published) => change = published;
        renderer.ViewChangeRequested += Capture;
        renderer.BeginPan();
        renderer.UpdatePan(deltaX, deltaY);
        renderer.CommitPan();
        renderer.ViewChangeRequested -= Capture;
        if (change is not null)
        {
            renderer.Present(MapSceneViewReducer.Apply(renderer.Scene, change).Scene);
        }
    }

    private static (double X, double Y) OnScreen(MapSceneRendererViewModel renderer, double planX, double planY)
    {
        var preX = planX + renderer.CameraPreTranslateX;
        var preY = planY + renderer.CameraPreTranslateY;
        var radians = renderer.CameraRotationDegrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        return (
            (((cosine * preX) - (sine * preY)) * renderer.CameraZoom) + renderer.CameraPostTranslateX,
            (((sine * preX) + (cosine * preY)) * renderer.CameraZoom) + renderer.CameraPostTranslateY);
    }

    private static void ZoomTo(MapSceneRendererViewModel renderer, double zoom)
    {
        var scene = renderer.Scene;
        var camera = scene.View.Camera;
        renderer.Present(new(
            scene.Revision + 1,
            scene.LocationId,
            scene.VariantKey,
            scene.TransformVersion,
            scene.Bounds,
            scene.FloorIds,
            scene.Capabilities,
            scene.View with { Camera = new(camera.CenterX, camera.CenterY, zoom, camera.BearingDegrees, camera.PitchDegrees) },
            scene.Layers,
            scene.Objects,
            scene.Assets));
    }

    private static MapSceneRendererViewModel Renderer(MapSceneSnapshot scene, Size? artwork = null) =>
        new(scene, Presentation, null, _ => new TestArtwork(artwork ?? new Size(2048, 1195)), showsDetailsPanel: false);

    private static MapSceneSnapshot Scene(MapSceneBounds bounds, long revision = 1, string locationId = "customs")
    {
        var layer = new MapSceneLayer(new("extracts"), "Extracts", 10, true);
        return new(
            revision,
            locationId,
            $"{locationId}-interactive",
            "transform-1",
            bounds,
            ["ground"],
            new(
                MapSceneCapability.Available,
                MapSceneCapability.Unavailable("No reviewed floor stack."),
                MapSceneCapability.Unavailable("No reviewed interior model.")),
            new(
                MapSceneMode.Flat2D,
                null,
                new(
                    bounds.MinimumX + (bounds.Width / 2),
                    bounds.MinimumY + (bounds.Height / 2),
                    1,
                    0,
                    0),
                [new(layer.Id, true)]),
            [layer],
            [
                new(
                    new($"{locationId}:exit"),
                    layer.Id,
                    MapSceneObjectKind.Extract,
                    MapSceneTruthKind.StaticReference,
                    "Exit",
                    null,
                    MapSceneGeometry.At(new(
                        bounds.MinimumX + (bounds.Width / 4),
                        bounds.MinimumY + (bounds.Height / 4))),
                    [],
                    new("fixture", new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero))),
            ],
            [Asset()]);
    }

    private static MapSceneAsset Asset() => new(
        new("asset:camera-fixture"),
        MapSceneAssetKind.Background2D,
        new("https://example.test/maps/plan.svg"),
        new("https://example.test/licence"),
        new string('c', 64),
        "Example map author",
        "map-1",
        "game-1",
        MapSceneAssetReviewStatus.Reviewed,
        new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero));

    private sealed class TestArtwork(Size size) : IImage
    {
        public Size Size { get; } = size;

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }
    }
}
