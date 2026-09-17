using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Where the other players in this raid started, from where this one did.
/// </summary>
/// <remarks>
/// <para>
/// The side and category values asserted here are the feed's own, counted across every map:
/// 3018 spawn points, sides "all" on 860, "pmc" on 528, "scav" on 1628, and categories
/// "player", "bot", "boss", "botpmc" and "sniper" in combination.
/// </para>
/// <para>
/// Built from the same <see cref="SpawnGrouping"/> the map markers use, so most anchors below
/// sit well past <see cref="SpawnGrouping.WithinMetres"/> from every spawn: close enough and the
/// "leave out my own start" rule (tested on its own further down) would quietly remove a row a
/// test means to assert on.
/// </para>
/// </remarks>
public sealed class SpawnProximityTests
{
    [Fact]
    public void Lists_player_spawn_areas_near_the_start_nearest_first()
    {
        var features = new[]
        {
            Spawn("Near", 100, 0, "pmc", "player"),
            Spawn("Nearer", 60, 0, "all", "player"),
            Spawn("Far", 450, 0, "pmc", "player"),
        };

        var found = SpawnProximity.Near(features, At(0, 0), At(0, 0), MapFeatureFaction.Pmc);

        Assert.Equal(["Nearer", "Near"], found.Select(spawn => spawn.Name));
        Assert.Equal(60, found[0].MetresFromStart, 3);
    }

    [Fact]
    public void Several_points_in_one_area_become_one_row()
    {
        // The bug this whole package is about: "Spawn · Village 0 m from your start", "10 m",
        // "13 m", "13 m", "15 m", "16 m", "17 m" for what is one place, not seven rows.
        var features = new[]
        {
            Spawn("Village", 200, 0, "pmc", "player"),
            Spawn("Village", 210, 5, "pmc", "player"),
            Spawn("Village", 190, 10, "pmc", "player"),
            Spawn("Village", 205, -5, "pmc", "player"),
        };

        var found = SpawnProximity.Near(features, At(0, 0), null, MapFeatureFaction.Pmc);

        var row = Assert.Single(found);
        Assert.Equal("Village · 4 points", row.Name);
    }

    [Fact]
    public void Leaves_out_the_points_bots_start_at()
    {
        // "botpmc" is a scripted PMC and is not a player. A panel that listed every bot spawn
        // would be a panel nobody reads, which is the same as not having one.
        var features = new[]
        {
            Spawn("Player", 100, 0, "pmc", "player"),
            Spawn("Bot", 102, 0, "scav", "bot"),
            Spawn("Boss", 104, 0, "scav", "boss,bot"),
            Spawn("Scripted", 106, 0, "scav", "botpmc"),
            Spawn("Sniper", 108, 0, "scav", "bot,sniper"),
        };

        var found = SpawnProximity.Near(features, At(0, 0), null, MapFeatureFaction.Unknown);

        Assert.Equal(["Player"], found.Select(spawn => spawn.Name));
    }

    [Fact]
    public void Reads_all_as_a_shared_spawn_rather_than_an_unknown_one()
    {
        // The commonest value in the feed. Treating it as unknown left the largest group of
        // player spawns with no side at all.
        var shared = Spawn("Shared", 60, 0, "all", "player");

        Assert.Equal(MapFeatureFaction.Shared, shared.Side);
        Assert.Single(SpawnProximity.Near([shared], At(0, 0), null, MapFeatureFaction.Pmc));
    }

    [Fact]
    public void Answers_for_the_side_the_player_is_actually_on()
    {
        var features = new[]
        {
            Spawn("PMC start", 100, 0, "pmc", "player"),
            Spawn("Scav start", 150, 0, "scav", "bot,player"),
        };

        Assert.Equal(
            ["PMC start"],
            SpawnProximity.Near(features, At(0, 0), null, MapFeatureFaction.Pmc).Select(s => s.Name));
    }

    [Fact]
    public void Lists_both_sides_when_the_raid_never_said_which_it_was()
    {
        // An empty panel on a raid whose side was never established reads as a broken feature.
        var features = new[]
        {
            Spawn("PMC start", 100, 0, "pmc", "player"),
            Spawn("Scav start", 150, 0, "scav", "bot,player"),
        };

        Assert.Equal(2, SpawnProximity.Near(features, At(0, 0), null, MapFeatureFaction.Unknown).Count);
    }

    [Theory]
    [InlineData("pmc")]
    [InlineData("scav")]
    [InlineData("all")]
    public void Lists_nothing_on_a_scav_run(string catalogSide)
    {
        // A scav joins twenty minutes in and arrives wherever the game puts them, so neither
        // where the PMCs started nor where the other scavs may arrive is a question they are
        // asking. The map's spawn layer is already empty for a scav; this now agrees with it.
        var features = new[] { Spawn("Start", 100, 0, catalogSide, "player") };

        Assert.Empty(SpawnProximity.Near(features, At(0, 0), null, MapFeatureFaction.Scav));
    }

    [Fact]
    public void Leaves_out_the_area_the_player_started_in()
    {
        var features = new[]
        {
            Spawn("Mine", 10, 0, "pmc", "player"),
            Spawn("Theirs", 100, 0, "pmc", "player"),
        };

        // The anchor sits inside the "Mine" area (well under SpawnGrouping.WithinMetres from
        // it), so that is where this player started, not one of "the others".
        var found = SpawnProximity.Near(features, At(0, 0), null, MapFeatureFaction.Pmc);

        Assert.Equal(["Theirs"], found.Select(spawn => spawn.Name));
    }

    [Fact]
    public void Keeps_the_only_area_when_it_is_the_players_own()
    {
        var features = new[] { Spawn("Mine", 5, 0, "pmc", "player") };

        Assert.Empty(SpawnProximity.Near(features, At(0, 0), null, MapFeatureFaction.Pmc));
    }

    [Fact]
    public void Does_not_exclude_an_area_the_anchor_merely_happens_to_be_nearest_to()
    {
        // Nearest is not the same as "mine". An anchor a hundred metres from the closest area
        // did not start there; it started somewhere the catalog has no point for, or ran before
        // taking its first screenshot.
        var features = new[] { Spawn("Not mine", 100, 0, "pmc", "player") };

        var found = SpawnProximity.Near(features, At(0, 0), null, MapFeatureFaction.Pmc);

        Assert.Equal(["Not mine"], found.Select(spawn => spawn.Name));
    }

    [Fact]
    public void Stops_at_the_radius()
    {
        var features = new[] { Spawn("Just outside", 301, 0, "pmc", "player") };

        Assert.Empty(SpawnProximity.Near(features, At(0, 0), null, MapFeatureFaction.Unknown));
        Assert.Single(SpawnProximity.Near(features, At(0, 0), null, MapFeatureFaction.Unknown, radiusMetres: 350));
    }

    [Fact]
    public void Widened_to_three_hundred_metres_by_default()
    {
        var features = new[] { Spawn("Edge of the old radius", 200, 0, "pmc", "player") };

        // A hundred and fifty used to be the cutoff; areas call for a wider one, and 200 m must
        // now come back without callers asking for it explicitly.
        Assert.Single(SpawnProximity.Near(features, At(0, 0), null, MapFeatureFaction.Unknown));
        Assert.Equal(300, SpawnProximity.DefaultRadiusMetres);
    }

    [Fact]
    public void Keeps_the_list_short_enough_to_read_while_a_raid_starts()
    {
        // Areas spaced well past the grouping radius so each stays its own row, and a large
        // explicit radius so this is a test of the row cap, not the distance cutoff.
        var features = Enumerable
            .Range(1, 20)
            .Select(index => Spawn($"Spawn {index}", index * 100, 0, "pmc", "player"))
            .ToArray();

        var found = SpawnProximity.Near(features, At(0, 0), null, MapFeatureFaction.Unknown, radiusMetres: 10_000);

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
        var found = Assert.Single(
            SpawnProximity.Near([Spawn("A", 60, 0, "pmc", "player")], At(0, 0), null, MapFeatureFaction.Unknown));

        Assert.Null(found.MetresFromPlayer);
        Assert.Null(found.Bearing);
    }

    [Fact]
    public void Panel_rows_are_the_same_areas_the_map_draws()
    {
        // MapFeatureProjection draws one marker per SpawnGrouping area; the panel and the threat
        // lines built from it must be the same areas, or a row, a line and a marker could each
        // answer a different question.
        var features = new[]
        {
            Spawn("Village A", 500, 500, "pmc", "player"),
            Spawn("Village B", 510, 505, "pmc", "player"),
            Spawn("Ridge", 900, 500, "pmc", "player"),
        };

        var grouped = SpawnGrouping.Collapse(features)
            .Where(feature => feature.Kind == MapFeatureKind.Spawn)
            .Select(feature => feature.Position)
            .ToArray();
        var near = SpawnProximity
            .Near(features, At(0, 0), null, MapFeatureFaction.Pmc, radiusMetres: 5_000)
            .Select(spawn => spawn.Position)
            .ToArray();

        Assert.Equal(grouped, near);
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
