using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Waypoints are a plan and stay; pings say "look here" and fade.
/// </summary>
public sealed class GroupMarksTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AWaypointStaysUntilSomebodyClearsIt()
    {
        var clock = new MovableClock(Now);
        var marks = new GroupMarks(clock);
        marks.AddWaypoint("room", "MaxGooner", "customs", 1, 2, 3, "Dorms");

        clock.Advance(TimeSpan.FromHours(6));

        Assert.Single(marks.Read("room").Waypoints);
    }

    [Fact]
    public void APingStopsMeaningNow()
    {
        // The whole reason for having both. A plan that quietly became forty stale "look here"
        // marks would be worse than either one on its own.
        var clock = new MovableClock(Now);
        var marks = new GroupMarks(clock);
        marks.AddPing("room", "MaxGooner", "customs", 1, 2, 3, null);

        Assert.Single(marks.Read("room").Pings);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(marks.Read("room").Pings);
    }

    [Fact]
    public void ReachingOneRecordsWhoGotThere()
    {
        var marks = new GroupMarks(new MovableClock(Now));
        var waypoint = marks.AddWaypoint("room", "MaxGooner", "customs", 1, 2, 3, null);

        Assert.True(marks.Complete("room", waypoint.Id, "Geo"));

        var stored = Assert.Single(marks.Read("room").Waypoints);
        Assert.Equal("Geo", stored.CompletedBy);
        Assert.NotNull(stored.CompletedUtc);
        // Twice is not an error worth an exception, but it must not rewrite who arrived first.
        Assert.False(marks.Complete("room", waypoint.Id, "Someone else"));
    }

    [Fact]
    public void GroupsDoNotSeeEachOthersMarks()
    {
        var marks = new GroupMarks(new MovableClock(Now));
        marks.AddWaypoint("one", "A", "customs", 1, 2, 3, null);

        Assert.Empty(marks.Read("two").Waypoints);
    }

    [Fact]
    public void ClearingOnlyTheReachedOnesLeavesThePlan()
    {
        var marks = new GroupMarks(new MovableClock(Now));
        var done = marks.AddWaypoint("room", "A", "customs", 1, 2, 3, null);
        marks.AddWaypoint("room", "A", "customs", 4, 5, 6, null);
        marks.Complete("room", done.Id, "A");

        Assert.Equal(1, marks.Clear("room", "customs", reachedOnly: true));
        Assert.Single(marks.Read("room").Waypoints);
    }

    [Fact]
    public void ClearingOneMapLeavesAnother()
    {
        var marks = new GroupMarks(new MovableClock(Now));
        marks.AddWaypoint("room", "A", "customs", 1, 2, 3, null);
        marks.AddWaypoint("room", "A", "streets-of-tarkov", 4, 5, 6, null);

        marks.Clear("room", "customs", reachedOnly: false);

        Assert.Equal("streets-of-tarkov", Assert.Single(marks.Read("room").Waypoints).MapId);
    }

    [Fact]
    public void AGroupCannotDrownItsOwnMapInMarks()
    {
        // Somebody leaning on a mouse button should lose their oldest plan, not be refused and
        // not fill the server.
        var marks = new GroupMarks(new MovableClock(Now));
        for (var i = 0; i < 200; i++)
        {
            marks.AddWaypoint("room", "A", "customs", i, 0, 0, null);
        }

        var waypoints = marks.Read("room").Waypoints;
        Assert.True(waypoints.Count <= 60);
        Assert.Equal(199, waypoints[^1].X);
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
