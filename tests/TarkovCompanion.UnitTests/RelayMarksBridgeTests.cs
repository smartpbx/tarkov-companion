using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.UnitTests;

public sealed class RelayMarksBridgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RejectedDispositionNeverTouchesTheLocalStore()
    {
        var store = new FakeRaidMarkStore();
        var bridge = await CreateBridgeAsync(store);
        var command = Upsert(markId: 1, expectedRevision: 0, x: 5, y: 5);

        await bridge.ReconcileAsync(command, CommandDisposition.RejectedConflict, CancellationToken.None);

        Assert.Empty(store.Marks);
    }

    [Fact]
    public async Task AppliedCreateAddsExactlyOneLocalMark()
    {
        var store = new FakeRaidMarkStore();
        var bridge = await CreateBridgeAsync(store);
        var command = Upsert(markId: 1, expectedRevision: 0, x: 10, y: 20, label: "Loot");

        await bridge.ReconcileAsync(command, CommandDisposition.Applied, CancellationToken.None);

        var mark = Assert.Single(store.Marks);
        Assert.Equal(RaidMarkKind.Waypoint, mark.Kind);
        Assert.Equal(10, mark.State.X);
        Assert.Equal(20, mark.State.Y);
        Assert.Equal("Loot", mark.State.Label);
    }

    [Fact]
    public async Task AppliedEditMovesTheSameLocalMarkInsteadOfDuplicatingIt()
    {
        var store = new FakeRaidMarkStore();
        var bridge = await CreateBridgeAsync(store);
        await bridge.ReconcileAsync(Upsert(markId: 1, expectedRevision: 0, x: 10, y: 20), CommandDisposition.Applied, CancellationToken.None);

        await bridge.ReconcileAsync(Upsert(markId: 1, expectedRevision: 1, x: 99, y: 99, label: "Moved"), CommandDisposition.Applied, CancellationToken.None);

        var mark = Assert.Single(store.Marks);
        Assert.Equal(99, mark.State.X);
        Assert.Equal(99, mark.State.Y);
        Assert.Equal("Moved", mark.State.Label);
    }

    [Fact]
    public async Task AppliedDeleteRemovesTheMappedLocalMarkAndIsIdempotent()
    {
        var store = new FakeRaidMarkStore();
        var bridge = await CreateBridgeAsync(store);
        await bridge.ReconcileAsync(Upsert(markId: 1, expectedRevision: 0, x: 1, y: 1), CommandDisposition.Applied, CancellationToken.None);

        await bridge.ReconcileAsync(Delete(markId: 1, expectedRevision: 1), CommandDisposition.Applied, CancellationToken.None);
        // A replayed delivery of the same delete finds no mapping left and does nothing, rather
        // than throwing or removing an unrelated mark that happens to reuse the store's next id.
        await bridge.ReconcileAsync(Delete(markId: 1, expectedRevision: 1), CommandDisposition.Applied, CancellationToken.None);

        Assert.Empty(store.Marks);
    }

    [Fact]
    public async Task NonPingOrWaypointKindsAreIgnored()
    {
        var store = new FakeRaidMarkStore();
        var bridge = await CreateBridgeAsync(store);
        var command = Upsert(markId: 1, expectedRevision: 0, x: 1, y: 1, kind: MapMarkKind.Note);

        await bridge.ReconcileAsync(command, CommandDisposition.Applied, CancellationToken.None);

        Assert.Empty(store.Marks);
    }

    // --- #584: a ping on a paired tablet's Marks list expires on its own -----------------------

    [Fact]
    public async Task APairedPingLeavesTheCanonicalMarksOnItsOwnWhenItsTimeRunsOut()
    {
        // Real time: this proves the bridge's own timer runs the expiry, with nothing else
        // happening (no command, no relay read). 300 ms is inside the 45 s bound a ping may ask for.
        var bridge = await CreateBridgeAsync(new FakeRaidMarkStore(), TimeProvider.System);
        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.CanonicalStateChanged += state =>
        {
            if (state.Marks.Marks.Count == 0 && state.Marks.Cursor.Revision.Value > 1)
            {
                gone.TrySetResult();
            }
        };
        var now = WholeMilliseconds(DateTimeOffset.UtcNow);

        var applied = await bridge.ApplyDesktopCommandAsync(Upsert(
            markId: 3,
            expectedRevision: 0,
            x: 1,
            y: 1,
            kind: MapMarkKind.Ping,
            issuedUtc: now,
            expiresUtc: now.AddMilliseconds(300)));
        Assert.Equal(CommandDisposition.Applied, applied);

        await gone.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void MarkExpiryDropsOnlyWhatIsDueAndBumpsTheMarksRevisionOnce()
    {
        var ping = PairedMark(1, MapMarkKind.Ping, Now.AddSeconds(45));
        var waypoint = PairedMark(2, MapMarkKind.Waypoint, null);
        var state = InitialState(new MarkAggregate(new AggregateCursor(new AggregateRevision(1), Command(99)), [ping, waypoint]));

        Assert.Equal(Now.AddSeconds(45), DesktopCanonicalStateMachine.NextMarkExpiry(state));
        var early = DesktopCanonicalStateMachine.ApplyMarkExpiry(state, Now.AddSeconds(44));
        Assert.Same(state, early.State);
        Assert.Empty(early.Updates);

        var due = DesktopCanonicalStateMachine.ApplyMarkExpiry(state, Now.AddSeconds(45));
        Assert.Equal(waypoint.MarkId, Assert.Single(due.State.Marks.Marks).MarkId);
        Assert.Equal(2, due.State.Marks.Cursor.Revision.Value);
        Assert.IsType<MarksCanonicalUpdate>(Assert.Single(due.Updates));
        Assert.Null(DesktopCanonicalStateMachine.NextMarkExpiry(due.State));
    }

    private static DateTimeOffset WholeMilliseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);

    private static async Task<RelayMarksBridge> CreateBridgeAsync(IRaidMarkStore store, TimeProvider? clock = null)
    {
        clock ??= new FakeTimeProvider(Now);
        var initial = InitialState();
        var authority = await DesktopCompanionAuthority.OpenAsync(new MemoryAuthorityStore(), initial);
        return new RelayMarksBridge(authority, store, clock);
    }

    private static MapMark PairedMark(int seed, MapMarkKind kind, DateTimeOffset? expiresUtc) => new(
        Mark(seed),
        1,
        Command(seed + 50),
        kind,
        MapMarkScope.PairedDevice,
        new CompanionDeviceId(Guid.NewGuid()),
        new MapMarkState("customs", null, 1, 1, null, expiresUtc),
        CoordinateSpaceKind.World,
        "v1",
        null,
        "#00AACC",
        Now,
        Now);

    private static CanonicalCompanionState InitialState(MarkAggregate? marks = null)
    {
        var initial = new CanonicalCompanionState(
            new AuthorityEpoch(Guid.NewGuid()),
            new WorkspaceId(Guid.NewGuid()),
            "desktop",
            new GlobalRevision(marks?.Cursor.Revision.Value ?? 0),
            new CompanionDeviceId(Guid.NewGuid()),
            new DeviceModeAggregate(AggregateCursor.Empty, [], null, null),
            new WorkspaceAggregate(
                AggregateCursor.Empty,
                new WorkspaceProjection(WorkspaceKind.Raid, null, null, null, null, [], [], null, [], [], [], null)),
            marks ?? new MarkAggregate(AggregateCursor.Empty, []),
            new CaptureIntentAggregate(AggregateCursor.Empty, null),
            ProfilePreferencesAggregate.Empty);
        return initial;
    }

    private static UpsertMarkCommand Upsert(
        int markId,
        long expectedRevision,
        double x,
        double y,
        string? label = null,
        MapMarkKind kind = MapMarkKind.Waypoint,
        DateTimeOffset? issuedUtc = null,
        DateTimeOffset? expiresUtc = null) => new(
        Command(markId * 10),
        new AggregateRevision(expectedRevision + 1),
        issuedUtc ?? Now,
        (issuedUtc ?? Now).AddSeconds(30),
        Mark(markId),
        expectedRevision,
        new MapMarkDraft(
            kind,
            MapMarkScope.PairedDevice,
            new MapMarkState("customs", null, x, y, label, expiresUtc),
            CoordinateSpaceKind.World,
            "v1",
            null,
            "#00AACC"));

    private static DeleteMarkCommand Delete(int markId, long expectedRevision) => new(
        Command(markId * 10 + 1),
        new AggregateRevision(expectedRevision + 1),
        Now,
        Now.AddSeconds(30),
        Mark(markId),
        expectedRevision);

    private static CommandId Command(int seed) => new(new Guid(seed, 0, 0, new byte[8]));

    private static MarkId Mark(int seed) => new(new Guid(0, 0, 0, [0, 0, 0, 0, 0, 0, 0, (byte)seed]));

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeRaidMarkStore : IRaidMarkStore
    {
        private readonly List<RaidMark> _marks = [];

        public IReadOnlyList<RaidMark> Marks => _marks;

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
            var mark = new RaidMark(Guid.NewGuid(), kind, new MapMarkState(mapId, floorId, x, y, label, null), DateTimeOffset.UtcNow);
            _marks.Add(mark);
            Changed?.Invoke();
            return Task.FromResult(mark);
        }

        public Task MoveAsync(Guid id, double x, double y, CancellationToken cancellationToken = default)
        {
            var index = _marks.FindIndex(mark => mark.Id == id);
            if (index >= 0)
            {
                var current = _marks[index];
                var state = new MapMarkState(current.State.MapId, current.State.FloorId, x, y, current.State.Label, current.State.ExpiresUtc);
                _marks[index] = current with { State = state };
                Changed?.Invoke();
            }

            return Task.CompletedTask;
        }

        public Task RenameAsync(Guid id, string? label, CancellationToken cancellationToken = default)
        {
            var index = _marks.FindIndex(mark => mark.Id == id);
            if (index >= 0)
            {
                var current = _marks[index];
                var state = new MapMarkState(current.State.MapId, current.State.FloorId, current.State.X, current.State.Y, label, current.State.ExpiresUtc);
                _marks[index] = current with { State = state };
                Changed?.Invoke();
            }

            return Task.CompletedTask;
        }

        public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _marks.RemoveAll(mark => mark.Id == id);
            Changed?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryAuthorityStore : IDesktopCompanionAuthorityStore
    {
        private DesktopCompanionAuthorityState? _state;

        public ValueTask<DesktopCompanionAuthorityState?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_state);

        public ValueTask SaveAsync(DesktopCompanionAuthorityState state, CancellationToken cancellationToken = default)
        {
            _state = state;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IDisposable> AcquireExclusiveLeaseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IDisposable>(new NoopLease());

        private sealed class NoopLease : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
