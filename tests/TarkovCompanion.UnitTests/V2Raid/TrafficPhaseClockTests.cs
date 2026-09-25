using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Strategy;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// #889: the traffic card's phase comes from the same clock the strip shows, and says so only when
/// a clock was used.
/// </summary>
public sealed class TrafficPhaseClockTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_clock_read_or_set_to_eight_minutes_left_is_late_however_recent_the_start()
    {
        var raid = InRaid(started: Now.AddMinutes(-5)) with { RaidClock = TimeSpan.FromMinutes(8), RaidClockReadUtc = Now };

        Assert.Equal(RaidPhase.Late, RaidCockpitViewModel.ClockPhase(raid, "customs", clockSetByHand: false, Now));
    }

    [Fact]
    public void A_read_clock_keeps_running_after_it_was_read()
    {
        var raid = InRaid(started: null) with { RaidClock = TimeSpan.FromMinutes(30), RaidClockReadUtc = Now.AddMinutes(-5) };

        Assert.Equal(RaidPhase.Mid, RaidCockpitViewModel.ClockPhase(raid, "customs", clockSetByHand: false, Now));
    }

    [Fact]
    public void Without_a_read_clock_the_start_counts()
    {
        Assert.Equal(RaidPhase.Early, RaidCockpitViewModel.ClockPhase(InRaid(Now.AddMinutes(-5)), "customs", false, Now));
        Assert.Equal(RaidPhase.Mid, RaidCockpitViewModel.ClockPhase(InRaid(Now.AddMinutes(-20)), "customs", false, Now));
    }

    [Fact]
    public void A_scavs_join_time_is_not_a_raid_clock_unless_set_by_hand()
    {
        var scav = InRaid(Now.AddMinutes(-2)) with { Side = "scav" };

        Assert.Null(RaidCockpitViewModel.ClockPhase(scav, "customs", clockSetByHand: false, Now));
        Assert.Equal(RaidPhase.Early, RaidCockpitViewModel.ClockPhase(scav, "customs", clockSetByHand: true, Now));
    }

    [Fact]
    public void Nothing_to_go_on_or_another_map_is_no_clock_phase()
    {
        Assert.Null(RaidCockpitViewModel.ClockPhase(InRaid(started: null), "customs", false, Now));
        Assert.Null(RaidCockpitViewModel.ClockPhase(InRaid(Now.AddMinutes(-20)), "woods", false, Now));
    }

    private static RaidSnapshot InRaid(DateTimeOffset? started) => new(
        Guid.NewGuid(),
        RaidLifecycleState.InRaid,
        "customs",
        started,
        Now,
        Confidence.Certain,
        null,
        [],
        false);
}
