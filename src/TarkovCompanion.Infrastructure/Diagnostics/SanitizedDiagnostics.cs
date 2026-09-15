using TarkovCompanion.Application.Services.Execution;

namespace TarkovCompanion.Infrastructure.Diagnostics;

/// <summary>
/// The deliberately small vocabulary that reaches a diagnostic sink.
/// </summary>
/// <remarks>
/// Diagnostics previously accepted formatted log messages. A formatted message can accidentally
/// carry a screenshot path, a display name, or an exception body, and no downstream redactor can
/// prove it found every variant. This closed record carries only operational categories and
/// bounded numbers. Persistence is intentionally left to the v2 persistence contract (#270).
/// </remarks>
public sealed record SanitizedDiagnosticEvent
{
    public SanitizedDiagnosticEvent(
        DiagnosticEventKind Kind,
        CorrelationId correlationId,
        DateTimeOffset OccurredUtc,
        DiagnosticOutcome Outcome,
        RuntimeFailureKind FailureKind,
        int? DurationMilliseconds = null,
        int? Attempt = null)
    {
        if (OccurredUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Diagnostic timestamps must be UTC.", nameof(OccurredUtc));
        }

        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        if (!correlationId.IsDefined)
        {
            throw new ArgumentException("A runtime correlation id is required.", nameof(correlationId));
        }

        if (!Enum.IsDefined(Outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(Outcome));
        }

        if (!Enum.IsDefined(FailureKind))
        {
            throw new ArgumentOutOfRangeException(nameof(FailureKind));
        }

        if (DurationMilliseconds is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DurationMilliseconds));
        }

        if (Attempt is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(Attempt));
        }

        this.Kind = Kind;
        this.CorrelationId = correlationId;
        this.OccurredUtc = OccurredUtc;
        this.Outcome = Outcome;
        this.FailureKind = FailureKind;
        // An operation that takes longer than the reporting ceiling is still a real operation.
        // Capping preserves the closed numeric surface without making diagnostics the failure.
        this.DurationMilliseconds = DurationMilliseconds is { } duration
            ? Math.Min(duration, 300_000)
            : null;
        this.Attempt = Attempt;
    }

    public DiagnosticEventKind Kind { get; }

    public CorrelationId CorrelationId { get; }

    public DateTimeOffset OccurredUtc { get; }

    public DiagnosticOutcome Outcome { get; }

    public RuntimeFailureKind FailureKind { get; }

    public int? DurationMilliseconds { get; }

    public int? Attempt { get; }
}

public enum DiagnosticEventKind
{
    Startup = 1,
    Synchronization = 2,
    RecognitionStage = 3,
    DatabaseOperation = 4,
    MapOperation = 5,
    DeviceRelayOperation = 6,
    UpdateOperation = 7,
    Failure = 8,
    CrashRecovery = 9,
}

public enum DiagnosticOutcome
{
    Unknown = 0,
    Started = 1,
    Succeeded = 2,
    Deferred = 3,
    Rejected = 4,
    Failed = 5,
    Recovered = 6,
}

/// <summary>
/// Adapts the canonical runtime fault vocabulary to the closed diagnostic event shape.
/// </summary>
/// <remarks>
/// The runtime owns correlation and failure classification. Keeping this adapter on the
/// Infrastructure side avoids a second identifier or failure DTO that Application could never
/// emit, while preserving the diagnostics persistence seam for #270.
/// </remarks>
public static class RuntimeDiagnosticAdapter
{
    public static SanitizedDiagnosticEvent FromRuntimeFault(
        DiagnosticEventKind kind,
        CorrelationId correlationId,
        RuntimeFault fault,
        int? durationMilliseconds = null,
        int? attempt = null)
    {
        ArgumentNullException.ThrowIfNull(fault);
        return new(
            kind,
            correlationId,
            fault.OccurredUtc,
            DiagnosticOutcome.Failed,
            fault.Kind,
            durationMilliseconds,
            attempt);
    }
}

/// <summary>
/// A bounded, already-sanitized recovery note that a #270 storage adapter may persist.
/// </summary>
public sealed record CrashRecoveryState
{
    public const int MaximumEvents = 12;

    public static CrashRecoveryState Empty { get; } = new([]);

    public CrashRecoveryState(IReadOnlyList<SanitizedDiagnosticEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count > MaximumEvents || events.Any(diagnosticEvent => diagnosticEvent is null))
        {
            throw new ArgumentOutOfRangeException(nameof(events));
        }

        // The caller can pass a mutable List through IReadOnlyList. Copy after validation so a
        // later append or null replacement cannot bypass the recovery bound or alias this state.
        Events = [.. events];
    }

    public IReadOnlyList<SanitizedDiagnosticEvent> Events { get; }

    public CrashRecoveryState Add(SanitizedDiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);

        return new([.. Events.Append(diagnosticEvent).TakeLast(MaximumEvents)]);
    }

    /// <summary>The startup UI can truthfully say whether the preceding run failed without exposing a stack trace.</summary>
    public SanitizedDiagnosticEvent? PreviousFailure
    {
        get
        {
            for (var index = Events.Count - 1; index >= 0; index--)
            {
                var diagnosticEvent = Events[index];
                if (diagnosticEvent.Outcome == DiagnosticOutcome.Recovered)
                {
                    return null;
                }

                if (diagnosticEvent.Outcome == DiagnosticOutcome.Failed)
                {
                    return diagnosticEvent;
                }
            }

            return null;
        }
    }
}
