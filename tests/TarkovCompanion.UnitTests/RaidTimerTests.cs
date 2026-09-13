using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// How long is left in a raid, and how that is known.
/// </summary>
/// <remarks>
/// Two sources and they are not equal claims. Counting from the game's confirmation against
/// the map's length works from the moment a raid begins and knows nothing when the companion
/// was started mid-raid. The timer on the extract list is the game's own number.
/// </remarks>
public sealed class RaidTimerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 4, 0, 0, TimeSpan.Zero);

    /// <summary>Verbatim from a real 3840x1080 extract-list screenshot.</summary>
    [Fact]
    public void TheClockIsReadOffTheExtractList() =>
        Assert.Equal(
            TimeSpan.FromMinutes(28) + TimeSpan.FromSeconds(10),
            RaidTimer.Read(["Find an extraction point", "0:28:10", "EXIT@1", "Ventilation Shaft"]));

    /// <summary>
    /// The raid clock is the longer of the two, where an exit has its own countdown.
    /// </summary>
    [Fact]
    public void TheLongerClockIsTheRaidOne() =>
        Assert.Equal(TimeSpan.FromMinutes(28), RaidTimer.Read(["0:00:45", "0:28:00"]));

    /// <summary>No map runs longer than an hour, so a bigger number is something else.</summary>
    [Theory]
    [InlineData("2:15:00")]
    [InlineData("99:99:99")]
    [InlineData("0:00:00")]
    public void SomethingShapedLikeAClockButNotOneIsIgnored(string text) =>
        Assert.Null(RaidTimer.Read([text]));

    [Theory]
    [InlineData("112, -44")]
    [InlineData("P 7 225 588")]
    [InlineData("")]
    public void OrdinaryTextIsNotAClock(string text) =>
        Assert.Null(RaidTimer.Read([text]));

    [Fact]
    public void NothingReadIsNoClock() => Assert.Null(RaidTimer.Read([]));

    /// <summary>A reading wins, and keeps counting down from when it was read.</summary>
    [Fact]
    public void AReadingBeatsACountAndKeepsTicking()
    {
        var remaining = RaidTimer.Resolve(
            (TimeSpan.FromMinutes(28), Now.AddMinutes(-3)),
            startedUtc: Now.AddMinutes(-30),
            length: TimeSpan.FromMinutes(40),
            Now);

        Assert.Equal(RaidTimeBasis.Observed, remaining.Basis);
        Assert.Equal(TimeSpan.FromMinutes(25), remaining.Remaining);
        Assert.Equal("0:25:00", remaining.Display);
    }

    [Fact]
    public void WithoutAReadingItIsCountedFromTheStart()
    {
        var remaining = RaidTimer.Resolve(
            observed: null,
            startedUtc: Now.AddMinutes(-12),
            length: TimeSpan.FromMinutes(40),
            Now);

        Assert.Equal(RaidTimeBasis.Counted, remaining.Basis);
        Assert.Equal(TimeSpan.FromMinutes(28), remaining.Remaining);
        Assert.Equal("Counted from the start", remaining.Detail);
    }

    /// <summary>
    /// A companion started mid-raid knows the raid and not when it began.
    /// </summary>
    [Fact]
    public void WithoutAStartThereIsNothingToSay()
    {
        var remaining = RaidTimer.Resolve(null, null, TimeSpan.FromMinutes(40), Now);

        Assert.Equal(RaidTimeBasis.Unknown, remaining.Basis);
        Assert.Null(remaining.Remaining);
        Assert.Equal("Unknown", remaining.Display);
    }

    [Fact]
    public void AMapWithNoStatedLengthCannotBeCounted() =>
        Assert.Equal(
            RaidTimeBasis.Unknown,
            RaidTimer.Resolve(null, Now.AddMinutes(-5), null, Now).Basis);

    /// <summary>A raid that has run over reads zero rather than counting backwards.</summary>
    [Fact]
    public void TimeUpIsZero()
    {
        var remaining = RaidTimer.Resolve(null, Now.AddMinutes(-50), TimeSpan.FromMinutes(40), Now);

        Assert.Equal(TimeSpan.Zero, remaining.Remaining);
        Assert.Equal("0:00:00", remaining.Display);
    }

    [Fact]
    public void AnHourIsShownWithItsHour() =>
        Assert.Equal(
            "1:00:00",
            RaidTimer.Resolve(null, Now, TimeSpan.FromHours(1), Now).Display);

    /// <summary>
    /// An exit whose timer is not decided says so, and is not read as a time.
    /// </summary>
    /// <remarks>
    /// The game draws "??:??:??" and OCR renders the question marks as digits — "22:22:22" is
    /// what a real screen produced. It was rejected only because it exceeds the one-hour cap,
    /// which is luck rather than a rule: the same marker read with a leading zero gives
    /// "0:22:22", a perfectly plausible raid clock that would have been taken as one and shown
    /// to the player as the game's own number.
    /// </remarks>
    [Theory]
    [InlineData("EXFIL 3: ZB-014 ??:??:??")]
    [InlineData("EXFIL 3: ZB-014 ?:??:??")]
    [InlineData("EXFIL 3: ZB-014 0:2?:22")]
    [InlineData("EXFIL 3: ZB-014 22:22:22")]
    public void TheUnknownTimerMarkerIsNotReadAsAClock(string line) =>
        Assert.Null(RaidTimer.Read([line]));

    /// <summary>
    /// A repeated-digit clock is still a clock, and this is the test that says so.
    /// </summary>
    /// <remarks>
    /// The first attempt at the marker check rejected any reading whose digits were all the
    /// same character, on the theory that "0:22:22" was "??:??:??" read with a leading zero.
    /// It is also twenty-two minutes and twenty-two seconds, which every raid passes through.
    /// Throwing away a real reading once a raid, to catch a marker the one-hour cap already
    /// rejects at "22:22:22", is a bad trade.
    /// </remarks>
    [Theory]
    [InlineData("0:22:22", 22, 22)]
    [InlineData("0:11:11", 11, 11)]
    [InlineData("0:28:10", 28, 10)]
    public void ARepeatedDigitClockIsStillRead(string line, int minutes, int seconds) =>
        Assert.Equal(new TimeSpan(0, minutes, seconds), RaidTimer.Read([line]));

    /// <summary>
    /// One undecided exit does not cost the raid clock that is on the same screen.
    /// </summary>
    /// <remarks>
    /// The marker check is per line, not per screen, because the raid timer and an exit's
    /// countdown are different rows of the same panel.
    /// </remarks>
    [Fact]
    public void AnUndecidedExitDoesNotHideTheRaidClock() =>
        Assert.Equal(
            new TimeSpan(0, 28, 10),
            RaidTimer.Read(["0:28:10", "EXFIL 3: ZB-014 ??:??:??"]));
}
