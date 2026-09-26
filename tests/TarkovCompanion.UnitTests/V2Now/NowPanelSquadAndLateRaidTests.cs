using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Now;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.UnitTests.V2Now;

/// <summary>[#712 0-5, 0-6] SQUAD's waypoint and ping flash; the late-raid leave line and its margin; the "wrong?" chips.</summary>
public sealed class NowPanelSquadAndLateRaidTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_leave_margin_moves_the_leave_by_time_and_the_line_carries_its_estimate_label()
    {
        using var scope = NowPanelStateTests.English();
        using var zone = LocalTime.UseZone(TimeZoneInfo.Utc);
        NowExit[] exits = [NowPanelStateTests.Exit("ZB-1011", 310, "W", offered: true)];

        // 9 minutes left, 4 minutes' walk: 2 minutes' margin leaves at 14:03, 4 minutes' at 14:01.
        var two = NowPanelState.Project(NowPanelStateTests.InRaid(26, 35), Now, exits);
        var four = NowPanelState.Project(NowPanelStateTests.InRaid(26, 35), Now, exits, leaveMargin: TimeSpan.FromMinutes(4));

        Assert.Equal($"Leave for ZB-1011 by {LocalTime.ShortTime(Now.AddMinutes(3))} (4 min walk, 2 min margin)", two.NowNote);
        Assert.Equal($"Leave for ZB-1011 by {LocalTime.ShortTime(Now.AddMinutes(1))} (4 min walk, 4 min margin)", four.NowNote);
        Assert.Equal(NowTone.Late, four.Tone);
        Assert.True(four.HasLeaveEstimate);
        Assert.Equal(NowText.LeaveEstimate, four.LeaveEstimate);

        // 6 minutes' margin on the same raid is past the time to leave: red.
        var six = NowPanelState.Project(NowPanelStateTests.InRaid(26, 35), Now, exits, leaveMargin: TimeSpan.FromMinutes(6));
        Assert.Equal(NowTone.Urgent, six.Tone);
        Assert.Equal("Leave for ZB-1011 now (4 min walk)", six.NowNote);
        Assert.True(six.HasLeaveEstimate);

        // Not late: no leave line, so no label either.
        var mid = NowPanelState.Project(NowPanelStateTests.InRaid(19, 35), Now, exits);
        Assert.False(mid.HasLeaveEstimate);
        Assert.Empty(mid.LeaveEstimate);
    }

    [Fact]
    public void The_margin_setting_reads_only_whole_minutes_in_range_and_defaults_to_two()
    {
        Assert.Equal(2, LeaveMarginSetting.Parse(null));
        Assert.Equal(2, LeaveMarginSetting.Parse("eleven"));
        Assert.Equal(2, LeaveMarginSetting.Parse("11"));
        Assert.Equal(2, LeaveMarginSetting.Parse("-1"));
        Assert.Equal(0, LeaveMarginSetting.Parse("0"));
        Assert.Equal(5, LeaveMarginSetting.Parse("5"));

        var setting = new LeaveMarginSetting(null);
        Assert.Equal(1, setting.Change(-1));
        Assert.Equal(0, setting.Change(-5));
        Assert.Equal(LeaveMarginSetting.Maximum, setting.Change(20));
    }

    [Fact]
    public void The_panel_uses_the_margin_it_is_given()
    {
        using var scope = NowPanelStateTests.English();
        using var zone = LocalTime.UseZone(TimeZoneInfo.Utc);
        var clock = new FixedClock(Now);
        using var panel = new NowPanelViewModel(null, clock, post: action => action(), tick: false);
        panel.Show(NowPanelStateTests.InRaid(26, 35));
        panel.SetExits([NowPanelStateTests.Exit("ZB-1011", 310, "W", offered: true)]);
        Assert.Contains("2 min margin", panel.State.NowNote, StringComparison.Ordinal);

        panel.LeaveMargin = TimeSpan.FromMinutes(3);

        Assert.Equal($"Leave for ZB-1011 by {LocalTime.ShortTime(Now.AddMinutes(2))} (4 min walk, 3 min margin)", panel.State.NowNote);
    }

    [Fact]
    public void Each_wrong_chip_opens_Corrections_at_its_own_fact_and_the_side_chip_names_the_side()
    {
        using var scope = NowPanelStateTests.English();
        var revealed = new List<NowMoreTopic>();
        using var panel = new NowPanelViewModel(null, post: action => action(), tick: false);
        var host = new NowPanelHost(() => true, revealed.Add) { Panel = panel };
        var situation = NowPanelStateTests.InRaid(19, 35);
        panel.Show(situation with { Side = new(SituationSide.Pmc, Confidence.Certain, SituationSource.GameLog, Now, "log") });

        Assert.Equal(NowText.WrongSide(SituationSide.Pmc), panel.State.YouSide);
        Assert.Contains(RaidText.Pmc, panel.State.YouSide, StringComparison.Ordinal);

        panel.WrongSideCommand.Execute(null);
        host.CloseMoreCommand.Execute(null);
        panel.WrongExitsCommand.Execute(null);

        Assert.Equal([NowMoreTopic.CorrectSide, NowMoreTopic.CorrectExits], revealed);
        Assert.Equal(["corrections"], NowPanelHost.CardsOf(NowMoreTopic.CorrectSide));
        Assert.Equal(["corrections"], NowPanelHost.CardsOf(NowMoreTopic.CorrectExits));
        Assert.True(host.IsMoreOpen);
    }

    [Fact]
    public async Task Waypoint_from_a_row_asks_for_that_squadmate_by_name_and_only_where_a_ping_could_go()
    {
        using var scope = NowPanelStateTests.English();
        var asked = new List<string>();
        using var panel = new NowPanelViewModel(null, post: action => action(), tick: false)
        {
            WaypointMember = (name, _) =>
            {
                asked.Add(name);
                return Task.FromResult(true);
            },
        };
        panel.Show(NowPanelStateTests.InRaid(19, 35));

        await ((AsyncDelegateCommand)panel.SquadRows[0].WaypointCommand).ExecuteAsync();
        // Riley is on another map: nothing of theirs to place a waypoint at.
        await ((AsyncDelegateCommand)panel.SquadRows[1].WaypointCommand).ExecuteAsync();

        Assert.Equal(["Geo"], asked);
        Assert.True(panel.SquadRows[0].IsPulsing);
    }

    [Fact]
    public void A_squad_ping_flashes_that_members_row_and_nobody_elses()
    {
        using var scope = NowPanelStateTests.English();
        using var panel = new NowPanelViewModel(null, post: action => action(), tick: false);
        panel.Show(NowPanelStateTests.InRaid(19, 35));

        Assert.True(panel.FlashMember("Sam"));
        Assert.False(panel.FlashMember("Nobody"));

        Assert.Equal([false, false, true], panel.SquadRows.Select(row => row.IsPulsing));
    }

    [Fact]
    public void Only_a_squadmates_new_ping_counts_never_one_standing_at_the_first_look_or_one_of_ours()
    {
        var watch = new SquadPingWatch();
        var geo = Member("Geo");
        var standing = Ping(1, "Geo");

        Assert.Empty(watch.Arrived(Group([geo], standing)));

        var arrived = watch.Arrived(Group([geo], standing, Ping(2, "Geo"), Ping(3, "Me"), Ping(4, "Geo")), isOwn: id => id == 4);

        Assert.Equal([2L], arrived.Select(ping => ping.Id));
        Assert.Empty(watch.Arrived(Group([geo], Ping(2, "Geo"))));
        Assert.Equal([5L], watch.Arrived(Group([geo], Ping(2, "Geo"), Ping(5, "Geo"))).Select(ping => ping.Id));
    }

    [Theory]
    [InlineData(900, 400, MapEdge.Right)]
    [InlineData(100, 400, MapEdge.Left)]
    [InlineData(500, 10, MapEdge.Top)]
    [InlineData(500, 790, MapEdge.Bottom)]
    // 45 degrees up and to the right on a wide map is nearer the top than the right.
    [InlineData(800, 100, MapEdge.Top)]
    [InlineData(500, 400, MapEdge.None)]
    public void The_edge_lit_is_the_one_nearest_the_pings_direction_on_screen(double x, double y, MapEdge expected) =>
        Assert.Equal(expected, SquadPingWatch.EdgeToward(500, 400, x, y, 1000, 800));

    [Fact]
    public void The_edge_lights_for_two_seconds_in_the_squadmates_colour()
    {
        var clock = new ManualClock(Now);
        using var edge = new SquadEdgePulseViewModel(clock, action => action());

        edge.Pulse(MapEdge.Left, "#4DA3FF");
        Assert.True(edge.IsLeft);
        Assert.Equal("#4DA3FF", edge.Colour);

        clock.Advance(TimeSpan.FromSeconds(1.9));
        Assert.True(edge.IsLeft);
        clock.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(MapEdge.None, edge.Edge);
    }

    private static GroupMemberView Member(string name) =>
        new(name, "customs", RaidLifecycleState.InRaid, "PMC", new(10, 0, 10), 0, TimeSpan.FromSeconds(5), [], []);

    private static GroupPingView Ping(long id, string by) => new(id, by, "customs", 50, 0, 50, null, Now);

    private static GroupSnapshot Group(IReadOnlyList<GroupMemberView> members, params GroupPingView[] pings) =>
        new(true, members, string.Empty, Now) { Pings = pings };

    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A clock whose one-shot timers fire when it is advanced past them.</summary>
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private readonly List<Timer> _timers = [];
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state, _now + dueTime);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var timer in _timers.Where(timer => !timer.Fired && timer.Due <= _now).ToArray())
            {
                timer.Fire();
            }
        }

        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            public DateTimeOffset Due { get; private set; } = due;

            public bool Fired { get; private set; }

            public void Fire()
            {
                Fired = true;
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = clock.GetUtcNow() + dueTime;
                Fired = false;
                return true;
            }

            public void Dispose() => Fired = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
