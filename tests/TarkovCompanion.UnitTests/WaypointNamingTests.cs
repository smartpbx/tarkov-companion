using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What a mark is called, which until now was "1", "2" and "3".
/// </summary>
/// <remarks>
/// Both clients sent <c>label: null</c> for every waypoint ever dropped, and the server has
/// stored a label since the day the marks were written. So the squad's plan was a row of
/// numbered dots and the only way to say which one you meant was to point at a screen four
/// people cannot see.
/// </remarks>
public sealed class WaypointNamingTests
{
    [Fact]
    public void A_mark_takes_the_name_of_the_nearest_named_thing()
    {
        var name = WaypointNaming.Describe(
            At(10, 0),
            [Feature(MapFeatureKind.Lock, "Dorm room 214", 12, 0)],
            []);

        Assert.Equal("Dorm room 214", name);
    }

    [Fact]
    public void A_place_name_counts_as_much_as_a_catalog_feature()
    {
        // The label layer carries the names a player would actually say out loud, and the
        // catalog carries exits and doors. Neither is the better source; the nearer one is.
        var name = WaypointNaming.Describe(
            At(0, 0),
            [Feature(MapFeatureKind.Extract, "RUAF Roadblock", 30, 0)],
            [Place("Big Red", 5, 0)]);

        Assert.Equal("Big Red", name);
    }

    [Fact]
    public void Nothing_near_enough_leaves_the_mark_unnamed()
    {
        // A mark called after something eighty metres away is worse than a mark called "3",
        // because "3" is not a claim about anything.
        Assert.Null(WaypointNaming.Describe(
            At(0, 0),
            [Feature(MapFeatureKind.Extract, "RUAF Roadblock", 80, 0)],
            [Place("Big Red", 90, 0)]));
    }

    [Fact]
    public void Loot_never_names_a_mark()
    {
        // The difference between this working and not. Woods alone has 815 loot positions, so
        // one is within range of anywhere on the map, and every mark a squad dropped would
        // have come out called "Duffle bag".
        var name = WaypointNaming.Describe(
            At(0, 0),
            [Feature(MapFeatureKind.Loot, "Duffle bag", 1, 0), Feature(MapFeatureKind.Extract, "Outskirts", 20, 0)],
            []);

        Assert.Equal("Outskirts", name);
    }

    [Fact]
    public void A_mark_with_only_loot_near_it_is_left_unnamed()
    {
        Assert.Null(WaypointNaming.Describe(At(0, 0), [Feature(MapFeatureKind.Loot, "Duffle bag", 1, 0)], []));
    }

    [Fact]
    public void A_place_name_drawn_across_two_lines_comes_out_as_one()
    {
        // Place names are laid out as artwork and several carry line breaks so they sit inside
        // a building. A waypoint name goes in a list and a tooltip, where a newline is a hole.
        var name = WaypointNaming.Describe(At(0, 0), [], [Place("Old\nGas Station", 3, 0)]);

        Assert.Equal("Old Gas Station", name);
    }

    [Fact]
    public void The_second_number_on_a_place_is_read_as_world_z()
    {
        // Which is how the projection reads it when it draws the same labels: it builds a
        // world position of (X, 0, Y). Read as height instead, a place fifty metres north
        // would be scored as though it were underfoot.
        var mark = At(0, 50);

        Assert.Equal("Fortress", WaypointNaming.Describe(mark, [], [Place("Fortress", 0, 50)]));
        Assert.Null(WaypointNaming.Describe(mark, [], [Place("Fortress", 50, 0)]));
    }

    [Fact]
    public void Height_does_not_decide_the_name()
    {
        // Distance here is the ground distance the map is drawn in, which is what SpawnProximity
        // has always measured. A mark on the third floor of Dorms is in Dorms.
        var name = WaypointNaming.Describe(
            new(0, 30, 0),
            [Feature(MapFeatureKind.Lock, "Dorm room 314", 5, 0)],
            []);

        Assert.Equal("Dorm room 314", name);
    }

    [Fact]
    public void A_map_with_nothing_on_it_names_nothing_rather_than_failing()
    {
        Assert.Null(WaypointNaming.Describe(At(0, 0), [], []));
    }

    [Fact]
    public void A_feature_or_place_with_no_name_is_not_one()
    {
        Assert.Null(WaypointNaming.Describe(
            At(0, 0),
            [Feature(MapFeatureKind.Extract, "  ", 1, 0)],
            [Place(string.Empty, 1, 0)]));
    }

    private static WorldPosition At(double x, double z) => new(x, 0, z);

    private static MapFeature Feature(MapFeatureKind kind, string name, double x, double z) =>
        new(kind, name, At(x, z));

    private static MapCatalogLabel Place(string text, double x, double z) =>
        new(text, new(x, z), 0, 100, null, null);
}
