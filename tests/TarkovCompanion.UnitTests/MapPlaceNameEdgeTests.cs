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
        Assert.Equal(0, name.TextLeft, 3);
    }

    [Fact]
    public void A_name_past_the_right_edge_is_moved_back_too()
    {
        var name = Name("Railroad to Military Base", centerX: 1995, canvasWidth: 2000);

        Assert.True(name.Nudge < 0);
        Assert.Equal(2000, name.TextLeft + name.TextWidth, 3);
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
        var overhang = (name.TextWidth / 2) - 5;

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

    private static MapPlaceNameViewModel Name(string text, double centerX, double canvasWidth) =>
        new(text, centerX, 500, 0, 14, false, false) { CanvasWidth = canvasWidth };
}
