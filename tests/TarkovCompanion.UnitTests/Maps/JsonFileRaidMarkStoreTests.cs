using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Infrastructure.Maps;

namespace TarkovCompanion.UnitTests.Maps;

public sealed class JsonFileRaidMarkStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"raid-marks-{Guid.NewGuid():N}");

    private string StorePath => Path.Combine(_directory, "raid-marks.json");

    [Fact]
    public async Task AddedMarksSurviveALoadFromANewStoreInstance()
    {
        var first = new JsonFileRaidMarkStore(StorePath, new FakeTimeProvider(new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero)));
        var added = await first.AddAsync(RaidMarkKind.Waypoint, "factory", "ground", 25, 60, "Fall back here");

        var second = new JsonFileRaidMarkStore(StorePath);
        await second.LoadAsync();

        var reloaded = Assert.Single(second.Marks);
        Assert.Equal(added.Id, reloaded.Id);
        Assert.Equal(RaidMarkKind.Waypoint, reloaded.Kind);
        Assert.Equal("factory", reloaded.State.MapId);
        Assert.Equal("ground", reloaded.State.FloorId);
        Assert.Equal(25, reloaded.State.X);
        Assert.Equal(60, reloaded.State.Y);
        Assert.Equal("Fall back here", reloaded.State.Label);
    }

    [Fact]
    public async Task MoveUpdatesThePositionAndRemoveDropsTheMark()
    {
        var store = new JsonFileRaidMarkStore(StorePath);
        var mark = await store.AddAsync(RaidMarkKind.Ping, "factory", null, 10, 10, label: null);

        await store.MoveAsync(mark.Id, 15, 20);
        Assert.Equal(15, Assert.Single(store.Marks).State.X);
        Assert.Equal(20, Assert.Single(store.Marks).State.Y);

        await store.RemoveAsync(mark.Id);
        Assert.Empty(store.Marks);
    }

    [Fact]
    public async Task RenameSetsACustomLabelAndAnEmptyNameClearsItBackToNumbered()
    {
        var store = new JsonFileRaidMarkStore(StorePath);
        var mark = await store.AddAsync(RaidMarkKind.Waypoint, "factory", null, 10, 10, label: null);
        Assert.Null(Assert.Single(store.Marks).State.Label);

        await store.RenameAsync(mark.Id, "Extract cache");
        Assert.Equal("Extract cache", Assert.Single(store.Marks).State.Label);

        await store.RenameAsync(mark.Id, "   ");
        Assert.Null(Assert.Single(store.Marks).State.Label);

        // Position and floor are untouched by a rename, the same guarantee MoveAsync gives for
        // the label: each edits only the field it names.
        await store.RenameAsync(mark.Id, "Rename again");
        var renamed = Assert.Single(store.Marks);
        Assert.Equal("Rename again", renamed.State.Label);
        Assert.Equal(10, renamed.State.X);
        Assert.Equal(10, renamed.State.Y);
    }

    [Fact]
    public async Task ChangedFiresForAddMoveAndRemove()
    {
        var store = new JsonFileRaidMarkStore(StorePath);
        // The lazy first load fires Changed on its own (empty is still a fact worth telling a
        // subscriber), so it is consumed here rather than folded into the count below.
        await store.LoadAsync();
        var raised = 0;
        store.Changed += () => raised++;

        var mark = await store.AddAsync(RaidMarkKind.Ping, "factory", null, 10, 10, label: null);
        await store.MoveAsync(mark.Id, 11, 11);
        await store.RemoveAsync(mark.Id);

        Assert.Equal(3, raised);
    }

    // --- Issue 584: a ping expires, wherever it was placed; a waypoint never does -------------

    [Fact]
    public async Task APingGetsA45SecondExpiryAndAWaypointGetsNone()
    {
        var now = new DateTimeOffset(2026, 9, 21, 20, 0, 0, TimeSpan.Zero);
        var store = new JsonFileRaidMarkStore(StorePath, new FakeTimeProvider(now));

        // A tablet's ping reaches this exact method through RelayMarksBridge, the same call a
        // desktop right-click makes — so this one seam covers a ping from either place.
        var ping = await store.AddAsync(RaidMarkKind.Ping, "factory", null, 10, 10, label: null);
        var waypoint = await store.AddAsync(RaidMarkKind.Waypoint, "factory", null, 10, 10, label: null);

        Assert.Equal(now + TimeSpan.FromSeconds(45), ping.State.ExpiresUtc);
        Assert.Null(waypoint.State.ExpiresUtc);
    }

    [Fact]
    public async Task ATabletsPingExpiresAndADesktopsPingExpiresTheSameWayAWaypointDoesNot()
    {
        var clock = new AdvanceableTimeProvider(new(2026, 9, 21, 20, 0, 0, TimeSpan.Zero));
        var store = new JsonFileRaidMarkStore(StorePath, clock);

        // "Tablet's" and "desktop's" are the same call from the store's point of view — both a
        // right-click and a tablet's upsertMark command reach IRaidMarkStore.AddAsync identically
        // (RelayMarksBridge applies the tablet's the same way RaidCockpitViewModel applies its
        // own) — so two AddAsync calls stand in for the two origins.
        var tabletPing = await store.AddAsync(RaidMarkKind.Ping, "factory", null, 1, 1, label: null);
        var desktopPing = await store.AddAsync(RaidMarkKind.Ping, "factory", null, 2, 2, label: null);
        var waypoint = await store.AddAsync(RaidMarkKind.Waypoint, "factory", null, 3, 3, label: null);

        clock.Advance(TimeSpan.FromSeconds(44));
        Assert.Equal(3, store.Marks.Count);

        clock.Advance(TimeSpan.FromSeconds(2)); // 46s total: past both pings' 45s lifetime
        var remaining = store.Marks;
        Assert.Single(remaining);
        Assert.Equal(waypoint.Id, remaining[0].Id);
        Assert.DoesNotContain(remaining, mark => mark.Id == tabletPing.Id || mark.Id == desktopPing.Id);
    }

    [Fact]
    public async Task RestartingWithAnExpiredPingOnDiskLoadsClean()
    {
        var placedAt = new DateTimeOffset(2026, 9, 21, 20, 0, 0, TimeSpan.Zero);
        var first = new JsonFileRaidMarkStore(StorePath, new FakeTimeProvider(placedAt));
        var ping = await first.AddAsync(RaidMarkKind.Ping, "factory", null, 5, 5, label: null);
        var waypoint = await first.AddAsync(RaidMarkKind.Waypoint, "factory", null, 6, 6, "Regroup here");

        // A raw read of the file, the way another process (or a person) would — proves the
        // expired ping was actually written with its expiry, not just held in memory.
        var onDisk = await File.ReadAllTextAsync(StorePath);
        Assert.Contains(ping.Id.ToString(), onDisk, StringComparison.Ordinal);

        // The desktop was closed for a minute; long past the ping's 45s.
        var second = new JsonFileRaidMarkStore(StorePath, new FakeTimeProvider(placedAt.AddMinutes(1)));
        await second.LoadAsync();

        var loaded = Assert.Single(second.Marks);
        Assert.Equal(waypoint.Id, loaded.Id);

        // "Cleans itself": the file on disk no longer names the expired ping either, so a third
        // instance (or the same file read directly) never sees it again.
        var cleaned = await File.ReadAllTextAsync(StorePath);
        Assert.DoesNotContain(ping.Id.ToString(), cleaned, StringComparison.Ordinal);
        Assert.Contains(waypoint.Id.ToString(), cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExpiredPingIsDroppedOnItsOwnWithoutAnyOtherMutation()
    {
        // Real time, not the frozen clock the tests above use: this is the one test that proves
        // the store schedules its own timer for the next expiry (issue 584's "without waiting for
        // another change") rather than only proving Marks filters correctly on a read the test
        // itself triggers. A millisecond lifetime is the test-only seam pingLifetime exists for.
        var store = new JsonFileRaidMarkStore(StorePath, TimeProvider.System, pingLifetime: TimeSpan.FromMilliseconds(30));
        await store.AddAsync(RaidMarkKind.Ping, "factory", null, 1, 1, label: null);

        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += () => fired.TrySetResult();

        await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Read via the field the test can see directly (not Marks, which would filter on its own
        // read and could mask a timer that never actually ran).
        Assert.Empty(store.Marks);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A frozen clock a test can move forward by hand, for asserting expiry without a
    /// real wait. Deliberately does not override <c>CreateTimer</c>: this repository's fake clocks
    /// never drive a live timer (see RelayTestClock), so a store's self-scheduled timer is proven
    /// separately, in real time, by <see cref="AnExpiredPingIsDroppedOnItsOwnWithoutAnyOtherMutation"/>.</summary>
    private sealed class AdvanceableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
