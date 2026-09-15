using System.Collections.Immutable;
using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests;

public sealed class RaidActivityCoordinatorTests
{
    /// <summary>
    /// The marker is on the map even when the database will not take the row.
    /// </summary>
    /// <remarks>
    /// This is the test that would have caught it. Publishing used to be the last thing each
    /// Apply method did, so a write that threw — a full disk, an antivirus lock, the catalog
    /// refresh holding the write lock past the busy timeout — took the position with it, and
    /// the watcher rethrew and tore observation down. The player saw no marker and a cleared
    /// squad list because a row could not be inserted.
    /// </remarks>
    [Fact]
    public async Task APositionIsShownEvenWhenRecordingItFails()
    {
        var store = Store();
        var coordinator = new RaidActivityCoordinator(
            new RaidStateService(),
            new FailingRaidHistory(),
            new StubProfileService(),
            store);

        await Assert.ThrowsAsync<IOException>(() =>
            coordinator.ApplyPositionAsync(Position(), CancellationToken.None));

        // The throw is still a throw — the queue that stops it reaching the watcher is a
        // separate change. What matters here is that it no longer costs the player the marker.
        Assert.NotNull(store.Current.Raid.LastKnownPosition);
        Assert.Equal(120.5, store.Current.Raid.LastKnownPosition!.Position.X);
    }

    [Fact]
    public async Task ExtractsAndLifecycleAreShownBeforeTheyAreRecorded()
    {
        var store = Store();
        var coordinator = new RaidActivityCoordinator(
            new RaidStateService(),
            new FailingRaidHistory(),
            new StubProfileService(),
            store);

        await Assert.ThrowsAsync<IOException>(() => coordinator.ApplyEvidenceAsync(
            new(
                RaidEvidenceKind.LogLine,
                DateTimeOffset.UnixEpoch,
                "bigmap",
                RaidLifecycleState.InRaid,
                Confidence.Certain,
                "in a raid") { StartsNewRaid = true },
            CancellationToken.None));

        Assert.Equal(RaidLifecycleState.InRaid, store.Current.Raid.State);
        Assert.Equal("bigmap", store.Current.Raid.MapId);
    }

    /// <summary>The ordering the database requires is not the ordering that moved.</summary>
    /// <remarks>
    /// raid_events.raid_id is NOT NULL REFERENCES raids(id), so the raid row has to be started
    /// before any event is recorded against it. Publishing moved ahead of both; their order
    /// relative to each other did not.
    /// </remarks>
    [Fact]
    public async Task TheRaidIsStartedBeforeAnyEventIsRecordedAgainstIt()
    {
        var history = new RecordingRaidHistory();
        var coordinator = new RaidActivityCoordinator(
            new RaidStateService(),
            history,
            new StubProfileService(),
            Store());

        await coordinator.ApplyEvidenceAsync(
            new(
                RaidEvidenceKind.LogLine,
                DateTimeOffset.UnixEpoch,
                "bigmap",
                RaidLifecycleState.InRaid,
                Confidence.Certain,
                "in a raid") { StartsNewRaid = true },
            CancellationToken.None);
        await coordinator.ApplyPositionAsync(Position(), CancellationToken.None);

        Assert.Equal("start", history.Calls[0]);
        Assert.Contains("position", history.Calls);
        Assert.All(history.Calls.Skip(1), call => Assert.NotEqual("start", call));
    }

    /// <summary>A refused durable transition leaves the raid exactly as it was.</summary>
    /// <remarks>
    /// Evidence used to be applied to the live raid state before the outbox was asked to accept
    /// its record. When the store refused, nothing was published, but the state service already
    /// held the new raid; the next observation built on it, and its start was never recorded.
    /// </remarks>
    [Fact]
    public async Task ARefusedDurableTransitionLeavesTheRaidStateUntouched()
    {
        var store = Store();
        var state = new RaidStateService();
        var history = new RecordingRaidHistory();
        var outboxStore = new FixtureOutboxStore(capacity: 1);
        await using var outbox = new RaidHistoryOutbox(history, store: outboxStore);
        var coordinator = new RaidActivityCoordinator(state, outbox, new StubProfileService(), store);

        await Assert.ThrowsAsync<OutboxCapacityException>(() => coordinator.ApplyEvidenceAsync(
            InRaid(),
            CancellationToken.None));

        Assert.Null(state.Current.RaidId);
        Assert.Equal(RaidLifecycleState.Unknown, state.Current.State);
        Assert.Equal(RaidLifecycleState.Unknown, store.Current.Raid.State);
        Assert.Empty(await outboxStore.ListAsync(CancellationToken.None));

        await using var roomy = new RaidHistoryOutbox(history, store: new FixtureOutboxStore());
        var accepted = new RaidActivityCoordinator(state, roomy, new StubProfileService(), store);
        var current = await accepted.ApplyEvidenceAsync(InRaid(), CancellationToken.None);
        await roomy.FlushAsync(CancellationToken.None);

        Assert.Equal(RaidLifecycleState.InRaid, state.Current.State);
        Assert.Equal(current.RaidId, store.Current.Raid.RaidId);
        Assert.Equal(["start", "state"], history.Calls);
    }

    [Fact]
    public async Task ADurableTransitionIsPublishedOnlyAfterItsRecordIsAccepted()
    {
        var store = Store();
        var state = new RaidStateService();
        var outboxStore = new GatedOutboxStore();
        await using var outbox = new RaidHistoryOutbox(new RecordingRaidHistory(), store: outboxStore);
        var coordinator = new RaidActivityCoordinator(state, outbox, new StubProfileService(), store);

        var applying = coordinator.ApplyPositionAsync(Position(), CancellationToken.None);
        await outboxStore.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(state.Current.LastKnownPosition);
        Assert.Null(store.Current.Raid.LastKnownPosition);
        Assert.False(applying.IsCompleted);

        outboxStore.Release.TrySetResult();
        var current = await applying.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(current.LastKnownPosition);
        Assert.Equal(current.RaidId, state.Current.RaidId);
        Assert.Equal(120.5, store.Current.Raid.LastKnownPosition!.Position.X);
    }

    [Fact]
    public async Task DurableHistoryRefusesARaidStateThatCannotStageATransition()
    {
        var store = Store();
        await using var outbox = new RaidHistoryOutbox(new RecordingRaidHistory());
        var coordinator = new RaidActivityCoordinator(
            new UnstagedRaidState(),
            outbox,
            new StubProfileService(),
            store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ApplyEvidenceAsync(
            InRaid(),
            CancellationToken.None));
        Assert.Empty(await outbox.Store.ListAsync(CancellationToken.None));
    }

    private static RaidEvidence InRaid() => new(
        RaidEvidenceKind.LogLine,
        DateTimeOffset.UnixEpoch,
        "bigmap",
        RaidLifecycleState.InRaid,
        Confidence.Certain,
        "in a raid") { StartsNewRaid = true };

    private sealed class GatedOutboxStore : IOutboxStore
    {
        private readonly FixtureOutboxStore _inner = new();

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OutboxEnqueueReceipt> EnqueueAsync(OutboxItem item, CancellationToken cancellationToken) =>
            _inner.EnqueueAsync(item, cancellationToken);

        public async Task<ImmutableArray<OutboxEnqueueReceipt>> EnqueueBatchAsync(
            ImmutableArray<OutboxItem> items,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task;
            return await _inner.EnqueueBatchAsync(items, cancellationToken);
        }

        public Task<ImmutableArray<OutboxStoredItem>> LeaseNextAsync(
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            int maximumCount,
            CancellationToken cancellationToken) =>
            _inner.LeaseNextAsync(nowUtc, leaseDuration, maximumCount, cancellationToken);

        public Task<bool> CompleteAsync(OperationId operationId, OutboxLeaseToken leaseToken, DateTimeOffset completedUtc, CancellationToken cancellationToken) =>
            _inner.CompleteAsync(operationId, leaseToken, completedUtc, cancellationToken);

        public Task<bool> RenewLeaseAsync(
            OperationId operationId,
            OutboxLeaseToken leaseToken,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            _inner.RenewLeaseAsync(operationId, leaseToken, nowUtc, leaseDuration, cancellationToken);

        public Task<bool> RetryAsync(OperationId operationId, OutboxLeaseToken leaseToken, DateTimeOffset retryingUtc, DateTimeOffset notBeforeUtc, RuntimeFault fault, CancellationToken cancellationToken) =>
            _inner.RetryAsync(operationId, leaseToken, retryingUtc, notBeforeUtc, fault, cancellationToken);

        public Task<bool> DeadLetterAsync(OperationId operationId, OutboxLeaseToken leaseToken, RuntimeFault fault, DateTimeOffset deadLetteredUtc, CancellationToken cancellationToken) =>
            _inner.DeadLetterAsync(operationId, leaseToken, fault, deadLetteredUtc, cancellationToken);

        public Task<int> RecoverExpiredLeasesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            _inner.RecoverExpiredLeasesAsync(nowUtc, cancellationToken);

        public Task<bool> ManualRetryAsync(OperationId operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            _inner.ManualRetryAsync(operationId, nowUtc, cancellationToken);

        public Task<bool> ResolveDeadLetterAsync(
            OperationId operationId,
            DateTimeOffset resolvedUtc,
            CancellationToken cancellationToken) =>
            _inner.ResolveDeadLetterAsync(operationId, resolvedUtc, cancellationToken);

        public Task<OutboxSnapshot> GetSnapshotAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            _inner.GetSnapshotAsync(nowUtc, cancellationToken);

        public Task<ImmutableArray<OutboxStoredItem>> ListAsync(CancellationToken cancellationToken) =>
            _inner.ListAsync(cancellationToken);
    }

    /// <summary>A raid state with no way to work a transition out before making it current.</summary>
    private sealed class UnstagedRaidState : IRaidStateService
    {
        private readonly RaidStateService _inner = new();

        public RaidSnapshot Current => _inner.Current;

        public RaidSnapshot Apply(RaidEvidence evidence) => _inner.Apply(evidence);

        public RaidSnapshot ApplyPosition(ScreenshotPosition position) => _inner.ApplyPosition(position);

        public RaidSnapshot ApplyExtracts(
            IReadOnlyList<ActiveExtract> extracts,
            DateTimeOffset observedUtc,
            TimeSpan? raidClock = null,
            IReadOnlyList<string>? linesNotMatched = null,
            IReadOnlyList<string>? transits = null) =>
            _inner.ApplyExtracts(extracts, observedUtc, raidClock, linesNotMatched, transits);

        public RaidSnapshot Adopt(Guid raidId, DateTimeOffset? startedUtc, IReadOnlyList<ScreenshotPosition> trail) =>
            _inner.Adopt(raidId, startedUtc, trail);
    }

    private static RuntimeStateStore Store() => new(new(
        false,
        Offline: true,
        GameMode.Regular,
        "en",
        TimeSpan.FromHours(9),
        TimeSpan.FromMinutes(5)));

    private static ScreenshotPosition Position() => new(
        DateTimeOffset.UnixEpoch,
        new WorldPosition(120.5, 3.0, -45.0),
        new QuaternionOrientation(0, 0, 0, 1),
        90,
        null,
        null,
        "2026-09-13[12-00]_120.5, 3.0, -45.0_0.0, 0.0, 0.0, 1.0_12.00 (0).png");

    private sealed class FailingRaidHistory : IRaidHistoryService
    {
        public Task<Guid> StartAsync(RaidHistoryEntry raid, CancellationToken cancellationToken) =>
            Task.FromResult(raid.Id);

        public Task RecordEventAsync(
            Guid raidId,
            string type,
            DateTimeOffset timestampUtc,
            string payloadJson,
            CancellationToken cancellationToken) =>
            throw new IOException("There is not enough space on the disk.");

        public Task EndAsync(
            Guid raidId,
            DateTimeOffset endUtc,
            string? outcome,
            string? notes,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<RaidHistoryEntry>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RaidHistoryEntry>>([]);

        public Task<IReadOnlyList<string>> ListEventPayloadsAsync(
            Guid raidId,
            string type,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<RaidTrail>> ListTrailsForMapAsync(
            string mapId,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RaidTrail>>([]);

        public Task<IReadOnlyList<ScreenshotPosition>> ListPositionsAsync(
            Guid raidId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScreenshotPosition>>([]);

        public Task ExportCsvAsync(Stream destination, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ExportJsonAsync(Stream destination, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingRaidHistory : IRaidHistoryService
    {
        public List<string> Calls { get; } = [];

        public Task<Guid> StartAsync(RaidHistoryEntry raid, CancellationToken cancellationToken)
        {
            Calls.Add("start");
            return Task.FromResult(raid.Id);
        }

        public Task RecordEventAsync(
            Guid raidId,
            string type,
            DateTimeOffset timestampUtc,
            string payloadJson,
            CancellationToken cancellationToken)
        {
            Calls.Add(type);
            return Task.CompletedTask;
        }

        public Task EndAsync(
            Guid raidId,
            DateTimeOffset endUtc,
            string? outcome,
            string? notes,
            CancellationToken cancellationToken)
        {
            Calls.Add("end");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RaidHistoryEntry>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RaidHistoryEntry>>([]);

        public Task<IReadOnlyList<string>> ListEventPayloadsAsync(
            Guid raidId,
            string type,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<RaidTrail>> ListTrailsForMapAsync(
            string mapId,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RaidTrail>>([]);

        public Task<IReadOnlyList<ScreenshotPosition>> ListPositionsAsync(
            Guid raidId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScreenshotPosition>>([]);

        public Task ExportCsvAsync(Stream destination, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ExportJsonAsync(Stream destination, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubProfileService : IPlayerProfileService
    {
        private static readonly PlayerProfile Profile = new(
            Guid.Parse("6f1c1f4a-0b6e-4a0f-9c2f-2a7a3c4d5e6f"),
            "Coordinator profile",
            GameMode.Regular,
            1,
            Faction.Usec,
            null,
            new Dictionary<string, int>(),
            new HashSet<string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            new HashSet<string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, EventItemState>(),
            new Dictionary<string, string>(),
            DateTimeOffset.UnixEpoch);

        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(Profile);

        public Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => Task.FromResult("{}");

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) =>
            Task.FromResult(Profile);
    }
}
