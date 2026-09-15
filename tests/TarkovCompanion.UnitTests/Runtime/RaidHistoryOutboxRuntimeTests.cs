using System.Collections.Immutable;
using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Runtime;

public sealed class RaidHistoryOutboxRuntimeTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RaidStartEventsAndEndReplayInAggregateOrder()
    {
        var history = new RecordingHistory();
        await using var outbox = new RaidHistoryOutbox(
            history,
            timeProvider: new ManualTimeProvider(Epoch),
            jitter: new ExactJitter());
        var raidId = Guid.NewGuid();

        await outbox.StartAsync(
            new(raidId, Guid.NewGuid(), "customs", "Regular", Epoch, null, null, null),
            default);
        await outbox.RecordEventAsync(raidId, "position", Epoch, "{}", default);
        await outbox.RecordEventAsync(raidId, "state", Epoch, "{}", default);
        await outbox.EndAsync(raidId, Epoch.AddMinutes(1), null, null, default);
        await outbox.FlushAsync(default);

        Assert.Equal(["start", "position", "state", "end"], history.Calls);
        Assert.Equal(4, outbox.Snapshot.Counts.Completed);
        var stored = await outbox.Store.ListAsync(default);
        Assert.Equal([1L, 2L, 3L, 4L], stored.Select(item => item.Item.AggregateSequence));
        Assert.All(stored, item => Assert.Equal(OutboxDeliveryState.Completed, item.State));
    }

    [Fact]
    public async Task StoreFailureIsReturnedBeforeTheWriteIsAccepted()
    {
        await using var outbox = new RaidHistoryOutbox(
            new RecordingHistory(),
            timeProvider: new ManualTimeProvider(Epoch),
            store: new RejectingStore());

        await Assert.ThrowsAsync<IOException>(() => outbox.RecordEventAsync(
            Guid.NewGuid(),
            "position",
            Epoch,
            "{}",
            default));

        Assert.Equal(0, outbox.Snapshot.Counts.Total);
    }

    private sealed class RecordingHistory : IRaidHistoryService
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

        public Task<IReadOnlyList<ScreenshotPosition>> ListPositionsAsync(
            Guid raidId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScreenshotPosition>>([]);

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

        public Task ExportCsvAsync(Stream destination, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ExportJsonAsync(Stream destination, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RejectingStore : IOutboxStore
    {
        public Task<OutboxEnqueueReceipt> EnqueueAsync(OutboxItem item, CancellationToken cancellationToken) =>
            Task.FromException<OutboxEnqueueReceipt>(new IOException("private store failure"));

        public Task<ImmutableArray<OutboxStoredItem>> LeaseNextAsync(
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            int maximumCount,
            CancellationToken cancellationToken) =>
            Task.FromResult<ImmutableArray<OutboxStoredItem>>([]);

        public Task<bool> CompleteAsync(OperationId operationId, OutboxLeaseToken leaseToken, DateTimeOffset completedUtc, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> RetryAsync(OperationId operationId, OutboxLeaseToken leaseToken, DateTimeOffset notBeforeUtc, RuntimeFault fault, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> DeadLetterAsync(OperationId operationId, OutboxLeaseToken leaseToken, RuntimeFault fault, DateTimeOffset deadLetteredUtc, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<int> RecoverExpiredLeasesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<bool> ManualRetryAsync(OperationId operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<OutboxSnapshot> GetSnapshotAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            Task.FromResult(OutboxSnapshot.Empty);

        public Task<ImmutableArray<OutboxStoredItem>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ImmutableArray<OutboxStoredItem>>([]);
    }

    private sealed class ExactJitter : IRetryJitter
    {
        public TimeSpan Apply(RetryJitterContext context) => context.BaseDelay;
    }
}
