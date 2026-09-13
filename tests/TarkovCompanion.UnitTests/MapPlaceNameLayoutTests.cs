using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Choosing which of the catalog's place names are drawn when they will not all fit.
/// </summary>
/// <remarks>
/// Reported with a screenshot of Customs: "Administration Gate" and "Factory Checkpoint" drawn
/// on top of each other, both clipped and neither readable.
/// </remarks>
public sealed class MapPlaceNameLayoutTests
{
    [Fact]
    public void Two_names_on_the_same_spot_become_one()
    {
        // An unreadable name is worse than a missing one: it still costs the space and still
        // has to be read before it can be dismissed.
        var names = new[]
        {
            Name("Administration Gate", 100, 100, size: 14),
            Name("Factory Checkpoint", 104, 102, size: 12),
        };

        var drawn = MapPlaceNameLayout.Choose(names, [], 1);

        Assert.True(drawn[0], "the larger name survives");
        Assert.False(drawn[1]);
    }

    [Fact]
    public void The_catalog_decides_which_one_matters()
    {
        // The larger of the two is the one a mapper drew larger. It is the only signal there is.
        var names = new[]
        {
            Name("boiler", 100, 100, size: 10),
            Name("Dorms", 102, 101, size: 20),
        };

        var drawn = MapPlaceNameLayout.Choose(names, [], 1);

        Assert.False(drawn[0]);
        Assert.True(drawn[1]);
    }

    [Fact]
    public void The_same_name_is_dropped_every_time()
    {
        // Two names of the same size flickering between frames would be worse than either
        // choice, so the tie breaks on something that does not change.
        var names = new[] { Name("Bravo", 100, 100, size: 14), Name("Alpha", 102, 101, size: 14) };

        var first = MapPlaceNameLayout.Choose(names, [], 1);
        var again = MapPlaceNameLayout.Choose(names, [], 1);

        Assert.Equal(first, again);
        Assert.True(first[1], "alphabetical order breaks the tie");
        Assert.False(first[0]);
    }

    [Fact]
    public void A_name_under_a_marker_is_dropped()
    {
        // The other half of the report: "Warehouse 17" with a disc sitting on the middle of it.
        var names = new[] { Name("Warehouse 17", 100, 100, size: 14) };

        Assert.False(MapPlaceNameLayout.Choose(names, [(110, 106)], 1)[0]);
        Assert.True(MapPlaceNameLayout.Choose(names, [(400, 400)], 1)[0]);
    }

    [Fact]
    public void Names_that_do_not_touch_are_all_drawn()
    {
        var names = new[]
        {
            Name("Dorms", 0, 0, size: 14),
            Name("Big Red", 500, 0, size: 14),
            Name("Crackhouse", 0, 500, size: 14),
        };

        Assert.All(MapPlaceNameLayout.Choose(names, [], 1), drawn => Assert.True(drawn));
    }

    [Fact]
    public void Zoom_changes_who_collides_with_whom()
    {
        // Names hold their size on screen while the map moves under them, so two that are clear
        // at one zoom overlap at another. Pulled far enough back, these two land on each other.
        var names = new[] { Name("Alpha", 0, 0, size: 14), Name("Bravo", 300, 0, size: 14) };

        Assert.All(MapPlaceNameLayout.Choose(names, [], 1), drawn => Assert.True(drawn));
        Assert.Contains(false, MapPlaceNameLayout.Choose(names, [], 0.02));
    }

    [Fact]
    public void An_unknown_zoom_drops_nothing()
    {
        // Nothing is known about where anything is on screen, and dropping names on a guess
        // would be losing them for no reason.
        var names = new[] { Name("Alpha", 100, 100, size: 14), Name("Bravo", 100, 100, size: 14) };

        Assert.All(MapPlaceNameLayout.Choose(names, [], 0), drawn => Assert.True(drawn));
        Assert.All(MapPlaceNameLayout.Choose(names, [], double.NaN), drawn => Assert.True(drawn));
    }

    [Fact]
    public void No_names_is_not_a_failure() =>
        Assert.Empty(MapPlaceNameLayout.Choose([], [], 1));

    private static MapPlaceNameCandidate Name(string text, double left, double top, double size) =>
        new(left, top, text.Length * size * 0.55, size * 1.35, size, text);
}
