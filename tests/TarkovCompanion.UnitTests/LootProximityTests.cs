using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What the map says is worth looking in, near where the player is standing.
/// </summary>
public sealed class LootProximityTests
{
    [Fact]
    public void Lists_what_is_near_the_player_nearest_first()
    {
        var features = new[]
        {
            Loot("Ration supply crate", 30, 0),
            Loot("Duffle bag", 10, 0),
            Loot("Weapon box", 300, 0),
        };

        var found = LootProximity.Near(features, At(0, 0));

        Assert.Equal(["Duffle bag", "Ration supply crate"], found.Select(loot => loot.Name));
        Assert.Equal(10, found[0].Metres, 3);
        Assert.Equal("E", found[0].Bearing);
    }

    [Fact]
    public void Says_one_thing_once()
    {
        // Forty drawers in a barracks is one fact about that barracks, not forty facts, and
        // forty identical rows would push everything else off a panel read mid-raid.
        var features = Enumerable
            .Range(1, 40)
            .Select(index => Loot("Drawer", index, 0))
            .Append(Loot("Medical supply crate", 41, 0))
            .ToArray();

        var found = LootProximity.Near(features, At(0, 0));

        Assert.Equal(["Drawer", "Medical supply crate"], found.Select(loot => loot.Name));
    }

    [Fact]
    public void Leaves_out_everything_that_is_not_loot()
    {
        var features = new[]
        {
            new MapFeature(MapFeatureKind.Extract, "Outskirts", At(5, 0)),
            new MapFeature(MapFeatureKind.Spawn, "Spawn", At(6, 0)),
            new MapFeature(MapFeatureKind.Lock, "A door", At(7, 0)),
            Loot("Duffle bag", 8, 0),
        };

        var found = LootProximity.Near(features, At(0, 0));

        Assert.Equal(["Duffle bag"], found.Select(loot => loot.Name));
    }

    [Fact]
    public void Stops_at_the_radius()
    {
        // A container a hundred metres away is not "near you" in a game where a hundred metres
        // can be a building, a fence and somebody with a rifle.
        var features = new[] { Loot("Duffle bag", 51, 0) };

        Assert.Empty(LootProximity.Near(features, At(0, 0)));
        Assert.Single(LootProximity.Near(features, At(0, 0), radiusMetres: 100));
    }

    [Fact]
    public void Keeps_the_list_short_enough_to_read_mid_raid()
    {
        var features = Enumerable
            .Range(1, 20)
            .Select(index => Loot($"Thing {index}", index, 0))
            .ToArray();

        Assert.Equal(6, LootProximity.Near(features, At(0, 0)).Count);
    }

    [Fact]
    public void Reports_nothing_rather_than_failing_on_a_map_with_no_loot_synced()
    {
        Assert.Empty(LootProximity.Near([], At(0, 0)));
    }

    private static WorldPosition At(double x, double z) => new(x, 0, z);

    private static MapFeature Loot(string name, double x, double z) =>
        new(MapFeatureKind.Loot, name, At(x, z), null, "Somewhere loot can spawn, not somewhere loot is");
}
