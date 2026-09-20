using System.Globalization;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [Issue 551] Fit fills the card with the map, however it is turned and however much empty
/// canvas its artwork carries.
/// </summary>
/// <remarks>
/// "especially on maps i rotated, the default fit-zoom is too zoomed out and the map does not
/// fill up the bounds." His Streets of Tarkov, a tall map turned a quarter to lie along a wide
/// card, filled about half of it each way: fit was zoom one of a projection that had fitted the
/// plan standing up.
/// </remarks>
public sealed class MapFitGeometryTests
{
    private const double CardWidth = 1344;
    private const double CardHeight = 861;
    private const double Margin = 22;

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(37)]
    [InlineData(-143)]
    public void What_is_fitted_touches_the_margin_on_one_axis_and_is_inside_it_on_the_other(double bearing)
    {
        // A tall rectangle, as Streets is, given by its corners.
        (double X, double Y)[] corners = [(400, 22), (944, 22), (944, 839), (400, 839)];

        var fit = MapFitGeometry.For(corners, bearing, CardWidth, CardHeight, Margin)!.Value;

        var (left, top, right, bottom) = OnScreen(corners, fit, bearing);
        Assert.True(left >= Margin - 1e-6 && top >= Margin - 1e-6, $"cropped at {left},{top}");
        Assert.True(right <= CardWidth - Margin + 1e-6 && bottom <= CardHeight - Margin + 1e-6, $"cropped at {right},{bottom}");
        var fillsWidth = Math.Abs(left - Margin) < 1e-6 && Math.Abs(right - (CardWidth - Margin)) < 1e-6;
        var fillsHeight = Math.Abs(top - Margin) < 1e-6 && Math.Abs(bottom - (CardHeight - Margin)) < 1e-6;
        Assert.True(fillsWidth || fillsHeight, $"the fit leaves room on both axes: {left},{top} to {right},{bottom}");
        // And it is centred on the other one.
        Assert.Equal(CardWidth - right, left, 6);
        Assert.Equal(CardHeight - bottom, top, 6);
    }

    [Fact]
    public void A_tall_map_turned_a_quarter_is_fitted_larger_than_it_was_standing_up()
    {
        (double X, double Y)[] corners = [(400, 22), (944, 22), (944, 839), (400, 839)];

        var upright = MapFitGeometry.For(corners, 0, CardWidth, CardHeight, Margin)!.Value;
        var turned = MapFitGeometry.For(corners, 90, CardWidth, CardHeight, Margin)!.Value;

        Assert.Equal(1, upright.Zoom, 6);
        // 817 tall fitted 817 of height; lying down it is 544 tall and 817 wide, so the height
        // allows 817/544 = 1.50 and the width 1300/817 = 1.59.
        Assert.Equal(817d / 544d, turned.Zoom, 6);
    }

    [Fact]
    public void Artwork_with_an_empty_margin_is_fitted_to_what_is_on_it_not_to_its_canvas()
    {
        // The plan's rectangle is 1300 x 817 of card; everything on the map is inside the middle
        // 60% of it, as on a drawing with a wide dark border.
        var content = new List<(double X, double Y)>();
        for (var step = 0; step <= 10; step++)
        {
            content.Add((22 + 260 + (78 * step), 22 + 163));
            content.Add((22 + 260 + (78 * step), 22 + 163 + 490));
        }

        var fit = MapFitGeometry.For(content, 0, CardWidth, CardHeight, Margin)!.Value;

        Assert.True(fit.Zoom > 1.6, $"fitted to the canvas, not the map: {fit.Zoom}");
        var (left, top, right, bottom) = OnScreen(content, fit, 0);
        Assert.True(left >= Margin - 1e-6 && top >= Margin - 1e-6 && right <= CardWidth - Margin + 1e-6 && bottom <= CardHeight - Margin + 1e-6);
    }

    [Fact]
    public void At_an_odd_bearing_points_are_fitted_tighter_than_the_rectangle_around_them()
    {
        // The corners of a rectangle turned 37 degrees reach much further than a map drawn in
        // the middle of it does.
        (double X, double Y)[] corners = [(0, 0), (1000, 0), (1000, 600), (0, 600)];
        (double X, double Y)[] cross = [(500, 0), (1000, 300), (500, 600), (0, 300)];

        var rectangle = MapFitGeometry.For(corners, 37, CardWidth, CardHeight, Margin)!.Value;
        var points = MapFitGeometry.For(cross, 37, CardWidth, CardHeight, Margin)!.Value;

        Assert.True(points.Zoom > rectangle.Zoom * 1.15, $"{points.Zoom} vs {rectangle.Zoom}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(37)]
    public void Fit_on_the_renderer_keeps_every_extract_inside_the_card_and_fills_it(double bearing)
    {
        var renderer = Renderer(bearing);

        renderer.FitPlanCommand.Execute(null);

        var camera = renderer.Scene.View.Camera;
        Assert.Equal(bearing, camera.BearingDegrees, 6);
        var marks = renderer.PointMarkers.Where(marker => marker.SceneObject?.Kind == MapSceneObjectKind.Extract).ToArray();
        Assert.Equal(12, marks.Length);
        double left = double.PositiveInfinity, top = double.PositiveInfinity, right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        foreach (var mark in marks)
        {
            var point = mark.SceneObject!.Geometry.Points[0];
            Assert.True(TryViewport(renderer, point, out var x, out var y));
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
        }

        // Every extract's whole 44px marker is on the card...
        Assert.True(left >= 22 && top >= 22 && right <= CardWidth - 22 && bottom <= CardHeight - 22, $"{left},{top} to {right},{bottom}");
        // ...and between them they span one axis of it up to the margin (22 + 4%, about 56px).
        var spansWidth = right - left > CardWidth - 120;
        var spansHeight = bottom - top > CardHeight - 120;
        Assert.True(spansWidth || spansHeight, $"the map does not fill the card: {left},{top} to {right},{bottom} at zoom {camera.Zoom}");
    }

    [Fact]
    public void A_fitted_map_that_is_turned_is_fitted_again_and_one_the_player_zoomed_is_left_alone()
    {
        var renderer = Renderer(0);
        renderer.FitPlanCommand.Execute(null);
        var upright = renderer.Scene.View.Camera.Zoom;

        renderer.SetBearing(90);
        var turned = renderer.Scene.View.Camera.Zoom;
        Assert.True(turned > upright * 1.2, $"turned {turned}, upright {upright}");

        renderer.RequestZoom(1);
        var zoomed = renderer.Scene.View.Camera;
        renderer.SetBearing(0);
        Assert.Equal(zoomed.Zoom, renderer.Scene.View.Camera.Zoom, 9);
        renderer.SetViewportSize(1000, 700);
        Assert.Equal(zoomed.Zoom, renderer.Scene.View.Camera.Zoom, 9);
    }

    [Fact]
    public void A_wide_map_turned_a_quarter_can_still_be_zoomed_out_to_the_whole_plan()
    {
        var renderer = Renderer(90, width: 800, height: 300);

        Assert.True(renderer.MinimumZoom < 1, $"{renderer.MinimumZoom}");
    }

    private static bool TryViewport(MapSceneRendererViewModel renderer, MapScenePoint point, out double x, out double y)
    {
        // Search the card for the pixel that unprojects to the point: the renderer's own inverse,
        // so this does not restate the camera maths it is checking.
        x = y = double.NaN;
        Assert.True(renderer.TryScenePointAt(renderer.CanvasWidth / 2, renderer.CanvasHeight / 2, out var middle));
        Assert.True(renderer.TryScenePointAt((renderer.CanvasWidth / 2) + 1, renderer.CanvasHeight / 2, out var right));
        Assert.True(renderer.TryScenePointAt(renderer.CanvasWidth / 2, (renderer.CanvasHeight / 2) + 1, out var down));
        // Scene units per pixel along the screen's two axes: a 2x2 linear map, inverted.
        double a = right.X - middle.X, b = down.X - middle.X, c = right.Y - middle.Y, d = down.Y - middle.Y;
        var determinant = (a * d) - (b * c);
        if (Math.Abs(determinant) < 1e-12)
        {
            return false;
        }

        var dx = point.X - middle.X;
        var dy = point.Y - middle.Y;
        x = (renderer.CanvasWidth / 2) + (((d * dx) - (b * dy)) / determinant);
        y = (renderer.CanvasHeight / 2) + (((-c * dx) + (a * dy)) / determinant);
        return true;
    }

    private static MapSceneRendererViewModel Renderer(double bearing, double width = 400, double height = 600)
    {
        var layer = new MapSceneLayer(new("extracts"), "Extracts", 10, true);
        // A plan with a wide empty border: every extract is inside the middle 60% of it.
        var objects = new List<MapSceneObject>();
        for (var index = 0; index < 12; index++)
        {
            var angle = index * Math.PI / 6;
            objects.Add(new(
                new($"extract:{index}"),
                layer.Id,
                MapSceneObjectKind.Extract,
                MapSceneTruthKind.StaticReference,
                $"Extract {index}",
                null,
                MapSceneGeometry.At(new((width / 2) + (Math.Cos(angle) * width * 0.3), (height / 2) + (Math.Sin(angle) * height * 0.3))),
                [],
                new DataProvenance("fixture", new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero), Confidence: new Confidence(1))));
        }

        var scene = new MapSceneSnapshot(
            1,
            "streets-of-tarkov",
            "streets",
            "streets",
            new MapSceneBounds(0, 0, width, height),
            [],
            new(MapSceneCapability.Available, MapSceneCapability.Unavailable("no stack"), MapSceneCapability.Unavailable("no interior")),
            new(MapSceneMode.Flat2D, null, new(width / 2, height / 2, 1, bearing, 0), [new(layer.Id, true)]),
            [layer],
            objects,
            []);
        var renderer = new MapSceneRendererViewModel(
            scene,
            MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc),
            showsDetailsPanel: false);
        renderer.ViewChangeRequested += change =>
        {
            var result = MapSceneViewReducer.Apply(renderer.Scene, change);
            if (result.Status is MapSceneViewChangeStatus.Applied or MapSceneViewChangeStatus.Unchanged)
            {
                renderer.Present(result.Scene);
            }
        };
        renderer.SetViewportSize(CardWidth, CardHeight);
        return renderer;
    }

    /// <summary>Where points land on the card under a fit: turn, scale about the fit's centre, centre on the card.</summary>
    private static (double Left, double Top, double Right, double Bottom) OnScreen(
        IEnumerable<(double X, double Y)> points,
        MapFitGeometry.Fit fit,
        double bearing)
    {
        var radians = bearing * Math.PI / 180;
        double left = double.PositiveInfinity, top = double.PositiveInfinity, right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        foreach (var (x, y) in points)
        {
            var dx = x - fit.CentreX;
            var dy = y - fit.CentreY;
            var screenX = (CardWidth / 2) + (fit.Zoom * ((Math.Cos(radians) * dx) + (Math.Sin(radians) * dy)));
            var screenY = (CardHeight / 2) + (fit.Zoom * ((-Math.Sin(radians) * dx) + (Math.Cos(radians) * dy)));
            left = Math.Min(left, screenX);
            right = Math.Max(right, screenX);
            top = Math.Min(top, screenY);
            bottom = Math.Max(bottom, screenY);
        }

        return (left, top, right, bottom);
    }
}
