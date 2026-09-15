using System.Security.Cryptography;

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
        DiagnosticCorrelationId CorrelationId,
        DateTimeOffset OccurredUtc,
        DiagnosticOutcome Outcome,
        DiagnosticFailureCode Failure,
        int? DurationMilliseconds = null,
        int? Attempt = null)
    {
        if (OccurredUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Diagnostic timestamps must be UTC.", nameof(OccurredUtc));
        }

        if (DurationMilliseconds is < 0 or > 300_000)
        {
            throw new ArgumentOutOfRangeException(nameof(DurationMilliseconds));
        }

        if (Attempt is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(Attempt));
        }

        this.Kind = Kind;
        this.CorrelationId = CorrelationId;
        this.OccurredUtc = OccurredUtc;
        this.Outcome = Outcome;
        this.Failure = Failure;
        this.DurationMilliseconds = DurationMilliseconds;
        this.Attempt = Attempt;
    }

    public DiagnosticEventKind Kind { get; }

    public DiagnosticCorrelationId CorrelationId { get; }

    public DateTimeOffset OccurredUtc { get; }

    public DiagnosticOutcome Outcome { get; }

    public DiagnosticFailureCode Failure { get; }

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
/// Failure classes, not exception messages. The omitted message is the privacy boundary.
/// </summary>
public enum DiagnosticFailureCode
{
    None = 0,
    Cancelled = 1,
    TimedOut = 2,
    Unavailable = 3,
    InvalidInput = 4,
    RateLimited = 5,
    StorageUnavailable = 6,
    StorageFull = 7,
    NetworkFailure = 8,
    Unexpected = 9,
}

/// <summary>A random per-operation join key; it is never derived from a person, room, or path.</summary>
public readonly record struct DiagnosticCorrelationId
{
    private const int ByteLength = 16;

    private DiagnosticCorrelationId(string value) => Value = value;

    public string Value { get; }

    public static DiagnosticCorrelationId Create() =>
        new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(ByteLength)));

    public static bool TryParse(string? value, out DiagnosticCorrelationId correlationId)
    {
        if (value is { Length: ByteLength * 2 } && value.All(Uri.IsHexDigit))
        {
            correlationId = new(value.ToLowerInvariant());
            return true;
        }

        correlationId = default;
        return false;
    }

    public override string ToString() => Value;
}

/// <summary>
/// A bounded, already-sanitized recovery note that a #270 storage adapter may persist.
/// </summary>
public sealed record CrashRecoveryState
{
    public const int MaximumEvents = 12;

    public static CrashRecoveryState Empty { get; } = new([]);

    public CrashRecoveryState(IReadOnlyList<SanitizedDiagnosticEvent> Events)
    {
        ArgumentNullException.ThrowIfNull(Events);
        if (Events.Count > MaximumEvents || Events.Any(diagnosticEvent => diagnosticEvent is null))
        {
            throw new ArgumentOutOfRangeException(nameof(Events));
        }

        this.Events = Events;
    }

    public IReadOnlyList<SanitizedDiagnosticEvent> Events { get; }

    public CrashRecoveryState Add(SanitizedDiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);

        return new([.. Events.Append(diagnosticEvent).TakeLast(MaximumEvents)]);
    }

    /// <summary>The startup UI can truthfully say whether the preceding run failed without exposing a stack trace.</summary>
    public SanitizedDiagnosticEvent? PreviousFailure =>
        Events.LastOrDefault(diagnosticEvent => diagnosticEvent.Outcome == DiagnosticOutcome.Failed);
}
