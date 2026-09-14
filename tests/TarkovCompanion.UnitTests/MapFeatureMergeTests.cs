using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// One marker for an exit upstream publishes once per faction.
/// </summary>
/// <remarks>
/// Seen on Customs in the page gallery: two labels reading "RUAF Roadblock", one above and one
/// below a single disc. One disc because there are two, 0.86 m apart, which at 23% zoom is the
/// same pixel; two labels because the layout will not stack two names on one spot.
///
/// Every case below is a real pair from the live feed. Across all seventeen maps and a hundred
/// and fifty-two exits there are exactly five pairs closer than three metres: three are the
/// same exit twice, and two are genuinely different exits that must not be merged.
/// </remarks>
public sealed class MapFeatureMergeTests
{
    /// <summary>
    /// The case the application actually sees, and the one the first attempt refused.
    /// </summary>
    /// <remarks>
    /// The extract name is a translated field, so both copies of a shared exit arrive with the
    /// same display name: "RUAF Roadblock" twice, at the same spot, one pmc and one scav. A
    /// rule that required the names to differ turned away the very pair it was written for,
    /// and the map came back byte-identical to the run before it.
    /// </remarks>
    [Fact]
    public void The_same_name_twice_at_the_same_spot_is_one_exit()
    {
        var merged = MapFeatureMerge.Collapse([
            Exit("RUAF Roadblock", "pmc", -10.25, -138.45),
            Exit("RUAF Roadblock", "scav", -9.44, -138.74),
        ]);

        var only = Assert.Single(merged);
        Assert.Equal("RUAF Roadblock", only.Name);
        Assert.Equal(MapFeatureFaction.Shared, only.Side);
    }

    [Fact]
    public void A_suffixed_scav_copy_is_the_same_exit()
    {
        // Customs, 0.86 m apart.
        var merged = MapFeatureMerge.Collapse([
            Exit("RUAF Roadblock", "pmc", -10.25, -138.45),
            Exit("RUAF Roadblock_scav", "scav", -9.44, -138.74),
        ]);

        var only = Assert.Single(merged);
        Assert.Equal("RUAF Roadblock", only.Name);
        Assert.Equal(MapFeatureFaction.Shared, only.Side);
    }

    [Fact]
    public void A_prefixed_scav_copy_is_the_same_exit()
    {
        // Shoreline, exactly coincident.
        var merged = MapFeatureMerge.Collapse([
            Exit("Road to Customs", "pmc", 100, 200),
            Exit("Scav Road to Customs", "scav", 100, 200),
        ]);

        var only = Assert.Single(merged);
        Assert.Equal("Road to Customs", only.Name);
        Assert.Equal(MapFeatureFaction.Shared, only.Side);
    }

    [Fact]
    public void An_internal_name_pairs_the_same_way()
    {
        // Lighthouse. The names are upstream's own tokens rather than anything readable, and
        // the rule is about the qualifier rather than about the words.
        var merged = MapFeatureMerge.Collapse([
            Exit("Shorl_free", "pmc", 0, 0),
            Exit("Shorl_free_scav", "scav", 0, 0),
        ]);

        Assert.Equal("Shorl_free", Assert.Single(merged).Name);
    }

    /// <summary>
    /// The case that rules out merging on distance, which was the obvious thing to do.
    /// </summary>
    /// <remarks>
    /// Customs puts these 1.03 m apart — closer than the RUAF pair that <b>is</b> one exit.
    /// Merging them would tell a PMC they can leave through a scav exit and would delete one of
    /// two real ways out, which is worse than drawing two labels.
    /// </remarks>
    [Fact]
    public void Two_different_exits_at_the_same_spot_stay_two()
    {
        var merged = MapFeatureMerge.Collapse([
            Exit("Old Road Gate", "scav", 100, 200),
            Exit("Dorms V-Ex", "pmc", 100.8, 200.6),
        ]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Two_scav_exits_at_the_same_spot_stay_two()
    {
        // Streets, exactly coincident and both scav. Neither the side nor the name agrees.
        var merged = MapFeatureMerge.Collapse([
            Exit("scav_e2", "scav", 0, 0),
            Exit("scav_e6", "scav", 0, 0),
        ]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void The_same_pair_far_apart_is_two_exits()
    {
        // The name rule is not enough on its own: a map could name a scav exit after a PMC one
        // at the other end of it.
        var merged = MapFeatureMerge.Collapse([
            Exit("RUAF Roadblock", "pmc", 0, 0),
            Exit("RUAF Roadblock_scav", "scav", 0, 80),
        ]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void A_spawn_is_never_merged_into_an_exit()
    {
        // Spawns share names and positions with exits all the time and answer a different
        // question; only ways out are collapsed.
        var merged = MapFeatureMerge.Collapse([
            Exit("RUAF Roadblock", "pmc", 0, 0),
            new(MapFeatureKind.Spawn, "RUAF Roadblock_scav", new WorldPosition(0, 0, 0), "scav", null),
        ]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void A_map_with_nothing_to_merge_comes_back_unchanged()
    {
        var exits = new[] { Exit("Crossroads", "shared", 0, 0), Exit("Big Red", "pmc", 400, 400) };

        Assert.Equal(["Crossroads", "Big Red"], MapFeatureMerge.Collapse(exits).Select(exit => exit.Name));
    }

    [Fact]
    public void Nothing_at_all_is_nothing_at_all()
    {
        Assert.Empty(MapFeatureMerge.Collapse([]));
    }

    private static MapFeature Exit(string name, string faction, double x, double z) =>
        new(MapFeatureKind.Extract, name, new WorldPosition(x, 0, z), faction, null);
}
