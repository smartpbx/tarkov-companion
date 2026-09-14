using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The two bars the game draws in the corner, which were read and thrown away.
/// </summary>
/// <remarks>
/// The recogniser has read them on every frame since it was written, turned the result into one
/// evidence string, and dropped it — <c>HudBar.Fraction</c> had no production caller anywhere.
/// So a player who photographed themselves at a quarter of something had handed over the number
/// and been told nothing.
///
/// Named by colour throughout. What each bar measures has not been established, the code that
/// reads them says so, and calling one "stamina" would be a guess printed as a fact.
/// </remarks>
public sealed class RaidHudReadingTests
{
    [Fact]
    public void A_bar_is_measured_against_the_longest_it_has_been()
    {
        Assert.Equal(0.25, new RaidHudBar("Blue", 40, 160).Fraction!.Value, 6);
    }

    [Fact]
    public void The_first_reading_of_a_bar_has_nothing_to_compare_against()
    {
        // Null rather than one. A companion that answered "full" the first time it saw a bar
        // would be right only by accident, and wrong in the one case that matters: the first
        // screenshot somebody takes after running themselves empty.
        Assert.Null(new RaidHudBar("Blue", 40, 40).Fraction);
    }

    [Fact]
    public void A_bar_longer_than_anything_seen_before_is_the_new_longest_rather_than_over_full()
    {
        // Which is what the caller does with it. Reporting 110% would be reporting that
        // somebody has more of something than the thing can hold.
        Assert.Null(new RaidHudBar("Blue", 200, 160).Fraction);
    }

    [Fact]
    public void A_bar_with_no_length_yet_is_not_divided_by()
    {
        Assert.Null(new RaidHudBar("Green", 0, 0).Fraction);
    }

    [Fact]
    public void An_empty_bar_against_a_known_longest_is_a_real_zero()
    {
        // The one case where zero is an answer rather than an absence: the bar was found, it
        // was measured, and it was empty.
        Assert.Equal(0, new RaidHudBar("Green", 0, 160).Fraction!.Value, 6);
    }

    [Fact]
    public void A_frame_with_no_display_is_recorded_as_one()
    {
        // The game fades its display out of roughly one screenshot in eight. "The display was
        // not in that frame" is a different statement from "the bar was empty", and showing
        // the second when the first is true is how a companion tells somebody they are dying
        // when they are not.
        var faded = new RaidHudReading(
            false,
            "The game had faded its display out of this screenshot.",
            DateTimeOffset.Parse("2026-09-14T01:00:00Z"),
            []);

        Assert.False(faded.IsPresent);
        Assert.Empty(faded.Bars);
    }

    [Fact]
    public void A_reading_carries_when_it_was_read()
    {
        // Like the raid clock and for the same reason. Bars move continuously, so a reading
        // four minutes old is a claim about four minutes ago and has to be shown as one.
        var read = DateTimeOffset.Parse("2026-09-14T01:00:00Z");

        Assert.Equal(read, new RaidHudReading(true, "Two bars.", read, []).ReadUtc);
    }
}
