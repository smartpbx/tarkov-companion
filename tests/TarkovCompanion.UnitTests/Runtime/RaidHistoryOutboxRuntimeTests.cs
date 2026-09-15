using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Quests;
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

        await outbox.AcceptAsync(
            [
                RaidHistoryCommand.StartRaid(new(raidId, Guid.NewGuid(), "customs", "Regular", Epoch, null, null, null)),
                RaidHistoryCommand.RecordPosition(raidId, Position()),
            ],
            default);
        await outbox.AcceptAsync([RaidHistoryCommand.RecordState(raidId, Evidence())], default);
        await outbox.EndAsync(raidId, Epoch.AddMinutes(1), null, null, default);
        await outbox.FlushAsync(default);

        Assert.Equal(["start", "position", "state", "end"], history.Types);
        var stored = await outbox.Store.ListAsync(default);
        Assert.Equal([1L, 2L, 3L, 4L], stored.Select(item => item.Item.AggregateSequence));
        Assert.All(stored, item => Assert.Equal(OutboxDeliveryState.Completed, item.State));
        await RuntimeTestTasks.UntilAsync(() => outbox.Snapshot.Counts.Completed == 4);
    }

    [Fact]
    public async Task StoreFailureIsReturnedBeforeTheWriteIsAcceptedAndPublishedAsHealth()
    {
        await using var outbox = new RaidHistoryOutbox(
            new RecordingHistory(),
            timeProvider: new ManualTimeProvider(Epoch),
            store: new RejectingStore());

        await Assert.ThrowsAsync<IOException>(() => outbox.AcceptAsync(
            [RaidHistoryCommand.RecordState(Guid.NewGuid(), Evidence())],
            default));

        Assert.Equal(0, outbox.Snapshot.Counts.Total);
        Assert.Equal(RuntimeFailureKind.Transient, outbox.Snapshot.LastAcceptanceFault!.Kind);
    }

    /// <summary>A transition's commands are one acceptance, so a refusal leaves no part of it stored.</summary>
    [Fact]
    public async Task ACapacityRefusalLeavesNoPartOfTheTransitionStored()
    {
        var history = new RecordingHistory();
        var store = new FixtureOutboxStore(capacity: 2);
        await using var outbox = new RaidHistoryOutbox(history, timeProvider: new ManualTimeProvider(Epoch), store: store);
        var raidId = Guid.NewGuid();

        await Assert.ThrowsAsync<OutboxCapacityException>(() => outbox.AcceptAsync(
            [
                RaidHistoryCommand.StartRaid(new(raidId, Guid.NewGuid(), "customs", "Regular", Epoch, null, null, null)),
                RaidHistoryCommand.RecordState(raidId, Evidence()),
                RaidHistoryCommand.EndRaid(raidId, Epoch, null, null),
            ],
            default));

        Assert.Empty(await store.ListAsync(default));
        Assert.Equal("outbox-admission-full", outbox.Snapshot.LastAcceptanceFault!.Code.Value);

        await outbox.AcceptAsync([RaidHistoryCommand.RecordState(raidId, Evidence())], default);
        await outbox.FlushAsync(default);
        Assert.Null(outbox.Snapshot.LastAcceptanceFault);
    }

    /// <summary>Cancellation that arrives after the store took the commands cannot unsay it.</summary>
    [Fact]
    public async Task CancellationAfterDurableAcceptanceStillReportsAcceptance()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new CancelAfterAcceptStore(cancellation);
        await using var outbox = new RaidHistoryOutbox(
            new RecordingHistory(),
            timeProvider: new ManualTimeProvider(Epoch),
            store: store);

        var accepted = await outbox.AcceptAsync(
            [RaidHistoryCommand.RecordState(Guid.NewGuid(), Evidence())],
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        var stored = Assert.Single(await store.ListAsync(default));
        Assert.Equal(Assert.Single(accepted), stored.Item.OperationId);
    }

    [Fact]
    public async Task PumpBacksOffAfterStoreFaultsAndManualRecoveryResumesDelivery()
    {
        var time = new ManualTimeProvider(Epoch);
        var history = new RecordingHistory();
        var store = new LeaseFaultStore(failures: 2);
        await using var outbox = new RaidHistoryOutbox(history, timeProvider: time, store: store);

        await outbox.AcceptAsync([RaidHistoryCommand.RecordState(Guid.NewGuid(), Evidence())], default);
        await RuntimeTestTasks.UntilAsync(() => outbox.Snapshot.ConsecutivePumpFaults == 1);
        Assert.Equal(OutboxPumpState.Faulted, outbox.Snapshot.PumpState);
        Assert.Equal("dependency-io", outbox.Snapshot.LastPumpFault!.Code.Value);
        Assert.Empty(history.Types);

        // The pump resumes by itself on injected time rather than waiting for another command.
        await RuntimeTestTasks.AdvanceUntilAsync(
            time,
            TimeSpan.FromSeconds(1),
            () => outbox.Snapshot.ConsecutivePumpFaults >= 2);

        Assert.True(outbox.RequestPumpRecovery());
        await outbox.FlushAsync(default).WaitAsync(TimeSpan.FromSeconds(30));
        await RuntimeTestTasks.UntilAsync(() => outbox.Snapshot.LastPumpFault is null);

        Assert.Equal(["state"], history.Types);
        Assert.NotEqual(OutboxPumpState.Faulted, outbox.Snapshot.PumpState);
        Assert.Equal(0, outbox.Snapshot.ConsecutivePumpFaults);
        Assert.NotNull(outbox.Snapshot.LastSuccessfulPumpUtc);
    }

    [Fact]
    public async Task UnknownAcknowledgementStaysVisibleWithoutBecomingAPumpBackoff()
    {
        var time = new ManualTimeProvider(Epoch);
        var history = new RecordingHistory();
        var outbox = new RaidHistoryOutbox(
            history,
            timeProvider: time,
            store: new AcknowledgementLosingStore());

        await outbox.AcceptAsync(
            [RaidHistoryCommand.RecordState(Guid.NewGuid(), Evidence())],
            default);
        await RuntimeTestTasks.UntilAsync(() =>
            outbox.Snapshot.LastPumpFault?.Code.Value == "outbox-acknowledgement-unknown");

        Assert.Equal(0, outbox.Snapshot.ConsecutivePumpFaults);
        Assert.Equal(["state"], history.Types);
        await outbox.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(OutboxPumpState.Stopped, outbox.Snapshot.PumpState);
    }

    [Fact]
    public async Task LateAcknowledgementSettlementWakesThePumpAndRefreshesHealth()
    {
        var time = new ManualTimeProvider(Epoch);
        var history = new RecordingHistory();
        await using var outbox = new RaidHistoryOutbox(
            history,
            timeProvider: time,
            store: new OneAcknowledgementFaultStore());

        await outbox.AcceptAsync(
            [RaidHistoryCommand.RecordState(Guid.NewGuid(), Evidence())],
            default);
        await RuntimeTestTasks.UntilAsync(() =>
            outbox.Snapshot.LastPumpFault?.Code.Value == "outbox-acknowledgement-pending");

        time.Advance(TimeSpan.FromSeconds(1));
        await RuntimeTestTasks.UntilAsync(() => outbox.Snapshot.Counts.Completed == 1);

        Assert.Null(outbox.Snapshot.LastPumpFault);
        Assert.Equal(0, outbox.Snapshot.ConsecutivePumpFaults);
        Assert.Equal(["state"], history.Types);
    }

    [Fact]
    public async Task GenericEventJsonCannotEnterTheOutbox()
    {
        await using var outbox = new RaidHistoryOutbox(
            new RecordingHistory(),
            timeProvider: new ManualTimeProvider(Epoch));
        var raidId = Guid.NewGuid();

        await Assert.ThrowsAsync<NotSupportedException>(() => outbox.RecordEventAsync(
            raidId,
            "state",
            Epoch,
            JsonSerializer.Serialize(Evidence()),
            default));
        await Assert.ThrowsAsync<NotSupportedException>(() => outbox.RecordEventAsync(
            raidId,
            "position",
            Epoch,
            "{\"position\":{\"x\":12.5},\"filename\":\"2026-09-15_12.5_42.1.png\"}",
            default));

        Assert.Empty(await outbox.Store.ListAsync(default));
    }

    [Fact]
    public async Task APositionTravelsWithoutItsScreenshotFilename()
    {
        var history = new RecordingHistory();
        await using var outbox = new RaidHistoryOutbox(history, timeProvider: new ManualTimeProvider(Epoch));
        var raidId = Guid.NewGuid();

        await outbox.AcceptAsync([RaidHistoryCommand.RecordPosition(raidId, Position())], default);
        await outbox.FlushAsync(default);

        var stored = Assert.Single(await outbox.Store.ListAsync(default));
        var payload = Encoding.UTF8.GetString(stored.Item.Payload.Bytes.AsSpan());
        Assert.Equal(OutboxCommandKind.RaidPositionRecorded, stored.Item.Command);
        Assert.DoesNotContain(Position().Filename, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(".png", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Json", payload, StringComparison.Ordinal);

        var delivered = Assert.Single(history.Payloads);
        var position = JsonSerializer.Deserialize<ScreenshotPosition>(delivered)!;
        Assert.Equal(Position().Position, position.Position);
        Assert.Equal(Position().Timestamp, position.Timestamp);
        Assert.DoesNotContain(".png", position.Filename, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("recorded-position-", position.Filename, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FreeTextNamingAScreenshotIsRefusedBeforeAnythingIsStored()
    {
        await using var outbox = new RaidHistoryOutbox(
            new RecordingHistory(),
            timeProvider: new ManualTimeProvider(Epoch));
        var raidId = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() => outbox.AcceptAsync(
            [RaidHistoryCommand.RecordState(raidId, Evidence() with { Summary = "Read 2026-09-15[12-00]_1.0, 2.0, 3.0.png" })],
            default));
        await Assert.ThrowsAsync<ArgumentException>(() => outbox.AcceptAsync(
            [RaidHistoryCommand.EndRaid(raidId, Epoch, null, "C:\\shots\\2026-09-15.jpg")],
            default));

        Assert.Empty(await outbox.Store.ListAsync(default));
        await outbox.AcceptAsync([RaidHistoryCommand.RecordState(raidId, Evidence())], default);
        Assert.Equal(1, Assert.Single(await outbox.Store.ListAsync(default)).Item.AggregateSequence);
    }

    /// <summary>
    /// Every typed codec must deliver exactly the row a direct store receives. A domain value
    /// with a get-only property — Confidence is one — silently read back as zero through a naive
    /// round trip.
    /// </summary>
    [Fact]
    public async Task TypedCommandsDeliverTheSameRowsAsADirectStore()
    {
        var raidId = Guid.NewGuid();
        var commands = new[]
        {
            RaidHistoryCommand.StartRaid(new(raidId, Guid.NewGuid(), "bigmap", "Regular", Epoch, null, "note", null)),
            RaidHistoryCommand.RecordState(raidId, Evidence() with
            {
                Side = "scav",
                SideBasis = "Ended with status Transfer.",
                StartsNewRaid = true,
                EventId = "event-1",
                ResumesSession = true,
            }),
            RaidHistoryCommand.RecordExtracts(
                raidId,
                Epoch.AddSeconds(5),
                [new ActiveExtract("zb-1011", "ZB-1011", new Confidence(0.72), "extract-list")]),
            RaidHistoryCommand.RecordScan(raidId, new ScanExecutionResult(
                true,
                true,
                "5c12613b86f7743bbe2c3f76",
                "Intelligence folder",
                123_456,
                61_728,
                "Keep",
                new Confidence(0.91),
                Epoch.AddSeconds(6),
                "screenshot-key",
                "Resolved Intelligence folder from an in-memory scan; no pixels were persisted.")),
            RaidHistoryCommand.RecordSale(raidId, new FleaSaleObservation("offer-1", "handbook-1", 3, Epoch.AddSeconds(7))),
            RaidHistoryCommand.RecordQuest(raidId, new QuestStatusObservation(
                "event-2",
                "5936d90786f7742b1420ba5b",
                RecordedTaskState.Completed,
                Epoch.AddSeconds(8))),
            RaidHistoryCommand.EndRaid(raidId, Epoch.AddMinutes(30), "Survived", "Closed by the game."),
        };
        var direct = new RecordingHistory();
        foreach (var command in commands)
        {
            await command.WriteAsync(direct, default);
        }

        var delivered = new RecordingHistory();
        await using (var outbox = new RaidHistoryOutbox(delivered, timeProvider: new ManualTimeProvider(Epoch)))
        {
            await outbox.AcceptAsync(commands, default);
            await outbox.FlushAsync(default);
        }

        Assert.Equal(direct.Types, delivered.Types);
        Assert.Equal(direct.Payloads, delivered.Payloads);
        Assert.Equal(direct.Timestamps, delivered.Timestamps);
        Assert.Equal(direct.Entries, delivered.Entries);
        Assert.Contains(delivered.Payloads, payload => payload.Contains("0.91", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACommandTheClosedCodecCannotReadIsDeadLetteredAndPublished()
    {
        var store = new FixtureOutboxStore();
        var history = new RecordingHistory();
        await using var outbox = new RaidHistoryOutbox(history, timeProvider: new ManualTimeProvider(Epoch), store: store);
        var operationId = OperationId.New();
        await store.EnqueueAsync(
            new OutboxItem(
                operationId,
                new($"key:{operationId}"),
                CorrelationId.New(),
                new("raid-history"),
                OutboxCommandKind.RaidStateRecorded,
                OutboxContractVersion.Current,
                new("raid:corrupt"),
                1,
                Epoch,
                Epoch,
                Epoch.AddHours(1),
                OutboxPayload.CreateGenericJson("{\"state\":\"ready\"}"),
                OutboxAttemptPolicy.Default),
            default);

        Assert.True(outbox.RequestPumpRecovery());
        await RuntimeTestTasks.UntilAsync(() => outbox.Snapshot.DeadLetters.Length == 1);

        var deadLetter = Assert.Single(outbox.Snapshot.DeadLetters);
        Assert.Equal(operationId, deadLetter.OperationId);
        Assert.Equal("outbox-payload-invalid", deadLetter.LastFault!.Code.Value);
        Assert.Equal(1, deadLetter.AttemptCount);
        Assert.Empty(history.Types);
    }

    [Fact]
    public async Task ManualDeadLetterRetryDeliversAndClearsTheHealthEntry()
    {
        var history = new RecordingHistory { FailuresBeforeSuccess = 1 };
        await using var outbox = new RaidHistoryOutbox(history, timeProvider: new ManualTimeProvider(Epoch));
        var published = 0;
        outbox.Changed += (_, _) => Interlocked.Increment(ref published);

        var accepted = await outbox.AcceptAsync([RaidHistoryCommand.RecordState(Guid.NewGuid(), Evidence())], default);
        await outbox.FlushAsync(default);
        await RuntimeTestTasks.UntilAsync(() => outbox.Snapshot.DeadLetters.Length == 1);
        Assert.Equal(Assert.Single(accepted), Assert.Single(outbox.Snapshot.DeadLetters).OperationId);
        Assert.True(published > 0);

        Assert.True(await outbox.RetryDeadLetterAsync(Assert.Single(accepted), default));
        await outbox.FlushAsync(default);
        await RuntimeTestTasks.UntilAsync(() => outbox.Snapshot.DeadLetters.IsEmpty);

        Assert.Equal(["state"], history.Types);
        Assert.Equal(1, outbox.Snapshot.Counts.Completed);
    }

    [Fact]
    public async Task ExplicitResolutionUnwedgesADeadLetterWithoutReplayingIt()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore(capacity: 2);
        var history = new RecordingHistory { FailuresBeforeSuccess = 1 };
        await using var outbox = new RaidHistoryOutbox(history, timeProvider: time, store: store);
        var raidId = Guid.NewGuid();

        var accepted = await outbox.AcceptAsync(
            [RaidHistoryCommand.RecordState(raidId, Evidence()), RaidHistoryCommand.EndRaid(raidId, Epoch, null, null)],
            default);
        await RuntimeTestTasks.UntilAsync(() => outbox.Snapshot.DeadLetters.Length == 1);

        var deadLetter = Assert.Single(outbox.Snapshot.DeadLetters);
        Assert.True(deadLetter.CanRetry);
        Assert.True(await outbox.ResolveDeadLetterAsync(Assert.Single(accepted), default));
        await outbox.FlushAsync(default);

        Assert.Equal(["end"], history.Types);
        Assert.Equal(OutboxDeliveryState.Completed, Assert.Single(
            await store.ListAsync(default), item => item.Item.OperationId == accepted[1]).State);
    }

    [Fact]
    public async Task DisposeWaitsForAnAcceptanceAlreadyStoringItsCommands()
    {
        var store = new BlockingEnqueueStore();
        var history = new RecordingHistory();
        var outbox = new RaidHistoryOutbox(history, timeProvider: new ManualTimeProvider(Epoch), store: store);

        var accepting = outbox.AcceptAsync([RaidHistoryCommand.RecordState(Guid.NewGuid(), Evidence())], default);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var disposing = outbox.DisposeAsync().AsTask();
        await RuntimeTestTasks.DrainAsync();
        Assert.False(disposing.IsCompleted);

        store.Release.TrySetResult();
        var accepted = await accepting.WaitAsync(TimeSpan.FromSeconds(30));
        await disposing.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Single(accepted);
        Assert.Equal(["state"], history.Types);
        Assert.Equal(OutboxPumpState.Stopped, outbox.Snapshot.PumpState);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => outbox.AcceptAsync(
            [RaidHistoryCommand.RecordState(Guid.NewGuid(), Evidence())],
            default));
    }

    [Fact]
    public async Task TimedOutDisposeCancelsItsWaiterAndLaterDisposeCanFinish()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new BlockingEnqueueStore();
        var outbox = new RaidHistoryOutbox(new RecordingHistory(), timeProvider: time, store: store);

        var accepting = outbox.AcceptAsync([RaidHistoryCommand.RecordState(Guid.NewGuid(), Evidence())], default);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var firstDispose = outbox.DisposeAsync().AsTask();
        time.Advance(TimeSpan.FromSeconds(10));
        await firstDispose.WaitAsync(TimeSpan.FromSeconds(30));

        // The bounded wait was cancelled rather than left queued. Once acceptance releases its
        // lock, a second disposal can take the lock and complete normally.
        store.Release.TrySetResult();
        await accepting.WaitAsync(TimeSpan.FromSeconds(30));
        await outbox.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OutboxPumpState.Stopped, outbox.Snapshot.PumpState);
    }

    private static RaidEvidence Evidence() => new(
        RaidEvidenceKind.LogLine,
        Epoch,
        "bigmap",
        RaidLifecycleState.InRaid,
        new Confidence(0.97),
        "fixture raid state");

    private static ScreenshotPosition Position() => new(
        Epoch,
        new(120.5, 3, -45),
        new(0, 0, 0, 1),
        90,
        TimeSpan.FromMinutes(12),
        2,
        "2026-09-15[12-00]_120.5, 3, -45_0, 0, 0, 1_12.00 (2).png");

    private sealed class RecordingHistory : IRaidHistoryService
    {
        private readonly Lock _gate = new();
        private readonly List<string> _types = [];
        private readonly List<string> _payloads = [];
        private readonly List<DateTimeOffset> _timestamps = [];
        private readonly List<string> _entries = [];
        private int _failures;

        public int FailuresBeforeSuccess { get; init; }

        public IReadOnlyList<string> Types => Copy(_types);

        public IReadOnlyList<string> Payloads => Copy(_payloads);

        public IReadOnlyList<DateTimeOffset> Timestamps => Copy(_timestamps);

        public IReadOnlyList<string> Entries => Copy(_entries);

        public Task<Guid> StartAsync(RaidHistoryEntry raid, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _types.Add("start");
                _entries.Add(JsonSerializer.Serialize(raid));
            }

            return Task.FromResult(raid.Id);
        }

        public Task RecordEventAsync(
            Guid raidId,
            string type,
            DateTimeOffset timestampUtc,
            string payloadJson,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_failures < FailuresBeforeSuccess)
                {
                    _failures++;
                    return Task.FromException(new RuntimeFaultException(new(
                        RuntimeFailureKind.Validation,
                        new("fixture-rejected"),
                        RuntimeRecoveryAction.RetryManually,
                        new("test:dead-letter"),
                        timestampUtc)));
                }

                _types.Add(type);
                _payloads.Add(payloadJson);
                _timestamps.Add(timestampUtc);
            }

            return Task.CompletedTask;
        }

        public Task EndAsync(
            Guid raidId,
            DateTimeOffset endUtc,
            string? outcome,
            string? notes,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _types.Add("end");
                _entries.Add($"{raidId:N}|{endUtc:O}|{outcome}|{notes}");
            }

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

        private IReadOnlyList<T> Copy<T>(List<T> source)
        {
            lock (_gate)
            {
                return [.. source];
            }
        }
    }

    /// <summary>Delegates every store operation to a fixture store; subclasses override one seam.</summary>
    private class DelegatingStore : IOutboxStore
    {
        protected FixtureOutboxStore Inner { get; } = new();

        public virtual Task<OutboxEnqueueReceipt> EnqueueAsync(OutboxItem item, CancellationToken cancellationToken) =>
            Inner.EnqueueAsync(item, cancellationToken);

        public virtual Task<ImmutableArray<OutboxEnqueueReceipt>> EnqueueBatchAsync(
            ImmutableArray<OutboxItem> items,
            CancellationToken cancellationToken) =>
            Inner.EnqueueBatchAsync(items, cancellationToken);

        public virtual Task<ImmutableArray<OutboxStoredItem>> LeaseNextAsync(
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            int maximumCount,
            CancellationToken cancellationToken) =>
            Inner.LeaseNextAsync(nowUtc, leaseDuration, maximumCount, cancellationToken);

        public virtual Task<bool> CompleteAsync(OperationId operationId, OutboxLeaseToken leaseToken, DateTimeOffset completedUtc, CancellationToken cancellationToken) =>
            Inner.CompleteAsync(operationId, leaseToken, completedUtc, cancellationToken);

        public Task<bool> RenewLeaseAsync(
            OperationId operationId,
            OutboxLeaseToken leaseToken,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            Inner.RenewLeaseAsync(operationId, leaseToken, nowUtc, leaseDuration, cancellationToken);

        public Task<bool> RetryAsync(OperationId operationId, OutboxLeaseToken leaseToken, DateTimeOffset retryingUtc, DateTimeOffset notBeforeUtc, RuntimeFault fault, CancellationToken cancellationToken) =>
            Inner.RetryAsync(operationId, leaseToken, retryingUtc, notBeforeUtc, fault, cancellationToken);

        public Task<bool> DeadLetterAsync(OperationId operationId, OutboxLeaseToken leaseToken, RuntimeFault fault, DateTimeOffset deadLetteredUtc, CancellationToken cancellationToken) =>
            Inner.DeadLetterAsync(operationId, leaseToken, fault, deadLetteredUtc, cancellationToken);

        public Task<int> RecoverExpiredLeasesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            Inner.RecoverExpiredLeasesAsync(nowUtc, cancellationToken);

        public Task<bool> ManualRetryAsync(OperationId operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            Inner.ManualRetryAsync(operationId, nowUtc, cancellationToken);

        public Task<bool> ResolveDeadLetterAsync(
            OperationId operationId,
            DateTimeOffset resolvedUtc,
            CancellationToken cancellationToken) =>
            Inner.ResolveDeadLetterAsync(operationId, resolvedUtc, cancellationToken);

        public Task<OutboxSnapshot> GetSnapshotAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            Inner.GetSnapshotAsync(nowUtc, cancellationToken);

        public Task<ImmutableArray<OutboxStoredItem>> ListAsync(CancellationToken cancellationToken) =>
            Inner.ListAsync(cancellationToken);
    }

    private sealed class RejectingStore : DelegatingStore
    {
        public override Task<ImmutableArray<OutboxEnqueueReceipt>> EnqueueBatchAsync(
            ImmutableArray<OutboxItem> items,
            CancellationToken cancellationToken) =>
            Task.FromException<ImmutableArray<OutboxEnqueueReceipt>>(new IOException("private store failure"));
    }

    private sealed class AcknowledgementLosingStore : DelegatingStore
    {
        public override Task<bool> CompleteAsync(
            OperationId operationId,
            OutboxLeaseToken leaseToken,
            DateTimeOffset completedUtc,
            CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class OneAcknowledgementFaultStore : DelegatingStore
    {
        private int _completeCalls;

        public override Task<bool> CompleteAsync(
            OperationId operationId,
            OutboxLeaseToken leaseToken,
            DateTimeOffset completedUtc,
            CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _completeCalls) == 1
                ? Task.FromException<bool>(new IOException("acknowledgement unavailable"))
                : base.CompleteAsync(operationId, leaseToken, completedUtc, cancellationToken);
    }

    private sealed class CancelAfterAcceptStore(CancellationTokenSource cancellation) : DelegatingStore
    {
        public override async Task<ImmutableArray<OutboxEnqueueReceipt>> EnqueueBatchAsync(
            ImmutableArray<OutboxItem> items,
            CancellationToken cancellationToken)
        {
            var receipts = await Inner.EnqueueBatchAsync(items, cancellationToken);
            await cancellation.CancelAsync();
            return receipts;
        }
    }

    private sealed class LeaseFaultStore(int failures) : DelegatingStore
    {
        private int _remaining = failures;

        public override Task<ImmutableArray<OutboxStoredItem>> LeaseNextAsync(
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            int maximumCount,
            CancellationToken cancellationToken) =>
            Interlocked.Decrement(ref _remaining) >= 0
                ? Task.FromException<ImmutableArray<OutboxStoredItem>>(new IOException("private lease failure"))
                : base.LeaseNextAsync(nowUtc, leaseDuration, maximumCount, cancellationToken);
    }

    private sealed class BlockingEnqueueStore : DelegatingStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<ImmutableArray<OutboxEnqueueReceipt>> EnqueueBatchAsync(
            ImmutableArray<OutboxItem> items,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task;
            return await Inner.EnqueueBatchAsync(items, cancellationToken);
        }
    }

    private sealed class ExactJitter : IRetryJitter
    {
        public TimeSpan Apply(RetryJitterContext context) => context.BaseDelay;
    }
}
