using System.Text.Json;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests.Debrief;

public sealed class RaidCoverageTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Survived", RaidOutcomeBucket.Survived)]
    [InlineData("Run-through", RaidOutcomeBucket.RunThrough)]
    [InlineData("MIA", RaidOutcomeBucket.Mia)]
    [InlineData("Died", RaidOutcomeBucket.Died)]
    [InlineData("Killed by a boss", RaidOutcomeBucket.Died)]
    [InlineData("Closed on restart", RaidOutcomeBucket.Unknown)]
    [InlineData(null, RaidOutcomeBucket.Unknown)]
    [InlineData("  ", RaidOutcomeBucket.Unknown)]
    public void An_outcome_falls_in_one_bucket(string? outcome, RaidOutcomeBucket expected) =>
        Assert.Equal(expected, RaidCoverage.Classify(outcome));

    [Fact]
    public void Survival_counts_extracted_over_raids_with_an_outcome_and_ignores_the_rest()
    {
        var map = Assert.Single(RaidCoverage.ByMap(
        [
            Raid("customs", "Survived"),
            Raid("customs", "Run-through"),
            Raid("customs", "Died"),
            Raid("customs", "MIA"),
            Raid("customs", null),
        ]));

        Assert.Equal(5, map.Raids);
        Assert.Equal(2, map.Extracted);
        Assert.Equal(1, map.Died);
        Assert.Equal(4, map.OutcomesRecorded);
        Assert.Equal(0.5, map.SurvivalRate);
    }

    [Fact]
    public void A_map_with_no_outcome_has_no_survival_rate_rather_than_zero()
    {
        var map = Assert.Single(RaidCoverage.ByMap([Raid("woods", null), Raid("woods", "Closed on restart")]));

        Assert.Null(map.SurvivalRate);
    }

    [Fact]
    public void Extracts_used_count_only_against_the_list_that_raid_offered()
    {
        var map = Assert.Single(RaidCoverage.ByMap(
        [
            Raid("customs", "Survived", offered: ["Crossroads", "RUAF Roadblock"], used: "crossroads"),
            Raid("customs", "Survived", offered: ["Crossroads", "Smuggler's Boat"], used: "Crossroads"),
            // Taken where no list was photographed: cannot be checked, so counted apart.
            Raid("customs", "Survived", used: "ZB-1011"),
            // Named an extract this raid's own list did not offer: not in the numerator.
            Raid("customs", "Survived", offered: ["Old Gas Station"], used: "Trailer Park"),
            Raid("customs", "Died"),
        ]));

        Assert.Equal(["Crossroads", "RUAF Roadblock", "Smuggler's Boat", "Old Gas Station"], map.Offered);
        Assert.Equal(["crossroads"], map.UsedOfOffered);
        Assert.Equal(3, map.RaidsWithOffered);
        Assert.Equal(1, map.UsedUnchecked);
    }

    [Fact]
    public void Maps_are_busiest_first_and_raids_without_a_map_are_left_out()
    {
        var maps = RaidCoverage.ByMap([Raid("woods", null), Raid("customs", null), Raid("Customs", null), Raid(null, null)]);

        Assert.Equal(["customs", "woods"], maps.Select(map => map.MapId));
        Assert.Equal(2, maps[0].Raids);
    }

    [Fact]
    public void The_value_series_is_oldest_first_and_skips_raids_without_a_value()
    {
        var series = RaidCoverage.ValueSeries(
        [
            Raid("customs", null, started: Now.AddDays(-1), value: 300),
            Raid("customs", null, started: Now.AddDays(-3), value: 100),
            Raid("woods", null, started: Now.AddDays(-2)),
            Raid("woods", null, started: Now, value: 0),
        ]);

        Assert.Equal([100L, 300L, 0L], series.Select(point => point.ValueRoubles));
    }

    [Fact]
    public void Offered_extracts_are_the_union_of_a_raids_lists_and_null_when_none_was_photographed()
    {
        Assert.Null(RaidOfferedExtracts.Union([]));
        // A photographed list the scan read nothing from is not a record of an empty offer.
        Assert.Null(RaidOfferedExtracts.Union(["[]", "not json"]));

        var offered = RaidOfferedExtracts.Union([List("Crossroads", "RUAF Roadblock"), List("crossroads", "Trailer Park")]);

        Assert.Equal(["Crossroads", "RUAF Roadblock", "Trailer Park"], offered);
    }

    [Fact]
    public void The_newest_extract_used_wins_and_an_empty_one_clears_it()
    {
        var first = new RaidExtractUsed("Crossroads", Now).ToPayload();
        var second = new RaidExtractUsed("RUAF Roadblock", Now.AddMinutes(1)).ToPayload();
        var cleared = new RaidExtractUsed(null, Now.AddMinutes(2)).ToPayload();

        Assert.Equal("RUAF Roadblock", RaidExtractUsed.Latest([second, first, "{"]));
        Assert.Null(RaidExtractUsed.Latest([first, second, cleared]));
        Assert.Null(RaidExtractUsed.Latest([]));
    }

    [Fact]
    public void Archive_is_the_newest_change_and_restorable()
    {
        var archived = new RaidArchive(true, Now).ToPayload();
        var restored = new RaidArchive(false, Now.AddMinutes(1)).ToPayload();

        Assert.False(RaidArchive.IsArchived([]));
        Assert.True(RaidArchive.IsArchived([archived]));
        Assert.False(RaidArchive.IsArchived([restored, archived]));
    }

    private static string List(params string[] names) =>
        JsonSerializer.Serialize(names.Select(name => new ActiveExtract($"id:{name}", name, new Confidence(0.9), "extract-list")).ToArray());

    private static RaidCoverageInput Raid(
        string? map,
        string? outcome,
        IReadOnlyList<string>? offered = null,
        string? used = null,
        DateTimeOffset? started = null,
        long? value = null) =>
        new(Guid.NewGuid(), map, started ?? Now, outcome, offered, used, value);
}
