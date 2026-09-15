using TarkovCompanion.Application.Services.Execution;

namespace TarkovCompanion.UnitTests.Runtime;

public sealed class OutboxProcessorTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DuplicateIdempotencyKeyReturnsOriginalReceipt()
    {
        var store = new FixtureOutboxStore();
        var first = Item("aggregate-a", 1, new("same-key"));
        var duplicate = Item("aggregate-a", 1, new("same-key"));

        var accepted = await store.EnqueueAsync(first, default);
        var repeated = await store.EnqueueAsync(duplicate, default);

        Assert.True(accepted.Added);
        Assert.False(repeated.Added);
        Assert.Equal(first.OperationId, repeated.OperationId);
        Assert.Single(await store.ListAsync(default));
    }

    [Fact]
    public async Task BoundedStoreRejectsBeforeAcceptanceInsteadOfDropping()
    {
        var store = new FixtureOutboxStore(capacity: 1);
        await EnqueueAsync(store, Item("aggregate-a", 1));

        await Assert.ThrowsAsync<OutboxCapacityException>(() =>
            store.EnqueueAsync(Item("aggregate-b", 1), default));
        Assert.Single(await store.ListAsync(default));
    }

    [Fact]
    public async Task PoisonCommandBlocksOnlyItsAggregate()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore();
        var handler = new RecordingHandler(item =>
            item.AggregateId.Value == "aggregate-a" && item.AggregateSequence == 1
                ? new RuntimeFaultException(new(
                    RuntimeFailureKind.Validation,
                    new("poison-command"),
                    RuntimeRecoveryAction.None,
                    new("test:poison"),
                    time.GetUtcNow()))
                : null);
        var processor = new OutboxProcessor(store, handler, time, new ExactJitter());
        await EnqueueAsync(store, Item("aggregate-a", 1));
        await EnqueueAsync(store, Item("aggregate-a", 2));
        await EnqueueAsync(store, Item("aggregate-b", 1));
        await EnqueueAsync(store, Item("aggregate-b", 2));

        var first = await processor.ProcessBatchAsync(10, TimeSpan.FromSeconds(10), default);
        var second = await processor.ProcessBatchAsync(10, TimeSpan.FromSeconds(10), default);
        var states = await store.ListAsync(default);

        Assert.Equal(2, first.Leased);
        Assert.Equal(1, first.DeadLettered);
        Assert.Equal(1, second.Leased);
        Assert.DoesNotContain(handler.Delivered, item =>
            item.AggregateId.Value == "aggregate-a" && item.AggregateSequence == 2);
        Assert.Contains(handler.Delivered, item =>
            item.AggregateId.Value == "aggregate-b" && item.AggregateSequence == 2);
        Assert.Equal(
            OutboxDeliveryState.Pending,
            Assert.Single(states, item => item.Item.AggregateId.Value == "aggregate-a" && item.Item.AggregateSequence == 2).State);
    }

    [Fact]
    public async Task ExpiredLeaseIsRecoveredAndOldTokenCannotCommit()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore();
        var item = Item("aggregate-a", 1);
        await EnqueueAsync(store, item);
        var first = Assert.Single(await store.LeaseNextAsync(
            time.GetUtcNow(),
            TimeSpan.FromSeconds(5),
            1,
            default));

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, await store.RecoverExpiredLeasesAsync(time.GetUtcNow(), default));
        var replay = Assert.Single(await store.LeaseNextAsync(
            time.GetUtcNow(),
            TimeSpan.FromSeconds(5),
            1,
            default));

        Assert.NotEqual(first.LeaseToken, replay.LeaseToken);
        Assert.Equal(2, replay.AttemptCount);
        Assert.False(await store.CompleteAsync(item.OperationId, first.LeaseToken!.Value, time.GetUtcNow(), default));
        Assert.True(await store.CompleteAsync(item.OperationId, replay.LeaseToken!.Value, time.GetUtcNow(), default));
    }

    [Fact]
    public async Task LeaseCrashAtMaxAttemptsDeadLettersInsteadOfExceedingTheBudget()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore();
        var item = Item("aggregate-a", 1, maxAttempts: 1);
        await EnqueueAsync(store, item);
        await store.LeaseNextAsync(time.GetUtcNow(), TimeSpan.FromSeconds(5), 1, default);

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, await store.RecoverExpiredLeasesAsync(time.GetUtcNow(), default));

        var stored = Assert.Single(await store.ListAsync(default));
        Assert.Equal(OutboxDeliveryState.DeadLetter, stored.State);
        Assert.Equal(1, stored.AttemptCount);
        Assert.Empty(await store.LeaseNextAsync(time.GetUtcNow(), TimeSpan.FromSeconds(5), 1, default));
    }

    [Fact]
    public async Task TerminalRowsRemainInspectableWithoutPermanentlyConsumingCapacity()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore(capacity: 1);
        var completed = Item("aggregate-a", 1);
        await EnqueueAsync(store, completed);
        var completedLease = Assert.Single(await store.LeaseNextAsync(
            time.GetUtcNow(), TimeSpan.FromSeconds(5), 1, default));
        Assert.True(await store.CompleteAsync(
            completed.OperationId,
            completedLease.LeaseToken!.Value,
            time.GetUtcNow(),
            default));

        var deadLetter = Item("aggregate-b", 1);
        await EnqueueAsync(store, deadLetter);
        var deadLease = Assert.Single(await store.LeaseNextAsync(
            time.GetUtcNow(), TimeSpan.FromSeconds(5), 1, default));
        Assert.True(await store.DeadLetterAsync(
            deadLetter.OperationId,
            deadLease.LeaseToken!.Value,
            new(
                RuntimeFailureKind.Validation,
                new("fixture-poison"),
                RuntimeRecoveryAction.RetryManually,
                new("test:capacity"),
                time.GetUtcNow()),
            time.GetUtcNow(),
            default));

        await EnqueueAsync(store, Item("aggregate-c", 1));
        Assert.Equal(3, (await store.ListAsync(default)).Length);
    }

    [Fact]
    public async Task BatchAcceptanceStoresEveryItemOrNone()
    {
        var store = new FixtureOutboxStore(capacity: 2);

        await Assert.ThrowsAsync<OutboxCapacityException>(() => store.EnqueueBatchAsync(
            [Item("aggregate-a", 1), Item("aggregate-a", 2), Item("aggregate-a", 3)],
            default));
        Assert.Empty(await store.ListAsync(default));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.EnqueueBatchAsync(
            [Item("aggregate-b", 1), Item("aggregate-b", 1)],
            default));
        Assert.Empty(await store.ListAsync(default));

        var receipts = await store.EnqueueBatchAsync([Item("aggregate-c", 1), Item("aggregate-c", 2)], default);
        Assert.All(receipts, receipt => Assert.True(receipt.Added));
        Assert.Equal([1L, 2L], (await store.ListAsync(default)).Select(item => item.Item.AggregateSequence));
    }

    [Fact]
    public async Task CompletedRowsAreRetainedOnlyWithinTheirBound()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore(capacity: 10, completedRetention: 2);
        var processor = new OutboxProcessor(store, new RecordingHandler(_ => null), time, new ExactJitter());
        for (var sequence = 1; sequence <= 5; sequence++)
        {
            await EnqueueAsync(store, Item($"aggregate-{sequence}", 1));
        }

        var result = await processor.ProcessBatchAsync(10, TimeSpan.FromSeconds(5), default);

        Assert.Equal(5, result.Completed);
        var stored = await store.ListAsync(default);
        Assert.Equal(2, stored.Length);
        Assert.All(stored, item => Assert.Equal(OutboxDeliveryState.Completed, item.State));
        Assert.Equal(2, (await store.GetSnapshotAsync(time.GetUtcNow(), default)).Counts.Completed);
    }

    [Fact]
    public async Task DeadLetterHealthNamesTheCommandWithoutItsPayload()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore();
        var handler = new RecordingHandler(_ => new RuntimeFaultException(new(
            RuntimeFailureKind.Validation,
            new("poison-command"),
            RuntimeRecoveryAction.None,
            new("test:poison"),
            time.GetUtcNow())));
        var processor = new OutboxProcessor(store, handler, time, new ExactJitter());
        var poison = Item("aggregate-a", 1);
        await EnqueueAsync(store, poison);
        await EnqueueAsync(store, Item("aggregate-a", 2));

        await processor.ProcessBatchAsync(10, TimeSpan.FromSeconds(5), default);
        var snapshot = await store.GetSnapshotAsync(time.GetUtcNow(), default);

        var deadLetter = Assert.Single(snapshot.DeadLetters);
        Assert.Equal(poison.OperationId, deadLetter.OperationId);
        Assert.Equal(OutboxCommandKind.RaidStateRecorded, deadLetter.Command);
        Assert.Equal("poison-command", deadLetter.LastFault!.Code.Value);
        Assert.Equal(1, snapshot.Counts.DeadLetter);
        Assert.Equal(1, snapshot.Counts.Pending);
        Assert.DoesNotContain(
            typeof(OutboxDeadLetterSnapshot).GetProperties(),
            property => property.PropertyType == typeof(OutboxPayload)
                || property.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ManualRetryIsBoundedByOutstandingCapacity()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore(capacity: 1);
        var shouldFail = true;
        var handler = new DelegateHandler((item, context, token) => shouldFail
            ? Task.FromException(new RuntimeFaultException(new(
                RuntimeFailureKind.Validation,
                new("poison-command"),
                RuntimeRecoveryAction.None,
                new("test:bounded-retry"),
                time.GetUtcNow())))
            : Task.CompletedTask);
        var processor = new OutboxProcessor(store, handler, time, new ExactJitter());
        var poison = Item("aggregate-a", 1);
        await EnqueueAsync(store, poison);
        await processor.ProcessBatchAsync(1, TimeSpan.FromSeconds(5), default);
        await EnqueueAsync(store, Item("aggregate-b", 1));

        Assert.False(await store.ManualRetryAsync(poison.OperationId, time.GetUtcNow(), default));

        shouldFail = false;
        await processor.ProcessBatchAsync(1, TimeSpan.FromSeconds(5), default);
        Assert.True(await store.ManualRetryAsync(poison.OperationId, time.GetUtcNow(), default));
    }

    [Fact]
    public async Task RetryAndCrashReplayKeepTheSameIdempotencyKey()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore();
        var keys = new List<IdempotencyKey>();
        var sideEffects = 0;
        var handler = new DelegateHandler((item, context, token) =>
        {
            keys.Add(context.IdempotencyKey);
            sideEffects++;
            return sideEffects == 1
                ? Task.FromException(new IOException("crash after side effect"))
                : Task.CompletedTask;
        });
        var processor = new OutboxProcessor(store, handler, time, new ExactJitter());
        var item = Item("aggregate-a", 1);
        await EnqueueAsync(store, item);

        var first = await processor.ProcessBatchAsync(1, TimeSpan.FromSeconds(5), default);
        Assert.Equal(1, first.Retrying);
        Assert.Equal(OutboxDeliveryState.Retrying, Assert.Single(await store.ListAsync(default)).State);

        time.Advance(TimeSpan.FromMilliseconds(100));
        var replay = await processor.ProcessBatchAsync(1, TimeSpan.FromSeconds(5), default);

        Assert.Equal(1, replay.Completed);
        Assert.Equal(2, sideEffects);
        Assert.Single(keys.Distinct());
        Assert.Equal(item.IdempotencyKey, keys[0]);
    }

    [Fact]
    public async Task AttemptTimeoutRetriesWhenTheHandlerIgnoresCancellation()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore();
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken attemptToken = default;
        var handler = new DelegateHandler((item, context, token) =>
        {
            attemptToken = token;
            return never.Task;
        });
        var processor = new OutboxProcessor(store, handler, time, new ExactJitter());
        await EnqueueAsync(store, Item("aggregate-a", 1));

        var processing = processor.ProcessBatchAsync(1, TimeSpan.FromSeconds(5), default);
        await RuntimeTestTasks.DrainAsync();
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await processing;

        Assert.Equal(1, result.Retrying);
        Assert.True(attemptToken.IsCancellationRequested);
        Assert.False(never.Task.IsCompleted);
        Assert.Equal(OutboxDeliveryState.Retrying, Assert.Single(await store.ListAsync(default)).State);
    }

    [Fact]
    public async Task DeadLetterCanBeRetriedManually()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore();
        var shouldFail = true;
        var handler = new DelegateHandler((item, context, token) => shouldFail
            ? Task.FromException(new RuntimeFaultException(new(
                RuntimeFailureKind.Unsupported,
                new("unsupported-command"),
                RuntimeRecoveryAction.Upgrade,
                new("test:manual"),
                time.GetUtcNow())))
            : Task.CompletedTask);
        var processor = new OutboxProcessor(store, handler, time, new ExactJitter());
        var item = Item("aggregate-a", 1);
        await EnqueueAsync(store, item);
        await processor.ProcessBatchAsync(1, TimeSpan.FromSeconds(5), default);
        Assert.Equal(OutboxDeliveryState.DeadLetter, Assert.Single(await store.ListAsync(default)).State);

        shouldFail = false;
        Assert.True(await store.ManualRetryAsync(item.OperationId, time.GetUtcNow(), default));
        await processor.ProcessBatchAsync(1, TimeSpan.FromSeconds(5), default);

        Assert.Equal(OutboxDeliveryState.Completed, Assert.Single(await store.ListAsync(default)).State);
    }

    [Fact]
    public async Task ExpiredCommandDeadLettersWithoutCallingHandler()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore();
        var handler = new RecordingHandler(_ => null);
        await EnqueueAsync(store, Item("aggregate-a", 1, expiresUtc: Epoch.AddSeconds(1)));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(await store.LeaseNextAsync(time.GetUtcNow(), TimeSpan.FromSeconds(5), 1, default));
        Assert.Empty(handler.Delivered);
        Assert.Equal(OutboxDeliveryState.DeadLetter, Assert.Single(await store.ListAsync(default)).State);
    }

    private static async Task EnqueueAsync(FixtureOutboxStore store, OutboxItem item) =>
        Assert.True((await store.EnqueueAsync(item, default)).Added);

    private static OutboxItem Item(
        string aggregate,
        long sequence,
        IdempotencyKey? key = null,
        DateTimeOffset? expiresUtc = null,
        int maxAttempts = 3)
    {
        var operation = OperationId.New();
        return new(
            operation,
            key ?? new($"key:{operation}"),
            CorrelationId.New(),
            new("outbox-test"),
            OutboxCommandKind.RaidStateRecorded,
            OutboxContractVersion.Current,
            new(aggregate),
            sequence,
            Epoch,
            Epoch,
            expiresUtc ?? Epoch.AddHours(1),
            OutboxPayload.CreateGenericJson("{\"state\":\"ready\"}"),
            new(
                maxAttempts,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromSeconds(1),
                2));
    }

    private sealed class ExactJitter : IRetryJitter
    {
        public TimeSpan Apply(RetryJitterContext context) => context.BaseDelay;
    }

    private sealed class RecordingHandler(Func<OutboxItem, Exception?> failure) : IOutboxCommandHandler
    {
        public List<OutboxItem> Delivered { get; } = [];

        public Task HandleAsync(
            OutboxItem item,
            OutboxDeliveryContext context,
            CancellationToken cancellationToken)
        {
            Delivered.Add(item);
            return failure(item) is { } exception
                ? Task.FromException(exception)
                : Task.CompletedTask;
        }
    }

    private sealed class DelegateHandler(
        Func<OutboxItem, OutboxDeliveryContext, CancellationToken, Task> handle) : IOutboxCommandHandler
    {
        public Task HandleAsync(
            OutboxItem item,
            OutboxDeliveryContext context,
            CancellationToken cancellationToken) =>
            handle(item, context, cancellationToken);
    }
}
