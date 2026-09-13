using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Keeping a place name on the map when the catalog puts it at the edge of one.
/// </summary>
/// <remarks>
/// Reported as two labels drawn on top of each other, and that is what it looked like: on
/// Customs, "ministration Gate" beside "ry Checkpoint", both starting exactly at the map's left
/// edge. They were never overlapping. They are "Administration Gate" and "Factory Checkpoint",
/// each with its first characters cut off by the canvas, landing on the same row and reading as
/// one illegible run.
/// </remarks>
public sealed class MapPlaceNameEdgeTests
{
    [Fact]
    public void A_name_past_the_left_edge_is_moved_back_onto_the_map()
    {
        var name = Name("Administration Gate", centerX: 10, canvasWidth: 2000);

        Assert.True(name.Nudge > 0);
        Assert.Equal(0, name.CenterX + name.Nudge - (name.TextWidthOnCanvas / 2), 3);
    }

    [Fact]
    public void A_name_past_the_right_edge_is_moved_back_too()
    {
        var name = Name("Railroad to Military Base", centerX: 1995, canvasWidth: 2000);

        Assert.True(name.Nudge < 0);
        Assert.Equal(2000, name.CenterX + name.Nudge + (name.TextWidthOnCanvas / 2), 3);
    }

    [Fact]
    public void A_name_in_the_middle_of_the_map_is_left_where_the_catalog_put_it()
    {
        // Nothing moves unless it would otherwise be cut. A label nudged for no reason is a
        // label pointing slightly at the wrong thing.
        var name = Name("Dorms", centerX: 1000, canvasWidth: 2000);

        Assert.Equal(0, name.Nudge);
        Assert.Equal(1000, name.TextLeft + (name.TextWidth / 2), 3);
    }

    [Fact]
    public void It_moves_only_as_far_as_it_takes()
    {
        // A name a few pixels inboard still names the thing it is beside; half a name names
        // nothing. Moving it further than necessary trades one problem for another.
        var name = Name("Scav Checkpoint", centerX: 5, canvasWidth: 2000);
        var overhang = (name.TextWidthOnCanvas / 2) - 5;

        Assert.Equal(overhang, name.Nudge, 3);
    }

    [Fact]
    public void An_unknown_canvas_never_pushes_a_name_off_the_left()
    {
        // Before a map has been measured the width is zero, and treating that as "the map is
        // zero wide" would drag every name to the same place.
        var name = Name("Dorms", centerX: 1000, canvasWidth: 0);

        Assert.Equal(0, name.Nudge);
    }

    [Fact]
    public void It_moves_further_the_further_the_map_is_pulled_back()
    {
        // The mistake this file has made three times: the text's size is in screen pixels and
        // never changes, the position it is compared against is in canvas units, and at 23%
        // zoom those are more than four times apart. A nudge computed in screen pixels is a
        // quarter of the one needed, which is why the label was still cut off.
        var close = Name("Administration Gate", centerX: 10, canvasWidth: 4000, zoom: 1);
        var far = Name("Administration Gate", centerX: 10, canvasWidth: 4000, zoom: 0.23);

        Assert.True(far.Nudge > close.Nudge * 3, $"{far.Nudge} against {close.Nudge}");
        Assert.Equal(0, far.CenterX + far.Nudge - (far.TextWidthOnCanvas / 2), 3);
    }

    [Fact]
    public void The_same_name_at_two_zooms_is_two_different_records()
    {
        // This is the fix, not a detail. A record compares by value, and an items control given
        // equal items reuses its containers and never re-reads anything computed. Holding the
        // zoom on the record is what makes the two differ, so the containers rebuild and the
        // nudge is recomputed. Measured before this: the label strip was pixel-identical across
        // a build that changed the arithmetic, because the arithmetic never ran again.
        var close = Name("Administration Gate", centerX: 10, canvasWidth: 4000, zoom: 1);
        var far = Name("Administration Gate", centerX: 10, canvasWidth: 4000, zoom: 0.23);

        Assert.NotEqual(close, far);
    }

    private static MapPlaceNameViewModel Name(
        string text,
        double centerX,
        double canvasWidth,
        double zoom = 1) =>
        new(text, centerX, 500, 0, 14, false, false)
        {
            CanvasWidth = canvasWidth,
            Zoom = zoom,
        };
}
