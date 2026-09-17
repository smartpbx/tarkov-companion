using Avalonia;
using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Whether a fitted map runs an extract label under the floating chrome.
/// </summary>
/// <remarks>
/// Reported on Streets, screenshotted with every piece of chrome open: labels at the top, the
/// left edge and the bottom-right sat under the two toolbars, the Layers expander and the
/// status expander. Fit had no idea any of the four existed and scaled into the whole viewport
/// regardless.
/// </remarks>
public sealed class MapOverlayFitTests
{
    private static readonly Size Viewport = new(1000, 800);

    [Fact]
    public void No_overlays_leaves_the_whole_viewport_minus_the_margin()
    {
        var free = MapOverlayFit.ComputeFreeRect(Viewport, [], margin: 16);

        Assert.Equal(new Rect(16, 16, 968, 768), free);
    }

    [Fact]
    public void A_top_left_overlay_is_excluded_on_both_axes_it_sits_near()
    {
        // The location/view/floor toolbar plus the Layers key beneath it, sitting in the
        // corner: it should carve out its own rectangle from both the top and the left.
        var topLeft = new Rect(12, 12, 260, 90);

        var free = MapOverlayFit.ComputeFreeRect(Viewport, [topLeft], margin: 16);

        Assert.Equal(topLeft.Right + 16, free.Left);
        Assert.Equal(topLeft.Bottom + 16, free.Top);
        Assert.Equal(Viewport.Width - 16, free.Right);
        Assert.Equal(Viewport.Height - 16, free.Bottom);
    }

    [Fact]
    public void Corner_overlays_on_every_side_leave_a_strip_down_the_middle()
    {
        var topLeft = new Rect(12, 12, 300, 80);
        var topRight = new Rect(700, 12, 280, 40);
        var bottomLeft = new Rect(12, 700, 200, 90);

        var free = MapOverlayFit.ComputeFreeRect(Viewport, [topLeft, topRight, bottomLeft], margin: 16);

        Assert.Equal(topLeft.Right + 16, free.Left);
        Assert.Equal(topLeft.Bottom + 16, free.Top);
        Assert.Equal(topRight.Left - 16, free.Right);
        Assert.Equal(bottomLeft.Top - 16, free.Bottom);
    }

    [Fact]
    public void An_overlay_with_no_area_reserves_nothing()
    {
        // What a faded-out (idle-hidden) or collapsed overlay reports if it is measured at all.
        var free = MapOverlayFit.ComputeFreeRect(Viewport, [new Rect(12, 12, 0, 0)], margin: 16);

        Assert.Equal(new Rect(16, 16, 968, 768), free);
    }

    [Fact]
    public void Overlays_that_would_cover_everything_fall_back_to_the_whole_viewport()
    {
        var everything = new Rect(0, 0, 1000, 800);

        var free = MapOverlayFit.ComputeFreeRect(Viewport, [everything], margin: 16);

        Assert.True(free.Width > 0);
        Assert.True(free.Height > 0);
    }
}
