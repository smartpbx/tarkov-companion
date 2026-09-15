using TarkovCompanion.Application.Services.Execution;

namespace TarkovCompanion.UnitTests.Runtime;

public sealed class BackgroundWorkSupervisorTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReservedAdmissionAndPriorityKeepInteractiveCaptureAheadOfBackgroundWork()
    {
        var time = new ManualTimeProvider(Epoch);
        await using var supervisor = new BackgroundWorkSupervisor(time, Options(capacity: 3, burst: 4));
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var first = supervisor.Submit(Request(WorkPriority.Background), async (_, token) =>
        {
            order.Add("first-background");
            firstStarted.TrySetResult();
            await releaseFirst.Task.WaitAsync(token);
        });
        await firstStarted.Task;

        var queuedBackground = supervisor.Submit(Request(WorkPriority.Background), (_, _) =>
        {
            order.Add("second-background");
            return Task.CompletedTask;
        });
        var rejectedBackground = supervisor.Submit(Request(WorkPriority.Background), (_, _) => Task.CompletedTask);
        var capture = supervisor.Submit(Request(WorkPriority.UserBlocking), (_, _) =>
        {
            order.Add("contextual-capture");
            return Task.CompletedTask;
        });

        Assert.True(first.Accepted);
        Assert.True(queuedBackground.Accepted);
        Assert.False(rejectedBackground.Accepted);
        Assert.True(capture.Accepted);
        Assert.Equal(BackgroundWorkState.Rejected, (await rejectedBackground.Handle.Completion).State);

        releaseFirst.TrySetResult();
        await Task.WhenAll(first.Handle.Completion, capture.Handle.Completion, queuedBackground.Handle.Completion);
        Assert.Equal(
            ["first-background", "contextual-capture", "second-background"],
            order);
        Assert.Equal(1, supervisor.Snapshot.Resources.Rejected);
    }

    [Fact]
    public async Task PriorityBurstIsBoundedSoBackgroundEventuallyRuns()
    {
        var time = new ManualTimeProvider(Epoch);
        await using var supervisor = new BackgroundWorkSupervisor(time, Options(capacity: 6, burst: 2));
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var first = supervisor.Submit(Request(WorkPriority.Normal), async (_, token) =>
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task.WaitAsync(token);
        });
        await firstStarted.Task;

        var background = supervisor.Submit(Request(WorkPriority.Background), (_, _) => Add("background"));
        var high1 = supervisor.Submit(Request(WorkPriority.Interactive), (_, _) => Add("high-1"));
        var high2 = supervisor.Submit(Request(WorkPriority.Interactive), (_, _) => Add("high-2"));
        var high3 = supervisor.Submit(Request(WorkPriority.Interactive), (_, _) => Add("high-3"));
        releaseFirst.TrySetResult();

        await Task.WhenAll(
            first.Handle.Completion,
            background.Handle.Completion,
            high1.Handle.Completion,
            high2.Handle.Completion,
            high3.Handle.Completion);
        Assert.Equal(["high-1", "high-2", "background", "high-3"], order);

        Task Add(string value)
        {
            order.Add(value);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task HeavyExclusiveRunsAloneAndAnInteractiveRequestJumpsQueuedCpuWork()
    {
        var time = new ManualTimeProvider(Epoch);
        var options = new BackgroundWorkSupervisorOptions(
            capacity: 4,
            reservedInteractiveAdmission: 1,
            maxConcurrent: 2,
            lightLimit: 2,
            ioLimit: 2,
            cpuLimit: 1,
            maxPriorityBurst: 3,
            terminalHistoryLimit: 32,
            defaultStopTimeout: TimeSpan.FromSeconds(2));
        await using var supervisor = new BackgroundWorkSupervisor(time, options);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heavyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHeavy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = supervisor.Submit(Request(WorkPriority.Background), async (_, token) =>
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task.WaitAsync(token);
        });
        await firstStarted.Task;

        var second = supervisor.Submit(Request(WorkPriority.Normal), (_, _) =>
        {
            secondStarted.TrySetResult();
            return Task.CompletedTask;
        });
        var heavy = supervisor.Submit(
            Request(WorkPriority.Interactive, WorkloadClass.HeavyExclusive),
            async (_, token) =>
            {
                heavyStarted.TrySetResult();
                await releaseHeavy.Task.WaitAsync(token);
            });
        releaseFirst.TrySetResult();
        await heavyStarted.Task;

        Assert.False(secondStarted.Task.IsCompleted);
        Assert.True(supervisor.Snapshot.Resources.HeavyExclusiveRunning);
        Assert.Equal(1, supervisor.Snapshot.Resources.Running);

        releaseHeavy.TrySetResult();
        await Task.WhenAll(first.Handle.Completion, heavy.Handle.Completion, second.Handle.Completion);
        Assert.True(secondStarted.Task.IsCompleted);
    }

    [Fact]
    public async Task BackgroundHeavyExclusiveCannotBeStarvedByLaterUserBlockingWork()
    {
        var time = new ManualTimeProvider(Epoch);
        await using var supervisor = new BackgroundWorkSupervisor(time, Options(capacity: 6, burst: 2));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var first = supervisor.Submit(Request(WorkPriority.Normal), async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        await started.Task;

        var heavy = supervisor.Submit(
            Request(WorkPriority.Background, WorkloadClass.HeavyExclusive),
            (_, _) => Add("heavy"));
        var high1 = supervisor.Submit(Request(WorkPriority.UserBlocking), (_, _) => Add("high-1"));
        var high2 = supervisor.Submit(Request(WorkPriority.UserBlocking), (_, _) => Add("high-2"));
        var high3 = supervisor.Submit(Request(WorkPriority.UserBlocking), (_, _) => Add("high-3"));
        release.TrySetResult();

        await Task.WhenAll(
            first.Handle.Completion,
            heavy.Handle.Completion,
            high1.Handle.Completion,
            high2.Handle.Completion,
            high3.Handle.Completion);
        Assert.Equal(["high-1", "high-2", "heavy", "high-3"], order);

        Task Add(string value)
        {
            order.Add(value);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// With spare slots, an exclusive item must not be delayed by interactive work admitted after
    /// its original blockers have returned. An already-running reserved-lane item drains first.
    /// </summary>
    [Fact]
    public async Task PendingHeavyExclusiveDrainsConcurrentWorkWithinTheBurstBound()
    {
        var time = new ManualTimeProvider(Epoch);
        await using var supervisor = new BackgroundWorkSupervisor(
            time,
            Options(capacity: 8, burst: 1, maxConcurrent: 2, lightLimit: 2));
        var gate = new object();
        var order = new List<string>();
        var startedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = supervisor.Submit(Request(WorkPriority.Interactive, WorkloadClass.Light), async (_, token) =>
        {
            startedA.TrySetResult();
            await releaseA.Task.WaitAsync(token);
        });
        var b = supervisor.Submit(Request(WorkPriority.Interactive, WorkloadClass.Light), async (_, token) =>
        {
            startedB.TrySetResult();
            await releaseB.Task.WaitAsync(token);
        });
        await Task.WhenAll(startedA.Task, startedB.Task);

        var heavy = supervisor.Submit(
            Request(WorkPriority.Background, WorkloadClass.HeavyExclusive),
            (_, _) => Add("heavy"));
        var c = supervisor.Submit(Request(WorkPriority.UserBlocking, WorkloadClass.Light), async (_, _) =>
        {
            lock (gate)
            {
                order.Add("c");
            }

            cStarted.TrySetResult();
            await releaseC.Task;
        });
        var d = supervisor.Submit(Request(WorkPriority.UserBlocking, WorkloadClass.Light), (_, _) => Add("d"));

        releaseA.TrySetResult();
        await a.Handle.Completion;
        await cStarted.Task;
        lock (gate)
        {
            Assert.Equal(["c"], order);
        }

        releaseB.TrySetResult();
        await b.Handle.Completion;
        Assert.False(c.Handle.Completion.IsCompleted);
        Assert.False(heavy.Handle.Completion.IsCompleted);
        Assert.False(d.Handle.Completion.IsCompleted);

        releaseC.TrySetResult();
        await Task.WhenAll(b.Handle.Completion, heavy.Handle.Completion, c.Handle.Completion, d.Handle.Completion);
        lock (gate)
        {
            Assert.Equal(["c", "heavy", "d"], order);
        }

        Task Add(string value)
        {
            lock (gate)
            {
                order.Add(value);
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ManualRetryCreatesANewOperationAndKeepsTerminalHistory()
    {
        var time = new ManualTimeProvider(Epoch);
        await using var supervisor = new BackgroundWorkSupervisor(time, Options(capacity: 3, burst: 2));
        var calls = 0;
        var original = supervisor.Submit(Request(WorkPriority.Normal, idempotent: true), (_, _) =>
        {
            calls++;
            return calls == 1
                ? Task.FromException(new InvalidOperationException("unsafe private detail"))
                : Task.CompletedTask;
        });
        Assert.Equal(BackgroundWorkState.Faulted, (await original.Handle.Completion).State);

        var retryId = OperationId.New();
        var retry = supervisor.Retry(original.Handle.OperationId, retryId);
        Assert.Equal(BackgroundWorkState.Succeeded, (await retry.Handle.Completion).State);
        Assert.Equal(retryId, retry.Handle.OperationId);
        Assert.Equal(
            [BackgroundWorkState.Faulted, BackgroundWorkState.Succeeded],
            supervisor.Snapshot.Operations.Select(operation => operation.State));
    }

    [Fact]
    public async Task ManualRetryRejectsWorkWithoutAnIdempotencyGuarantee()
    {
        var time = new ManualTimeProvider(Epoch);
        await using var supervisor = new BackgroundWorkSupervisor(time, Options(capacity: 2, burst: 2));
        var original = supervisor.Submit(Request(WorkPriority.Normal), (_, _) => Task.CompletedTask);
        await original.Handle.Completion;

        Assert.Throws<InvalidOperationException>(() => supervisor.Retry(
            original.Handle.OperationId,
            OperationId.New()));
    }

    [Fact]
    public async Task CallerRetryIsRefusedForWorkTheSupervisorRestartsItself()
    {
        var time = new ManualTimeProvider(Epoch);
        await using var supervisor = new BackgroundWorkSupervisor(time, Options(capacity: 2, burst: 2));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var calls = 0;

        // An already-cancelled token runs its registration inline during admission; that must
        // neither deadlock nor start the work.
        var admission = supervisor.Submit(
            Request(WorkPriority.Normal, idempotent: true, restartMode: OperationRestartMode.OnFailure),
            (_, _) =>
            {
                calls++;
                return Task.CompletedTask;
            },
            cancelled.Token);

        Assert.Equal(BackgroundWorkState.Cancelled, (await admission.Handle.Completion).State);
        Assert.Equal(0, calls);
        Assert.Throws<InvalidOperationException>(() => supervisor.Retry(
            admission.Handle.OperationId,
            OperationId.New()));
    }

    [Fact]
    public async Task SubscriberFaultDoesNotPreventLaterSubscribersOrTheProducer()
    {
        var time = new ManualTimeProvider(Epoch);
        await using var supervisor = new BackgroundWorkSupervisor(time, Options(capacity: 2, burst: 2));
        var observed = 0;
        supervisor.Changed += (_, _) => throw new InvalidOperationException("subscriber detail");
        supervisor.Changed += (_, _) => Interlocked.Increment(ref observed);

        var admission = supervisor.Submit(Request(WorkPriority.Normal), (_, _) => Task.CompletedTask);
        await admission.Handle.Completion;

        Assert.True(observed > 0);
        Assert.True(supervisor.Snapshot.Resources.SubscriberFaults > 0);
    }

    [Fact]
    public async Task StopCancelsRunningAndPendingOperationsIdempotently()
    {
        var time = new ManualTimeProvider(Epoch);
        await using var supervisor = new BackgroundWorkSupervisor(time, Options(capacity: 3, burst: 2));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = supervisor.Submit(Request(WorkPriority.Normal), async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, time, token);
        });
        var pending = supervisor.Submit(Request(WorkPriority.Background), (_, _) => Task.CompletedTask);
        await started.Task;

        var first = await supervisor.StopAsync(TimeSpan.FromSeconds(2));
        var second = await supervisor.StopAsync(TimeSpan.FromSeconds(2));

        Assert.True(first.CompletedWithinDeadline);
        Assert.True(second.CompletedWithinDeadline);
        Assert.Equal(BackgroundWorkState.Cancelled, (await running.Handle.Completion).State);
        Assert.Equal(BackgroundWorkState.Cancelled, (await pending.Handle.Completion).State);
    }

    /// <summary>A stop that runs out of time reports the truth rather than a completion.</summary>
    /// <remarks>
    /// The earlier version marked the ignoring operation terminal and gave its slot back, so the
    /// same user work was still running while the scheduler believed the slot was free.
    /// </remarks>
    [Fact]
    public async Task StopRemainsBoundedWhenADependencyIgnoresCancellation()
    {
        var time = new ManualTimeProvider(Epoch);
        await using var supervisor = new BackgroundWorkSupervisor(time, Options(capacity: 2, burst: 2));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = supervisor.Submit(Request(WorkPriority.Normal), async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
        });
        await started.Task;

        var stopping = supervisor.StopAsync(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        var first = await stopping;

        Assert.False(first.CompletedWithinDeadline);
        Assert.Equal(1, first.UnfinishedOperations);
        Assert.False(operation.Handle.Completion.IsCompleted);
        Assert.Equal(BackgroundWorkState.Running, Assert.Single(supervisor.Snapshot.Operations).State);
        Assert.Null(Assert.Single(supervisor.Snapshot.Operations).CompletedUtc);
        Assert.Equal(1, supervisor.Snapshot.Resources.Running);
        Assert.False(release.Task.IsCompleted);

        release.TrySetResult();
        AssertLateOutcome((await operation.Handle.Completion).State);
        var second = await supervisor.StopAsync(TimeSpan.FromSeconds(1));
        Assert.True(second.CompletedWithinDeadline);
        Assert.Equal(0, second.UnfinishedOperations);
    }

    [Fact]
    public async Task StopTimeoutKeepsTheExactResourceClassAndRefusesNewWork()
    {
        var time = new ManualTimeProvider(Epoch);
        await using var supervisor = new BackgroundWorkSupervisor(time, Options(capacity: 4, burst: 2));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ignoring = supervisor.Submit(Request(WorkPriority.Normal), async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
        });
        await started.Task;
        var queued = supervisor.Submit(Request(WorkPriority.Normal), (_, _) => Task.CompletedTask);

        var stopping = supervisor.StopAsync(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        var stop = await stopping;

        Assert.False(stop.CompletedWithinDeadline);
        Assert.Equal(1, stop.UnfinishedOperations);
        Assert.Equal(BackgroundWorkState.Cancelled, (await queued.Handle.Completion).State);
        var resources = supervisor.Snapshot.Resources;
        Assert.Equal(1, resources.Running);
        Assert.Equal(1, resources.RunningCPU);
        Assert.NotNull(supervisor.Snapshot.Operations.Single(operation =>
            operation.OperationId == ignoring.Handle.OperationId).LastFault);
        Assert.False(supervisor.Submit(Request(WorkPriority.UserBlocking), (_, _) => Task.CompletedTask).Accepted);

        release.TrySetResult();
        AssertLateOutcome((await ignoring.Handle.Completion).State);
        await RuntimeTestTasks.UntilAsync(() => supervisor.Snapshot.Resources.RunningCPU == 0);
    }

    [Fact]
    public async Task DisposeAfterATimedOutStopLeavesTheLateCompletionSafe()
    {
        var time = new ManualTimeProvider(Epoch);
        var supervisor = new BackgroundWorkSupervisor(time, Options(capacity: 2, burst: 2));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = supervisor.Submit(Request(WorkPriority.Normal), async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
        });
        await started.Task;

        var disposing = supervisor.DisposeAsync().AsTask();
        await RuntimeTestTasks.AdvanceUntilAsync(time, TimeSpan.FromSeconds(2), () => disposing.IsCompleted);
        await disposing;

        Assert.False(operation.Handle.Completion.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() =>
            supervisor.Submit(Request(WorkPriority.Normal), (_, _) => Task.CompletedTask));

        release.TrySetResult();
        var result = await operation.Handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        AssertLateOutcome(result.State);
        await RuntimeTestTasks.UntilAsync(() => supervisor.Snapshot.Resources.Running == 0);
    }

    [Fact]
    public async Task AttemptTimeoutRetainsItsResourceAndCompletionUntilIgnoredWorkExits()
    {
        var time = new ManualTimeProvider(Epoch);
        await using var supervisor = new BackgroundWorkSupervisor(time, Options(capacity: 2, burst: 2));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = supervisor.Submit(Request(WorkPriority.Normal, timeout: TimeSpan.FromSeconds(1)), async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
        });
        await started.Task;

        await RuntimeTestTasks.AdvanceUntilAsync(
            time,
            TimeSpan.FromSeconds(1),
            () => Assert.Single(supervisor.Snapshot.Operations).LastFault is not null);

        Assert.False(operation.Handle.Completion.IsCompleted);
        Assert.Equal(1, supervisor.Snapshot.Resources.Running);
        Assert.Equal(BackgroundWorkState.Running, Assert.Single(supervisor.Snapshot.Operations).State);

        release.TrySetResult();
        Assert.Equal(BackgroundWorkState.TimedOut, (await operation.Handle.Completion).State);
        Assert.Equal(0, supervisor.Snapshot.Resources.Running);
    }

    [Fact]
    public async Task OnFailureRestartsAFailedRunAfterItsInjectedBackoff()
    {
        var time = new ManualTimeProvider(Epoch);
        await using var supervisor = new BackgroundWorkSupervisor(
            time,
            Options(capacity: 4, burst: 2, minimumRestartDelay: TimeSpan.FromSeconds(5)));
        var calls = 0;
        var admission = supervisor.Submit(
            Request(WorkPriority.Normal, idempotent: true, restartMode: OperationRestartMode.OnFailure),
            (_, _) => Interlocked.Increment(ref calls) == 1
                ? Task.FromException(new IOException("private"))
                : Task.CompletedTask);

        await RuntimeTestTasks.UntilAsync(() =>
            Assert.Single(supervisor.Snapshot.Operations).State == BackgroundWorkState.Restarting);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(1, supervisor.Snapshot.Resources.Restarting);
        Assert.Equal(0, supervisor.Snapshot.Resources.Running);
        Assert.False(admission.Handle.Completion.IsCompleted);

        await RuntimeTestTasks.AdvanceUntilAsync(
            time,
            TimeSpan.FromSeconds(5),
            () => admission.Handle.Completion.IsCompleted);
        var result = await admission.Handle.Completion;

        Assert.Equal(BackgroundWorkState.Succeeded, result.State);
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Equal(1, Assert.Single(supervisor.Snapshot.Operations).Restarts);
        Assert.Equal(0, supervisor.Snapshot.Resources.Restarting);
    }

    [Fact]
    public async Task AlwaysRestartsAfterSuccessUntilTheCallerCancels()
    {
        var time = new ManualTimeProvider(Epoch);
        await using var supervisor = new BackgroundWorkSupervisor(
            time,
            Options(capacity: 4, burst: 2, minimumRestartDelay: TimeSpan.FromSeconds(1)));
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var admission = supervisor.Submit(
            Request(WorkPriority.Normal, idempotent: true, restartMode: OperationRestartMode.Always),
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            },
            cancellation.Token);

        await RuntimeTestTasks.AdvanceUntilAsync(time, TimeSpan.FromSeconds(1), () => Volatile.Read(ref calls) >= 3);
        await cancellation.CancelAsync();
        await RuntimeTestTasks.AdvanceUntilAsync(
            time,
            TimeSpan.FromSeconds(1),
            () => admission.Handle.Completion.IsCompleted);
        var result = await admission.Handle.Completion;
        var callsAtCompletion = Volatile.Read(ref calls);

        Assert.Contains(result.State, new[] { BackgroundWorkState.Cancelled, BackgroundWorkState.Succeeded });
        Assert.True(Assert.Single(supervisor.Snapshot.Operations).Restarts >= 2);
        time.Advance(TimeSpan.FromSeconds(10));
        await RuntimeTestTasks.DrainAsync();
        Assert.Equal(callsAtCompletion, Volatile.Read(ref calls));
    }

    [Fact]
    public void AutomaticRestartRequiresAnIdempotencyGuarantee()
    {
        Assert.Throws<ArgumentException>(() => OperationPolicy.Once(
            TimeSpan.FromSeconds(1),
            WorkloadClass.Light,
            OperationRestartMode.OnFailure,
            IdempotencyRequirement.SingleAttempt));
        Assert.Throws<ArgumentException>(() => OperationPolicy.Once(
            TimeSpan.FromSeconds(1),
            WorkloadClass.Light,
            OperationRestartMode.Always,
            IdempotencyRequirement.SingleAttempt));
        Assert.Throws<ArgumentOutOfRangeException>(() => Options(
            capacity: 2,
            burst: 1,
            minimumRestartDelay: TimeSpan.Zero));
    }

    /// <summary>
    /// A callback that does synchronous work before its first await must not block the dispatcher
    /// thread from dispatching other items. Concurrency counts are already committed under the
    /// scheduler lock before the callback is ever invoked.
    /// </summary>
    [Fact]
    public async Task SynchronousCallbackPrefixDoesNotStallTheDispatcher()
    {
        var time = new ManualTimeProvider(Epoch);
        await using var supervisor = new BackgroundWorkSupervisor(
            time, Options(capacity: 4, burst: 2, maxConcurrent: 2, lightLimit: 2));

        // A blocking wait inside the callback is the worst-case synchronous prefix:
        // without a yield before invocation it would occupy the dispatcher thread.
        var gate = new SemaphoreSlim(0, 1);
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var a = supervisor.Submit(Request(WorkPriority.Normal, WorkloadClass.Light), (_, _) =>
        {
            gate.Wait();
            return Task.CompletedTask;
        });
        var b = supervisor.Submit(Request(WorkPriority.UserBlocking, WorkloadClass.Light), (_, _) =>
        {
            bStarted.TrySetResult();
            return Task.CompletedTask;
        });

        // B must start even though A is stuck in gate.Wait. If the dispatcher ran A's prefix
        // synchronously, B would be stuck behind A and bStarted would never fire.
        await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A's callback has not returned (gate still held), so its slot and completion are live.
        Assert.False(a.Handle.Completion.IsCompleted);

        gate.Release();
        await Task.WhenAll(a.Handle.Completion, b.Handle.Completion);
    }

    /// <summary>
    /// A never-ending lower-priority task that ignores cancellation must not indefinitely block
    /// UserBlocking/Interactive work when a HeavyExclusive is also pending. Reserved work keeps
    /// progressing beyond MaxPriorityBurst while that original blocker remains; the heavy gets
    /// the next turn as soon as the blocker really returns.
    /// </summary>
    [Fact]
    public async Task UserBlockingWorkRunsWhileHeavyExclusiveIsPendingAndLowerPriorityTaskIsUncooperative()
    {
        var time = new ManualTimeProvider(Epoch);
        await using var supervisor = new BackgroundWorkSupervisor(
            time, Options(capacity: 8, burst: 2, maxConcurrent: 2, lightLimit: 2));
        var gate = new object();
        var order = new List<string>();
        var longRunningStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A long-running Normal task that ignores cancellation (simulates an uncooperative run).
        var longRunning = supervisor.Submit(Request(WorkPriority.Normal, WorkloadClass.Light), async (_, _) =>
        {
            longRunningStarted.TrySetResult();
            await release.Task;
        });
        await longRunningStarted.Task;

        // Heavy exclusive needs all slots free — can't start while longRunning holds one.
        var heavy = supervisor.Submit(
            Request(WorkPriority.Background, WorkloadClass.HeavyExclusive),
            (_, _) => Add("heavy"));

        // UserBlocking should keep using the otherwise-idle reserved lane.
        var ub1 = supervisor.Submit(Request(WorkPriority.UserBlocking, WorkloadClass.Light), (_, _) => Add("ub1"));
        var ub2 = supervisor.Submit(Request(WorkPriority.UserBlocking, WorkloadClass.Light), (_, _) => Add("ub2"));
        var ub3 = supervisor.Submit(Request(WorkPriority.UserBlocking, WorkloadClass.Light), (_, _) => Add("ub3"));

        await Task.WhenAll(ub1.Handle.Completion, ub2.Handle.Completion, ub3.Handle.Completion);
        lock (gate) { Assert.Equal(["ub1", "ub2", "ub3"], order); }

        // A later arrival still runs even though the finite priority burst was exhausted.
        var ub4 = supervisor.Submit(Request(WorkPriority.Interactive, WorkloadClass.Light), (_, _) => Add("ub4"));
        await ub4.Handle.Completion;
        lock (gate) { Assert.Equal(["ub1", "ub2", "ub3", "ub4"], order); }

        Assert.False(heavy.Handle.Completion.IsCompleted);
        Assert.Equal(1, supervisor.Snapshot.Resources.Running);
        Assert.Equal(1, supervisor.Snapshot.Resources.RunningLight);

        release.TrySetResult();
        await Task.WhenAll(longRunning.Handle.Completion, heavy.Handle.Completion);
        lock (gate) { Assert.Equal(["ub1", "ub2", "ub3", "ub4", "heavy"], order); }

        Task Add(string s)
        {
            lock (gate) { order.Add(s); }
            return Task.CompletedTask;
        }
    }

    /// <summary>What an operation that ignored cancellation may truthfully end as once it returns.</summary>
    /// <remarks>
    /// The stop's cancellation reaches the executor's wait asynchronously. If the ignoring work
    /// returns first it really did succeed, and reporting that is correct; what must never happen
    /// is a terminal state, or a released slot, before it returns — asserted before release.
    /// </remarks>
    private static void AssertLateOutcome(BackgroundWorkState state) =>
        Assert.Contains(state, new[] { BackgroundWorkState.Cancelled, BackgroundWorkState.Succeeded });

    private static BackgroundWorkRequest Request(
        WorkPriority priority,
        WorkloadClass workloadClass = WorkloadClass.CPU,
        bool idempotent = false,
        OperationRestartMode restartMode = OperationRestartMode.Manual,
        TimeSpan? timeout = null)
    {
        var operation = OperationId.New();
        return new(
            new(
                new("capture-runtime"),
                operation,
                CorrelationId.New(),
                new("capture-fixture"),
                OperationPolicy.Once(
                    timeout ?? TimeSpan.FromMinutes(1),
                    workloadClass,
                    restartMode,
                    idempotent
                        ? IdempotencyRequirement.Guaranteed
                        : IdempotencyRequirement.SingleAttempt)),
            new($"scope:{operation}"),
            priority);
    }

    private static BackgroundWorkSupervisorOptions Options(
        int capacity,
        int burst,
        int maxConcurrent = 1,
        int lightLimit = 1,
        TimeSpan? minimumRestartDelay = null) => new(
        capacity,
        reservedInteractiveAdmission: 1,
        maxConcurrent: maxConcurrent,
        lightLimit: lightLimit,
        ioLimit: 1,
        cpuLimit: 1,
        maxPriorityBurst: burst,
        terminalHistoryLimit: 32,
        defaultStopTimeout: TimeSpan.FromSeconds(2),
        minimumRestartDelay: minimumRestartDelay);
}
