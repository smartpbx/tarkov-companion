using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Keeping a marker's name off a place name that cannot move out of its way.
/// </summary>
/// <remarks>
/// Seen in the Windows page gallery on Customs: "Administration Gate" and "Factory Checkpoint"
/// drawn on top of each other, both clipped and neither readable. The catalog's place names are
/// drawn where the catalog puts them, at its own size and angle, so they cannot be moved to a
/// slot — but a name that cannot move is still a name that has to be avoided.
/// </remarks>
public sealed class MapPlaceNameCollisionTests
{
    [Fact]
    public void A_marker_name_moves_out_of_a_place_names_way()
    {
        var marker = new MapLabelCandidate(100, 100, 80, 17, 0);
        // Sitting exactly where the first slot below the disc would put the name.
        var occupied = new[] { new MapLabelLayout.MapLabelObstacle(60, 115, 140, 134) };

        var withObstacle = MapLabelLayout.Arrange([marker], 1, occupied);
        var without = MapLabelLayout.Arrange([marker], 1, []);

        Assert.Equal(0, without[0]);
        Assert.NotEqual(0, withObstacle[0]);
        Assert.NotEqual(MapLabelLayout.Hidden, withObstacle[0]);
    }

    [Fact]
    public void A_name_with_nowhere_left_is_dropped_rather_than_drawn_over_one()
    {
        // An unreadable name is worse than no name: it still costs the space.
        var marker = new MapLabelCandidate(100, 100, 80, 17, 0);
        var boxedIn = new[] { new MapLabelLayout.MapLabelObstacle(0, 0, 300, 300) };

        Assert.Equal(MapLabelLayout.Hidden, MapLabelLayout.Arrange([marker], 1, boxedIn)[0]);
    }

    [Fact]
    public void Nothing_occupied_behaves_exactly_as_before()
    {
        var markers = new[]
        {
            new MapLabelCandidate(100, 100, 80, 17, 0),
            new MapLabelCandidate(300, 300, 80, 17, 1),
        };

        Assert.Equal(MapLabelLayout.Arrange(markers, 1), MapLabelLayout.Arrange(markers, 1, []));
    }

    [Fact]
    public void A_place_names_text_is_measured_rather_than_its_box()
    {
        // The box is 400 wide because it has to hold the longest name with the text centred in
        // it. Treating all of that as covered ground would blank out most of the map's names.
        var name = new MapPlaceNameViewModel("Dorms", 100, 100, 0, 14, false, false);

        Assert.Equal(400, name.Width);
        Assert.True(name.TextWidth < 100, $"text width was {name.TextWidth}");
        Assert.True(name.TextHeight < name.Height);
        Assert.Equal(100, name.TextLeft + (name.TextWidth / 2), 3);
    }

    [Fact]
    public void A_very_long_place_name_never_claims_more_than_its_box()
    {
        var name = new MapPlaceNameViewModel(new string('x', 400), 100, 100, 0, 14, false, false);

        Assert.Equal(name.Width, name.TextWidth);
    }
}
