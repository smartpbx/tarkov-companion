using Avalonia;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Turning a map a quarter turn so it fits the screen it is read on.
/// </summary>
/// <remarks>
/// Reported as Shoreline being very vertical on a 1920×1080 screen. A map whose long axis runs
/// across the screen's short one is scaled down to fit its height and then leaves half the panel
/// empty either side of it.
///
/// The surface's transform origin is its top-left corner, so a bare rotation swings three
/// quarters of the map into negative coordinates where a scroll viewer cannot reach it. Every
/// case here is really the same question: does the map come back to the corner it started from.
/// </remarks>
public sealed class MapSurfaceTurnTests
{
    private const double Width = 400;
    private const double Height = 1000;

    [Fact]
    public void No_turn_leaves_every_point_where_it_was()
    {
        Assert.Equal(Matrix.Identity, MapSurfaceTurn.For(0, Width, Height));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void A_turned_map_still_starts_at_the_corner(int degrees)
    {
        // The whole of what the translation is for. Without it a quarter turn puts the map
        // somewhere a scroll viewer cannot follow, and it simply disappears.
        var corners = Corners(degrees);

        Assert.Equal(0, corners.Min(corner => corner.X), 6);
        Assert.Equal(0, corners.Min(corner => corner.Y), 6);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void A_quarter_turn_swaps_the_two_sides(int degrees)
    {
        var corners = Corners(degrees);

        Assert.Equal(Height, corners.Max(corner => corner.X), 6);
        Assert.Equal(Width, corners.Max(corner => corner.Y), 6);
    }

    [Fact]
    public void A_half_turn_keeps_the_shape_it_had()
    {
        var corners = Corners(180);

        Assert.Equal(Width, corners.Max(corner => corner.X), 6);
        Assert.Equal(Height, corners.Max(corner => corner.Y), 6);
    }

    [Fact]
    public void A_quarter_turn_takes_the_top_edge_down_the_right_hand_side()
    {
        // Which way round it goes, stated once. Clockwise: the point at the top-left ends up at
        // the top-right, the way turning a sheet of paper clockwise moves its top edge.
        var turned = MapSurfaceTurn.For(90, Width, Height).Transform(new Point(0, 0));

        Assert.Equal(new Point(Height, 0), turned);
    }

    [Fact]
    public void The_other_quarter_turn_goes_the_other_way()
    {
        Assert.Equal(new Point(0, Width), MapSurfaceTurn.For(270, Width, Height).Transform(new Point(0, 0)));
    }

    [Fact]
    public void A_turn_that_is_not_a_quarter_is_not_applied_at_all()
    {
        // Rather than approximated. A map at seventeen degrees is not something anybody asked
        // for, and the preference lives in a file somebody can open and edit.
        Assert.Equal(Matrix.Identity, MapSurfaceTurn.For(17, Width, Height));
        Assert.Equal(Matrix.Identity, MapSurfaceTurn.For(-90, Width, Height));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(360, 0)]
    [InlineData(450, 90)]
    [InlineData(-90, 270)]
    [InlineData(-450, 270)]
    [InlineData(45, 90)]
    [InlineData(89, 90)]
    public void Anything_stored_is_read_back_as_a_quarter_turn(int stored, int expected)
    {
        // Applied on the way in as well as on the way out, because the file is one a player can
        // open. A hand-typed -90 should not leave the map at an angle no button can undo.
        Assert.Equal(expected, MapVariantSelectionService.Normalize(stored));
    }

    private static Point[] Corners(int degrees)
    {
        var turn = MapSurfaceTurn.For(degrees, Width, Height);
        return
        [
            turn.Transform(new Point(0, 0)),
            turn.Transform(new Point(Width, 0)),
            turn.Transform(new Point(0, Height)),
            turn.Transform(new Point(Width, Height)),
        ];
    }
}
