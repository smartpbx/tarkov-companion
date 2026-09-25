using TarkovCompanion.App.ViewModels.V2.Raid;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// A squadmate's position age is written in coarse steps. Any difference in a scene object makes
/// the plan recreate every marker on it, and the exchange is five seconds apart, so text that
/// moved every second changed for every stationary teammate at every exchange.
/// </summary>
public sealed class RaidCockpitAgeTextTests
{
    [Theory]
    [InlineData(0, "From a screenshot just now")]
    [InlineData(14, "From a screenshot just now")]
    [InlineData(15, "From a screenshot 15 s ago")]
    [InlineData(29, "From a screenshot 15 s ago")]
    [InlineData(30, "From a screenshot 30 s ago")]
    [InlineData(59, "From a screenshot 45 s ago")]
    [InlineData(60, "From a screenshot 1 min ago")]
    [InlineData(185, "From a screenshot 3 min ago")]
    public void Age_is_written_in_steps(int seconds, string expected) =>
        Assert.Equal(expected, RaidCockpitViewModel.Describe(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Three_exchanges_in_a_row_for_a_teammate_who_has_not_moved_read_the_same()
    {
        var texts = new[] { 31, 36, 41 }.Select(seconds => RaidCockpitViewModel.Describe(TimeSpan.FromSeconds(seconds))).Distinct();

        Assert.Single(texts);
    }

    [Fact]
    public void An_unknown_age_says_so()
    {
        Assert.Equal("Position unknown", RaidCockpitViewModel.Describe(TimeSpan.MaxValue));
    }
}
