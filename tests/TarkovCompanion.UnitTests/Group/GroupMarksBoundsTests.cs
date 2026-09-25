using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests.Group;

/// <summary>
/// [#886] The relay's marks are bounded, swept, saved in order, and never renumbered onto
/// somebody else's mark by a restart.
/// </summary>
public sealed class GroupMarksBoundsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 20, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "tarkov-marks-886-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Against an open relay every new key used to leave a room behind for good.
    /// </summary>
    [Fact]
    public void New_rooms_are_refused_once_the_relay_holds_marks_for_as_many_as_it_will()
    {
        var marks = new GroupMarks(new MovableClock(Now));
        for (var index = 0; index < GroupMarks.MaximumRooms; index++)
        {
            Assert.NotNull(marks.AddWaypoint($"room-{index}", "Clay", "bigmap", 1, 2, 3, null));
        }

        Assert.Null(marks.AddWaypoint("one-too-many", "Clay", "bigmap", 1, 2, 3, null));
        Assert.Null(marks.AddPing("one-too-many", "Clay", "bigmap", 1, 2, 3, null));
        Assert.Equal(GroupMarks.MaximumRooms, marks.RoomCount);

        // A room that already has marks is never refused.
        Assert.NotNull(marks.AddWaypoint("room-0", "Clay", "bigmap", 4, 5, 6, null));
    }

    [Fact]
    public void The_sweep_drops_old_waypoints_expired_pings_and_the_rooms_they_leave_empty()
    {
        var clock = new MovableClock(Now);
        var marks = new GroupMarks(clock);
        marks.AddWaypoint("old-plan", "Clay", "bigmap", 1, 2, 3, "Dorms");
        marks.AddPing("pinged", "Clay", "bigmap", 1, 2, 3, null);
        clock.Advance(TimeSpan.FromDays(6));
        marks.AddWaypoint("fresh-plan", "Geo", "bigmap", 1, 2, 3, "Gas");
        clock.Advance(TimeSpan.FromDays(1.5));

        marks.Sweep();

        Assert.Empty(marks.Read("old-plan").Waypoints);
        Assert.Single(marks.Read("fresh-plan").Waypoints);
        Assert.Equal(1, marks.RoomCount);

        // A swept room comes back as soon as somebody marks in it again.
        Assert.NotNull(marks.AddWaypoint("old-plan", "Clay", "bigmap", 1, 2, 3, "Again"));
        Assert.Single(marks.Read("old-plan").Waypoints);
    }

    [Fact]
    public void Removing_a_room_forgets_its_marks_on_disk_as_well()
    {
        var path = Path.Combine(_directory, "marks.json");
        var marks = new GroupMarks(new MovableClock(Now), path);
        marks.AddWaypoint("removed", "Clay", "bigmap", 1, 2, 3, "Dorms");
        marks.AddWaypoint("kept", "Geo", "bigmap", 1, 2, 3, "Gas");

        marks.ClearRoom("removed");

        Assert.Empty(marks.Read("removed").Waypoints);
        var restarted = new GroupMarks(new MovableClock(Now), path);
        Assert.Empty(restarted.Read("removed").Waypoints);
        Assert.Single(restarted.Read("kept").Waypoints);
    }

    /// <summary>
    /// Two squadmates dropping waypoints together could have the older snapshot written last.
    /// </summary>
    /// <remarks>
    /// Run many times over because the window is narrow; on the old Save a run of this loses a
    /// waypoint from the file most times it is tried.
    /// </remarks>
    [Fact]
    public async Task Waypoints_dropped_at_once_all_reach_the_file()
    {
        const int Threads = 8;
        const int Each = 25;
        for (var round = 0; round < 10; round++)
        {
            var path = Path.Combine(_directory, $"marks-{round}.json");
            var marks = new GroupMarks(new MovableClock(Now), path);
            using var start = new Barrier(Threads);
            var workers = Enumerable.Range(0, Threads)
                .Select(thread => Task.Factory.StartNew(
                    () =>
                    {
                        start.SignalAndWait();
                        for (var index = 0; index < Each; index++)
                        {
                            marks.AddWaypoint($"room-{thread}", "Clay", "bigmap", index, 0, 0, null);
                        }
                    },
                    TaskCreationOptions.LongRunning))
                .ToArray();
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60));

            var restarted = new GroupMarks(new MovableClock(Now), path);
            var saved = Enumerable.Range(0, Threads).Sum(thread => restarted.Read($"room-{thread}").Waypoints.Count);
            Assert.Equal(Threads * Each, saved);
        }
    }

    /// <summary>
    /// A ping's id was handed out again after a restart, so the client removing that expired
    /// ping deleted a squadmate's new waypoint.
    /// </summary>
    [Fact]
    public void A_restart_never_reissues_an_id_a_ping_already_had()
    {
        var path = Path.Combine(_directory, "marks.json");
        var clock = new MovableClock(Now);
        var first = new GroupMarks(clock, path);
        var ping = first.AddPing("room", "Clay", "bigmap", 1, 2, 3, null)!;

        clock.Advance(TimeSpan.FromSeconds(20));
        var restarted = new GroupMarks(clock, path);
        var waypoint = restarted.AddWaypoint("room", "Geo", "bigmap", 4, 5, 6, "Gas")!;

        Assert.NotEqual(ping.Id, waypoint.Id);
        Assert.False(restarted.Remove("room", ping.Id));
        Assert.Single(restarted.Read("room").Waypoints);
    }

    [Fact]
    public void A_memory_only_relay_does_not_reissue_ids_either()
    {
        var clock = new MovableClock(Now);
        var ping = new GroupMarks(clock).AddPing("room", "Clay", "bigmap", 1, 2, 3, null)!;
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(new GroupMarks(clock).AddWaypoint("room", "Geo", "bigmap", 1, 2, 3, null)!.Id > ping.Id);
    }

    /// <summary>A removal scoped to its sender cannot take a squadmate's mark with it.</summary>
    [Fact]
    public void A_removal_scoped_to_its_sender_leaves_a_squadmates_mark()
    {
        var marks = new GroupMarks(new MovableClock(Now));
        var theirs = marks.AddWaypoint("room", "Geo", "bigmap", 1, 2, 3, "Gas")!;
        var mine = marks.AddPing("room", "Clay", "bigmap", 1, 2, 3, null)!;

        Assert.False(marks.Remove("room", theirs.Id, onlyBy: "Clay"));
        Assert.Single(marks.Read("room").Waypoints);

        Assert.True(marks.Remove("room", mine.Id, onlyBy: "clay "));
        Assert.Empty(marks.Read("room").Pings);

        // Unscoped, as every older client sends it: the group's marks are the group's.
        Assert.True(marks.Remove("room", theirs.Id));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
