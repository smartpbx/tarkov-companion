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

    /// <summary>
    /// The right-click that removes a waypoint also removes a ping early: whoever sent it gets
    /// to say the group has moved on, rather than waiting out the forty-five-second fade.
    /// </summary>
    [Fact]
    public void RemoveTakesOffAWaypointOrAPingById()
    {
        var marks = new GroupMarks(new MovableClock(Now));
        var waypoint = marks.AddWaypoint("room", "MaxGooner", "customs", 1, 2, 3, null);
        var ping = marks.AddPing("room", "MaxGooner", "customs", 4, 5, 6, null);

        Assert.True(marks.Remove("room", ping.Id));
        Assert.Empty(marks.Read("room").Pings);
        Assert.Single(marks.Read("room").Waypoints);

        Assert.True(marks.Remove("room", waypoint.Id));
        Assert.Empty(marks.Read("room").Waypoints);
    }

    [Fact]
    public void RemovingAnUnknownIdIsReportedRatherThanIgnored()
    {
        var marks = new GroupMarks(new MovableClock(Now));
        marks.AddWaypoint("room", "MaxGooner", "customs", 1, 2, 3, null);

        Assert.False(marks.Remove("room", 999_999));
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

public sealed class GroupMarkPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "tarkov-marks-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The squad's plan survives the restart the comment always claimed it would.
    /// </summary>
    /// <remarks>
    /// GroupMarks' own remark said "waypoints outlive a restart and positions do not, and that
    /// distinction is deliberate" — written in the same commit as the in-memory dictionary it
    /// sat on, with nothing in the server touching the filesystem. Every merge to main took
    /// the plan with it, roughly twice an hour.
    /// </remarks>
    [Fact]
    public void WaypointsSurviveARestart()
    {
        var first = new GroupMarks(TimeProvider.System, Path.Combine(_directory, "marks.json"));
        first.AddWaypoint("room", "Clay", "bigmap", 1, 2, 3, "Dorms");
        first.AddWaypoint("room", "Geo", "bigmap", 4, 5, 6, "Gas");

        var restarted = new GroupMarks(TimeProvider.System, Path.Combine(_directory, "marks.json"));
        var (waypoints, _) = restarted.Read("room");

        Assert.Equal(2, waypoints.Count);
        Assert.Equal(["Dorms", "Gas"], waypoints.Select(waypoint => waypoint.Label));
    }

    /// <summary>
    /// A restored waypoint's id is never handed out again.
    /// </summary>
    /// <remarks>
    /// Without seeding the counter from the largest restored id, numbering would start at one
    /// again and the first new waypoint would collide with a restored one — so completing
    /// either would complete the wrong mark, which is worse than losing the plan outright.
    /// </remarks>
    [Fact]
    public void TheNextIdIsSeededAboveWhatWasRestored()
    {
        var path = Path.Combine(_directory, "marks.json");
        var first = new GroupMarks(TimeProvider.System, path);
        first.AddWaypoint("room", "Clay", "bigmap", 1, 2, 3, "one");
        var second = first.AddWaypoint("room", "Clay", "bigmap", 1, 2, 3, "two");

        var restarted = new GroupMarks(TimeProvider.System, path);
        var fresh = restarted.AddWaypoint("room", "Clay", "bigmap", 9, 9, 9, "three");

        Assert.True(fresh.Id > second.Id, $"a new id {fresh.Id} must not collide with the restored {second.Id}");
        Assert.Equal(3, restarted.Read("room").Waypoints.Count);
    }

    /// <summary>Pings are not restored, because one that survived would be claiming "now".</summary>
    [Fact]
    public void PingsAreNotKept()
    {
        var path = Path.Combine(_directory, "marks.json");
        var first = new GroupMarks(TimeProvider.System, path);
        first.AddWaypoint("room", "Clay", "bigmap", 1, 2, 3, "Dorms");
        first.AddPing("room", "Clay", "bigmap", 7, 8, 9, "here");

        var restarted = new GroupMarks(TimeProvider.System, path);
        var (waypoints, pings) = restarted.Read("room");

        Assert.Single(waypoints);
        Assert.Empty(pings);
    }

    /// <summary>Clearing a plan clears it on disk too, not just in memory.</summary>
    [Fact]
    public void ClearingIsPersistedAsWell()
    {
        var path = Path.Combine(_directory, "marks.json");
        var first = new GroupMarks(TimeProvider.System, path);
        first.AddWaypoint("room", "Clay", "bigmap", 1, 2, 3, "Dorms");
        first.Clear("room", mapId: null, reachedOnly: false);

        Assert.Empty(new GroupMarks(TimeProvider.System, path).Read("room").Waypoints);
    }

    /// <summary>An unreadable file costs one plan, not a server that will not start.</summary>
    [Fact]
    public void AnUnreadableStoreIsNotFatal()
    {
        var path = Path.Combine(_directory, "marks.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{ this is not json");

        var marks = new GroupMarks(TimeProvider.System, path);

        Assert.Empty(marks.Read("room").Waypoints);
        Assert.Equal("Dorms", marks.AddWaypoint("room", "Clay", "bigmap", 1, 2, 3, "Dorms").Label);
    }

    /// <summary>With nowhere to write, it behaves exactly as it did before.</summary>
    [Fact]
    public void WithoutAStorePathNothingIsWritten()
    {
        var marks = new GroupMarks(TimeProvider.System);
        marks.AddWaypoint("room", "Clay", "bigmap", 1, 2, 3, "Dorms");

        Assert.Single(marks.Read("room").Waypoints);
        Assert.False(Directory.Exists(_directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
