using System.Collections.Concurrent;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.ViewModels.V2.Team;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Infrastructure.Maps;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// #289: a "Squad" mark placed while the relay is away waits, says so, and goes out on reconnect
/// (the desktop half of the tablet's #766 queue).
/// </summary>
public sealed class GroupMarkQueueTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tc-mark-queue-" + Guid.NewGuid().ToString("N"));
    private readonly MovingClock _clock = new(new DateTimeOffset(2026, 9, 24, 20, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_mark_placed_offline_is_queued_and_sent_when_the_group_is_live_again()
    {
        var (store, relay, forwarder) = await ComposeAsync();
        using var _ = forwarder;
        relay.Up = false;

        var waypoint = await store.PlaceAsync("customs", null, 10, 20, "Regroup", RaidMarkScope.Squad, RaidMarkLifetime.UntilRemoved);
        await WaitUntilAsync(() => forwarder.IsQueued(waypoint.Id));

        Assert.Equal([waypoint.Id], forwarder.QueuedIds);
        var row = TeamWorkspaceViewModel.QueuedMarkRow(waypoint, _clock.GetUtcNow(), () => Task.CompletedTask);
        Assert.True(row.IsQueued);
        Assert.StartsWith("Queued · sends on reconnect", row.MetadataLabel, StringComparison.Ordinal);

        // Still offline: a snapshot that is not live is never passed in, so nothing is retried.
        Assert.Equal(1, relay.Attempts);

        relay.Up = true;
        _clock.Advance(TimeSpan.FromSeconds(6));
        forwarder.ObserveGroup(new HashSet<long>());
        await WaitUntilAsync(() => forwarder.IsForwarded(1));

        Assert.False(forwarder.IsQueued(waypoint.Id));
        Assert.Empty(forwarder.QueuedIds);
        Assert.Equal(waypoint.Id, forwarder.LocalIdFor(1));
        var (mapId, position, isPing) = Assert.Single(relay.Sent);
        Assert.Equal(("customs", 10d, false), (mapId, position.X, isPing));
    }

    [Fact]
    public async Task A_queued_mark_goes_out_where_it_is_now_not_where_it_was_placed()
    {
        var (store, relay, forwarder) = await ComposeAsync();
        using var _ = forwarder;
        relay.Up = false;
        var waypoint = await store.PlaceAsync("customs", null, 10, 20, null, RaidMarkScope.Squad, RaidMarkLifetime.UntilRemoved);
        await WaitUntilAsync(() => forwarder.IsQueued(waypoint.Id));

        await store.MoveAsync(waypoint.Id, 30, 40);
        relay.Up = true;
        _clock.Advance(TimeSpan.FromSeconds(6));
        forwarder.ObserveGroup(new HashSet<long>());
        await WaitUntilAsync(() => forwarder.IsForwarded(1));

        Assert.Equal(30d, Assert.Single(relay.Sent).Position.X);
    }

    [Fact]
    public async Task A_queued_mark_removed_or_narrowed_to_just_me_is_never_sent()
    {
        var (store, relay, forwarder) = await ComposeAsync();
        using var _ = forwarder;
        relay.Up = false;
        var removed = await store.PlaceAsync("customs", null, 1, 1, null, RaidMarkScope.Squad, RaidMarkLifetime.UntilRemoved);
        var narrowed = await store.PlaceAsync("customs", null, 2, 2, null, RaidMarkScope.Squad, RaidMarkLifetime.UntilRemoved);
        await WaitUntilAsync(() => forwarder.QueuedIds.Count == 2);

        await store.RemoveAsync(removed.Id);
        await store.SetOptionsAsync(narrowed.Id, RaidMarkScope.Private, RaidMarkLifetime.UntilRemoved);

        Assert.Empty(forwarder.QueuedIds);
        relay.Up = true;
        _clock.Advance(TimeSpan.FromSeconds(6));
        forwarder.ObserveGroup(new HashSet<long>());
        await Task.Delay(200);
        Assert.Empty(relay.Sent);
    }

    [Fact]
    public async Task A_mark_still_unsent_after_fifteen_minutes_is_given_up()
    {
        var (store, relay, forwarder) = await ComposeAsync();
        using var _ = forwarder;
        relay.Up = false;
        var waypoint = await store.PlaceAsync("customs", null, 1, 1, null, RaidMarkScope.Squad, RaidMarkLifetime.UntilRemoved);
        await WaitUntilAsync(() => forwarder.IsQueued(waypoint.Id));

        _clock.Advance(GroupMarkForwarder.QueueLimit + TimeSpan.FromSeconds(1));
        relay.Up = true;
        forwarder.ObserveGroup(new HashSet<long>());
        await Task.Delay(200);

        Assert.Empty(forwarder.QueuedIds);
        Assert.Empty(relay.Sent);
    }

    [Fact]
    public async Task A_relay_that_still_refuses_keeps_the_mark_queued_and_is_not_asked_on_every_snapshot()
    {
        var (store, relay, forwarder) = await ComposeAsync();
        using var _ = forwarder;
        relay.Up = false;
        var waypoint = await store.PlaceAsync("customs", null, 1, 1, null, RaidMarkScope.Squad, RaidMarkLifetime.UntilRemoved);
        await WaitUntilAsync(() => forwarder.IsQueued(waypoint.Id));

        _clock.Advance(TimeSpan.FromSeconds(6));
        forwarder.ObserveGroup(new HashSet<long>());
        await WaitUntilAsync(() => relay.Attempts == 2);
        // Snapshots within the retry gap ask nothing.
        forwarder.ObserveGroup(new HashSet<long>());
        forwarder.ObserveGroup(new HashSet<long>());
        await Task.Delay(200);

        Assert.Equal(2, relay.Attempts);
        Assert.True(forwarder.IsQueued(waypoint.Id));
    }

    private async Task<(JsonFileRaidMarkStore Store, FakeRelay Relay, GroupMarkForwarder Forwarder)> ComposeAsync()
    {
        var store = new JsonFileRaidMarkStore(Path.Combine(_directory, "marks.json"), _clock);
        await store.LoadAsync();
        var relay = new FakeRelay();
        var forwarder = new GroupMarkForwarder(
            store,
            mark => new WorldPosition(mark.State.X, 0, -mark.State.Y),
            relay.SendAsync,
            (_, _) => Task.CompletedTask,
            _clock);
        return (store, relay, forwarder);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private sealed class FakeRelay
    {
        private int _attempts;
        private long _nextId;

        public volatile bool Up = true;

        public ConcurrentQueue<(string MapId, WorldPosition Position, bool IsPing)> Sent { get; } = new();

        public int Attempts => Volatile.Read(ref _attempts);

        public Task<long?> SendAsync(string mapId, WorldPosition position, bool isPing, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            if (!Up)
            {
                // What GroupSessionService.SendMarkAsync answers when the relay cannot be reached.
                return Task.FromResult<long?>(null);
            }

            Sent.Enqueue((mapId, position, isPing));
            return Task.FromResult<long?>(Interlocked.Increment(ref _nextId));
        }
    }

    private sealed class MovingClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
