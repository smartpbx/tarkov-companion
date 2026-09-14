using Avalonia;
using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Whether Fit shows the exits as well as the picture.
/// </summary>
/// <remarks>
/// Seen the first time the gallery photographed Streets: the view marked Fit, reading 63%, and
/// still running off the panel at the bottom with "Transit to Interchange" and "Courtyard" cut
/// in half. One of those is a way out of the raid.
///
/// Fit frames the drawn artwork, which guarantees you can see the picture and not that you can
/// see the markers on it, because a marker is placed in upstream coordinates and those reach
/// past the drawing.
///
/// The allowance is measured rather than chosen. Across every map in the live feed the genuine
/// overhangs are Customs 3.2%, Interchange 1.5%, Factory 1.2%, Woods 0.4% and eight maps with
/// none — and Ground Zero has three exits at 123%, more than the whole map away. A tenth sits
/// in that gap: three times the largest real one, a twelfth of the smallest bad one.
/// </remarks>
public sealed class MapFitMarkerTests
{
    private static readonly Rect Content = new(0, 0, 1000, 1000);

    [Fact]
    public void A_marker_just_outside_the_artwork_is_taken_in()
    {
        // Customs' worst, at 3.2% — the case the report was actually about.
        var fitted = MapFitBounds.WithMarkers(Content, [new Point(1032, 500)]);

        Assert.Equal(1032, fitted.Right, 3);
    }

    [Fact]
    public void A_marker_half_a_map_away_is_left_outside()
    {
        // Ground Zero's, at 123%. Chasing it would fit the map to under half scale to make
        // room for blank ground, which is the regression ContentBounds exists to prevent.
        var fitted = MapFitBounds.WithMarkers(Content, [new Point(2230, 500)]);

        Assert.Equal(1000, fitted.Right, 3);
    }

    [Fact]
    public void The_allowance_sits_between_the_two()
    {
        Assert.True(MapFitBounds.MarkerAllowance > 0.032, "every real overhang has to fit inside it");
        Assert.True(MapFitBounds.MarkerAllowance < 1.23, "the pathological one has to fall outside it");
    }

    [Fact]
    public void A_marker_inside_the_artwork_changes_nothing()
    {
        Assert.Equal(Content, MapFitBounds.WithMarkers(Content, [new Point(500, 500)]));
    }

    [Fact]
    public void Each_axis_is_judged_on_its_own()
    {
        // Level with the map and far off to the side: the sideways pull is real and the
        // vertical one is not, and refusing the whole marker would lose the common case.
        var fitted = MapFitBounds.WithMarkers(Content, [new Point(1050, 5000)]);

        Assert.Equal(1050, fitted.Right, 3);
        Assert.Equal(1000, fitted.Bottom, 3);
    }

    [Fact]
    public void A_marker_before_the_left_edge_pulls_that_way_too()
    {
        var fitted = MapFitBounds.WithMarkers(Content, [new Point(-40, 500)]);

        Assert.Equal(-40, fitted.X, 3);
        Assert.Equal(1040, fitted.Width, 3);
    }

    [Fact]
    public void Nothing_drawn_is_nothing_to_stretch()
    {
        Assert.Equal(default, MapFitBounds.WithMarkers(default, [new Point(10, 10)]));
    }

    [Fact]
    public void No_markers_leaves_the_artwork_alone()
    {
        Assert.Equal(Content, MapFitBounds.WithMarkers(Content, []));
    }
}
