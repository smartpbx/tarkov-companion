using System.Collections.Concurrent;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// [#707] The Raid map once the player has left their raid and a squadmate has not: who is drawn,
/// what the strip offers, and whether a ping placed here reaches the group.
/// </summary>
public sealed class SquadAfterRaidCockpitTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Fails on main: an older companion keeps publishing its last screenshot after its raid ends,
    /// and the squad block drew it as a live teammate.
    /// </summary>
    [Fact]
    public void A_squadmate_who_has_left_their_raid_is_not_drawn_but_one_still_inside_is()
    {
        var built = RaidCockpitViewModel.BuildLiveLayers(
            Inputs([Mate("Geo", 30, 40, RaidLifecycleState.InRaid), Mate("Riley", 60, 70, RaidLifecycleState.PostRaid)]),
            Model(),
            NowUtc);

        var names = built.Objects
            .Where(item => item.Kind == MapSceneObjectKind.TeammateLastKnown)
            .Select(item => item.Label)
            .ToArray();
        Assert.Equal(["Geo"], names);
        Assert.DoesNotContain(built.Objects, item => item.Id.Value.Contains("Riley", StringComparison.Ordinal));
    }

    [Fact]
    public void The_strip_says_whose_map_this_is_or_offers_to_follow_them()
    {
        var geo = Mate("Geo", 30, 40, RaidLifecycleState.InRaid) with { MapId = "customs" };

        Assert.Equal(
            "Watching Geo",
            RaidCockpitViewModel.DescribeSquadWatch(geo, mapId => mapId == "customs", _ => "Customs"));
        Assert.Equal(
            "Follow Geo · Customs",
            RaidCockpitViewModel.DescribeSquadWatch(geo, _ => false, _ => "Customs"));
        Assert.Equal(
            string.Empty,
            RaidCockpitViewModel.DescribeSquadWatch(null, _ => true, _ => "Customs"));
    }

    /// <summary>
    /// A mark's plan point, turned back into the world, lands where the map would draw that
    /// world position — so the squadmate's companion puts the ping where it was placed.
    /// </summary>
    [Fact]
    public void A_mark_is_located_in_the_world_where_the_map_draws_it()
    {
        var model = Model();
        var mark = Mark(RaidMarkKind.Ping, 30, 40, floorId: "lower");

        var world = RaidCockpitViewModel.LocateMark(model, mark, playerHeight: 7);

        Assert.NotNull(world);
        Assert.True(model.TryMapPosition(world.Value, out var back));
        Assert.Equal(30, back.X, 6);
        Assert.Equal(40, back.Y, 6);
        // The floor's own band, not the player's height, because the mark was placed on it.
        Assert.Equal(0, world.Value.Y, 6);
        Assert.Equal(7, RaidCockpitViewModel.LocateMark(model, Mark(RaidMarkKind.Ping, 30, 40), 7)!.Value.Y, 6);
    }

    /// <summary>
    /// Fails on main, where there is no forwarder: a ping placed on the V2 map stayed in the
    /// local store and reached nobody.
    /// </summary>
    [Fact]
    public async Task A_ping_placed_on_the_map_is_sent_to_the_group_and_taken_off_when_removed()
    {
        var store = new FakeMarkStore(NowUtc);
        var sent = new ConcurrentQueue<(string MapId, WorldPosition Position, bool IsPing)>();
        var removed = new ConcurrentQueue<long>();
        using var forwarder = new GroupMarkForwarder(
            store,
            mark => new WorldPosition(mark.State.X, 0, -mark.State.Y),
            (mapId, position, isPing, _) =>
            {
                sent.Enqueue((mapId, position, isPing));
                return Task.FromResult<long?>(100 + sent.Count);
            },
            id =>
            {
                removed.Enqueue(id);
                return Task.CompletedTask;
            },
            new FixedClock(NowUtc));

        var ping = await store.AddAsync(RaidMarkKind.Ping, "customs", null, 30, 40, null);
        await WaitUntilAsync(() => forwarder.IsForwarded(101));

        var (mapId, position, isPing) = Assert.Single(sent);
        Assert.Equal("customs", mapId);
        Assert.True(isPing);
        Assert.Equal(30, position.X);
        Assert.Equal(-40, position.Z);

        await store.RemoveAsync(ping.Id);
        await WaitUntilAsync(() => !removed.IsEmpty);

        Assert.Equal([101L], removed.ToArray());
        Assert.False(forwarder.IsForwarded(101));
    }

    /// <summary>
    /// #289: a "Just me" mark never reaches the relay; widening it sends it, narrowing a sent
    /// one takes it off, and a new lifetime that makes a ping a waypoint is a remove and resend.
    /// </summary>
    [Fact]
    public async Task A_private_mark_is_never_sent_and_a_scope_change_sends_or_withdraws_it()
    {
        var store = new FakeMarkStore(NowUtc);
        var sent = new ConcurrentQueue<(Guid Mark, bool IsPing)>();
        var removed = new ConcurrentQueue<long>();
        var lastPlaced = Guid.Empty;
        using var forwarder = new GroupMarkForwarder(
            store,
            mark =>
            {
                lastPlaced = mark.Id;
                return new WorldPosition(mark.State.X, 0, -mark.State.Y);
            },
            (_, _, isPing, _) =>
            {
                sent.Enqueue((lastPlaced, isPing));
                return Task.FromResult<long?>(300 + sent.Count);
            },
            id =>
            {
                removed.Enqueue(id);
                return Task.CompletedTask;
            },
            new FixedClock(NowUtc));

        var secret = await store.PlaceAsync("customs", null, 5, 5, null, RaidMarkScope.Private, RaidMarkLifetime.Ping);
        var shared = await store.PlaceAsync("customs", null, 6, 6, null, RaidMarkScope.Squad, RaidMarkLifetime.Ping);
        await WaitUntilAsync(() => forwarder.IsForwarded(301));
        Assert.Equal([shared.Id], sent.Select(item => item.Mark).ToArray());

        // Moving or renaming a private mark still sends nothing.
        await store.MoveAsync(secret.Id, 7, 7);
        Assert.Single(sent);

        await store.SetOptionsAsync(secret.Id, RaidMarkScope.Squad, RaidMarkLifetime.Ping);
        await WaitUntilAsync(() => forwarder.IsForwarded(302));
        Assert.Equal(secret.Id, sent.Last().Mark);

        await store.SetOptionsAsync(shared.Id, RaidMarkScope.Private, RaidMarkLifetime.Ping);
        await WaitUntilAsync(() => !removed.IsEmpty);
        Assert.Equal([301L], removed.ToArray());
        Assert.False(forwarder.IsForwarded(301));

        // Ping to five minutes: the relay holds a ping 45 s, so it goes back as a waypoint.
        await store.SetOptionsAsync(secret.Id, RaidMarkScope.Squad, RaidMarkLifetime.FiveMinutes);
        await WaitUntilAsync(() => forwarder.IsForwarded(303));
        Assert.Equal([301L, 302L], removed.ToArray());
        Assert.False(sent.Last().IsPing);
        Assert.Equal(3, sent.Count);
    }

    /// <summary>
    /// #289 conflict: a squadmate removed our waypoint on the relay. The later action wins, so
    /// the local copy is handed back for removal, but only once the relay was seen holding it.
    /// </summary>
    [Fact]
    public async Task A_waypoint_removed_by_the_squad_is_reported_once_and_only_after_it_was_seen()
    {
        var store = new FakeMarkStore(NowUtc);
        using var forwarder = new GroupMarkForwarder(
            store,
            mark => new WorldPosition(mark.State.X, 0, -mark.State.Y),
            (_, _, _, _) => Task.FromResult<long?>(401),
            _ => Task.CompletedTask,
            new FixedClock(NowUtc));

        var waypoint = await store.PlaceAsync("customs", null, 5, 5, null, RaidMarkScope.Squad, RaidMarkLifetime.UntilRemoved);
        await WaitUntilAsync(() => forwarder.IsForwarded(401));

        // Not yet in a snapshot: still on its way, not removed.
        Assert.Empty(forwarder.ObserveGroup(new HashSet<long>()));
        Assert.Empty(forwarder.ObserveGroup(new HashSet<long> { 401 }));
        Assert.Equal(waypoint.Id, forwarder.LocalIdFor(401));

        Assert.Equal([waypoint.Id], forwarder.ObserveGroup(new HashSet<long>()));
        Assert.Empty(forwarder.ObserveGroup(new HashSet<long>()));
        Assert.Null(forwarder.LocalIdFor(401));
    }

    [Fact]
    public async Task A_moved_waypoint_is_replaced_on_the_relay_and_old_marks_are_never_replayed()
    {
        var store = new FakeMarkStore(NowUtc);
        await store.AddAsync(RaidMarkKind.Waypoint, "customs", null, 1, 1, null);
        var sent = new ConcurrentQueue<WorldPosition>();
        var removed = new ConcurrentQueue<long>();
        using var forwarder = new GroupMarkForwarder(
            store,
            mark => new WorldPosition(mark.State.X, 0, -mark.State.Y),
            (_, position, _, _) =>
            {
                sent.Enqueue(position);
                return Task.FromResult<long?>(200 + sent.Count);
            },
            id =>
            {
                removed.Enqueue(id);
                return Task.CompletedTask;
            },
            new FixedClock(NowUtc));

        var waypoint = await store.AddAsync(RaidMarkKind.Waypoint, "customs", null, 10, 10, null);
        await WaitUntilAsync(() => forwarder.IsForwarded(201));
        await store.MoveAsync(waypoint.Id, 20, 25);
        await WaitUntilAsync(() => forwarder.IsForwarded(202));

        Assert.Equal([10d, 20d], sent.Select(position => position.X));
        Assert.Equal([201L], removed.ToArray());
    }

    private static RaidMark Mark(RaidMarkKind kind, double x, double y, string? floorId = null) =>
        new(Guid.NewGuid(), kind, new MapMarkState("factory", floorId, x, y, null, null), NowUtc);

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        for (var attempt = 0; attempt < 200 && !ready(); attempt++)
        {
            await Task.Delay(10);
        }
    }

    private static RaidCockpitViewModel.LiveSceneInputs Inputs(IReadOnlyList<GroupMemberView> squad) => new(
        null,
        [],
        squad,
        _ => true,
        _ => "#FF00FF00",
        false,
        [],
        false);

    private static GroupMemberView Mate(string name, double planX, double planY, RaidLifecycleState state) =>
        new(name, "factory", state, "PMC", new(planX, 0, -planY), 0, TimeSpan.FromSeconds(5), [], []);

    /// <summary>The identity-on-X, negated-Z plan RaidCockpitLiveLayersTests uses.</summary>
    private static MapRenderModel Model()
    {
        var location = new MapLocation("factory", null, "Factory", null, null, []);
        var floors = new[]
        {
            new MapFloorDefinition("lower", "Lower", null, null, true, [new(-10, 10, [])]),
        };
        var variant = new MapVariant(
            location.Id,
            "factory-plan",
            MapProjectionKind.TwoDimensional,
            "2D",
            null,
            null,
            new("https://example.test/factory.svg"),
            null,
            256,
            null,
            null,
            new(new(0, 0), new(100, -100)),
            new(new(0, 0), new(100, -100)),
            new(1, 0, 1, 0, 0),
            null,
            null,
            null,
            "Example author",
            new("https://example.test/author"),
            [],
            floors,
            []);
        var overlays = Enum.GetValues<MapOverlayKind>()
            .Select(kind => new MapOverlayLayer(kind, kind.ToString(), true, false))
            .ToArray();
        return new(
            location,
            variant,
            new(MapBackgroundKind.Svg, variant.SvgPath!, "/cache/factory.svg", MapAssetAvailability.Available, null),
            MapTransformAvailability.Valid,
            "Validated transform.",
            overlays,
            [],
            floors,
            floors[0],
            "Example attribution",
            new("https://example.test/licence"));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeMarkStore(DateTimeOffset now) : IRaidMarkStore
    {
        private readonly List<RaidMark> _marks = [];

        public IReadOnlyList<RaidMark> Marks => [.. _marks];

        public event Action? Changed;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<RaidMark> AddAsync(
            RaidMarkKind kind,
            string mapId,
            string? floorId,
            double x,
            double y,
            string? label,
            CancellationToken cancellationToken = default)
        {
            var mark = new RaidMark(Guid.NewGuid(), kind, new MapMarkState(mapId, floorId, x, y, label, null), now);
            _marks.Add(mark);
            Changed?.Invoke();
            return Task.FromResult(mark);
        }

        public Task MoveAsync(Guid id, double x, double y, CancellationToken cancellationToken = default)
        {
            var index = _marks.FindIndex(mark => mark.Id == id);
            var old = _marks[index];
            _marks[index] = old with { State = new MapMarkState(old.State.MapId, old.State.FloorId, x, y, old.State.Label, null) };
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public Task RenameAsync(Guid id, string? label, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<RaidMark> PlaceAsync(string mapId, string? floorId, double x, double y, string? label, RaidMarkScope scope, RaidMarkLifetime lifetime, CancellationToken cancellationToken = default)
        {
            var mark = new RaidMark(Guid.NewGuid(), RaidMarkLifetimes.KindFor(lifetime), new MapMarkState(mapId, floorId, x, y, label, RaidMarkLifetimes.ExpiresUtc(lifetime, now)), now) { Scope = scope, Lifetime = lifetime };
            _marks.Add(mark);
            Changed?.Invoke();
            return Task.FromResult(mark);
        }

        public Task SetOptionsAsync(Guid id, RaidMarkScope scope, RaidMarkLifetime lifetime, CancellationToken cancellationToken = default)
        {
            var index = _marks.FindIndex(mark => mark.Id == id);
            if (index >= 0)
            {
                var old = _marks[index];
                _marks[index] = old with { Kind = RaidMarkLifetimes.KindFor(lifetime), Scope = scope, Lifetime = lifetime };
                Changed?.Invoke();
            }

            return Task.CompletedTask;
        }

        public Task EndRaidAsync(CancellationToken cancellationToken = default)
        {
            _marks.RemoveAll(mark => mark.Lifetime == RaidMarkLifetime.ThisRaid);
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _marks.RemoveAll(mark => mark.Id == id);
            Changed?.Invoke();
            return Task.CompletedTask;
        }
    }
}
