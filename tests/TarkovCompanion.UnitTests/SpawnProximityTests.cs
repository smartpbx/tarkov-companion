using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Where the other players in this raid started, from where this one did.
/// </summary>
/// <remarks>
/// The side and category values asserted here are the feed's own, counted across every map:
/// 3018 spawn points, sides "all" on 860, "pmc" on 528, "scav" on 1628, and categories
/// "player", "bot", "boss", "botpmc" and "sniper" in combination.
/// </remarks>
public sealed class SpawnProximityTests
{
    [Fact]
    public void Lists_player_spawns_near_the_start_nearest_first()
    {
        var features = new[]
        {
            Spawn("Near", 40, 0, "pmc", "player"),
            Spawn("Nearer", 10, 0, "all", "player"),
            Spawn("Far", 400, 0, "pmc", "player"),
        };

        var found = SpawnProximity.Near(features, At(0, 0), At(0, 0), MapFeatureFaction.Pmc);

        Assert.Equal(["Nearer", "Near"], found.Select(spawn => spawn.Name));
        Assert.Equal(10, found[0].MetresFromStart, 3);
    }

    [Fact]
    public void Leaves_out_the_points_bots_start_at()
    {
        // "botpmc" is a scripted PMC and is not a player. A panel that listed every bot spawn
        // would be a panel nobody reads, which is the same as not having one.
        var features = new[]
        {
            Spawn("Player", 10, 0, "pmc", "player"),
            Spawn("Bot", 12, 0, "scav", "bot"),
            Spawn("Boss", 14, 0, "scav", "boss,bot"),
            Spawn("Scripted", 16, 0, "scav", "botpmc"),
            Spawn("Sniper", 18, 0, "scav", "bot,sniper"),
        };

        var found = SpawnProximity.Near(features, At(0, 0), null, MapFeatureFaction.Unknown);

        Assert.Equal(["Player"], found.Select(spawn => spawn.Name));
    }

    [Fact]
    public void Reads_all_as_a_shared_spawn_rather_than_an_unknown_one()
    {
        // The commonest value in the feed. Treating it as unknown left the largest group of
        // player spawns with no side at all.
        var shared = Spawn("Shared", 10, 0, "all", "player");

        Assert.Equal(MapFeatureFaction.Shared, shared.Side);
        Assert.Single(SpawnProximity.Near([shared], At(0, 0), null, MapFeatureFaction.Pmc));
        Assert.Single(SpawnProximity.Near([shared], At(0, 0), null, MapFeatureFaction.Scav));
    }

    [Fact]
    public void Answers_for_the_side_the_player_is_actually_on()
    {
        var features = new[]
        {
            Spawn("PMC start", 10, 0, "pmc", "player"),
            Spawn("Scav start", 20, 0, "scav", "bot,player"),
        };

        Assert.Equal(["PMC start"], SpawnProximity.Near(features, At(0, 0), null, MapFeatureFaction.Pmc).Select(s => s.Name));
        Assert.Equal(["Scav start"], SpawnProximity.Near(features, At(0, 0), null, MapFeatureFaction.Scav).Select(s => s.Name));
    }

    [Fact]
    public void Lists_both_sides_when_the_raid_never_said_which_it_was()
    {
        // An empty panel on a raid whose side was never established reads as a broken feature.
        var features = new[]
        {
            Spawn("PMC start", 10, 0, "pmc", "player"),
            Spawn("Scav start", 20, 0, "scav", "bot,player"),
        };

        Assert.Equal(2, SpawnProximity.Near(features, At(0, 0), null, MapFeatureFaction.Unknown).Count);
    }

    [Fact]
    public void Stops_at_the_radius()
    {
        var features = new[] { Spawn("Just outside", 151, 0, "pmc", "player") };

        Assert.Empty(SpawnProximity.Near(features, At(0, 0), null, MapFeatureFaction.Unknown));
        Assert.Single(SpawnProximity.Near(features, At(0, 0), null, MapFeatureFaction.Unknown, radiusMetres: 200));
    }

    [Fact]
    public void Keeps_the_list_short_enough_to_read_while_a_raid_starts()
    {
        var features = Enumerable
            .Range(1, 20)
            .Select(index => Spawn($"Spawn {index}", index, 0, "pmc", "player"))
            .ToArray();

        var found = SpawnProximity.Near(features, At(0, 0), null, MapFeatureFaction.Unknown);

        Assert.Equal(8, found.Count);
        Assert.Equal("Spawn 1", found[0].Name);
    }

    [Fact]
    public void Measures_from_the_player_as_well_as_from_the_start()
    {
        var features = new[] { Spawn("Over there", 30, 40, "pmc", "player") };

        var found = Assert.Single(SpawnProximity.Near(features, At(0, 0), At(30, 0), MapFeatureFaction.Pmc));

        Assert.Equal(50, found.MetresFromStart, 3);
        Assert.Equal(40, found.MetresFromPlayer.GetValueOrDefault(), 3);
    }

    [Fact]
    public void Says_nothing_about_distance_from_a_player_who_has_not_been_seen()
    {
        var found = Assert.Single(SpawnProximity.Near([Spawn("A", 10, 0, "pmc", "player")], At(0, 0), null, MapFeatureFaction.Unknown));

        Assert.Null(found.MetresFromPlayer);
        Assert.Null(found.Bearing);
    }

    [Theory]
    [InlineData(0, -10, "N")]
    [InlineData(10, 0, "E")]
    [InlineData(0, 10, "S")]
    [InlineData(-10, 0, "W")]
    [InlineData(10, -10, "NE")]
    [InlineData(-10, 10, "SW")]
    [InlineData(0, 0, "here")]
    public void Points_the_way_using_the_same_north_the_map_does(double x, double z, string expected) =>
        Assert.Equal(expected, SpawnProximity.Compass(At(0, 0), At(x, z)));

    [Fact]
    public void Ignores_height_because_the_map_is_flat()
    {
        // A number printed beside a map has to agree with the map. Two points a hundred metres
        // apart on the picture are a hundred metres apart to whoever is reading it.
        var a = new WorldPosition(0, 0, 0);
        var b = new WorldPosition(30, 400, 40);

        Assert.Equal(50, SpawnProximity.Distance(a, b), 3);
    }

    private static WorldPosition At(double x, double z) => new(x, 0, z);

    private static MapFeature Spawn(string name, double x, double z, string sides, string categories) =>
        new(MapFeatureKind.Spawn, name, At(x, z), sides, categories);
}
