using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Which edge a name has to stay inside to stay readable.
/// </summary>
/// <remarks>
/// Both the marker names and the catalog's place names were kept inside the canvas, and the
/// canvas is not what a player can see. It is the upstream bounds, which routinely reach past
/// the drawn map — Customs publishes twenty tiles and draws sixteen — while Fit solves for the
/// drawn content. So a fitted map shows exactly the drawn rectangle, a name inside the canvas
/// can sit outside the panel, and nothing pulled it back.
///
/// Seen the first time the gallery photographed Streets: "Transit to Interchang", cut mid-word
/// by the panel's edge while sitting comfortably inside the canvas.
/// </remarks>
public sealed class MapNameDrawnEdgeTests
{
    private const double Zoom = 0.25;

    [Fact]
    public void A_name_past_the_drawn_right_edge_is_pulled_back_onto_it()
    {
        // The canvas runs to 4000 and the picture stops at 3000. A name centred at 2980 hangs
        // over the edge of the picture and off a fitted panel with it.
        var shift = MapViewModel.HorizontalShift(120, 2980, 0, 3000, Zoom);

        Assert.True(shift < 0, $"expected a leftward shift, got {shift}");
        Assert.Equal(((3000 * Zoom) - (2980 * Zoom)) - 60, shift, 3);
    }

    [Fact]
    public void The_same_name_is_left_alone_when_the_canvas_is_the_only_bound()
    {
        // What the old rule did, and why the symptom survived it: against the whole canvas the
        // name is nowhere near an edge.
        Assert.Equal(0, MapViewModel.HorizontalShift(120, 2980, 0, 4000, Zoom), 3);
    }

    [Fact]
    public void A_drawn_area_that_does_not_start_at_zero_pulls_from_the_left_too()
    {
        // Upstream bounds extend both ways. A picture starting at 800 has a left edge of its
        // own, and a name centred just inside it loses its beginning otherwise.
        var shift = MapViewModel.HorizontalShift(120, 820, 800, 3000, Zoom);

        Assert.True(shift > 0, $"expected a rightward shift, got {shift}");
        Assert.Equal(60 - ((820 * Zoom) - (800 * Zoom)), shift, 3);
    }

    [Fact]
    public void A_name_in_the_middle_of_the_picture_is_untouched()
    {
        Assert.Equal(0, MapViewModel.HorizontalShift(120, 1500, 0, 3000, Zoom), 3);
    }

    [Fact]
    public void A_name_too_wide_for_the_picture_is_left_centred()
    {
        // There is nowhere for it to go, and shifting it would only choose which end to lose.
        Assert.Equal(0, MapViewModel.HorizontalShift(900, 1500, 0, 3000, Zoom), 3);
    }

    [Fact]
    public void An_unmeasured_picture_is_no_bound_at_all()
    {
        Assert.Equal(0, MapViewModel.HorizontalShift(120, 2980, 0, 0, Zoom), 3);
    }

    /// <summary>
    /// A place name follows the same rule, through its own arithmetic.
    /// </summary>
    /// <remarks>
    /// The two are separate because a place name is positioned by the catalog and nudged, while
    /// a marker's name is placed into a slot. They have to agree about where the edge is.
    /// </remarks>
    [Fact]
    public void A_place_name_is_nudged_onto_the_drawn_area_as_well()
    {
        var name = new MapPlaceNameViewModel("Transit to Interchange", 2980, 500, 0, 12, false, false)
        {
            Zoom = Zoom,
            CanvasWidth = 4000,
            DrawnLeft = 0,
            DrawnWidth = 3000,
        };

        Assert.True(name.Nudge < 0, $"expected a leftward nudge, got {name.Nudge}");
    }

    [Fact]
    public void A_place_name_with_nothing_measured_falls_back_to_the_canvas()
    {
        var name = new MapPlaceNameViewModel("Transit to Interchange", 2980, 500, 0, 12, false, false)
        {
            Zoom = Zoom,
            CanvasWidth = 4000,
        };

        Assert.Equal(0, name.Nudge, 3);
    }
}
