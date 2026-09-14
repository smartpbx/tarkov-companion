namespace TarkovCompanion.Application.Services.Execution;

public enum RuntimeFailureKind
{
    Transient = 1,
    Timeout,
    Validation,
    Authentication,
    Version,
    Conflict,
    Unsupported,
    Cancelled,
    CircuitOpen,
    Unexpected,
}

public enum RuntimeRecoveryAction
{
    RetryAutomatically = 1,
    RetryManually,
    CheckConfiguration,
    Reauthenticate,
    Upgrade,
    ResolveConflict,
    None,
}

public readonly record struct RuntimeFaultCode
{
    public const int MaxLength = 64;

    public RuntimeFaultCode(string value) => Value = RuntimeIdentifier.Validate(value, nameof(value), MaxLength);

    public string Value { get; }

    public bool IsDefined => !string.IsNullOrEmpty(Value);

    public override string ToString() => Value;
}

public readonly record struct DiagnosticReference
{
    public const int MaxLength = 96;

    public DiagnosticReference(string value) => Value = RuntimeIdentifier.Validate(value, nameof(value), MaxLength);

    public string Value { get; }

    public bool IsDefined => !string.IsNullOrEmpty(Value);

    public override string ToString() => Value;
}

/// <summary>
/// A safe runtime failure. Exception messages and contextual payloads deliberately have no slot.
/// </summary>
public sealed record RuntimeFault
{
    public RuntimeFault(
        RuntimeFailureKind kind,
        RuntimeFaultCode code,
        RuntimeRecoveryAction recovery,
        DiagnosticReference diagnosticReference,
        DateTimeOffset occurredUtc)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!code.IsDefined)
        {
            throw new ArgumentException("A fault code is required.", nameof(code));
        }

        if (!Enum.IsDefined(recovery))
        {
            throw new ArgumentOutOfRangeException(nameof(recovery));
        }

        if (!diagnosticReference.IsDefined)
        {
            throw new ArgumentException("A diagnostic reference is required.", nameof(diagnosticReference));
        }

        Kind = kind;
        Code = code;
        Recovery = recovery;
        DiagnosticReference = diagnosticReference;
        OccurredUtc = occurredUtc.ToUniversalTime();
    }

    public RuntimeFailureKind Kind { get; }

    public RuntimeFaultCode Code { get; }

    public RuntimeRecoveryAction Recovery { get; }

    public DiagnosticReference DiagnosticReference { get; }

    public DateTimeOffset OccurredUtc { get; }

    // An unexpected dependency failure may be a provider-specific transient (SQLite busy is
    // one example) that the Application layer cannot classify without taking an Infrastructure
    // dependency. It may retry only behind the executor's separate idempotency gate.
    public bool IsRetryable => Kind is
        RuntimeFailureKind.Transient or RuntimeFailureKind.Timeout or RuntimeFailureKind.Unexpected;

    public static RuntimeFault FromException(Exception exception, TimeProvider timeProvider, DiagnosticReference reference)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var (kind, code, recovery) = exception switch
        {
            TimeoutException => (RuntimeFailureKind.Timeout, "operation-timeout", RuntimeRecoveryAction.RetryAutomatically),
            OperationCanceledException => (RuntimeFailureKind.Cancelled, "operation-cancelled", RuntimeRecoveryAction.RetryManually),
            UnauthorizedAccessException => (RuntimeFailureKind.Authentication, "operation-unauthorized", RuntimeRecoveryAction.Reauthenticate),
            ArgumentException or FormatException => (RuntimeFailureKind.Validation, "operation-invalid", RuntimeRecoveryAction.CheckConfiguration),
            NotSupportedException => (RuntimeFailureKind.Unsupported, "operation-unsupported", RuntimeRecoveryAction.Upgrade),
            IOException => (RuntimeFailureKind.Transient, "dependency-io", RuntimeRecoveryAction.RetryAutomatically),
            _ => (RuntimeFailureKind.Unexpected, "operation-failed", RuntimeRecoveryAction.RetryManually),
        };
        return new(kind, new(code), recovery, reference, timeProvider.GetUtcNow());
    }

    public static RuntimeFault CircuitOpen(TimeProvider timeProvider, DiagnosticReference reference) =>
        new(
            RuntimeFailureKind.CircuitOpen,
            new("dependency-circuit-open"),
            RuntimeRecoveryAction.RetryManually,
            reference,
            timeProvider.GetUtcNow());
}

/// <summary>Lets a dependency classify a failure without exposing its unsafe exception text.</summary>
public sealed class RuntimeFaultException(RuntimeFault fault) : Exception("A classified runtime operation failed.")
{
    public RuntimeFault Fault { get; } = fault ?? throw new ArgumentNullException(nameof(fault));
}
