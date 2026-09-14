using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// How big a place name is, as the marker layout is told about it.
/// </summary>
/// <remarks>
/// The sixth attempt at the same report, and the first with the pixels magnified. On Customs at
/// 23% zoom, "Scav Checkpoint" — a marker's name, on its dark tag — sits across the first seven
/// characters of the place name "Military Checkpoint", whose tail reads "y Checkpoint" out the
/// other side. Every earlier attempt read it as two labels clipped by the map's edge and moved
/// things that were never in the wrong place.
///
/// A place name is counter-scaled: its text is the same size on screen at every zoom while the
/// point it is centred on is in canvas units. The marker layout was handed the rectangle as
/// <c>(TextLeft + TextWidth) * zoom</c>, which scales the size as well as the position, so at
/// 23% zoom it was told every place name was a quarter of its real width — and it put a label
/// on ground it thought was empty.
///
/// <c>MapPlaceNameLayout.RectFor</c>'s own remark already said the two had to agree "or the two
/// would disagree about whether a name and a label collide". They disagreed.
/// </remarks>
public sealed class MapPlaceNameObstacleTests
{
    /// <summary>"Military Checkpoint" as the catalog draws it: 12 px text, 19 characters.</summary>
    private static MapPlaceNameCandidate Name(double centerX, double centerY) =>
        new(centerX, centerY, 133, 16, 12, "Military Checkpoint");

    [Fact]
    public void The_text_is_the_same_width_on_screen_at_every_zoom()
    {
        // The whole rule. A name that shrank with the map would be a name whose collisions
        // depended on the zoom in the wrong direction: pulled back, everything would fit.
        var near = MapPlaceNameLayout.RectFor(Name(1000, 500), 1);
        var far = MapPlaceNameLayout.RectFor(Name(1000, 500), 0.23);

        Assert.Equal(133, near.Right - near.Left, 3);
        Assert.Equal(133, far.Right - far.Left, 3);
    }

    [Fact]
    public void The_position_does_scale_with_the_map()
    {
        var far = MapPlaceNameLayout.RectFor(Name(1000, 500), 0.23);

        Assert.Equal(230, (far.Left + far.Right) / 2, 3);
        Assert.Equal(115, (far.Top + far.Bottom) / 2, 3);
    }

    /// <summary>
    /// The rectangle the old arithmetic produced, for comparison.
    /// </summary>
    /// <remarks>
    /// Not a rule anybody wants — it is here so the difference is a number rather than an
    /// argument. Thirty pixels against a hundred and thirty-three.
    /// </remarks>
    [Fact]
    public void The_arithmetic_that_was_there_shrank_it_to_a_quarter()
    {
        var name = Name(1000, 500);
        const double zoom = 0.23;
        var textLeft = name.CenterX - (name.Width / 2);
        var wrong = ((textLeft + name.Width) * zoom) - (textLeft * zoom);

        Assert.Equal(name.Width * zoom, wrong, 3);
        Assert.True(wrong < name.Width / 4, $"the old rectangle was {wrong:F0} px wide, not {name.Width:F0}");
    }

    [Fact]
    public void A_marker_name_is_kept_off_the_far_end_of_a_place_name()
    {
        // The reported picture, in numbers. The marker's disc is level with the place name and
        // far enough along it that only the correct rectangle reaches: with the old one the
        // label went into its first slot and landed on the word.
        const double zoom = 0.23;
        var place = Name(1000, 500);
        // Level with the name's row and far enough along it that only the full-width rectangle
        // reaches: the disc lands at screen (296, 95), so the slot below it runs from x 251 to
        // 341. The real text ends at x 296 and the quartered one at 245.
        var marker = new MapLabelCandidate(296 / zoom, 95 / zoom, 90, 17, 0);

        var right = MapPlaceNameLayout.RectFor(place, zoom);
        var quartered = new MapLabelLayout.MapLabelObstacle(
            (place.CenterX - (place.Width / 2)) * zoom,
            (place.CenterY - (place.Height / 2)) * zoom,
            (place.CenterX + (place.Width / 2)) * zoom,
            (place.CenterY + (place.Height / 2)) * zoom);

        var avoided = MapLabelLayout.Arrange(
            [marker],
            zoom,
            [new MapLabelLayout.MapLabelObstacle(right.Left, right.Top, right.Right, right.Bottom)]);
        var covered = MapLabelLayout.Arrange([marker], zoom, [quartered]);

        Assert.Equal(0, covered[0]);
        Assert.NotEqual(0, avoided[0]);
    }
}
