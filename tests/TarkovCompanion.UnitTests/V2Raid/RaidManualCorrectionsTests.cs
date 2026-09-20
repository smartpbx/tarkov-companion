using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// #286: what a player sets by hand wins over what was read, can be given back, and never
/// outlives the raid it was about.
/// </summary>
public sealed class RaidManualCorrectionsTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);

    private static RaidSnapshot InRaid(Guid id, string? side = "PMC") => new(
        id,
        RaidLifecycleState.InRaid,
        "customs",
        Start,
        Start,
        Confidence.Certain,
        null,
        [new ActiveExtract("crossroads", "Crossroads", Confidence.Certain, "screenshot")],
        false)
    {
        Side = side,
        SideBasis = "log",
        RaidClock = TimeSpan.FromMinutes(30),
        RaidClockReadUtc = Start.AddMinutes(5),
    };

    [Fact]
    public void Nothing_corrected_returns_the_automatic_snapshot_itself()
    {
        var automatic = InRaid(Guid.NewGuid());

        Assert.Same(automatic, new RaidManualCorrections().Apply(automatic));
    }

    [Fact]
    public void A_manual_side_overrides_the_side_that_was_read_and_says_so()
    {
        var corrections = new RaidManualCorrections();
        var automatic = InRaid(Guid.NewGuid());
        corrections.Apply(automatic);

        corrections.SetSide("scav");
        var shown = corrections.Apply(automatic);

        Assert.Equal("Scav", shown.Side);
        Assert.Equal(RaidManualCorrections.ManualBasis, shown.SideBasis);
        Assert.True(corrections.IsManual(RaidCorrectionField.Side));
        Assert.Equal("PMC", automatic.Side);
        // The length the one raid clock counts from follows the corrected side.
        Assert.Equal(
            TimeSpan.FromMinutes(25),
            RaidTimer.LengthFor(shown.Side, TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(25)));
    }

    [Fact]
    public void A_manual_time_left_is_what_the_one_raid_clock_resolves()
    {
        var corrections = new RaidManualCorrections();
        var automatic = InRaid(Guid.NewGuid());
        corrections.Apply(automatic);
        var now = Start.AddMinutes(10);

        corrections.SetTimeLeft(TimeSpan.FromMinutes(12), now);
        var shown = corrections.Apply(automatic);
        var remaining = RaidTimer.Resolve(
            (shown.RaidClock!.Value, shown.RaidClockReadUtc!.Value), shown.StartedUtc, TimeSpan.FromMinutes(40), now.AddMinutes(2));

        Assert.Equal(TimeSpan.FromMinutes(10), remaining.Remaining);
        Assert.True(corrections.IsManual(RaidCorrectionField.Clock));
    }

    [Fact]
    public void A_manual_start_takes_the_automatic_reading_away_so_the_count_is_from_it()
    {
        var corrections = new RaidManualCorrections();
        var automatic = InRaid(Guid.NewGuid());
        corrections.Apply(automatic);

        corrections.SetStarted(Start.AddMinutes(3));
        var shown = corrections.Apply(automatic);

        Assert.Equal(Start.AddMinutes(3), shown.StartedUtc);
        Assert.Null(shown.RaidClock);
        Assert.Null(shown.RaidClockReadUtc);
    }

    [Fact]
    public void A_manual_extract_list_replaces_the_scanned_one()
    {
        var corrections = new RaidManualCorrections();
        var automatic = InRaid(Guid.NewGuid());
        corrections.Apply(automatic);

        corrections.SetExtracts(["ZB-1011", "zb-1011", " Dorms V-Ex "]);
        var shown = corrections.Apply(automatic);

        Assert.Equal(["ZB-1011", "Dorms V-Ex"], shown.ActiveExtracts.Select(extract => extract.Name));
        Assert.All(shown.ActiveExtracts, extract => Assert.Equal(RaidManualCorrections.ManualBasis, extract.Source));
        Assert.Single(automatic.ActiveExtracts);
    }

    [Fact]
    public void An_empty_manual_extract_list_is_still_manual()
    {
        var corrections = new RaidManualCorrections();
        var automatic = InRaid(Guid.NewGuid());
        corrections.Apply(automatic);

        corrections.SetExtracts([]);

        Assert.Empty(corrections.Apply(automatic).ActiveExtracts);
        Assert.True(corrections.IsManual(RaidCorrectionField.Extracts));
    }

    [Fact]
    public void Return_to_automatic_gives_back_what_was_read_one_value_at_a_time()
    {
        var corrections = new RaidManualCorrections();
        var automatic = InRaid(Guid.NewGuid());
        corrections.Apply(automatic);
        corrections.SetSide("Scav");
        corrections.SetTimeLeft(TimeSpan.FromMinutes(12), Start.AddMinutes(10));

        corrections.ReturnToAutomatic(RaidCorrectionField.Side);
        var shown = corrections.Apply(automatic);

        Assert.Equal("PMC", shown.Side);
        Assert.Equal("log", shown.SideBasis);
        Assert.False(corrections.IsManual(RaidCorrectionField.Side));
        Assert.Equal(TimeSpan.FromMinutes(12), shown.RaidClock);

        corrections.ReturnToAutomatic(RaidCorrectionField.Clock);

        Assert.Same(automatic, corrections.Apply(automatic));
    }

    [Fact]
    public void A_new_raid_clears_every_manual_value_and_says_it_changed()
    {
        var corrections = new RaidManualCorrections();
        var first = InRaid(Guid.NewGuid());
        corrections.Apply(first);
        corrections.SetSide("Scav");
        corrections.SetTimeLeft(TimeSpan.FromMinutes(12), Start);
        corrections.SetExtracts(["ZB-1011"]);
        var told = 0;
        corrections.Changed += (_, _) => told++;

        var next = InRaid(Guid.NewGuid());
        var shown = corrections.Apply(next);

        Assert.Same(next, shown);
        Assert.Equal(1, told);
        Assert.False(corrections.IsManual(RaidCorrectionField.Side));
        Assert.False(corrections.IsManual(RaidCorrectionField.Clock));
        Assert.False(corrections.IsManual(RaidCorrectionField.Extracts));
        // And the raid that was corrected does not get them back by being shown again.
        Assert.Same(first, corrections.Apply(first));
    }

    [Fact]
    public void Nothing_is_laid_over_a_snapshot_that_is_not_in_a_raid()
    {
        var corrections = new RaidManualCorrections();
        var id = Guid.NewGuid();
        corrections.Apply(InRaid(id));
        corrections.SetSide("Scav");
        var after = InRaid(id) with { State = RaidLifecycleState.PostRaid };

        Assert.Same(after, corrections.Apply(after));
    }

    [Theory]
    [InlineData("23:10", 0, 23, 10)]
    [InlineData(" 7:05 ", 0, 7, 5)]
    [InlineData("23", 0, 23, 0)]
    [InlineData("1:02:30", 1, 2, 30)]
    public void Time_left_is_read_the_way_the_game_writes_it(string text, int hours, int minutes, int seconds)
    {
        Assert.True(RaidManualCorrections.TryParseTimeLeft(text, out var left));
        Assert.Equal(new TimeSpan(hours, minutes, seconds), left);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("12:75")]
    [InlineData("-5")]
    [InlineData("1:2:3:4")]
    [InlineData("500")]
    public void Time_left_that_is_not_a_time_is_refused(string text) =>
        Assert.False(RaidManualCorrections.TryParseTimeLeft(text, out _));

    [Fact]
    public void A_side_that_is_neither_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new RaidManualCorrections().SetSide("boss"));
    }
}
