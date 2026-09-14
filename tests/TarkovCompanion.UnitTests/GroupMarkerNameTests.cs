using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// A squadmate's name on the map, arranged against every other name on it.
/// </summary>
/// <remarks>
/// A squadmate was a cone and a dot with the name in a tooltip. Matching a ten-pixel swatch in
/// the panel to a thirteen-pixel dot on the map, in one of eight hues, while somebody is
/// shooting at you, is not "say which is which".
///
/// The thing these guard is that the names go through the <em>same</em> arrangement pass as the
/// feature names. A second pass would place a name into a slot the first had already given away,
/// which is how a name lands on top of an extract label — and the reason these are worth drawing
/// at all is that the map is crowded.
/// </remarks>
public sealed class GroupMarkerNameTests
{
    private const double Height = 17;

    [Fact]
    public void A_squadmate_outranks_an_offered_exit_for_the_slot_they_both_want()
    {
        // A feature's name can be read off the panel beside the map or worked out from the
        // shape of its marker. A person's cannot be worked out from anything, and unlike an
        // exit they move.
        var slots = MapLabelLayout.Arrange([Candidate(0, 0, priority: 2), Candidate(0, 0, priority: 3)], zoom: 1);

        Assert.NotEqual(MapLabelLayout.Hidden, slots[1]);
    }

    [Fact]
    public void A_name_that_fits_nowhere_is_dropped_rather_than_drawn_over_something()
    {
        // The same rule the feature names have always followed. The dot still reads, and the
        // panel beside the map still says who is where.
        var crowd = Enumerable.Range(0, 40).Select(_ => Candidate(0, 0, priority: 3)).ToArray();

        var slots = MapLabelLayout.Arrange(crowd, zoom: 1);

        Assert.Contains(MapLabelLayout.Hidden, slots);
    }

    [Fact]
    public void A_name_estimates_its_width_the_same_way_a_feature_name_does()
    {
        // The two are laid out against each other, so an estimate that differed between them
        // would let one sit inside the other and neither would know.
        var member = new GroupMarkerViewModel("Geo", 0, 0, 0, "detail", IsStale: false) { ShowsName = true };
        var feature = new MapOverlayElementViewModel("Geo", 0, 0, MapMarkerKind.Extract, false, false, false);

        Assert.Equal(feature.EstimatedNameWidth, member.EstimatedNameWidth, 6);
    }

    [Fact]
    public void A_name_is_not_drawn_when_the_setting_is_off()
    {
        var off = new GroupMarkerViewModel("Geo", 0, 0, 0, "detail", IsStale: false) { ShowsName = false };

        Assert.False(off.HasName);
    }

    [Fact]
    public void An_unnamed_member_has_no_name_to_draw()
    {
        // The relay bounds a display name but does not require one to be interesting, and a
        // tag containing nothing is a tag that reads as a rendering fault.
        var blank = new GroupMarkerViewModel(string.Empty, 0, 0, 0, "detail", IsStale: false) { ShowsName = true };

        Assert.False(blank.HasName);
    }

    private static MapLabelCandidate Candidate(double x, double y, int priority) =>
        new(x, y, 80, Height, priority);
}
