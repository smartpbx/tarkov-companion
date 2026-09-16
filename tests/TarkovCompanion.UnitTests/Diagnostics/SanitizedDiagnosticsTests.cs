using System.Reflection;
using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.Infrastructure.Diagnostics;

namespace TarkovCompanion.UnitTests.Diagnostics;

public sealed class SanitizedDiagnosticsTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StructuredEventHasAnExactClosedNonStringSurface()
    {
        var properties = typeof(SanitizedDiagnosticEvent).GetProperties()
            .Select(property => property.Name)
            .Order()
            .ToArray();

        Assert.Equal(
            ["Attempt", "CorrelationId", "DurationMilliseconds", "FailureKind", "Kind", "OccurredUtc", "Outcome", "RunId"],
            properties);
        Assert.DoesNotContain(typeof(SanitizedDiagnosticEvent).GetProperties(), property =>
            property.PropertyType == typeof(string));
    }

    [Fact]
    public void EventUsesTheCanonicalRuntimeIdentifiersAndFailureVocabulary()
    {
        var runId = CorrelationId.New();
        var correlationId = CorrelationId.New();
        var fault = new RuntimeFault(
            RuntimeFailureKind.Validation,
            new RuntimeFaultCode("fixture-invalid"),
            RuntimeRecoveryAction.CheckConfiguration,
            new DiagnosticReference("fixture:diagnostics"),
            UtcNow);

        var diagnosticEvent = RuntimeDiagnosticAdapter.FromRuntimeFault(
            DiagnosticEventKind.RecognitionStage,
            runId,
            correlationId,
            fault,
            durationMilliseconds: 12,
            attempt: 1);

        Assert.Equal(runId, diagnosticEvent.RunId);
        Assert.Equal(correlationId, diagnosticEvent.CorrelationId);
        Assert.Equal(RuntimeFailureKind.Validation, diagnosticEvent.FailureKind);
        Assert.Equal(DiagnosticOutcome.Failed, diagnosticEvent.Outcome);
        Assert.Equal(UtcNow, diagnosticEvent.OccurredUtc);
    }

    [Fact]
    public void FailureClassificationIsOptionalAndStrictlyMatchesTheOutcome()
    {
        var succeeded = CreateEvent(
            outcome: DiagnosticOutcome.Succeeded,
            failureKind: null);

        Assert.Null(succeeded.FailureKind);
        Assert.Throws<ArgumentException>(() => CreateEvent(
            outcome: DiagnosticOutcome.Failed,
            failureKind: null));
        Assert.Throws<ArgumentException>(() => CreateEvent(
            outcome: DiagnosticOutcome.Rejected,
            failureKind: RuntimeFailureKind.Validation));
    }

    [Fact]
    public void EventRejectsDefaultIdentifiersInvalidEnumsAndNonUtcTime()
    {
        Assert.Throws<ArgumentException>(() => new SanitizedDiagnosticEvent(
            DiagnosticEventKind.RecognitionStage,
            default,
            CorrelationId.New(),
            UtcNow,
            DiagnosticOutcome.Failed,
            RuntimeFailureKind.Unexpected));
        Assert.Throws<ArgumentException>(() => new SanitizedDiagnosticEvent(
            DiagnosticEventKind.RecognitionStage,
            CorrelationId.New(),
            default,
            UtcNow,
            DiagnosticOutcome.Failed,
            RuntimeFailureKind.Unexpected));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateEvent(kind: (DiagnosticEventKind)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateEvent(outcome: (DiagnosticOutcome)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateEvent(failureKind: (RuntimeFailureKind)999));
        Assert.Throws<ArgumentException>(() => CreateEvent(occurredUtc: UtcNow.ToOffset(TimeSpan.FromHours(1))));
    }

    [Fact]
    public void EventCapsLongDurationButRejectsNegativeDurationAndInvalidAttempt()
    {
        Assert.Equal(300_000, CreateEvent(durationMilliseconds: 300_001).DurationMilliseconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateEvent(durationMilliseconds: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateEvent(attempt: 101));
    }

    [Fact]
    public void CrashRecoveryCopiesInputsAndKeepsImmutableBounds()
    {
        var runId = CorrelationId.New();
        var suppliedEvents = new List<SanitizedDiagnosticEvent> { CreateEvent(runId: runId) };
        var suppliedBoundaries = new List<DiagnosticRunBoundary>
        {
            new(DiagnosticRunBoundaryKind.Started, runId, UtcNow),
        };
        var recovery = new CrashRecoveryState(suppliedEvents, suppliedBoundaries);

        suppliedEvents.Clear();
        suppliedBoundaries.Clear();

        Assert.Single(recovery.Events);
        Assert.Single(recovery.RunBoundaries);
        Assert.True(Assert.IsAssignableFrom<IList<SanitizedDiagnosticEvent>>(recovery.Events).IsReadOnly);
        Assert.True(Assert.IsAssignableFrom<IList<DiagnosticRunBoundary>>(recovery.RunBoundaries).IsReadOnly);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CrashRecoveryState(
            Enumerable.Repeat(CreateEvent(runId: runId), CrashRecoveryState.MaximumEvents + 1).ToArray(),
            [new(DiagnosticRunBoundaryKind.Started, runId, UtcNow)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CrashRecoveryState(
            [null!],
            [new(DiagnosticRunBoundaryKind.Started, runId, UtcNow)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CrashRecoveryState(
            [],
            Enumerable.Repeat(
                new DiagnosticRunBoundary(DiagnosticRunBoundaryKind.Started, CorrelationId.New(), UtcNow),
                CrashRecoveryState.MaximumRunBoundaries + 1).ToArray()));
    }

    [Fact]
    public void ANewRunRecordsAndReportsThePriorUncleanRunByCorrelation()
    {
        var priorRun = CorrelationId.New();
        var currentRun = CorrelationId.New();
        var failure = CreateEvent(runId: priorRun);
        var recovery = CrashRecoveryState.Empty
            .BeginRun(priorRun, UtcNow)
            .Add(failure)
            .BeginRun(currentRun, UtcNow.AddMinutes(1));

        var previous = recovery.InspectPreviousRun(currentRun);

        Assert.Equal(priorRun, previous.RunId);
        Assert.Equal(PreviousRunTermination.Unclean, previous.Termination);
        Assert.Equal(failure, previous.LastUnrecoveredFailure);
        Assert.Equal(
            [
                DiagnosticRunBoundaryKind.Started,
                DiagnosticRunBoundaryKind.UncleanExitObserved,
                DiagnosticRunBoundaryKind.Started,
            ],
            recovery.RunBoundaries.Select(boundary => boundary.Kind));
    }

    [Fact]
    public void AnUncleanRunDoesNotInventAFailureThatWasNeverRecorded()
    {
        var priorRun = CorrelationId.New();
        var currentRun = CorrelationId.New();
        var recovery = CrashRecoveryState.Empty
            .BeginRun(priorRun, UtcNow)
            .BeginRun(currentRun, UtcNow.AddSeconds(1));

        var previous = recovery.InspectPreviousRun(currentRun);

        Assert.Equal(PreviousRunTermination.Unclean, previous.Termination);
        Assert.Null(previous.LastUnrecoveredFailure);
    }

    [Fact]
    public void CleanAndRecoveredRunsNeverClaimAnUnrecoveredFailure()
    {
        var cleanRun = CorrelationId.New();
        var currentRun = CorrelationId.New();
        var cleanState = CrashRecoveryState.Empty
            .BeginRun(cleanRun, UtcNow)
            .Add(CreateEvent(runId: cleanRun))
            .CompleteRun(cleanRun, UtcNow.AddSeconds(2))
            .BeginRun(currentRun, UtcNow.AddSeconds(3));

        var clean = cleanState.InspectPreviousRun(currentRun);

        Assert.Equal(PreviousRunTermination.Clean, clean.Termination);
        Assert.Null(clean.LastUnrecoveredFailure);

        var failedRun = CorrelationId.New();
        var nextRun = CorrelationId.New();
        var recoveredOperation = CorrelationId.New();
        var recoveredState = CrashRecoveryState.Empty
            .BeginRun(failedRun, UtcNow)
            .Add(CreateEvent(runId: failedRun, correlationId: recoveredOperation))
            .Add(CreateEvent(
                runId: failedRun,
                correlationId: recoveredOperation,
                occurredUtc: UtcNow.AddSeconds(1),
                outcome: DiagnosticOutcome.Recovered,
                failureKind: null))
            .BeginRun(nextRun, UtcNow.AddSeconds(2));

        var recovered = recoveredState.InspectPreviousRun(nextRun);
        Assert.Equal(PreviousRunTermination.Unclean, recovered.Termination);
        Assert.Null(recovered.LastUnrecoveredFailure);
    }

    [Fact]
    public void RecoveryOnlyClearsTheFailureWithTheSameCorrelation()
    {
        var priorRun = CorrelationId.New();
        var currentRun = CorrelationId.New();
        var firstOperation = CorrelationId.New();
        var secondOperation = CorrelationId.New();
        var firstFailure = CreateEvent(runId: priorRun, correlationId: firstOperation);
        var secondFailure = CreateEvent(
            runId: priorRun,
            correlationId: secondOperation,
            occurredUtc: UtcNow.AddSeconds(1));
        var recovery = CrashRecoveryState.Empty
            .BeginRun(priorRun, UtcNow)
            .Add(firstFailure)
            .Add(secondFailure)
            .Add(CreateEvent(
                runId: priorRun,
                correlationId: firstOperation,
                occurredUtc: UtcNow.AddSeconds(2),
                outcome: DiagnosticOutcome.Recovered,
                failureKind: null))
            .BeginRun(currentRun, UtcNow.AddSeconds(3));

        Assert.Equal(secondFailure, recovery.InspectPreviousRun(currentRun).LastUnrecoveredFailure);
    }

    [Fact]
    public void RecoveryRefusesEventsFromAnotherRunAndReportsNoInventedRun()
    {
        var currentRun = CorrelationId.New();
        var state = CrashRecoveryState.Empty.BeginRun(currentRun, UtcNow);

        Assert.Throws<InvalidOperationException>(() => state.Add(CreateEvent(runId: CorrelationId.New())));
        Assert.Throws<InvalidOperationException>(() => state.InspectPreviousRun(CorrelationId.New()));
        Assert.Equal(
            PreviousRunTermination.None,
            state.InspectPreviousRun(currentRun).Termination);
    }

    [Fact]
    public void RecoveryJournalRejectsEventsOutsideTheirRunOrOutOfOrder()
    {
        var runId = CorrelationId.New();
        var start = new DiagnosticRunBoundary(DiagnosticRunBoundaryKind.Started, runId, UtcNow);
        var stop = new DiagnosticRunBoundary(
            DiagnosticRunBoundaryKind.CleanExit,
            runId,
            UtcNow.AddSeconds(2));

        Assert.Throws<ArgumentException>(() => new CrashRecoveryState(
            [CreateEvent(runId: runId, occurredUtc: UtcNow.AddSeconds(-1))],
            [start, stop]));
        Assert.Throws<ArgumentException>(() => new CrashRecoveryState(
            [CreateEvent(runId: runId, occurredUtc: UtcNow.AddSeconds(3))],
            [start, stop]));

        var open = CrashRecoveryState.Empty
            .BeginRun(runId, UtcNow)
            .Add(CreateEvent(runId: runId, occurredUtc: UtcNow.AddSeconds(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            open.Add(CreateEvent(runId: runId, occurredUtc: UtcNow.AddSeconds(1))));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            open.CompleteRun(runId, UtcNow.AddSeconds(1)));
    }

    [Fact]
    public void RunBoundariesRejectUndefinedValuesReuseAndNonUtcTime()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagnosticRunBoundary(
            (DiagnosticRunBoundaryKind)999,
            CorrelationId.New(),
            UtcNow));
        Assert.Throws<ArgumentException>(() => new DiagnosticRunBoundary(
            DiagnosticRunBoundaryKind.Started,
            default,
            UtcNow));
        Assert.Throws<ArgumentException>(() => new DiagnosticRunBoundary(
            DiagnosticRunBoundaryKind.Started,
            CorrelationId.New(),
            UtcNow.ToOffset(TimeSpan.FromHours(1))));

        var runId = CorrelationId.New();
        var state = CrashRecoveryState.Empty.BeginRun(runId, UtcNow);
        Assert.Throws<ArgumentException>(() => state.BeginRun(runId, UtcNow.AddSeconds(1)));
    }

    [Fact]
    public void ControlsUseSafeDefaultsAndTreatEnvironmentTelemetryAsARequest()
    {
        var defaults = DiagnosticRuntimeControls.FromEnvironment(_ => null);
        var invalid = DiagnosticRuntimeControls.FromEnvironment(name => name switch
        {
            "TARKOV_COMPANION_DIAGNOSTIC_LOG_LEVEL" => "verbose",
            "TARKOV_COMPANION_INTERNAL_TELEMETRY" => "yes",
            _ => null,
        });
        var requested = DiagnosticRuntimeControls.FromEnvironment(name => name switch
        {
            "TARKOV_COMPANION_DIAGNOSTIC_LOG_LEVEL" => "trace",
            "TARKOV_COMPANION_INTERNAL_TELEMETRY" => "enabled",
            _ => null,
        });

        Assert.Equal(DiagnosticLogVerbosity.Information, defaults.Verbosity);
        Assert.False(defaults.InternalTelemetryRequested);
        Assert.Equal(DiagnosticLogVerbosity.Information, invalid.Verbosity);
        Assert.False(invalid.InternalTelemetryRequested);
        Assert.Equal(DiagnosticLogVerbosity.Trace, requested.Verbosity);
        Assert.True(requested.InternalTelemetryRequested);
    }

    [Fact]
    public void TokensRequireUtcBoundedExpiryAndSafeShape()
    {
        const string current = "abcdefghijklmnopqrstuvwxyz0123456789-current";
        const string previous = "abcdefghijklmnopqrstuvwxyz0123456789-old-old";
        var expires = UtcNow.AddHours(1);

        Assert.True(DiagnosticTokenSet.TryParse(
            $"{current},{previous}@2026-09-15T13:00:00Z",
            UtcNow,
            out var tokens));
        var tokenSet = Assert.IsType<DiagnosticTokenSet>(tokens);
        Assert.True(tokenSet.IsValid(current, UtcNow));
        Assert.True(tokenSet.IsValid(previous, expires));
        Assert.False(tokenSet.IsValid(previous, expires.AddSeconds(1)));
        Assert.False(tokenSet.IsValid("short", UtcNow));
        Assert.False(tokenSet.IsValid(new string('a', 257), UtcNow));

        Assert.False(DiagnosticTokenSet.TryParse($"{current},{previous}", UtcNow, out _));
        Assert.False(DiagnosticTokenSet.TryParse($"{current},{previous}@2026-09-15", UtcNow, out _));
        Assert.False(DiagnosticTokenSet.TryParse($"{current},{previous}@2026-09-15T14:00:00+01:00", UtcNow, out _));
        Assert.False(DiagnosticTokenSet.TryParse($"{current},{previous}@2026-09-16T12:00:01Z", UtcNow, out _));
        Assert.False(DiagnosticTokenSet.TryParse($"{current},{current}@2026-09-15T13:00:00Z", UtcNow, out _));
        Assert.False(DiagnosticTokenSet.TryParse(
            $"{current},{previous}@2026-09-15T13:00:00Z,abcdefghijklmnopqrstuvwxyz0123456789-third@2026-09-15T13:00:00Z,abcdefghijklmnopqrstuvwxyz0123456789-fourth@2026-09-15T13:00:00Z",
            UtcNow,
            out _));
        Assert.False(DiagnosticTokenSet.TryParse($"{current},,{previous}@2026-09-15T13:00:00Z", UtcNow, out _));
        Assert.False(DiagnosticTokenSet.TryParse(
            $"{current},{previous}@2026-09-15T13:00:00Z",
            UtcNow.ToOffset(TimeSpan.FromHours(1)),
            out _));
        Assert.False(DiagnosticTokenSet.TryParse(new string('a', 10_000), UtcNow, out _));
    }

    [Fact]
    public void TokenSetRetainsNoRawCredentialAndParsingNearMaximumTimeDoesNotOverflow()
    {
        const string current = "abcdefghijklmnopqrstuvwxyz0123456789-current";
        const string previous = "abcdefghijklmnopqrstuvwxyz0123456789-old-old";
        var nearMaximum = new DateTimeOffset(9999, 12, 31, 23, 59, 58, TimeSpan.Zero);

        Assert.True(DiagnosticTokenSet.TryParse(
            $"{current},{previous}@9999-12-31T23:59:59Z",
            nearMaximum,
            out var tokens));
        var tokenSet = Assert.IsType<DiagnosticTokenSet>(tokens);
        Assert.Equal(nameof(DiagnosticTokenSet), tokenSet.ToString());
        Assert.DoesNotContain(current, tokenSet.ToString(), StringComparison.Ordinal);

        var nestedTypes = typeof(DiagnosticTokenSet).GetNestedTypes(BindingFlags.NonPublic);
        var retainedFields = typeof(DiagnosticTokenSet)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Concat(nestedTypes.SelectMany(type =>
                type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)));
        Assert.DoesNotContain(retainedFields, field => field.FieldType == typeof(string));
    }

    private static SanitizedDiagnosticEvent CreateEvent(
        DiagnosticEventKind kind = DiagnosticEventKind.RecognitionStage,
        CorrelationId runId = default,
        CorrelationId correlationId = default,
        DateTimeOffset? occurredUtc = null,
        DiagnosticOutcome outcome = DiagnosticOutcome.Failed,
        RuntimeFailureKind? failureKind = RuntimeFailureKind.Unexpected,
        int? durationMilliseconds = null,
        int? attempt = null) =>
        new(
            kind,
            runId.IsDefined ? runId : CorrelationId.New(),
            correlationId.IsDefined ? correlationId : CorrelationId.New(),
            occurredUtc ?? UtcNow,
            outcome,
            failureKind,
            durationMilliseconds,
            attempt);
}
