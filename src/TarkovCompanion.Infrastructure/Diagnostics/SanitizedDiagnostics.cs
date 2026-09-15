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
        DiagnosticEventKind kind,
        CorrelationId runId,
        CorrelationId correlationId,
        DateTimeOffset occurredUtc,
        DiagnosticOutcome outcome,
        RuntimeFailureKind? failureKind = null,
        int? durationMilliseconds = null,
        int? attempt = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!runId.IsDefined)
        {
            throw new ArgumentException("A run correlation id is required.", nameof(runId));
        }

        if (!correlationId.IsDefined)
        {
            throw new ArgumentException("A runtime correlation id is required.", nameof(correlationId));
        }

        if (occurredUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Diagnostic timestamps must be UTC.", nameof(occurredUtc));
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (failureKind is { } definedFailure && !Enum.IsDefined(definedFailure))
        {
            throw new ArgumentOutOfRangeException(nameof(failureKind));
        }

        // A non-failure event carrying a failure classification is ambiguous, while a failed
        // event without one cannot be routed or aggregated. Rejection is an expected outcome;
        // callers that classify it as a runtime failure must emit Failed instead.
        if ((outcome == DiagnosticOutcome.Failed) != failureKind.HasValue)
        {
            throw new ArgumentException(
                "FailureKind must be present exactly when Outcome is Failed.",
                nameof(failureKind));
        }

        if (durationMilliseconds is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        }

        if (attempt is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        Kind = kind;
        RunId = runId;
        CorrelationId = correlationId;
        OccurredUtc = occurredUtc;
        Outcome = outcome;
        FailureKind = failureKind;
        // An operation that takes longer than the reporting ceiling is still a real operation.
        // Capping preserves the closed numeric surface without making diagnostics the failure.
        DurationMilliseconds = durationMilliseconds is { } duration
            ? Math.Min(duration, 300_000)
            : null;
        Attempt = attempt;
    }

    public DiagnosticEventKind Kind { get; }

    /// <summary>The random process-run correlation, never a player, room, or device identity.</summary>
    public CorrelationId RunId { get; }

    public CorrelationId CorrelationId { get; }

    public DateTimeOffset OccurredUtc { get; }

    public DiagnosticOutcome Outcome { get; }

    public RuntimeFailureKind? FailureKind { get; }

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
        CorrelationId runId,
        CorrelationId correlationId,
        RuntimeFault fault,
        int? durationMilliseconds = null,
        int? attempt = null)
    {
        ArgumentNullException.ThrowIfNull(fault);
        return new(
            kind,
            runId,
            correlationId,
            fault.OccurredUtc,
            DiagnosticOutcome.Failed,
            fault.Kind,
            durationMilliseconds,
            attempt);
    }
}

/// <summary>One explicit process-run boundary in the bounded recovery journal.</summary>
public sealed record DiagnosticRunBoundary
{
    public DiagnosticRunBoundary(
        DiagnosticRunBoundaryKind kind,
        CorrelationId runId,
        DateTimeOffset occurredUtc)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!runId.IsDefined)
        {
            throw new ArgumentException("A run correlation id is required.", nameof(runId));
        }

        if (occurredUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Run-boundary timestamps must be UTC.", nameof(occurredUtc));
        }

        Kind = kind;
        RunId = runId;
        OccurredUtc = occurredUtc;
    }

    public DiagnosticRunBoundaryKind Kind { get; }

    public CorrelationId RunId { get; }

    public DateTimeOffset OccurredUtc { get; }
}

public enum DiagnosticRunBoundaryKind
{
    Started = 1,
    CleanExit = 2,
    UncleanExitObserved = 3,
}

public enum PreviousRunTermination
{
    None = 0,
    Clean = 1,
    Unclean = 2,
}

/// <summary>An honest interpretation of one explicitly identified preceding process run.</summary>
public sealed record PreviousRunRecovery
{
    public static PreviousRunRecovery None { get; } = new(null, PreviousRunTermination.None, null);

    public PreviousRunRecovery(
        CorrelationId? runId,
        PreviousRunTermination termination,
        SanitizedDiagnosticEvent? lastUnrecoveredFailure)
    {
        if (!Enum.IsDefined(termination))
        {
            throw new ArgumentOutOfRangeException(nameof(termination));
        }

        if (termination == PreviousRunTermination.None)
        {
            if (runId is not null || lastUnrecoveredFailure is not null)
            {
                throw new ArgumentException("An absent previous run cannot carry recovery evidence.");
            }
        }
        else if (runId is not { IsDefined: true })
        {
            throw new ArgumentException("A previous run correlation id is required.", nameof(runId));
        }

        if (termination == PreviousRunTermination.Clean && lastUnrecoveredFailure is not null)
        {
            throw new ArgumentException("A clean run has no unrecovered failure.", nameof(lastUnrecoveredFailure));
        }

        if (lastUnrecoveredFailure is { Outcome: not DiagnosticOutcome.Failed })
        {
            throw new ArgumentException(
                "Unrecovered failure evidence must be a failed diagnostic event.",
                nameof(lastUnrecoveredFailure));
        }

        if (lastUnrecoveredFailure is not null &&
            (runId is not { } previousRunId || lastUnrecoveredFailure.RunId != previousRunId))
        {
            throw new ArgumentException("Failure evidence must belong to the previous run.", nameof(lastUnrecoveredFailure));
        }

        RunId = runId;
        Termination = termination;
        LastUnrecoveredFailure = lastUnrecoveredFailure;
    }

    public CorrelationId? RunId { get; }

    public PreviousRunTermination Termination { get; }

    public SanitizedDiagnosticEvent? LastUnrecoveredFailure { get; }
}

/// <summary>
/// A bounded, already-sanitized recovery journal that a #270 storage adapter may persist.
/// </summary>
/// <remarks>
/// A crash cannot write its own marker. <see cref="BeginRun"/> therefore records an explicit
/// <see cref="DiagnosticRunBoundaryKind.UncleanExitObserved"/> for the prior run when its start
/// has no terminal marker. Recovery is grouped by the random run id, so an older failure cannot
/// be presented as evidence about a different launch.
/// </remarks>
public sealed record CrashRecoveryState
{
    public const int MaximumEvents = 12;
    public const int MaximumRunBoundaries = 12;

    public static CrashRecoveryState Empty { get; } = new([], []);

    public CrashRecoveryState(
        IReadOnlyList<SanitizedDiagnosticEvent> events,
        IReadOnlyList<DiagnosticRunBoundary> runBoundaries)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(runBoundaries);
        if (events.Count > MaximumEvents || events.Any(diagnosticEvent => diagnosticEvent is null))
        {
            throw new ArgumentOutOfRangeException(nameof(events));
        }

        if (runBoundaries.Count > MaximumRunBoundaries || runBoundaries.Any(boundary => boundary is null))
        {
            throw new ArgumentOutOfRangeException(nameof(runBoundaries));
        }

        ValidateJournal(events, runBoundaries);

        // A caller can pass mutable Lists through IReadOnlyList. Read-only wrappers around copies
        // prevent later replacement as well as append-based bypass of either journal bound.
        Events = Array.AsReadOnly(events.ToArray());
        RunBoundaries = Array.AsReadOnly(runBoundaries.ToArray());
    }

    public IReadOnlyList<SanitizedDiagnosticEvent> Events { get; }

    public IReadOnlyList<DiagnosticRunBoundary> RunBoundaries { get; }

    public CrashRecoveryState BeginRun(CorrelationId runId, DateTimeOffset occurredUtc)
    {
        if (!runId.IsDefined)
        {
            throw new ArgumentException("A run correlation id is required.", nameof(runId));
        }

        if (occurredUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Run-boundary timestamps must be UTC.", nameof(occurredUtc));
        }

        if (RunBoundaries.Any(boundary => boundary.RunId == runId))
        {
            throw new ArgumentException("A run correlation id cannot be reused.", nameof(runId));
        }

        if ((RunBoundaries.Count > 0 && occurredUtc < RunBoundaries[^1].OccurredUtc) ||
            (Events.Count > 0 && occurredUtc < Events[^1].OccurredUtc))
        {
            throw new ArgumentOutOfRangeException(nameof(occurredUtc));
        }

        var next = RunBoundaries.ToList();
        if (FindOpenRun(next) is { } openRun)
        {
            next.Add(new(DiagnosticRunBoundaryKind.UncleanExitObserved, openRun, occurredUtc));
        }

        next.Add(new(DiagnosticRunBoundaryKind.Started, runId, occurredUtc));
        var trimmedBoundaries = TrimBoundaries(next);
        return new(KeepEventsForKnownRuns(Events, trimmedBoundaries), trimmedBoundaries);
    }

    public CrashRecoveryState CompleteRun(CorrelationId runId, DateTimeOffset occurredUtc)
    {
        if (!runId.IsDefined)
        {
            throw new ArgumentException("A run correlation id is required.", nameof(runId));
        }

        if (occurredUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Run-boundary timestamps must be UTC.", nameof(occurredUtc));
        }

        if (FindOpenRun(RunBoundaries) != runId)
        {
            throw new InvalidOperationException("Only the currently open run can record a clean exit.");
        }

        if (occurredUtc < RunBoundaries[^1].OccurredUtc ||
            (Events.Count > 0 && occurredUtc < Events[^1].OccurredUtc))
        {
            throw new ArgumentOutOfRangeException(nameof(occurredUtc));
        }

        var trimmedBoundaries = TrimBoundaries(
            [.. RunBoundaries, new(DiagnosticRunBoundaryKind.CleanExit, runId, occurredUtc)]);
        return new(KeepEventsForKnownRuns(Events, trimmedBoundaries), trimmedBoundaries);
    }

    public CrashRecoveryState Add(SanitizedDiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        if (FindOpenRun(RunBoundaries) != diagnosticEvent.RunId)
        {
            throw new InvalidOperationException("Diagnostic recovery evidence must belong to the currently open run.");
        }

        if (diagnosticEvent.OccurredUtc < RunBoundaries[^1].OccurredUtc ||
            (Events.Count > 0 && diagnosticEvent.OccurredUtc < Events[^1].OccurredUtc))
        {
            throw new ArgumentOutOfRangeException(nameof(diagnosticEvent));
        }

        return new(
            [.. Events.Append(diagnosticEvent).TakeLast(MaximumEvents)],
            RunBoundaries);
    }

    /// <summary>
    /// Describes the most recent run other than <paramref name="currentRunId"/> without claiming
    /// that an unexpected exit necessarily emitted a failure first.
    /// </summary>
    public PreviousRunRecovery InspectPreviousRun(CorrelationId currentRunId)
    {
        if (!currentRunId.IsDefined)
        {
            throw new ArgumentException("A current run correlation id is required.", nameof(currentRunId));
        }

        if (FindOpenRun(RunBoundaries) != currentRunId)
        {
            throw new InvalidOperationException("Recovery inspection requires the currently open run.");
        }

        for (var index = RunBoundaries.Count - 1; index >= 0; index--)
        {
            var started = RunBoundaries[index];
            if (started.Kind != DiagnosticRunBoundaryKind.Started || started.RunId == currentRunId)
            {
                continue;
            }

            var terminal = RunBoundaries
                .Skip(index + 1)
                .FirstOrDefault(boundary =>
                    boundary.RunId == started.RunId &&
                    boundary.Kind is DiagnosticRunBoundaryKind.CleanExit or DiagnosticRunBoundaryKind.UncleanExitObserved);
            var termination = terminal?.Kind == DiagnosticRunBoundaryKind.CleanExit
                ? PreviousRunTermination.Clean
                : PreviousRunTermination.Unclean;
            SanitizedDiagnosticEvent? unrecoveredFailure = null;
            if (termination == PreviousRunTermination.Unclean)
            {
                var outstandingFailures = new Dictionary<CorrelationId, SanitizedDiagnosticEvent>();
                foreach (var diagnosticEvent in Events.Where(candidate => candidate.RunId == started.RunId))
                {
                    if (diagnosticEvent.Outcome == DiagnosticOutcome.Failed)
                    {
                        outstandingFailures[diagnosticEvent.CorrelationId] = diagnosticEvent;
                    }
                    else if (diagnosticEvent.Outcome == DiagnosticOutcome.Recovered)
                    {
                        outstandingFailures.Remove(diagnosticEvent.CorrelationId);
                    }
                }

                unrecoveredFailure = Events.LastOrDefault(candidate =>
                    candidate.RunId == started.RunId &&
                    outstandingFailures.TryGetValue(candidate.CorrelationId, out var outstanding) &&
                    ReferenceEquals(candidate, outstanding));
            }

            return new(started.RunId, termination, unrecoveredFailure);
        }

        return PreviousRunRecovery.None;
    }

    private static void ValidateJournal(
        IReadOnlyList<SanitizedDiagnosticEvent> events,
        IReadOnlyList<DiagnosticRunBoundary> boundaries)
    {
        var started = new HashSet<CorrelationId>();
        var startedUtc = new Dictionary<CorrelationId, DateTimeOffset>();
        var terminatedUtc = new Dictionary<CorrelationId, DateTimeOffset>();
        var terminated = new HashSet<CorrelationId>();
        CorrelationId? openRun = null;
        DateTimeOffset? lastBoundaryUtc = null;
        foreach (var boundary in boundaries)
        {
            if (lastBoundaryUtc is { } last && boundary.OccurredUtc < last)
            {
                throw new ArgumentException("Run boundaries must be chronological.", nameof(boundaries));
            }

            lastBoundaryUtc = boundary.OccurredUtc;
            if (boundary.Kind == DiagnosticRunBoundaryKind.Started)
            {
                if (openRun is not null || !started.Add(boundary.RunId))
                {
                    throw new ArgumentException("Run starts must be unique and non-overlapping.", nameof(boundaries));
                }

                startedUtc.Add(boundary.RunId, boundary.OccurredUtc);
                openRun = boundary.RunId;
            }
            else if (openRun != boundary.RunId || !terminated.Add(boundary.RunId))
            {
                throw new ArgumentException("A run terminal marker must close its one open start.", nameof(boundaries));
            }
            else
            {
                terminatedUtc.Add(boundary.RunId, boundary.OccurredUtc);
                openRun = null;
            }
        }

        DateTimeOffset? lastEventUtc = null;
        foreach (var diagnosticEvent in events)
        {
            if (!startedUtc.TryGetValue(diagnosticEvent.RunId, out var runStartedUtc) ||
                diagnosticEvent.OccurredUtc < runStartedUtc ||
                (terminatedUtc.TryGetValue(diagnosticEvent.RunId, out var runTerminatedUtc) &&
                 diagnosticEvent.OccurredUtc > runTerminatedUtc))
            {
                throw new ArgumentException(
                    "Every recovery event must fall within its recorded run.",
                    nameof(events));
            }

            if (lastEventUtc is { } last && diagnosticEvent.OccurredUtc < last)
            {
                throw new ArgumentException("Recovery events must be chronological.", nameof(events));
            }

            lastEventUtc = diagnosticEvent.OccurredUtc;
        }
    }

    private static IReadOnlyList<DiagnosticRunBoundary> TrimBoundaries(
        IReadOnlyList<DiagnosticRunBoundary> boundaries)
    {
        var trimmed = boundaries.TakeLast(MaximumRunBoundaries).ToList();
        while (trimmed.Count > 0 && trimmed[0].Kind != DiagnosticRunBoundaryKind.Started)
        {
            trimmed.RemoveAt(0);
        }

        return trimmed;
    }

    private static IReadOnlyList<SanitizedDiagnosticEvent> KeepEventsForKnownRuns(
        IReadOnlyList<SanitizedDiagnosticEvent> events,
        IReadOnlyList<DiagnosticRunBoundary> boundaries)
    {
        var knownRuns = boundaries
            .Where(boundary => boundary.Kind == DiagnosticRunBoundaryKind.Started)
            .Select(boundary => boundary.RunId)
            .ToHashSet();
        return events.Where(diagnosticEvent => knownRuns.Contains(diagnosticEvent.RunId)).ToArray();
    }

    private static CorrelationId? FindOpenRun(IReadOnlyList<DiagnosticRunBoundary> boundaries)
    {
        for (var index = boundaries.Count - 1; index >= 0; index--)
        {
            var boundary = boundaries[index];
            if (boundary.Kind == DiagnosticRunBoundaryKind.Started)
            {
                return boundary.RunId;
            }

            if (boundary.Kind is DiagnosticRunBoundaryKind.CleanExit or DiagnosticRunBoundaryKind.UncleanExitObserved)
            {
                return null;
            }
        }

        return null;
    }
}
