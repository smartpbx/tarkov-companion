using TarkovCompanion.App.ViewModels.V2.Raid;

namespace TarkovCompanion.UnitTests.V2Raid;

public sealed class RaidExtractClockTextTests
{
    [Theory]
    [InlineData("23:10 left", "counted from start", "23:10 left · counted from start")]
    [InlineData("23:10 left", "from screenshot", "23:10 left · from screenshot")]
    [InlineData("23:10 left", "set by hand", "23:10 left · set by hand")]
    public void Known_time_names_its_basis(string clock, string detail, string expected) =>
        Assert.Equal(expected, RaidCockpitViewModel.DescribeExtractClock(true, clock, countsDown: true, detail));

    [Fact]
    public void Unknown_time_says_how_to_supply_it() =>
        Assert.Equal(
            "Time left unknown · open extracts or set by hand",
            RaidCockpitViewModel.DescribeExtractClock(true, "14:03 elapsed", countsDown: false, "open extracts or set by hand"));

    [Fact]
    public void No_raid_has_no_extract_clock_line() =>
        Assert.Empty(RaidCockpitViewModel.DescribeExtractClock(false, string.Empty, countsDown: false, "open extracts or set by hand"));
}
