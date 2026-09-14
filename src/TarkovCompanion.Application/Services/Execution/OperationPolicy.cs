namespace TarkovCompanion.Application.Services.Execution;

public enum WorkloadClass
{
    Light = 1,
    IO,
    CPU,
    HeavyExclusive,
}

public enum WorkPriority
{
    Background = 1,
    Normal,
    Interactive,
    UserBlocking,
}

public enum OperationRestartMode
{
    Never = 1,
    Manual,
    OnFailure,
    Always,
}

public enum IdempotencyRequirement
{
    SingleAttempt = 1,
    Guaranteed,
    RequireKey,
}

/// <summary>One immutable, validated execution and resilience budget.</summary>
public sealed record OperationPolicy
{
    public const int MaxAttemptsLimit = 16;
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(24);

    public OperationPolicy(
        TimeSpan attemptTimeout,
        TimeSpan totalTimeout,
        int maxAttempts,
        TimeSpan initialRetryDelay,
        TimeSpan maxRetryDelay,
        double retryBackoffFactor,
        double jitterRatio,
        int circuitFailureThreshold,
        TimeSpan circuitOpenInterval,
        WorkloadClass workloadClass,
        OperationRestartMode restartMode,
        IdempotencyRequirement idempotencyRequirement)
    {
        AttemptTimeout = PositiveDuration(attemptTimeout, nameof(attemptTimeout));
        TotalTimeout = PositiveDuration(totalTimeout, nameof(totalTimeout));
        if (totalTimeout < attemptTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(totalTimeout), "The total timeout cannot be shorter than one attempt.");
        }

        if (maxAttempts is < 1 or > MaxAttemptsLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        if (initialRetryDelay < TimeSpan.Zero || initialRetryDelay > MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(initialRetryDelay));
        }

        if (maxRetryDelay < initialRetryDelay || maxRetryDelay > MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetryDelay));
        }

        if (!double.IsFinite(retryBackoffFactor) || retryBackoffFactor is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(retryBackoffFactor));
        }

        if (!double.IsFinite(jitterRatio) || jitterRatio is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterRatio));
        }

        if (circuitFailureThreshold is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(circuitFailureThreshold));
        }

        CircuitOpenInterval = PositiveDuration(circuitOpenInterval, nameof(circuitOpenInterval));
        WorkloadClass = Defined(workloadClass, nameof(workloadClass));
        RestartMode = Defined(restartMode, nameof(restartMode));
        IdempotencyRequirement = Defined(idempotencyRequirement, nameof(idempotencyRequirement));
        if (idempotencyRequirement == IdempotencyRequirement.SingleAttempt && maxAttempts != 1)
        {
            throw new ArgumentException("Non-idempotent work must have exactly one attempt.", nameof(maxAttempts));
        }

        MaxAttempts = maxAttempts;
        InitialRetryDelay = initialRetryDelay;
        MaxRetryDelay = maxRetryDelay;
        RetryBackoffFactor = retryBackoffFactor;
        JitterRatio = jitterRatio;
        CircuitFailureThreshold = circuitFailureThreshold;
    }

    public TimeSpan AttemptTimeout { get; }

    public TimeSpan TotalTimeout { get; }

    public int MaxAttempts { get; }

    public TimeSpan InitialRetryDelay { get; }

    public TimeSpan MaxRetryDelay { get; }

    public double RetryBackoffFactor { get; }

    public double JitterRatio { get; }

    public int CircuitFailureThreshold { get; }

    public TimeSpan CircuitOpenInterval { get; }

    public WorkloadClass WorkloadClass { get; }

    public OperationRestartMode RestartMode { get; }

    public IdempotencyRequirement IdempotencyRequirement { get; }

    public static OperationPolicy Once(
        TimeSpan timeout,
        WorkloadClass workloadClass = WorkloadClass.Light,
        OperationRestartMode restartMode = OperationRestartMode.Manual,
        IdempotencyRequirement idempotencyRequirement = IdempotencyRequirement.SingleAttempt) =>
        new(
            timeout,
            timeout,
            1,
            TimeSpan.Zero,
            TimeSpan.Zero,
            1,
            0,
            3,
            TimeSpan.FromMinutes(1),
            workloadClass,
            restartMode,
            idempotencyRequirement);

    private static TimeSpan PositiveDuration(TimeSpan value, string parameterName) =>
        value <= TimeSpan.Zero || value > MaximumDuration
            ? throw new ArgumentOutOfRangeException(parameterName)
            : value;

    private static TEnum Defined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum =>
        Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(parameterName);
}
