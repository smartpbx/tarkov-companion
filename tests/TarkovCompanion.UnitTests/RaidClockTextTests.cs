using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.UnitTests;

/// <summary>The one raid clock every surface shows.</summary>
public sealed class RaidClockTextTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_counted_raid_shows_time_left_not_time_elapsed()
    {
        // The reported render: 14:03 into a 35-minute Customs raid.
        var now = Start + new TimeSpan(0, 14, 3);
        var remaining = RaidTimer.Resolve(null, Start, TimeSpan.FromMinutes(35), now);

        Assert.Equal("20:57 left", remaining.ClockText(Start, now));
    }

    [Fact]
    public void A_clock_read_from_a_screenshot_wins_and_keeps_counting_down()
    {
        var read = Start + TimeSpan.FromMinutes(5);
        var now = read + TimeSpan.FromSeconds(70);
        var remaining = RaidTimer.Resolve((new TimeSpan(0, 28, 10), read), Start, TimeSpan.FromMinutes(35), now);

        Assert.Equal(RaidTimeBasis.Observed, remaining.Basis);
        Assert.Equal("27:00 left", remaining.ClockText(Start, now));
    }

    [Fact]
    public void A_raid_of_unknown_length_counts_up_and_says_so()
    {
        var now = Start + new TimeSpan(0, 9, 5);
        var remaining = RaidTimer.Resolve(null, Start, length: null, now);

        Assert.Equal("09:05 elapsed", remaining.ClockText(Start, now));
    }

    [Fact]
    public void Nothing_known_shows_no_clock_and_an_overrun_stops_at_zero()
    {
        var now = Start + TimeSpan.FromMinutes(50);

        Assert.Equal(string.Empty, RaidTimeRemaining.Unknown.ClockText(null, now));
        Assert.Equal("00:00 left", RaidTimer.Resolve(null, Start, TimeSpan.FromMinutes(35), now).ClockText(Start, now));
    }
}
