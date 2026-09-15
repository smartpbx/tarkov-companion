using TarkovCompanion.Application.Services.Execution;

namespace TarkovCompanion.UnitTests.Runtime;

public sealed class ResilienceExecutorTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RetriesFollowInjectedTimeAndBoundedExponentialDelays()
    {
        var time = new ManualTimeProvider(Epoch);
        var executor = new ResilienceExecutor(time, new ExactJitter());
        var attempts = 0;
        var execution = executor.ExecuteAsync(
            Request(ExecutionPrimitiveTests.RetryPolicy(IdempotencyRequirement.Guaranteed)),
            (_, _) =>
            {
                attempts++;
                return attempts < 3
                    ? Task.FromException<int>(new IOException("private dependency detail"))
                    : Task.FromResult(42);
            },
            default);

        await RuntimeTestTasks.UntilAsync(() =>
            Volatile.Read(ref attempts) == 1
            && time.NextTimerUtc == Epoch.AddMilliseconds(100));
        Assert.False(execution.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(100));
        await RuntimeTestTasks.UntilAsync(() =>
            Volatile.Read(ref attempts) == 2
            && time.NextTimerUtc == Epoch.AddMilliseconds(300));
        Assert.False(execution.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(200));
        var result = await execution;
        Assert.True(result.Succeeded);
        Assert.Equal(42, result.Value);
        Assert.Equal(3, result.Attempts);
    }

    [Fact]
    public async Task TimeoutCompletesEvenWhenDependencyIgnoresCancellation()
    {
        var time = new ManualTimeProvider(Epoch);
        var executor = new ResilienceExecutor(time, new ExactJitter());
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = OperationPolicy.Once(TimeSpan.FromSeconds(1));

        var execution = executor.ExecuteAsync(
            Request(policy),
            (_, _) =>
            {
                started.TrySetResult();
                return never.Task;
            },
            default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await RuntimeTestTasks.UntilAsync(() => time.NextTimerUtc == Epoch.AddSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await execution;

        Assert.False(result.Succeeded);
        Assert.Equal(RuntimeFailureKind.Timeout, result.Fault!.Kind);
        Assert.Equal("operation-attempt-timeout", result.Fault.Code.Value);
        Assert.False(never.Task.IsCompleted);
    }

    /// <summary>
    /// The wait for an attempt can observe the caller's cancellation before the token link has
    /// passed it to the attempt. Disposing the attempt's source on the way out then removed the
    /// link, and an operation that honours cancellation ran for ever. A supervisor stop hung on it.
    /// </summary>
    [Fact]
    public async Task CallerCancellationAlwaysReachesTheRunningAttempt()
    {
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var time = new ManualTimeProvider(Epoch);
            var executor = new ResilienceExecutor(time, new ExactJitter());
            using var cancellation = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? attempt = null;

            var execution = executor.ExecuteAsync<int>(
                Request(OperationPolicy.Once(TimeSpan.FromMinutes(1))),
                (_, token) =>
                {
                    attempt = Task.Delay(Timeout.InfiniteTimeSpan, time, token);
                    started.TrySetResult();
                    return WaitThenAnswerAsync(attempt);
                },
                cancellation.Token);
            await started.Task;
            await cancellation.CancelAsync();
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.False(result.Succeeded);
            Assert.Equal(RuntimeFailureKind.Cancelled, result.Fault!.Kind);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt!.WaitAsync(TimeSpan.FromSeconds(30)));
        }

        static async Task<int> WaitThenAnswerAsync(Task attempt)
        {
            await attempt;
            return 1;
        }
    }

    [Fact]
    public async Task ValidationAuthenticationVersionConflictAndUnsupportedFaultsAreNotRetried()
    {
        foreach (var kind in new[]
                 {
                     RuntimeFailureKind.Validation,
                     RuntimeFailureKind.Authentication,
                     RuntimeFailureKind.Version,
                     RuntimeFailureKind.Conflict,
                     RuntimeFailureKind.Unsupported,
                 })
        {
            var time = new ManualTimeProvider(Epoch);
            var executor = new ResilienceExecutor(time, new ExactJitter());
            var attempts = 0;
            var result = await executor.ExecuteAsync<int>(
                Request(ExecutionPrimitiveTests.RetryPolicy(IdempotencyRequirement.Guaranteed)),
                (_, _) =>
                {
                    attempts++;
                    return Task.FromException<int>(new RuntimeFaultException(new(
                        kind,
                        new("classified-failure"),
                        RuntimeRecoveryAction.None,
                        new("test:classified"),
                        time.GetUtcNow())));
                },
                default);

            Assert.False(result.Succeeded);
            Assert.Equal(1, attempts);
            Assert.Equal(kind, result.Fault!.Kind);
        }
    }

    [Fact]
    public async Task AnUnexpectedFailureRetriesOnlyWithAnIdempotencyGuarantee()
    {
        var time = new ManualTimeProvider(Epoch);
        var executor = new ResilienceExecutor(time, new ExactJitter());
        var attempts = 0;
        var execution = executor.ExecuteAsync(
            Request(ExecutionPrimitiveTests.RetryPolicy(IdempotencyRequirement.Guaranteed)),
            (_, _) => ++attempts == 1
                ? Task.FromException<int>(new InvalidOperationException("provider-specific private detail"))
                : Task.FromResult(7),
            default);

        await RuntimeTestTasks.UntilAsync(() =>
            Volatile.Read(ref attempts) == 1
            && time.NextTimerUtc == Epoch.AddMilliseconds(100));
        time.Advance(TimeSpan.FromMilliseconds(100));
        var result = await execution;

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task CircuitMovesClosedOpenHalfOpenAndClosedOnInjectedTime()
    {
        var time = new ManualTimeProvider(Epoch);
        var executor = new ResilienceExecutor(time, new ExactJitter());
        var policy = new OperationPolicy(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            1,
            TimeSpan.Zero,
            TimeSpan.Zero,
            1,
            0,
            2,
            TimeSpan.FromSeconds(5),
            WorkloadClass.IO,
            OperationRestartMode.Manual,
            IdempotencyRequirement.Guaranteed);

        var first = await executor.ExecuteAsync<int>(Request(policy), Failure, default);
        var second = await executor.ExecuteAsync<int>(Request(policy), Failure, default);
        var rejected = await executor.ExecuteAsync(Request(policy), (_, _) => Task.FromResult(1), default);

        Assert.False(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal(CircuitState.Open, Assert.Single(executor.Circuits).State);
        Assert.Equal(RuntimeFailureKind.CircuitOpen, rejected.Fault!.Kind);
        Assert.Equal(0, rejected.Attempts);

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(CircuitState.HalfOpen, Assert.Single(executor.Circuits).State);
        var probe = await executor.ExecuteAsync(Request(policy), (_, _) => Task.FromResult(7), default);
        Assert.True(probe.Succeeded);
        Assert.Equal(CircuitState.Closed, Assert.Single(executor.Circuits).State);

        static Task<int> Failure(OperationAttemptContext context, CancellationToken token) =>
            Task.FromException<int>(new IOException("not exported"));
    }

    [Fact]
    public void DefaultJitterIsStableForTheSameOperationAndAttempt()
    {
        var jitter = new DeterministicRetryJitter();
        var operation = OperationId.New();
        var context = new RetryJitterContext(operation, 2, TimeSpan.FromSeconds(1), 0.2);

        Assert.Equal(jitter.Apply(context), jitter.Apply(context));
    }

    /// <summary>
    /// A caller cancellation during a half-open probe must release the probe slot without
    /// closing the circuit or recording a success. Nothing was learned about the dependency.
    /// </summary>
    [Fact]
    public async Task CallerCancellationReleasesHalfOpenProbeWithoutChangingCircuitState()
    {
        var time = new ManualTimeProvider(Epoch);
        var executor = new ResilienceExecutor(time, new ExactJitter());
        var policy = new OperationPolicy(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10),
            1,
            TimeSpan.Zero,
            TimeSpan.Zero,
            1,
            0,
            2,
            TimeSpan.FromSeconds(5),
            WorkloadClass.IO,
            OperationRestartMode.Manual,
            IdempotencyRequirement.Guaranteed);

        // Open the circuit.
        for (var i = 0; i < 2; i++)
        {
            await executor.ExecuteAsync<int>(Request(policy), Failure, default);
        }

        Assert.Equal(CircuitState.Open, Assert.Single(executor.Circuits).State);

        // Advance past the open interval so the circuit moves to HalfOpen.
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(CircuitState.HalfOpen, Assert.Single(executor.Circuits).State);

        // A probe runs but the caller cancels before the operation completes.
        using var cancellation = new CancellationTokenSource();
        var probePending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = executor.ExecuteAsync<int>(Request(policy), (_, _) => probePending.Task, cancellation.Token);
        await RuntimeTestTasks.DrainAsync();
        Assert.Equal(CircuitState.HalfOpen, Assert.Single(executor.Circuits).State);
        Assert.True(Assert.Single(executor.Circuits).ProbeInProgress);

        await cancellation.CancelAsync();
        var result = await execution;

        // Caller cancel: classified as Cancelled, probe released, circuit stays HalfOpen.
        Assert.False(result.Succeeded);
        Assert.Equal(RuntimeFailureKind.Cancelled, result.Fault!.Kind);
        Assert.Equal(CircuitState.HalfOpen, Assert.Single(executor.Circuits).State);
        Assert.False(Assert.Single(executor.Circuits).ProbeInProgress);

        // The next probe can still acquire the half-open slot and, on success, close the circuit.
        probePending.TrySetResult(1);
        var probe = await executor.ExecuteAsync(Request(policy), (_, _) => Task.FromResult(42), default);
        Assert.True(probe.Succeeded);
        Assert.Equal(CircuitState.Closed, Assert.Single(executor.Circuits).State);

        static Task<int> Failure(OperationAttemptContext context, CancellationToken token) =>
            Task.FromException<int>(new IOException("dependency fault"));
    }

    /// <summary>
    /// An OperationCanceledException thrown by the dependency itself (not the caller's token)
    /// must be classified as Timeout, be retryable, and count against the circuit — so a
    /// dependency that internally times-out can open its circuit.
    /// </summary>
    [Fact]
    public async Task DependencyOriginatedCancellationIsClassifiedAsTimeoutAndCountsAgainstCircuit()
    {
        var time = new ManualTimeProvider(Epoch);
        var executor = new ResilienceExecutor(time, new ExactJitter());
        var policy = new OperationPolicy(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10),
            1,
            TimeSpan.Zero,
            TimeSpan.Zero,
            1,
            0,
            1,
            TimeSpan.FromSeconds(5),
            WorkloadClass.IO,
            OperationRestartMode.Manual,
            IdempotencyRequirement.Guaranteed);

        // The dependency throws its own OperationCanceledException (e.g. an HttpClient timeout)
        // while the caller's token is NOT cancelled.
        var result = await executor.ExecuteAsync<int>(
            Request(policy),
            (_, _) => Task.FromException<int>(new TaskCanceledException("dependency timed out")),
            default);

        Assert.False(result.Succeeded);
        Assert.Equal(RuntimeFailureKind.Timeout, result.Fault!.Kind);
        Assert.Equal("operation-dependency-cancelled", result.Fault.Code.Value);
        Assert.True(result.Fault.IsRetryable);
        // The single failure should have opened the circuit (threshold = 1).
        Assert.Equal(CircuitState.Open, Assert.Single(executor.Circuits).State);
    }

    /// <summary>
    /// A dependency may block before it returns a Task. Its invocation belongs on the default
    /// scheduler so the executor can still arm and report the attempt deadline while retaining
    /// that blocked invocation as unfinished work.
    /// </summary>
    [Fact]
    public async Task SynchronousDependencyPrefixCannotPreventTheAttemptDeadline()
    {
        var time = new ManualTimeProvider(Epoch);
        var executor = new ResilienceExecutor(time, new ExactJitter());
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();

        var execution = Task.Run(() => executor.ExecuteAsync(
            Request(OperationPolicy.Once(TimeSpan.FromSeconds(1))),
            (_, _) =>
            {
                callbackEntered.TrySetResult();
                releaseCallback.Wait();
                return Task.FromResult(42);
            },
            default));

        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await RuntimeTestTasks.UntilAsync(() => time.NextTimerUtc == Epoch.AddSeconds(1));
            time.Advance(TimeSpan.FromSeconds(1));
            await RuntimeTestTasks.UntilAsync(() => execution.IsCompleted);

            var result = await execution;
            Assert.False(result.Succeeded);
            Assert.Equal(RuntimeFailureKind.Timeout, result.Fault!.Kind);
            Assert.Equal("operation-attempt-timeout", result.Fault.Code.Value);
        }
        finally
        {
            releaseCallback.Set();
        }

        await RuntimeTestTasks.DrainAsync();
    }

    /// <summary>
    /// A cancellation callback is dependency code and may block indefinitely. Reporting a
    /// deadline must remain prompt; ownership of the callback and invocation continues behind
    /// the returned unfinished-attempt fence until both settle.
    /// </summary>
    [Fact]
    public async Task BlockedAttemptCancellationCallbackDoesNotBlockTimeoutReporting()
    {
        var time = new ManualTimeProvider(Epoch);
        var executor = new ResilienceExecutor(time, new ExactJitter());
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();

        var execution = executor.ExecuteAsync(
            Request(OperationPolicy.Once(TimeSpan.FromSeconds(1))),
            (_, token) =>
            {
                token.Register(() =>
                {
                    callbackEntered.TrySetResult();
                    releaseCallback.Wait();
                });
                operationStarted.TrySetResult();
                return pending.Task;
            },
            default);

        await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await RuntimeTestTasks.UntilAsync(() => time.NextTimerUtc == Epoch.AddSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await RuntimeTestTasks.UntilAsync(() => execution.IsCompleted);
            var result = await execution;
            Assert.False(result.Succeeded);
            Assert.Equal(RuntimeFailureKind.Timeout, result.Fault!.Kind);
        }
        finally
        {
            releaseCallback.Set();
            pending.TrySetResult(1);
        }

        await RuntimeTestTasks.DrainAsync();
    }

    private static OperationExecutionRequest Request(OperationPolicy policy) => new(
        new("runtime-test"),
        OperationId.New(),
        CorrelationId.New(),
        new("fixture-dependency"),
        policy);

    private sealed class ExactJitter : IRetryJitter
    {
        public TimeSpan Apply(RetryJitterContext context) => context.BaseDelay;
    }
}
