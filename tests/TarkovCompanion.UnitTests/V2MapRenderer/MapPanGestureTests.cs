using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.Views.V2.MapRenderer;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [Issue 551] Zoom with the wheel, drag with the mouse, and the map stays where it was left.
/// </summary>
/// <remarks>
/// "i also still have the issue if i zoom in on a map, and then try to pan it, it snaps back to
/// where i zoomed." MapPanHoldsTests drives the view model and has always passed. The fault was
/// in the view: releasing the pointer capture raised PointerCaptureLost, whose handler cancelled
/// the drag before the release handler committed it. So this drives the real view with a real
/// pointer, and lets a scene rebuild land both during the drag and after it.
/// </remarks>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class MapPanGestureTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(37)]
    public void A_drag_after_a_wheel_zoom_moves_the_map_by_the_drag_and_it_stays_there(double bearing)
    {
        Run(bearing, (window, view, renderer) =>
        {
            var plan = view.FindControl<Border>("PlanViewport")!;
            Point InWindow(double x, double y) => plan.TranslatePoint(new Point(x, y), window)!.Value;

            // Wheel in over a point well off the middle: that ground stays under the cursor.
            var wheelAt = new Point((plan.Bounds.Width / 2) + 90, (plan.Bounds.Height / 2) - 60);
            // (Measured over the last notch. On the first ones the whole plan is still narrower
            // than the card on one axis, and the clamp keeps it centred there by design.)
            var fitted = renderer.Scene.View.Camera.Zoom;
            var underWheel = default(MapScenePoint);
            for (var notch = 0; notch < 6; notch++)
            {
                Assert.True(renderer.TryScenePointAt(wheelAt.X, wheelAt.Y, out underWheel));
                window.MouseWheel(InWindow(wheelAt.X, wheelAt.Y), new Vector(0, 1));
                Dispatcher.UIThread.RunJobs();
            }

            Assert.True(renderer.Scene.View.Camera.Zoom > fitted * 3, $"the wheel did not zoom: {renderer.Scene.View.Camera.Zoom}");
            Assert.True(renderer.TryScenePointAt(wheelAt.X, wheelAt.Y, out var stillUnderWheel));
            Assert.Equal(underWheel.X, stillUnderWheel.X, 3);
            Assert.Equal(underWheel.Y, stillUnderWheel.Y, 3);

            // Grab the ground under the pointer and drag it 120 left, 80 up.
            var zoomed = renderer.Scene.View.Camera;
            var from = new Point(plan.Bounds.Width / 2, plan.Bounds.Height / 2);
            var to = new Point(from.X - 120, from.Y - 80);
            Assert.True(renderer.TryScenePointAt(from.X, from.Y, out var grabbed));
            window.MouseDown(InWindow(from.X, from.Y), MouseButton.Left);
            window.MouseMove(InWindow(from.X - 60, from.Y - 40));
            // A rebuild lands mid-drag (the raid clock ticks once a second): same view, again.
            renderer.Present(renderer.Scene);
            window.MouseMove(InWindow(to.X, to.Y));
            window.MouseUp(InWindow(to.X, to.Y), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            var panned = renderer.Scene.View.Camera;
            Assert.False(renderer.IsPanning);
            Assert.Equal(zoomed.Zoom, panned.Zoom, 6);
            Assert.True(
                Math.Abs(panned.CenterX - zoomed.CenterX) + Math.Abs(panned.CenterY - zoomed.CenterY) > 1,
                $"the camera went back to where it was zoomed: {panned}");
            // The ground that was grabbed is under the pointer where it was let go.
            Assert.True(renderer.TryScenePointAt(to.X, to.Y, out var dropped));
            Assert.Equal(grabbed.X, dropped.X, 3);
            Assert.Equal(grabbed.Y, dropped.Y, 3);
            // Nothing of the drag's own offset is left over on top of the committed camera.
            Assert.Equal(renderer.CanvasWidth / 2, renderer.CameraPostTranslateX, 3);

            // And the next rebuild leaves it there.
            renderer.Present(renderer.Scene);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(panned, renderer.Scene.View.Camera);
        });
    }

    [Fact]
    public void A_capture_lost_while_the_button_is_still_down_puts_the_plan_back()
    {
        // What the capture-lost handler is for, kept: another control takes the pointer mid-drag.
        Run(0, (window, view, renderer) =>
        {
            var plan = view.FindControl<Border>("PlanViewport")!;
            Point InWindow(double x, double y) => plan.TranslatePoint(new Point(x, y), window)!.Value;
            for (var step = 0; step < 6; step++)
            {
                renderer.RequestZoom(1);
            }

            var before = renderer.Scene.View.Camera;
            window.MouseDown(InWindow(400, 300), MouseButton.Left);
            window.MouseMove(InWindow(300, 240));
            Assert.True(renderer.IsPanning);
            // Capturing something else takes it from the plan.
            var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);
            plan.RaiseEvent(new PointerCaptureLostEventArgs(plan, pointer));
            Assert.False(renderer.IsPanning);
            Assert.Equal(before, renderer.Scene.View.Camera);
        });
    }

    /// <summary>
    /// [#286] Draw mode: a left-drag draws a line where the pointer went and leaves the camera
    /// alone; a middle-drag, or Space with a left-drag, still pans; Escape asks to leave.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void In_draw_mode_a_left_drag_draws_and_the_pan_gestures_still_pan(double bearing)
    {
        Run(bearing, (window, view, renderer) =>
        {
            var plan = view.FindControl<Border>("PlanViewport")!;
            Point InWindow(double x, double y) => plan.TranslatePoint(new Point(x, y), window)!.Value;
            for (var step = 0; step < 4; step++)
            {
                renderer.RequestZoom(1);
            }

            var strokes = new List<IReadOnlyList<MapScenePoint>>();
            var escaped = 0;
            view.StrokeDrawn += (_, points) => strokes.Add(points);
            view.ModeEscaped += (_, _) => escaped++;
            view.IsDrawing = true;

            var camera = renderer.Scene.View.Camera;
            var from = new Point(300, 300);
            var to = new Point(520, 380);
            Assert.True(renderer.TryScenePointAt(from.X, from.Y, out var start));
            Assert.True(renderer.TryScenePointAt(to.X, to.Y, out var end));
            window.MouseDown(InWindow(from.X, from.Y), MouseButton.Left);
            window.MouseMove(InWindow(400, 250));
            window.MouseMove(InWindow(to.X, to.Y));
            window.MouseUp(InWindow(to.X, to.Y), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            var stroke = Assert.Single(strokes);
            Assert.Equal(start.X, stroke[0].X, 3);
            Assert.Equal(start.Y, stroke[0].Y, 3);
            Assert.Equal(end.X, stroke[^1].X, 3);
            Assert.Equal(end.Y, stroke[^1].Y, 3);
            Assert.Equal(camera, renderer.Scene.View.Camera);

            // Middle-drag pans, and draws nothing.
            window.MouseDown(InWindow(400, 300), MouseButton.Middle);
            window.MouseMove(InWindow(340, 260));
            window.MouseUp(InWindow(300, 240), MouseButton.Middle);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(strokes);
            var afterMiddle = renderer.Scene.View.Camera;
            Assert.NotEqual(camera, afterMiddle);

            // Space held with a left-drag pans too.
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.MouseDown(InWindow(400, 300), MouseButton.Left);
            window.MouseMove(InWindow(460, 330));
            window.MouseUp(InWindow(480, 340), MouseButton.Left);
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Dispatcher.UIThread.RunJobs();
            Assert.Single(strokes);
            Assert.NotEqual(afterMiddle, renderer.Scene.View.Camera);

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.Equal(1, escaped);

            // Off again (the default), a left-drag pans and draws nothing.
            view.IsDrawing = false;
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.Equal(1, escaped);
            var beforeNavigate = renderer.Scene.View.Camera;
            window.MouseDown(InWindow(400, 300), MouseButton.Left);
            window.MouseMove(InWindow(340, 260));
            window.MouseUp(InWindow(300, 240), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(strokes);
            Assert.NotEqual(beforeNavigate, renderer.Scene.View.Camera);
        });
    }

    /// <summary>
    /// #938: the camera moves mid-stroke (a wheel zoom, Follow, a tablet pan). Each point is where
    /// it was drawn on the map then; converting the whole stroke with the camera it ended on moved
    /// its start to wherever that pixel pointed after the zoom.
    /// </summary>
    [Fact]
    public void A_stroke_keeps_where_it_was_drawn_when_the_camera_moves_mid_stroke()
    {
        Run(0, (window, view, renderer) =>
        {
            var plan = view.FindControl<Border>("PlanViewport")!;
            Point InWindow(double x, double y) => plan.TranslatePoint(new Point(x, y), window)!.Value;
            for (var step = 0; step < 3; step++)
            {
                renderer.RequestZoom(1);
            }

            var strokes = new List<IReadOnlyList<MapScenePoint>>();
            view.StrokeDrawn += (_, points) => strokes.Add(points);
            view.IsDrawing = true;

            var from = new Point(300, 300);
            var middle = new Point(420, 330);
            var to = new Point(520, 380);
            Assert.True(renderer.TryScenePointAt(from.X, from.Y, out var start));
            Assert.True(renderer.TryScenePointAt(middle.X, middle.Y, out var passed));
            window.MouseDown(InWindow(from.X, from.Y), MouseButton.Left);
            window.MouseMove(InWindow(middle.X, middle.Y));
            var before = renderer.Scene.View.Camera;
            renderer.RequestZoom(1);
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(before, renderer.Scene.View.Camera);
            Assert.True(renderer.TryScenePointAt(to.X, to.Y, out var end));
            window.MouseMove(InWindow(to.X, to.Y));
            window.MouseUp(InWindow(to.X, to.Y), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            var stroke = Assert.Single(strokes);
            Assert.Equal(start.X, stroke[0].X, 3);
            Assert.Equal(start.Y, stroke[0].Y, 3);
            Assert.Equal(passed.X, stroke[1].X, 3);
            Assert.Equal(passed.Y, stroke[1].Y, 3);
            Assert.Equal(end.X, stroke[^1].X, 3);
            Assert.Equal(end.Y, stroke[^1].Y, 3);
        });
    }

    /// <summary>
    /// [#286] Inspect and Route modes: a plain click is handed to the host at the scene point under
    /// it and selects nothing; a left-drag still pans and is not a click; Escape asks to leave.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void In_a_click_mode_a_click_is_the_modes_and_a_drag_still_pans(double bearing)
    {
        Run(bearing, (window, view, renderer) =>
        {
            var plan = view.FindControl<Border>("PlanViewport")!;
            Point InWindow(double x, double y) => plan.TranslatePoint(new Point(x, y), window)!.Value;
            for (var step = 0; step < 4; step++)
            {
                renderer.RequestZoom(1);
            }

            var clicks = new List<MapScenePoint>();
            var escaped = 0;
            view.ModeClicked += (_, point) => clicks.Add(point);
            view.ModeEscaped += (_, _) => escaped++;
            view.IsClickMode = true;

            var camera = renderer.Scene.View.Camera;
            Assert.True(renderer.TryScenePointAt(420, 310, out var expected));
            window.MouseDown(InWindow(420, 310), MouseButton.Left);
            window.MouseUp(InWindow(420, 310), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var click = Assert.Single(clicks);
            Assert.Equal(expected.X, click.X, 3);
            Assert.Equal(expected.Y, click.Y, 3);
            Assert.Equal(camera, renderer.Scene.View.Camera);
            Assert.False(renderer.HasSelection);

            // The popover follows the spot: the scene point projects back to where it was clicked.
            Assert.True(renderer.TryViewportPointAt(click, out var backX, out var backY));
            Assert.Equal(420, backX, 3);
            Assert.Equal(310, backY, 3);

            // A drag pans and is not a click.
            window.MouseDown(InWindow(400, 300), MouseButton.Left);
            window.MouseMove(InWindow(340, 260));
            window.MouseUp(InWindow(300, 240), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(clicks);
            Assert.NotEqual(camera, renderer.Scene.View.Camera);

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.Equal(1, escaped);

            view.IsClickMode = false;
            window.MouseDown(InWindow(420, 310), MouseButton.Left);
            window.MouseUp(InWindow(420, 310), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(clicks);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.Equal(1, escaped);
        });
    }

    private static void Run(double bearing, Action<Window, MapSceneRendererView, MapSceneRendererViewModel> body)
    {
        using var session = HeadlessSessions.StartNew(typeof(MapMarkClipTests.MarkClipApp));
        session.Dispatch(
            () =>
            {
                var renderer = Renderer(bearing);
                var view = new MapSceneRendererView { DataContext = renderer };
                var window = new Window { Width = 1400, Height = 900, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                try
                {
                    body(window, view, renderer);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None).GetAwaiter().GetResult();
    }

    private static MapSceneRendererViewModel Renderer(double bearing)
    {
        var scene = new MapSceneSnapshot(
            1,
            "customs",
            "customs",
            "customs",
            new MapSceneBounds(0, 0, 400, 300),
            [],
            new(MapSceneCapability.Available, MapSceneCapability.Unavailable("no stack"), MapSceneCapability.Unavailable("no interior")),
            new(MapSceneMode.Flat2D, null, new(200, 150, 1, bearing, 0), []),
            [],
            [],
            []);
        var renderer = new MapSceneRendererViewModel(
            scene,
            MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc),
            showsDetailsPanel: false);
        // The host half of the loop, as the cockpit wires it.
        renderer.ViewChangeRequested += change =>
        {
            var result = MapSceneViewReducer.Apply(renderer.Scene, change);
            if (result.Status is MapSceneViewChangeStatus.Applied or MapSceneViewChangeStatus.Unchanged)
            {
                renderer.Present(result.Scene);
            }
        };
        return renderer;
    }
}
