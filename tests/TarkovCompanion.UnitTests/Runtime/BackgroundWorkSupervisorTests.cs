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
    public async Task SubscriberFaultDoesNotPreventLaterSubscribersOrTheProducer()
    {
        var time = new ManualTimeProvider(Epoch);
        await using var supervisor = new BackgroundWorkSupervisor(time, Options(capacity: 2, burst: 2));
        var observed = 0;
        supervisor.Changed += (_, _) => throw new InvalidOperationException("subscriber detail");
        supervisor.Changed += (_, _) => observed++;

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

        var first = await supervisor.StopAsync(TimeSpan.FromSeconds(1));
        var second = await supervisor.StopAsync(TimeSpan.FromSeconds(1));

        Assert.True(first.CompletedWithinDeadline);
        Assert.True(second.CompletedWithinDeadline);
        Assert.Equal(0, first.UnfinishedOperations);
        Assert.Equal(0, second.UnfinishedOperations);
        Assert.Equal(BackgroundWorkState.Cancelled, (await operation.Handle.Completion).State);
        Assert.False(release.Task.IsCompleted);

        release.TrySetResult();
    }

    private static BackgroundWorkRequest Request(
        WorkPriority priority,
        WorkloadClass workloadClass = WorkloadClass.CPU,
        bool idempotent = false)
    {
        var operation = OperationId.New();
        return new(
            new(
                new("capture-runtime"),
                operation,
                CorrelationId.New(),
                new("capture-fixture"),
                OperationPolicy.Once(
                    TimeSpan.FromMinutes(1),
                    workloadClass,
                    OperationRestartMode.Manual,
                    idempotent
                        ? IdempotencyRequirement.Guaranteed
                        : IdempotencyRequirement.SingleAttempt)),
            new($"scope:{operation}"),
            priority);
    }

    private static BackgroundWorkSupervisorOptions Options(int capacity, int burst) => new(
        capacity,
        reservedInteractiveAdmission: 1,
        maxConcurrent: 1,
        lightLimit: 1,
        ioLimit: 1,
        cpuLimit: 1,
        maxPriorityBurst: burst,
        terminalHistoryLimit: 32,
        defaultStopTimeout: TimeSpan.FromSeconds(2));
}
