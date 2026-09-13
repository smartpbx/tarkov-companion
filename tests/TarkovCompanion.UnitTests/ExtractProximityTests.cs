using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The ways out of a map, which used to be whatever a scan of the extract screen recognised.
/// </summary>
/// <remarks>
/// The readout that answers "how do I get out" was empty until a screenshot of the extract panel
/// was matched, and on a real Woods screen the facts file records one exit in five matching.
/// These tests hold the list to the other source: the map catalog, which has every exit on it
/// whether or not anything has been recognised.
/// </remarks>
public sealed class ExtractProximityTests
{
    [Fact]
    public void Lists_every_exit_on_the_map_nearest_first()
    {
        var features = new[]
        {
            Exit("Old Gas Station", 300, 0),
            Exit("RUAF Roadblock", 40, 0),
            Exit("Crossroads", 120, 0),
        };

        var found = ExtractProximity.Near(features, At(0, 0), MapFeatureFaction.Pmc);

        Assert.Equal(
            ["RUAF Roadblock", "Crossroads", "Old Gas Station"],
            found.Select(exit => exit.Name));
        Assert.Equal(40, found[0].MetresFromPlayer!.Value, 3);
        Assert.Equal("E", found[0].Bearing);
    }

    [Fact]
    public void An_exit_for_the_other_side_is_left_out_rather_than_ranked_last()
    {
        // Not a worse option, not an option. Drawing it sends somebody to a door that will not
        // open, and it would be the nearest row on the panel while they ran to it.
        var features = new[]
        {
            Exit("Scav Bridge", 10, 0, "scav"),
            Exit("RUAF Roadblock", 200, 0, "pmc"),
        };

        var found = ExtractProximity.Near(features, At(0, 0), MapFeatureFaction.Pmc);

        Assert.Equal(["RUAF Roadblock"], found.Select(exit => exit.Name));
    }

    [Fact]
    public void A_shared_exit_and_an_unlabelled_one_both_stay()
    {
        // Shared is for everybody. Silence is not a statement that it cannot be used, and the
        // feed is silent about a good many exits.
        var features = new[]
        {
            Exit("Smuggler's Boat", 10, 0, "shared"),
            Exit("Unknown Way", 20, 0),
        };

        var found = ExtractProximity.Near(features, At(0, 0), MapFeatureFaction.Scav);

        Assert.Equal(["Smuggler's Boat", "Unknown Way"], found.Select(exit => exit.Name));
    }

    [Fact]
    public void Before_the_side_is_known_nothing_is_filtered_out()
    {
        var features = new[] { Exit("Scav Bridge", 10, 0, "scav"), Exit("RUAF", 20, 0, "pmc") };

        var found = ExtractProximity.Near(features, At(0, 0), MapFeatureFaction.Unknown);

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void A_transit_sits_below_the_real_exits()
    {
        // Leaving the raid is the usual question. A transit to another map is a decision
        // somebody makes deliberately, so a near one does not displace a way home.
        var features = new[]
        {
            new MapFeature(MapFeatureKind.Transit, "To Streets", At(5, 0)),
            Exit("RUAF Roadblock", 400, 0),
        };

        var found = ExtractProximity.Near(features, At(0, 0), MapFeatureFaction.Pmc);

        Assert.Equal(["RUAF Roadblock", "To Streets"], found.Select(exit => exit.Name));
        Assert.True(found[1].IsTransit);
    }

    [Fact]
    public void A_recognised_exit_is_marked_and_lifted_above_a_nearer_one()
    {
        // The scan's contribution. A confirmed exit is worth more than a nearer unconfirmed
        // one, because the confirmation is the game's own word that it is open this raid.
        var features = new[] { Exit("Crossroads", 10, 0), Exit("RUAF Roadblock", 500, 0) };

        var found = ExtractProximity.Near(
            features,
            At(0, 0),
            MapFeatureFaction.Pmc,
            ["ruaf roadblock"]);

        Assert.Equal("RUAF Roadblock", found[0].Name);
        Assert.True(found[0].WasOffered);
        Assert.False(found[1].WasOffered);
    }

    [Fact]
    public void The_list_exists_before_the_player_has_been_placed()
    {
        // The whole point. A player who has taken no screenshot yet still gets the exits their
        // side can use; what they do not get is a distance, because there is nothing to measure
        // from and a distance from the map's origin would be a number that means nothing.
        var features = new[] { Exit("RUAF Roadblock", 40, 0), Exit("Crossroads", 10, 0) };

        var found = ExtractProximity.Near(features, player: null, MapFeatureFaction.Pmc);

        Assert.Equal(["Crossroads", "RUAF Roadblock"], found.Select(exit => exit.Name));
        Assert.All(found, exit => Assert.Null(exit.MetresFromPlayer));
        Assert.All(found, exit => Assert.Equal(string.Empty, exit.Bearing));
    }

    [Fact]
    public void Everything_that_is_not_a_way_out_is_left_out()
    {
        var features = new[]
        {
            new MapFeature(MapFeatureKind.Loot, "Duffle bag", At(1, 0)),
            new MapFeature(MapFeatureKind.Spawn, "Spawn", At(2, 0)),
            new MapFeature(MapFeatureKind.Lock, "A door", At(3, 0)),
            Exit("RUAF Roadblock", 40, 0),
        };

        var found = ExtractProximity.Near(features, At(0, 0), MapFeatureFaction.Pmc);

        Assert.Equal(["RUAF Roadblock"], found.Select(exit => exit.Name));
    }

    [Fact]
    public void A_map_whose_features_have_not_synced_reports_nothing_rather_than_failing()
    {
        Assert.Empty(ExtractProximity.Near([], At(0, 0), MapFeatureFaction.Pmc));
    }

    [Fact]
    public void The_list_is_short_enough_to_read_mid_raid()
    {
        var features = Enumerable
            .Range(1, 20)
            .Select(index => Exit($"Way {index:00}", index * 10, 0))
            .ToArray();

        Assert.Equal(6, ExtractProximity.Near(features, At(0, 0), MapFeatureFaction.Pmc).Count);
    }

    private static WorldPosition At(double x, double z) => new(x, 0, z);

    private static MapFeature Exit(string name, double x, double z, string? faction = null) =>
        new(MapFeatureKind.Extract, name, At(x, z), faction);
}
