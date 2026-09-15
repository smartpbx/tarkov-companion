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

        await RuntimeTestTasks.DrainAsync();
        Assert.Equal(1, attempts);
        Assert.False(execution.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(100));
        await RuntimeTestTasks.DrainAsync();
        Assert.Equal(2, attempts);
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
        var policy = OperationPolicy.Once(TimeSpan.FromSeconds(1));

        var execution = executor.ExecuteAsync(Request(policy), (_, _) => never.Task, default);
        await RuntimeTestTasks.DrainAsync();
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await execution;

        Assert.False(result.Succeeded);
        Assert.Equal(RuntimeFailureKind.Timeout, result.Fault!.Kind);
        Assert.Equal("operation-attempt-timeout", result.Fault.Code.Value);
        Assert.False(never.Task.IsCompleted);
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

        await RuntimeTestTasks.DrainAsync();
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
