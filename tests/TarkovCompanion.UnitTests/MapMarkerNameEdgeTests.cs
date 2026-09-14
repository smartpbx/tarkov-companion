using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Keeping a marker's name on the map when its disc is near the edge of one.
/// </summary>
/// <remarks>
/// Reported as "ministration Gate" beside "ry Checkpoint". Those are the extracts
/// <b>Administration Gate</b> and <b>Military Checkpoint</b> — both confirmed as extracts in
/// tarkov.dev's own data, at the western end of Customs — each with its first characters cut
/// off by the canvas. The name tag is centred on its disc, and a disc near the edge puts half
/// the tag outside, where the canvas clips it.
///
/// Four earlier attempts fixed place names instead. These are marker names: they are drawn on a
/// dark tag, which a place name never is.
/// </remarks>
public sealed class MapMarkerNameEdgeTests
{
    [Fact]
    public void A_name_hanging_off_the_west_edge_is_pulled_back_onto_the_map()
    {
        // A disc 24 canvas units in at 23% zoom is five screen pixels from the edge, with a
        // name a hundred pixels wide. It loses its first word without this.
        var shift = Shift(nameWidth: 100, centerX: 24, canvasWidth: 8000, zoom: 0.23);

        Assert.True(shift > 0);
        Assert.Equal(50, (24 * 0.23) + shift, 3);
    }

    [Fact]
    public void A_name_hanging_off_the_east_edge_is_pulled_back_too()
    {
        var shift = Shift(nameWidth: 100, centerX: 7990, canvasWidth: 8000, zoom: 0.23);

        Assert.True(shift < 0);
        Assert.Equal(8000 * 0.23, (7990 * 0.23) + shift + 50, 3);
    }

    [Fact]
    public void A_name_in_the_middle_of_the_map_is_left_under_its_own_disc()
    {
        // Nothing moves unless it would otherwise be cut. A name nudged for no reason points
        // slightly at the wrong disc.
        Assert.Equal(0, Shift(nameWidth: 100, centerX: 4000, canvasWidth: 8000, zoom: 0.23));
    }

    [Fact]
    public void It_moves_only_as_far_as_it_takes()
    {
        var shift = Shift(nameWidth: 100, centerX: 0, canvasWidth: 8000, zoom: 0.23);

        Assert.Equal(50, shift, 3);
    }

    [Fact]
    public void It_moves_further_the_further_the_map_is_pulled_back()
    {
        // The name holds its size on screen while the map shrinks under it, so the same disc is
        // closer to the edge in screen terms at a lower zoom and needs a bigger shift. Getting
        // this backwards is what made the last attempt a no-op.
        var close = Shift(nameWidth: 100, centerX: 100, canvasWidth: 8000, zoom: 1);
        var far = Shift(nameWidth: 100, centerX: 100, canvasWidth: 8000, zoom: 0.23);

        Assert.Equal(0, close);
        Assert.True(far > 0, $"at 23% the disc is {100 * 0.23} pixels in and the name is 100 wide");
    }

    [Fact]
    public void A_name_too_wide_for_the_whole_map_is_left_alone()
    {
        // There is nowhere for it to go, and shifting it would only choose which end to lose.
        Assert.Equal(0, Shift(nameWidth: 400, centerX: 10, canvasWidth: 800, zoom: 0.23));
    }

    [Fact]
    public void An_unmeasured_canvas_moves_nothing()
    {
        Assert.Equal(0, Shift(nameWidth: 100, centerX: 10, canvasWidth: 0, zoom: 1));
        Assert.Equal(0, Shift(nameWidth: 100, centerX: 10, canvasWidth: double.NaN, zoom: 1));
    }

    [Fact]
    public void A_shifted_name_collides_where_it_lands_not_where_it_started()
    {
        // Measured after the first attempt: Administration Gate was pulled back onto the map
        // and went straight through Military Checkpoint, which lost two words instead of one.
        // The layout has to arrange the shifted position, so the shift is decided first and the
        // candidate carries it.
        // At 23% a disc 24 canvas units in sits 5.5 screen pixels from the edge, so a hundred
        // pixel name is pulled 44.5 right and ends up spanning 0 to 100. A neighbour at 500
        // spans 65 to 165: clear of where the name started, straight through where it lands.
        const double zoom = 0.23;
        var shift = Shift(nameWidth: 100, centerX: 24, canvasWidth: 8000, zoom: zoom);
        var shiftedCentre = 24 + (shift / zoom);

        var unaware = MapLabelLayout.Arrange(
            [new(24, 100, 100, 17, 1), new(500, 100, 100, 17, 1)],
            zoom);
        var aware = MapLabelLayout.Arrange(
            [new(shiftedCentre, 100, 100, 17, 1), new(500, 100, 100, 17, 1)],
            zoom);

        // Unshifted the two do not overlap and both take the first slot; shifted they do, so
        // the second has to move or go.
        Assert.Equal(0, unaware[0]);
        Assert.Equal(0, unaware[1]);
        Assert.NotEqual(0, aware[1]);
    }

    /// <summary>
    /// The whole canvas as the edges, which is what this rule used to take.
    /// </summary>
    /// <remarks>
    /// It now takes a left and a right, because the edge that matters is the drawn content
    /// rather than the canvas: upstream bounds reach past the picture, so a name inside the
    /// canvas can still be off the panel. These cases are unchanged by that — a canvas with
    /// nothing measured on it still passes 0 and the canvas width.
    /// </remarks>
    private static double Shift(double nameWidth, double centerX, double canvasWidth, double zoom) =>
        MapViewModel.HorizontalShift(nameWidth, centerX, 0, canvasWidth, zoom);
}
