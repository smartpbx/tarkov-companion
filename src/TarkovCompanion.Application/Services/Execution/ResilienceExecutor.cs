using System.Collections.Immutable;
using System.Diagnostics;

namespace TarkovCompanion.Application.Services.Execution;

public enum CircuitState
{
    Closed = 1,
    Open,
    HalfOpen,
}

public sealed record CircuitSnapshot(
    RuntimeDependencyId DependencyId,
    CircuitState State,
    int ConsecutiveFailures,
    DateTimeOffset? OpenUntilUtc,
    bool ProbeInProgress);

public readonly record struct RetryJitterContext(
    OperationId OperationId,
    int NextAttempt,
    TimeSpan BaseDelay,
    double MaximumRatio);

public interface IRetryJitter
{
    TimeSpan Apply(RetryJitterContext context);
}

/// <summary>A stable jitter source whose answer depends only on the operation and attempt.</summary>
public sealed class DeterministicRetryJitter : IRetryJitter
{
    public TimeSpan Apply(RetryJitterContext context)
    {
        if (context.BaseDelay == TimeSpan.Zero || context.MaximumRatio == 0)
        {
            return context.BaseDelay;
        }

        Span<byte> bytes = stackalloc byte[20];
        context.OperationId.Value.TryWriteBytes(bytes);
        BitConverter.TryWriteBytes(bytes[16..], context.NextAttempt);
        uint hash = 2166136261;
        foreach (var value in bytes)
        {
            hash = (hash ^ value) * 16777619;
        }

        var unit = hash / (double)uint.MaxValue;
        var multiplier = 1 + ((unit * 2) - 1) * context.MaximumRatio;
        var ticks = Math.Clamp(
            (long)Math.Round(context.BaseDelay.Ticks * multiplier, MidpointRounding.AwayFromZero),
            0,
            TimeSpan.MaxValue.Ticks);
        return TimeSpan.FromTicks(ticks);
    }
}

public sealed record OperationExecutionRequest
{
    public OperationExecutionRequest(
        RuntimeFeatureId featureId,
        OperationId operationId,
        CorrelationId correlationId,
        RuntimeDependencyId dependencyId,
        OperationPolicy policy,
        IdempotencyKey? idempotencyKey = null)
    {
        FeatureId = featureId.IsDefined ? featureId : throw new ArgumentException("A feature id is required.", nameof(featureId));
        OperationId = operationId.IsDefined ? operationId : throw new ArgumentException("An operation id is required.", nameof(operationId));
        CorrelationId = correlationId.IsDefined ? correlationId : throw new ArgumentException("A correlation id is required.", nameof(correlationId));
        DependencyId = dependencyId.IsDefined ? dependencyId : throw new ArgumentException("A dependency id is required.", nameof(dependencyId));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (policy.IdempotencyRequirement == IdempotencyRequirement.RequireKey
            && idempotencyKey is not { IsDefined: true })
        {
            throw new ArgumentException("This retry policy requires an idempotency key.", nameof(idempotencyKey));
        }

        IdempotencyKey = idempotencyKey;
    }

    public RuntimeFeatureId FeatureId { get; }

    public OperationId OperationId { get; }

    public CorrelationId CorrelationId { get; }

    public RuntimeDependencyId DependencyId { get; }

    public OperationPolicy Policy { get; }

    public IdempotencyKey? IdempotencyKey { get; }
}

public readonly record struct OperationAttemptContext(
    OperationExecutionRequest Request,
    int Attempt,
    DateTimeOffset StartedUtc);

public sealed record OperationExecutionResult<T>(
    bool Succeeded,
    T? Value,
    RuntimeFault? Fault,
    int Attempts,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc)
{
    public static OperationExecutionResult<T> Success(
        T value,
        int attempts,
        DateTimeOffset startedUtc,
        DateTimeOffset completedUtc) =>
        new(true, value, null, attempts, startedUtc, completedUtc);

    public static OperationExecutionResult<T> Failure(
        RuntimeFault fault,
        int attempts,
        DateTimeOffset startedUtc,
        DateTimeOffset completedUtc) =>
        new(false, default, fault, attempts, startedUtc, completedUtc);
}

/// <summary>Executes dependency work against injected time and one circuit per dependency.</summary>
public sealed class ResilienceExecutor(TimeProvider timeProvider, IRetryJitter? jitter = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly IRetryJitter _jitter = jitter ?? new DeterministicRetryJitter();
    private readonly Dictionary<RuntimeDependencyId, DependencyCircuitBreaker> _circuits = [];

    public ImmutableArray<CircuitSnapshot> Circuits
    {
        get
        {
            lock (_gate)
            {
                return [.. _circuits.OrderBy(pair => pair.Key.Value, StringComparer.Ordinal)
                    .Select(pair => pair.Value.Snapshot(_timeProvider.GetUtcNow()))];
            }
        }
    }

    public async Task<OperationExecutionResult<T>> ExecuteAsync<T>(
        OperationExecutionRequest request,
        Func<OperationAttemptContext, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);
        var startedUtc = _timeProvider.GetUtcNow();
        var deadlineUtc = AddBounded(startedUtc, request.Policy.TotalTimeout);
        var circuit = CircuitFor(request);
        var reference = new DiagnosticReference($"operation:{request.OperationId}");
        RuntimeFault? lastFault = null;

        for (var attempt = 1; attempt <= request.Policy.MaxAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                lastFault = RuntimeFault.FromException(
                    new OperationCanceledException(),
                    _timeProvider,
                    reference);
                return OperationExecutionResult<T>.Failure(lastFault, attempt - 1, startedUtc, _timeProvider.GetUtcNow());
            }

            var now = _timeProvider.GetUtcNow();
            if (now >= deadlineUtc)
            {
                lastFault = TimeoutFault(reference, "operation-total-timeout");
                return OperationExecutionResult<T>.Failure(lastFault, attempt - 1, startedUtc, now);
            }

            if (!circuit.TryAcquire(now))
            {
                lastFault = RuntimeFault.CircuitOpen(_timeProvider, reference);
                return OperationExecutionResult<T>.Failure(lastFault, attempt - 1, startedUtc, now);
            }

            var remaining = deadlineUtc - now;
            var attemptTimeout = remaining < request.Policy.AttemptTimeout
                ? remaining
                : request.Policy.AttemptTimeout;
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var attemptContext = new OperationAttemptContext(request, attempt, now);
                var pending = operation(attemptContext, attemptCancellation.Token)
                    ?? throw new InvalidOperationException("The operation returned no task.");
                var value = await pending
                    .WaitAsync(attemptTimeout, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                circuit.RecordSuccess();
                return OperationExecutionResult<T>.Success(value, attempt, startedUtc, _timeProvider.GetUtcNow());
            }
            catch (TimeoutException)
            {
                attemptCancellation.Cancel();
                lastFault = TimeoutFault(reference, "operation-attempt-timeout");
                circuit.RecordFailure(_timeProvider.GetUtcNow());
            }
            catch (RuntimeFaultException exception)
            {
                lastFault = exception.Fault;
                if (CountsAgainstCircuit(lastFault))
                {
                    circuit.RecordFailure(_timeProvider.GetUtcNow());
                }
                else
                {
                    circuit.RecordSuccess();
                }
            }
            catch (Exception exception)
            {
                lastFault = RuntimeFault.FromException(exception, _timeProvider, reference);
                if (CountsAgainstCircuit(lastFault))
                {
                    circuit.RecordFailure(_timeProvider.GetUtcNow());
                }
                else
                {
                    circuit.RecordSuccess();
                }
            }

            if (!CanRetry(request, lastFault) || attempt == request.Policy.MaxAttempts)
            {
                return OperationExecutionResult<T>.Failure(
                    lastFault,
                    attempt,
                    startedUtc,
                    _timeProvider.GetUtcNow());
            }

            var baseDelay = RetryDelay(request.Policy, attempt);
            var delay = _jitter.Apply(new(request.OperationId, attempt + 1, baseDelay, request.Policy.JitterRatio));
            if (delay < TimeSpan.Zero || delay > OperationPolicy.MaximumDuration)
            {
                throw new InvalidOperationException("The injected retry jitter returned an out-of-policy delay.");
            }

            if (delay > request.Policy.MaxRetryDelay)
            {
                delay = request.Policy.MaxRetryDelay;
            }

            var beforeDelay = _timeProvider.GetUtcNow();
            if (beforeDelay >= deadlineUtc || delay >= deadlineUtc - beforeDelay)
            {
                lastFault = TimeoutFault(reference, "operation-total-timeout");
                return OperationExecutionResult<T>.Failure(lastFault, attempt, startedUtc, beforeDelay);
            }

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    lastFault = RuntimeFault.FromException(
                        new OperationCanceledException(),
                        _timeProvider,
                        reference);
                    return OperationExecutionResult<T>.Failure(
                        lastFault,
                        attempt,
                        startedUtc,
                        _timeProvider.GetUtcNow());
                }
            }
        }

        throw new UnreachableException();
    }

    private DependencyCircuitBreaker CircuitFor(OperationExecutionRequest request)
    {
        lock (_gate)
        {
            if (!_circuits.TryGetValue(request.DependencyId, out var circuit))
            {
                circuit = new(
                    request.DependencyId,
                    request.Policy.CircuitFailureThreshold,
                    request.Policy.CircuitOpenInterval);
                _circuits.Add(request.DependencyId, circuit);
            }
            else
            {
                circuit.EnsurePolicy(
                    request.Policy.CircuitFailureThreshold,
                    request.Policy.CircuitOpenInterval);
            }

            return circuit;
        }
    }

    private RuntimeFault TimeoutFault(DiagnosticReference reference, string code) =>
        new(
            RuntimeFailureKind.Timeout,
            new(code),
            RuntimeRecoveryAction.RetryAutomatically,
            reference,
            _timeProvider.GetUtcNow());

    private static bool CanRetry(OperationExecutionRequest request, RuntimeFault fault) =>
        fault.IsRetryable && request.Policy.IdempotencyRequirement is
            IdempotencyRequirement.Guaranteed or IdempotencyRequirement.RequireKey;

    private static bool CountsAgainstCircuit(RuntimeFault fault) =>
        fault.Kind is RuntimeFailureKind.Transient or RuntimeFailureKind.Timeout or RuntimeFailureKind.Unexpected;

    private static TimeSpan RetryDelay(OperationPolicy policy, int completedAttempt)
    {
        var multiplier = Math.Pow(policy.RetryBackoffFactor, completedAttempt - 1);
        var ticks = Math.Min(policy.MaxRetryDelay.Ticks, policy.InitialRetryDelay.Ticks * multiplier);
        return TimeSpan.FromTicks((long)Math.Round(ticks, MidpointRounding.AwayFromZero));
    }

    private static DateTimeOffset AddBounded(DateTimeOffset value, TimeSpan duration)
    {
        try
        {
            return value + duration;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MaxValue;
        }
    }

    private sealed class DependencyCircuitBreaker(
        RuntimeDependencyId dependencyId,
        int failureThreshold,
        TimeSpan openInterval)
    {
        private readonly object _gate = new();
        private CircuitState _state = CircuitState.Closed;
        private int _consecutiveFailures;
        private DateTimeOffset? _openUntilUtc;
        private bool _probeInProgress;

        public void EnsurePolicy(int expectedThreshold, TimeSpan expectedInterval)
        {
            if (failureThreshold != expectedThreshold || openInterval != expectedInterval)
            {
                throw new InvalidOperationException("One dependency must use one circuit policy.");
            }
        }

        public bool TryAcquire(DateTimeOffset now)
        {
            lock (_gate)
            {
                if (_state == CircuitState.Open && now >= _openUntilUtc)
                {
                    _state = CircuitState.HalfOpen;
                    _probeInProgress = false;
                }

                if (_state == CircuitState.Open || (_state == CircuitState.HalfOpen && _probeInProgress))
                {
                    return false;
                }

                if (_state == CircuitState.HalfOpen)
                {
                    _probeInProgress = true;
                }

                return true;
            }
        }

        public void RecordSuccess()
        {
            lock (_gate)
            {
                _state = CircuitState.Closed;
                _consecutiveFailures = 0;
                _openUntilUtc = null;
                _probeInProgress = false;
            }
        }

        public void RecordFailure(DateTimeOffset now)
        {
            lock (_gate)
            {
                _consecutiveFailures = checked(_consecutiveFailures + 1);
                if (_state == CircuitState.HalfOpen || _consecutiveFailures >= failureThreshold)
                {
                    _state = CircuitState.Open;
                    _openUntilUtc = AddBounded(now, openInterval);
                }

                _probeInProgress = false;
            }
        }

        public CircuitSnapshot Snapshot(DateTimeOffset now)
        {
            lock (_gate)
            {
                var reportedState = _state == CircuitState.Open && now >= _openUntilUtc
                    ? CircuitState.HalfOpen
                    : _state;
                return new(
                    dependencyId,
                    reportedState,
                    _consecutiveFailures,
                    _openUntilUtc,
                    reportedState == CircuitState.HalfOpen && _probeInProgress);
            }
        }
    }
}
