using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The countdown moving on its own.
/// </summary>
/// <remarks>
/// Nothing in this application ticks. Every readout is rebuilt when the runtime store raises
/// Changed — a log line, a screenshot — and between those it can be minutes. So "Time left" sat
/// frozen at whatever it read when the last event landed.
///
/// That was survivable while the only clock was twenty pixels deep in the Raid sidebar. With it
/// at reading size along the top of the window it is not: a frozen countdown cannot be told
/// apart from a working one, and somebody plans an extract around it.
/// </remarks>
public sealed class RaidClockTickTests
{
    private static readonly DateTimeOffset Started = DateTimeOffset.Parse("2026-09-13T20:00:00Z");

    [Fact]
    public void The_same_raid_read_a_minute_later_has_a_minute_less_left()
    {
        // The whole of what the tick buys, at the level that decides it. If this were false the
        // timer could tick every second and still show the same number.
        var first = RaidTimer.Resolve(null, Started, TimeSpan.FromMinutes(40), Started.AddMinutes(10));
        var later = RaidTimer.Resolve(null, Started, TimeSpan.FromMinutes(40), Started.AddMinutes(11));

        Assert.NotEqual(first.Display, later.Display);
    }

    [Fact]
    public void A_clock_read_off_the_extract_screen_ages_forward_too()
    {
        // The reading is a fact about the moment the screenshot was taken, not about now. Three
        // minutes after it, the raid is three minutes further on, and a readout still showing
        // what the screen said is wrong by exactly the time somebody spent looking at it.
        var read = Started.AddMinutes(5);
        var clock = TimeSpan.FromMinutes(30);

        var atTheTime = RaidTimer.Resolve((clock, read), Started, TimeSpan.FromMinutes(40), read);
        var threeMinutesOn = RaidTimer.Resolve((clock, read), Started, TimeSpan.FromMinutes(40), read.AddMinutes(3));

        Assert.NotEqual(atTheTime.Display, threeMinutesOn.Display);
    }

    [Fact]
    public void A_raid_that_has_run_out_does_not_count_into_the_negative()
    {
        var over = RaidTimer.Resolve(null, Started, TimeSpan.FromMinutes(40), Started.AddMinutes(200));

        Assert.DoesNotContain("-", over.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_length_nothing_is_counted_down_from_a_number_that_was_invented()
    {
        var unknown = RaidTimer.Resolve(null, Started, length: null, Started.AddMinutes(10));

        Assert.False(string.IsNullOrWhiteSpace(RaidText.ClockBasis(unknown.Basis)));
    }
}
