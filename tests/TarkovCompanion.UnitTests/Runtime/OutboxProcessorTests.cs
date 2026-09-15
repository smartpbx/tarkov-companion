using System.Collections.Concurrent;
using System.Collections.Immutable;
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
        await using var processor = new OutboxProcessor(store, handler, time, new ExactJitter());
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
        var expiredFault = new RuntimeFault(
            RuntimeFailureKind.Transient,
            new("expired-lease"),
            RuntimeRecoveryAction.RetryAutomatically,
            new("test:expired-lease"),
            time.GetUtcNow());
        Assert.False(await store.RetryAsync(
            item.OperationId,
            first.LeaseToken!.Value,
            time.GetUtcNow(),
            time.GetUtcNow().AddSeconds(1),
            expiredFault,
            default));
        Assert.False(await store.DeadLetterAsync(
            item.OperationId,
            first.LeaseToken!.Value,
            expiredFault,
            time.GetUtcNow(),
            default));
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
    public async Task DeadLettersHoldBoundedCapacityUntilExplicitResolution()
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

        await Assert.ThrowsAsync<OutboxCapacityException>(() => store.EnqueueAsync(Item("aggregate-c", 1), default));
        Assert.True(await store.ResolveDeadLetterAsync(deadLetter.OperationId, time.GetUtcNow(), default));
        await EnqueueAsync(store, Item("aggregate-c", 1));
        Assert.Equal(2, (await store.ListAsync(default)).Length);
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
        await using var processor = new OutboxProcessor(
            store,
            new RecordingHandler(_ => null),
            time,
            new ExactJitter());
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
        await using var processor = new OutboxProcessor(store, handler, time, new ExactJitter());
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
    public async Task ManualRetryDoesNotNeedAdditionalCapacityAfterDeadLetterAdmission()
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
        await using var processor = new OutboxProcessor(store, handler, time, new ExactJitter());
        var poison = Item("aggregate-a", 1);
        await EnqueueAsync(store, poison);
        await processor.ProcessBatchAsync(1, TimeSpan.FromSeconds(5), default);
        await Assert.ThrowsAsync<OutboxCapacityException>(() => store.EnqueueAsync(Item("aggregate-b", 1), default));

        shouldFail = false;
        Assert.True(await store.ManualRetryAsync(poison.OperationId, time.GetUtcNow(), default));
        await processor.ProcessBatchAsync(1, TimeSpan.FromSeconds(5), default);
        Assert.Equal(OutboxDeliveryState.Completed, Assert.Single(await store.ListAsync(default)).State);
    }

    [Fact]
    public async Task RetryAndCrashReplayKeepTheSameIdempotencyKey()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore();
        var keys = new ConcurrentQueue<IdempotencyKey>();
        var sideEffects = 0;
        var handler = new DelegateHandler((item, context, token) =>
        {
            keys.Enqueue(context.IdempotencyKey);
            var invocation = Interlocked.Increment(ref sideEffects);
            return invocation == 1
                ? Task.FromException(new IOException("crash after side effect"))
                : Task.CompletedTask;
        });
        await using var processor = new OutboxProcessor(store, handler, time, new ExactJitter());
        var item = Item("aggregate-a", 1);
        await EnqueueAsync(store, item);

        var first = await processor.ProcessBatchAsync(1, TimeSpan.FromSeconds(5), default);
        Assert.Equal(1, first.Retrying);
        Assert.Equal(OutboxDeliveryState.Retrying, Assert.Single(await store.ListAsync(default)).State);

        time.Advance(TimeSpan.FromMilliseconds(100));
        var replay = await processor.ProcessBatchAsync(1, TimeSpan.FromSeconds(5), default);

        Assert.Equal(1, replay.Completed);
        Assert.Equal(2, Volatile.Read(ref sideEffects));
        Assert.Single(keys.Distinct());
        Assert.Equal(item.IdempotencyKey, keys.First());
    }

    [Fact]
    public async Task AttemptDeadlineReturnsWhileABlockedPrefixCannotHoldUnrelatedWork()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unrelated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var synchronousPrefix = new ManualResetEventSlim();
        var handler = new DelegateHandler((item, context, token) =>
        {
            if (item.AggregateId.Value == "aggregate-a")
            {
                entered.TrySetResult();
                synchronousPrefix.Wait();
                return Task.CompletedTask;
            }

            unrelated.TrySetResult();
            return Task.CompletedTask;
        });
        await using var processor = new OutboxProcessor(store, handler, time, new ExactJitter());
        await EnqueueAsync(store, Item("aggregate-a", 1));
        await EnqueueAsync(store, Item("aggregate-b", 1));

        var processing = processor.ProcessBatchAsync(2, TimeSpan.FromSeconds(5), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await unrelated.Task.WaitAsync(TimeSpan.FromSeconds(30));
            time.Advance(TimeSpan.FromSeconds(1));

            var result = await processing.WaitAsync(TimeSpan.FromSeconds(30));
            await RuntimeTestTasks.UntilAsync(() => processor.InFlightCount == 1);

            Assert.Equal(2, result.Leased);
            Assert.Equal(1, result.Completed);
            Assert.Equal(1, result.TimedOut);
            Assert.Equal("outbox-handler-timeout", result.LastFault!.Code.Value);
            Assert.Equal(1, processor.InFlightCount);
        }
        finally
        {
            synchronousPrefix.Set();
        }

        await RuntimeTestTasks.UntilAsync(() => processor.InFlightCount == 0);
    }

    [Fact]
    public async Task TimedOutHandlerLateFaultIsObservedBeforeTheHeadBecomesRetryable()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCalls = 0;
        await using var processor = new OutboxProcessor(
            store,
            new DelegateHandler((item, context, token) =>
            {
                Interlocked.Increment(ref handlerCalls);
                entered.TrySetResult();
                return late.Task;
            }),
            time,
            new ExactJitter());
        var item = Item("aggregate-a", 1);
        await EnqueueAsync(store, item);
        await EnqueueAsync(store, Item("aggregate-a", 2));

        var processing = processor.ProcessBatchAsync(1, TimeSpan.FromSeconds(5), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            time.Advance(TimeSpan.FromSeconds(1));
            var timedOut = await processing.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(1, timedOut.TimedOut);
            Assert.Equal(1, processor.InFlightCount);
            Assert.Equal(
                OutboxDeliveryState.Processing,
                Assert.Single(
                    await store.ListAsync(default),
                    stored => stored.Item.AggregateSequence == 1).State);
            Assert.Equal(0, (await processor.ProcessBatchAsync(
                1,
                TimeSpan.FromSeconds(5),
                default)).Leased);

            late.TrySetException(new IOException("late handler fault"));
            await RuntimeTestTasks.UntilAsync(() => processor.InFlightCount == 0);
            var settled = await processor.ProcessBatchAsync(1, TimeSpan.FromSeconds(5), default);

            Assert.Equal(1, settled.Retrying);
            Assert.Equal(1, Volatile.Read(ref handlerCalls));
            Assert.Equal(
                OutboxDeliveryState.Retrying,
                Assert.Single(
                    await store.ListAsync(default),
                    stored => stored.Item.AggregateSequence == 1).State);
            Assert.Equal(
                OutboxDeliveryState.Pending,
                Assert.Single(
                    await store.ListAsync(default),
                    stored => stored.Item.AggregateSequence == 2).State);
        }
        finally
        {
            late.TrySetException(new IOException("test cleanup"));
        }
    }

    [Fact]
    public async Task BlockingCancellationCallbackCannotBlockThePumpDeadline()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore();
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRelease = new ManualResetEventSlim();
        CancellationToken handlerToken = default;
        CancellationTokenRegistration registration = default;
        var handler = new DelegateHandler((item, context, token) =>
        {
            handlerToken = token;
            registration = token.Register(() =>
            {
                callbackEntered.TrySetResult();
                callbackRelease.Wait();
                throw new InvalidOperationException("hostile cancellation callback");
            });
            handlerEntered.TrySetResult();
            return handlerRelease.Task;
        });
        await using var processor = new OutboxProcessor(store, handler, time, new ExactJitter());
        await EnqueueAsync(store, Item("aggregate-a", 1));

        var processing = processor.ProcessBatchAsync(1, TimeSpan.FromSeconds(5), default);
        try
        {
            await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            time.Advance(TimeSpan.FromSeconds(1));
            var result = await processing.WaitAsync(TimeSpan.FromSeconds(30));
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(1, result.TimedOut);
            Assert.True(handlerToken.IsCancellationRequested);
            Assert.False(handlerRelease.Task.IsCompleted);
        }
        finally
        {
            handlerRelease.TrySetResult();
            callbackRelease.Set();
        }

        await RuntimeTestTasks.UntilAsync(() => processor.InFlightCount == 0);
        await registration.DisposeAsync();
    }

    [Fact]
    public async Task LeaseRenewsAcrossMultiplePeriodsAndFencesAConcurrentProcessor()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new ControlledStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCalls = 0;
        var competingCalls = 0;
        await using var processor = new OutboxProcessor(
            store,
            new DelegateHandler((item, context, token) =>
            {
                Interlocked.Increment(ref firstCalls);
                entered.TrySetResult();
                return release.Task;
            }),
            time,
            new ExactJitter());
        await using var competing = new OutboxProcessor(
            store,
            new DelegateHandler((item, context, token) =>
            {
                Interlocked.Increment(ref competingCalls);
                return Task.CompletedTask;
            }),
            time,
            new ExactJitter());
        await store.EnqueueAsync(Item("aggregate-a", 1), default);

        var processing = processor.ProcessBatchAsync(1, TimeSpan.FromMilliseconds(200), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            for (var period = 1; period <= 6; period++)
            {
                time.Advance(TimeSpan.FromMilliseconds(100));
                var expectedRenewals = period;
                await RuntimeTestTasks.UntilAsync(() => store.SuccessfulRenewals >= expectedRenewals);
                var competingPass = await competing.ProcessBatchAsync(
                    1,
                    TimeSpan.FromMilliseconds(200),
                    default);
                Assert.Equal(0, competingPass.Leased);
            }

            release.TrySetResult();
            var result = await processing.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, result.Completed);
            Assert.Equal(1, Volatile.Read(ref firstCalls));
            Assert.Equal(0, Volatile.Read(ref competingCalls));
            Assert.True(store.RenewCalls >= 6);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedLeaseHeartbeatIsReportedBeforeTheHandlerDeadline(bool throws)
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new ControlledStore
        {
            RenewOverride = (_, _, _, _, _, _) => throws
                ? Task.FromException<bool>(new IOException("renew unavailable"))
                : Task.FromResult(false),
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var processor = new OutboxProcessor(
            store,
            new DelegateHandler((item, context, token) =>
            {
                entered.TrySetResult();
                return release.Task;
            }),
            time,
            new ExactJitter());
        await store.EnqueueAsync(Item("aggregate-a", 1), default);

        var processing = processor.ProcessBatchAsync(1, TimeSpan.FromMilliseconds(200), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            time.Advance(TimeSpan.FromMilliseconds(100));
            var result = await processing.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(1, result.AcknowledgementsUnknown);
            Assert.Equal(1, result.LostLeaseRaces);
            Assert.Equal("outbox-acknowledgement-unknown", result.LastFault!.Code.Value);
            Assert.True(time.GetUtcNow() < Epoch.AddSeconds(1));
        }
        finally
        {
            release.TrySetResult();
        }

        await RuntimeTestTasks.UntilAsync(() => processor.InFlightCount == 0);
    }

    [Fact]
    public async Task DelayedLeaseHeartbeatLosesOwnershipAtTheLeaseDeadline()
    {
        var time = new ManualTimeProvider(Epoch);
        var renewalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRelease = new ManualResetEventSlim();
        CancellationTokenRegistration renewalRegistration = default;
        var store = new ControlledStore
        {
            RenewOverride = (_, _, _, _, _, cancellationToken) =>
            {
                renewalRegistration = cancellationToken.Register(() =>
                {
                    cancellationEntered.TrySetResult();
                    cancellationRelease.Wait();
                    throw new InvalidOperationException("hostile store cancellation callback");
                });
                renewalEntered.TrySetResult();
                return renewalRelease.Task;
            },
        };
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var processor = new OutboxProcessor(
            store,
            new DelegateHandler((item, context, token) =>
            {
                handlerEntered.TrySetResult();
                return handlerRelease.Task;
            }),
            time,
            new ExactJitter());
        await store.EnqueueAsync(Item("aggregate-a", 1), default);

        var processing = processor.ProcessBatchAsync(1, TimeSpan.FromMilliseconds(200), default);
        try
        {
            await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            time.Advance(TimeSpan.FromMilliseconds(100));
            await renewalEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            time.Advance(TimeSpan.FromMilliseconds(100));

            var result = await processing.WaitAsync(TimeSpan.FromSeconds(30));
            await cancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, result.AcknowledgementsUnknown);
            Assert.Equal("outbox-acknowledgement-unknown", result.LastFault!.Code.Value);
            Assert.False(renewalRelease.Task.IsCompleted);
        }
        finally
        {
            renewalRelease.TrySetResult(true);
            handlerRelease.TrySetResult();
            cancellationRelease.Set();
        }

        await RuntimeTestTasks.UntilAsync(() => processor.InFlightCount == 0);
        await renewalRegistration.DisposeAsync();
    }

    [Fact]
    public async Task ExpiredLeaseReturnedByTheStoreIsNotInvoked()
    {
        var time = new ManualTimeProvider(Epoch);
        var handlerCalls = 0;
        var store = new ControlledStore
        {
            AfterLease = call =>
            {
                if (call == 1)
                {
                    time.Advance(TimeSpan.FromMilliseconds(100));
                }
            },
        };
        await using var processor = new OutboxProcessor(
            store,
            new DelegateHandler((item, context, token) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.CompletedTask;
            }),
            time,
            new ExactJitter());
        await store.EnqueueAsync(Item("aggregate-a", 1), default);

        var result = await processor.ProcessBatchAsync(1, TimeSpan.FromMilliseconds(100), default);

        Assert.Equal(2, result.Leased);
        Assert.Equal(1, result.LostLeaseRaces);
        Assert.Equal(1, result.Completed);
        Assert.Equal(1, Volatile.Read(ref handlerCalls));
        Assert.Equal(2, Assert.Single(await store.ListAsync(default)).AttemptCount);
    }

    [Fact]
    public async Task SlowAcknowledgementDoesNotBlockLeaseHeartbeats()
    {
        var time = new ManualTimeProvider(Epoch);
        var acknowledgementEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgementRelease = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ControlledStore
        {
            CompleteOverride = async (_, _, _, _, _) =>
            {
                acknowledgementEntered.TrySetResult();
                return await acknowledgementRelease.Task.ConfigureAwait(false);
            },
        };
        await using var processor = new OutboxProcessor(
            store,
            new DelegateHandler((item, context, token) => Task.CompletedTask),
            time,
            new ExactJitter());
        await store.EnqueueAsync(Item("aggregate-a", 1), default);

        var processing = processor.ProcessBatchAsync(1, TimeSpan.FromMilliseconds(200), default);
        try
        {
            await acknowledgementEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            for (var period = 1; period <= 4; period++)
            {
                time.Advance(TimeSpan.FromMilliseconds(100));
                var expectedRenewals = period;
                await RuntimeTestTasks.UntilAsync(() => store.SuccessfulRenewals >= expectedRenewals);
            }

            acknowledgementRelease.TrySetResult(true);
            var result = await processing.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, result.Completed);
            Assert.True(store.RenewCalls >= 4);
            Assert.Single(store.CompleteTokens.Concat(store.RenewTokens).Distinct());
            await RuntimeTestTasks.UntilAsync(() => processor.InFlightCount == 0);
        }
        finally
        {
            acknowledgementRelease.TrySetResult(true);
        }
    }

    [Fact]
    public async Task ConcurrentHeartbeatLossCannotDowngradeTheUnknownAcknowledgementFence()
    {
        var time = new ManualTimeProvider(Epoch);
        var acknowledgementEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgementRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ControlledStore
        {
            CompleteOverride = async (_, _, _, _, _) =>
            {
                acknowledgementEntered.TrySetResult();
                await acknowledgementRelease.Task.ConfigureAwait(false);
                throw new IOException("late acknowledgement fault");
            },
            RenewOverride = (_, _, _, _, _, _) => Task.FromResult(false),
        };
        var handlerCalls = 0;
        await using var processor = new OutboxProcessor(
            store,
            new DelegateHandler((item, context, token) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.CompletedTask;
            }),
            time,
            new ExactJitter());
        await store.EnqueueAsync(Item("aggregate-a", 1), default);

        var processing = processor.ProcessBatchAsync(1, TimeSpan.FromMilliseconds(200), default);
        try
        {
            await acknowledgementEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            time.Advance(TimeSpan.FromMilliseconds(100));
            var lost = await processing.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(1, lost.AcknowledgementsUnknown);
            Assert.Equal("outbox-acknowledgement-unknown", lost.LastFault!.Code.Value);
        }
        finally
        {
            acknowledgementRelease.TrySetResult();
        }

        await RuntimeTestTasks.UntilAsync(() => processor.InFlightCount == 0);
        time.Advance(TimeSpan.FromMilliseconds(100));
        var fenced = await processor.ProcessBatchAsync(1, TimeSpan.FromMilliseconds(200), default);

        Assert.Equal(1, fenced.AcknowledgementsUnknown);
        Assert.Equal(1, Volatile.Read(ref handlerCalls));
    }

    [Fact]
    public async Task CallerCancellationDuringAcknowledgementRetainsTheUnknownFence()
    {
        var time = new ManualTimeProvider(Epoch);
        var acknowledgementEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgementRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ControlledStore
        {
            CompleteOverride = async (_, _, _, _, _) =>
            {
                acknowledgementEntered.TrySetResult();
                await acknowledgementRelease.Task.ConfigureAwait(false);
                throw new IOException("acknowledgement unavailable after caller cancellation");
            },
        };
        var handlerCalls = 0;
        await using var processor = new OutboxProcessor(
            store,
            new DelegateHandler((item, context, token) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.CompletedTask;
            }),
            time,
            new ExactJitter());
        await store.EnqueueAsync(Item("aggregate-a", 1), default);
        using var callerCancellation = new CancellationTokenSource();

        var processing = processor.ProcessBatchAsync(
            1,
            TimeSpan.FromMilliseconds(200),
            callerCancellation.Token);
        try
        {
            await acknowledgementEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await callerCancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        }
        finally
        {
            acknowledgementRelease.TrySetResult();
        }

        await RuntimeTestTasks.UntilAsync(() => processor.InFlightCount == 0);
        time.Advance(TimeSpan.FromMilliseconds(200));
        var fenced = await processor.ProcessBatchAsync(1, TimeSpan.FromMilliseconds(200), default);

        Assert.Equal(1, fenced.AcknowledgementsUnknown);
        Assert.Equal(1, Volatile.Read(ref handlerCalls));
    }

    [Fact]
    public async Task AcknowledgementRetriesBeyondOriginalExpiryWithoutReplayingHandler()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new ControlledStore(completeFailures: 7);
        var handlerCalls = 0;
        await using var processor = new OutboxProcessor(
            store,
            new DelegateHandler((item, context, token) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.CompletedTask;
            }),
            time,
            new ExactJitter());
        var item = Item("aggregate-a", 1);
        await store.EnqueueAsync(item, default);

        var first = await processor.ProcessBatchAsync(1, TimeSpan.FromMilliseconds(200), default);
        Assert.Equal(1, first.AcknowledgementsPending);
        Assert.Equal("outbox-acknowledgement-pending", first.LastFault!.Code.Value);

        await RuntimeTestTasks.AdvanceUntilAsync(
            time,
            TimeSpan.FromMilliseconds(50),
            () => processor.InFlightCount == 0);
        var settled = await processor.ProcessBatchAsync(1, TimeSpan.FromMilliseconds(200), default);

        Assert.True(time.GetUtcNow() > Epoch.AddMilliseconds(200));
        Assert.Equal(1, settled.Completed);
        Assert.Equal(1, Volatile.Read(ref handlerCalls));
        Assert.Equal(8, store.CompleteCalls);
        Assert.True(store.RenewCalls > 0);
        Assert.Single(store.CompleteTokens.Concat(store.RenewTokens).Distinct());
        Assert.Equal(0, store.RetryCalls);
        Assert.Equal(0, store.DeadLetterCalls);
        Assert.Equal(OutboxDeliveryState.Completed, Assert.Single(await store.ListAsync(default)).State);
    }

    [Fact]
    public async Task LostAcknowledgementOwnershipIsExplicitAndNeverBecomesAHandlerRetry()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new ControlledStore
        {
            CompleteOverride = (_, _, _, _, _) => Task.FromResult(false),
        };
        var handlerCalls = 0;
        await using var processor = new OutboxProcessor(
            store,
            new DelegateHandler((item, context, token) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.CompletedTask;
            }),
            time,
            new ExactJitter());
        await store.EnqueueAsync(Item("aggregate-a", 1), default);

        var result = await processor.ProcessBatchAsync(1, TimeSpan.FromMilliseconds(200), default);

        Assert.Equal(1, result.AcknowledgementsUnknown);
        Assert.Equal("outbox-acknowledgement-unknown", result.LastFault!.Code.Value);
        Assert.Equal(1, Volatile.Read(ref handlerCalls));
        Assert.Equal(0, store.RetryCalls);
        Assert.Equal(0, store.DeadLetterCalls);
        Assert.Equal(OutboxDeliveryState.Processing, Assert.Single(await store.ListAsync(default)).State);

        await RuntimeTestTasks.UntilAsync(() => processor.InFlightCount == 0);
        time.Advance(TimeSpan.FromMilliseconds(200));
        var afterExpiry = await processor.ProcessBatchAsync(1, TimeSpan.FromMilliseconds(200), default);

        Assert.Equal(1, afterExpiry.Leased);
        Assert.Equal(1, afterExpiry.AcknowledgementsUnknown);
        Assert.Equal(1, Volatile.Read(ref handlerCalls));
        Assert.Equal(1, store.CompleteCalls);
        await processor.DisposeAsync();
        await processor.StopCompletion.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task UnknownOperationStaysFencedWhileAnUnrelatedHeadStillProgresses()
    {
        var time = new ManualTimeProvider(Epoch);
        var fenced = Item("aggregate-a", 1);
        var store = new ControlledStore
        {
            RenewOverride = (_, operationId, _, _, _, _) =>
                Task.FromResult(operationId != fenced.OperationId),
        };
        var fencedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fencedRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fencedCalls = 0;
        var unrelatedCalls = 0;
        await using var processor = new OutboxProcessor(
            store,
            new DelegateHandler((item, context, token) =>
            {
                if (item.OperationId == fenced.OperationId)
                {
                    Interlocked.Increment(ref fencedCalls);
                    fencedEntered.TrySetResult();
                    return fencedRelease.Task;
                }

                Interlocked.Increment(ref unrelatedCalls);
                return Task.CompletedTask;
            }),
            time,
            new ExactJitter(),
            maximumConcurrentAttempts: 1);
        await store.EnqueueAsync(fenced, default);

        var firstPass = processor.ProcessBatchAsync(1, TimeSpan.FromMilliseconds(200), default);
        try
        {
            await fencedEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            time.Advance(TimeSpan.FromMilliseconds(100));
            var lost = await firstPass.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, lost.AcknowledgementsUnknown);
        }
        finally
        {
            fencedRelease.TrySetResult();
        }

        await RuntimeTestTasks.UntilAsync(() => processor.InFlightCount == 0);
        time.Advance(TimeSpan.FromMilliseconds(100));
        var unrelated = Item(
            "aggregate-b",
            1,
            createdUtc: time.GetUtcNow(),
            notBeforeUtc: time.GetUtcNow());
        await store.EnqueueAsync(unrelated, default);

        var progressed = await processor.ProcessBatchAsync(1, TimeSpan.FromMilliseconds(200), default);
        await RuntimeTestTasks.UntilAsync(() => processor.InFlightCount == 0);

        Assert.Equal(2, progressed.Leased);
        Assert.Equal(1, progressed.Completed);
        Assert.Equal(1, progressed.AcknowledgementsUnknown);
        Assert.Equal(1, Volatile.Read(ref fencedCalls));
        Assert.Equal(1, Volatile.Read(ref unrelatedCalls));
        Assert.Equal(0, processor.InFlightCount);
        Assert.Equal(
            OutboxDeliveryState.Completed,
            Assert.Single(
                await store.ListAsync(default),
                stored => stored.Item.OperationId == unrelated.OperationId).State);

        await processor.DisposeAsync();
        await processor.StopCompletion.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task HandlerConcurrencyAndRetainedAttemptStateStayBounded()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        await using var processor = new OutboxProcessor(
            store,
            new DelegateHandler((item, context, token) =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                {
                    twoEntered.TrySetResult();
                }

                return release.Task;
            }),
            time,
            new ExactJitter(),
            maximumConcurrentAttempts: 2);
        await EnqueueAsync(store, Item("aggregate-a", 1));
        await EnqueueAsync(store, Item("aggregate-b", 1));
        await EnqueueAsync(store, Item("aggregate-c", 1));

        var firstPass = processor.ProcessBatchAsync(3, TimeSpan.FromSeconds(5), default);
        try
        {
            await twoEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            time.Advance(TimeSpan.FromSeconds(1));
            var timedOut = await firstPass.WaitAsync(TimeSpan.FromSeconds(30));
            var fullPass = await processor.ProcessBatchAsync(3, TimeSpan.FromSeconds(5), default);

            Assert.Equal(2, timedOut.Leased);
            Assert.Equal(2, timedOut.TimedOut);
            Assert.Equal(2, processor.InFlightCount);
            Assert.Equal(0, fullPass.Leased);
            Assert.Equal(2, Volatile.Read(ref entered));
            Assert.Equal(OutboxDeliveryState.Pending, Assert.Single(
                await store.ListAsync(default), item => item.Item.AggregateId.Value == "aggregate-c").State);

            await processor.DisposeAsync();
            Assert.Equal(2, processor.InFlightCount);
            Assert.False(processor.StopCompletion.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await RuntimeTestTasks.UntilAsync(() => processor.InFlightCount == 0);
        await processor.StopCompletion.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task DisposeCompletesAnAdmittedBatchWhenItsHandlerHonorsCancellation()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var processor = new OutboxProcessor(
            store,
            new DelegateHandler((item, context, token) =>
            {
                entered.TrySetResult();
                return Task.Delay(TimeSpan.FromHours(1), time, token);
            }),
            time,
            new ExactJitter());
        await EnqueueAsync(store, Item("aggregate-a", 1));

        var processing = processor.ProcessBatchAsync(1, TimeSpan.FromSeconds(5), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await processor.DisposeAsync();

        var result = await processing.WaitAsync(TimeSpan.FromSeconds(30));
        await processor.StopCompletion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(RuntimeFailureKind.Cancelled, result.LastFault!.Kind);
        Assert.Equal(0, processor.InFlightCount);
    }

    [Fact]
    public async Task ExpiredDeadLetterRequiresResolutionBeforeItsAggregateCanProgress()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore(capacity: 2);
        var expired = Item("aggregate-a", 1, expiresUtc: Epoch.AddSeconds(1));
        await EnqueueAsync(store, expired);
        await EnqueueAsync(store, Item("aggregate-a", 2));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(await store.LeaseNextAsync(time.GetUtcNow(), TimeSpan.FromSeconds(5), 2, default));
        var deadLetter = Assert.Single((await store.GetSnapshotAsync(time.GetUtcNow(), default)).DeadLetters);
        Assert.False(deadLetter.CanRetry);
        Assert.True(deadLetter.RequiresResolution);
        Assert.False(await store.ManualRetryAsync(expired.OperationId, time.GetUtcNow(), default));
        Assert.True(await store.ResolveDeadLetterAsync(expired.OperationId, time.GetUtcNow(), default));

        var next = Assert.Single(await store.LeaseNextAsync(time.GetUtcNow(), TimeSpan.FromSeconds(5), 1, default));
        Assert.Equal(2, next.Item.AggregateSequence);
    }

    [Fact]
    public async Task RetryableDeadLettersTakePrecedenceInBoundedHealth()
    {
        var time = new ManualTimeProvider(Epoch);
        var store = new FixtureOutboxStore(capacity: 18);
        for (var index = 0; index < 17; index++)
        {
            await EnqueueAsync(store, Item($"expired-{index}", 1, expiresUtc: Epoch.AddSeconds(1)));
        }

        var retryable = Item("retryable", 1, expiresUtc: Epoch.AddDays(1));
        await EnqueueAsync(store, retryable);
        foreach (var leased in await store.LeaseNextAsync(time.GetUtcNow(), TimeSpan.FromSeconds(5), 18, default))
        {
            Assert.True(await store.DeadLetterAsync(
                leased.Item.OperationId,
                leased.LeaseToken!.Value,
                new(RuntimeFailureKind.Validation, new("fixture-poison"), RuntimeRecoveryAction.RetryManually,
                    new("test:dead-letter"), time.GetUtcNow()),
                time.GetUtcNow(),
                default));
        }

        time.Advance(TimeSpan.FromSeconds(1));
        var visible = await store.GetSnapshotAsync(time.GetUtcNow(), default);
        Assert.Equal(retryable.OperationId, visible.DeadLetters[0].OperationId);
        Assert.True(visible.DeadLetters[0].CanRetry);
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
        await using var processor = new OutboxProcessor(store, handler, time, new ExactJitter());
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
        int maxAttempts = 3,
        DateTimeOffset? createdUtc = null,
        DateTimeOffset? notBeforeUtc = null)
    {
        var operation = OperationId.New();
        var created = createdUtc ?? Epoch;
        return new(
            operation,
            key ?? new($"key:{operation}"),
            CorrelationId.New(),
            new("outbox-test"),
            OutboxCommandKind.RaidStateRecorded,
            OutboxContractVersion.Current,
            new(aggregate),
            sequence,
            created,
            notBeforeUtc ?? created,
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
        private readonly Lock _gate = new();
        private readonly List<OutboxItem> _delivered = [];

        public IReadOnlyList<OutboxItem> Delivered
        {
            get
            {
                lock (_gate)
                {
                    return [.. _delivered];
                }
            }
        }

        public Task HandleAsync(
            OutboxItem item,
            OutboxDeliveryContext context,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _delivered.Add(item);
            }

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

    private sealed class ControlledStore(int completeFailures = 0) : IOutboxStore
    {
        private readonly FixtureOutboxStore _inner = new();
        private readonly ConcurrentQueue<OutboxLeaseToken> _completeTokens = new();
        private readonly ConcurrentQueue<OutboxLeaseToken> _renewTokens = new();
        private int _completeCalls;
        private int _leaseCalls;
        private int _renewCalls;
        private int _successfulRenewals;
        private int _retryCalls;
        private int _deadLetterCalls;

        public Func<
            int,
            OperationId,
            OutboxLeaseToken,
            DateTimeOffset,
            TimeSpan,
            CancellationToken,
            Task<bool>>? RenewOverride { get; init; }

        public Func<
            int,
            OperationId,
            OutboxLeaseToken,
            DateTimeOffset,
            CancellationToken,
            Task<bool>>? CompleteOverride { get; init; }

        public Action<int>? AfterLease { get; init; }

        public int CompleteCalls => Volatile.Read(ref _completeCalls);

        public int RenewCalls => Volatile.Read(ref _renewCalls);

        public int SuccessfulRenewals => Volatile.Read(ref _successfulRenewals);

        public int RetryCalls => Volatile.Read(ref _retryCalls);

        public int DeadLetterCalls => Volatile.Read(ref _deadLetterCalls);

        public IReadOnlyList<OutboxLeaseToken> CompleteTokens => _completeTokens.ToArray();

        public IReadOnlyList<OutboxLeaseToken> RenewTokens => _renewTokens.ToArray();

        public Task<OutboxEnqueueReceipt> EnqueueAsync(OutboxItem item, CancellationToken cancellationToken) =>
            _inner.EnqueueAsync(item, cancellationToken);

        public Task<ImmutableArray<OutboxEnqueueReceipt>> EnqueueBatchAsync(
            ImmutableArray<OutboxItem> items,
            CancellationToken cancellationToken) => _inner.EnqueueBatchAsync(items, cancellationToken);

        public async Task<ImmutableArray<OutboxStoredItem>> LeaseNextAsync(
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            int maximumCount,
            CancellationToken cancellationToken)
        {
            var leased = await _inner.LeaseNextAsync(
                    nowUtc,
                    leaseDuration,
                    maximumCount,
                    cancellationToken)
                .ConfigureAwait(false);
            AfterLease?.Invoke(Interlocked.Increment(ref _leaseCalls));
            return leased;
        }

        public Task<bool> CompleteAsync(
            OperationId operationId,
            OutboxLeaseToken leaseToken,
            DateTimeOffset completedUtc,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _completeCalls);
            _completeTokens.Enqueue(leaseToken);
            return CompleteOverride?.Invoke(call, operationId, leaseToken, completedUtc, cancellationToken)
                ?? (call <= completeFailures
                    ? Task.FromException<bool>(new IOException("acknowledgement unavailable"))
                    : _inner.CompleteAsync(operationId, leaseToken, completedUtc, cancellationToken));
        }

        public async Task<bool> RenewLeaseAsync(
            OperationId operationId,
            OutboxLeaseToken leaseToken,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _renewCalls);
            _renewTokens.Enqueue(leaseToken);
            var renewed = await (RenewOverride?.Invoke(
                        call,
                        operationId,
                        leaseToken,
                        nowUtc,
                        leaseDuration,
                        cancellationToken)
                    ?? _inner.RenewLeaseAsync(
                        operationId,
                        leaseToken,
                        nowUtc,
                        leaseDuration,
                        cancellationToken))
                .ConfigureAwait(false);
            if (renewed)
            {
                Interlocked.Increment(ref _successfulRenewals);
            }

            return renewed;
        }

        public async Task<bool> RetryAsync(OperationId operationId, OutboxLeaseToken leaseToken, DateTimeOffset retryingUtc, DateTimeOffset notBeforeUtc, RuntimeFault fault, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _retryCalls);
            return await _inner.RetryAsync(operationId, leaseToken, retryingUtc, notBeforeUtc, fault, cancellationToken);
        }

        public async Task<bool> DeadLetterAsync(OperationId operationId, OutboxLeaseToken leaseToken, RuntimeFault fault, DateTimeOffset deadLetteredUtc, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _deadLetterCalls);
            return await _inner.DeadLetterAsync(operationId, leaseToken, fault, deadLetteredUtc, cancellationToken);
        }

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
}
