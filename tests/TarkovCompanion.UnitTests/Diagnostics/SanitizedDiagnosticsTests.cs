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
            ["Attempt", "CorrelationId", "DurationMilliseconds", "FailureKind", "Kind", "OccurredUtc", "Outcome"],
            properties);
        Assert.DoesNotContain(typeof(SanitizedDiagnosticEvent).GetProperties(), property =>
            property.PropertyType == typeof(string));
    }

    [Fact]
    public void EventUsesTheCanonicalRuntimeIdentifiersAndFailureVocabulary()
    {
        var correlationId = CorrelationId.New();
        var fault = new RuntimeFault(
            RuntimeFailureKind.Validation,
            new RuntimeFaultCode("fixture-invalid"),
            RuntimeRecoveryAction.CheckConfiguration,
            new DiagnosticReference("fixture:diagnostics"),
            UtcNow);

        var diagnosticEvent = RuntimeDiagnosticAdapter.FromRuntimeFault(
            DiagnosticEventKind.RecognitionStage,
            correlationId,
            fault,
            durationMilliseconds: 12,
            attempt: 1);

        Assert.Equal(correlationId, diagnosticEvent.CorrelationId);
        Assert.Equal(RuntimeFailureKind.Validation, diagnosticEvent.FailureKind);
        Assert.Equal(DiagnosticOutcome.Failed, diagnosticEvent.Outcome);
        Assert.Equal(UtcNow, diagnosticEvent.OccurredUtc);
    }

    [Fact]
    public void EventRejectsDefaultIdentifiersInvalidEnumsAndNonUtcTime()
    {
        Assert.Throws<ArgumentException>(() => new SanitizedDiagnosticEvent(
            DiagnosticEventKind.RecognitionStage,
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
    public void CrashRecoveryCopiesInputAndKeepsTheBoundAfterCallerMutation()
    {
        var supplied = new List<SanitizedDiagnosticEvent> { CreateEvent() };
        var recovery = new CrashRecoveryState(supplied);

        supplied.Clear();
        for (var index = 0; index < CrashRecoveryState.MaximumEvents + 1; index++)
        {
            supplied.Add(CreateEvent());
        }

        Assert.Single(recovery.Events);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CrashRecoveryState(
            Enumerable.Repeat(CreateEvent(), CrashRecoveryState.MaximumEvents + 1).ToArray()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CrashRecoveryState([null!]));
    }

    [Fact]
    public void RecoveryDoesNotReportAFailureAfterANewerRecovery()
    {
        var failed = CreateEvent(outcome: DiagnosticOutcome.Failed);
        var recovered = CreateEvent(outcome: DiagnosticOutcome.Recovered);

        Assert.Null(new CrashRecoveryState([failed, recovered]).PreviousFailure);
        Assert.Equal(failed, new CrashRecoveryState([recovered, failed]).PreviousFailure);
    }

    [Fact]
    public void ControlsUseSafeDefaultsForMissingOrInvalidEnvironmentValues()
    {
        var defaults = DiagnosticRuntimeControls.FromEnvironment(_ => null);
        var invalid = DiagnosticRuntimeControls.FromEnvironment(name => name switch
        {
            "TARKOV_COMPANION_DIAGNOSTIC_LOG_LEVEL" => "verbose",
            "TARKOV_COMPANION_INTERNAL_TELEMETRY" => "yes",
            _ => null,
        });
        var enabled = DiagnosticRuntimeControls.FromEnvironment(name => name switch
        {
            "TARKOV_COMPANION_DIAGNOSTIC_LOG_LEVEL" => "trace",
            "TARKOV_COMPANION_INTERNAL_TELEMETRY" => "enabled",
            _ => null,
        });

        Assert.Equal(DiagnosticLogVerbosity.Information, defaults.Verbosity);
        Assert.False(defaults.InternalTelemetryEnabled);
        Assert.Equal(DiagnosticLogVerbosity.Information, invalid.Verbosity);
        Assert.False(invalid.InternalTelemetryEnabled);
        Assert.Equal(DiagnosticLogVerbosity.Trace, enabled.Verbosity);
        Assert.True(enabled.InternalTelemetryEnabled);
    }

    [Fact]
    public void TokensRequireUtcBoundedExpiryAndSafeShape()
    {
        const string current = "abcdefghijklmnopqrstuvwxyz0123456789-current";
        const string previous = "abcdefghijklmnopqrstuvwxyz0123456789-old-old";
        var expires = UtcNow.AddHours(1);

        Assert.True(DiagnosticTokenSet.TryParse($"{current},{previous}@2026-09-15T13:00:00Z", UtcNow, out var tokens));
        var tokenSet = Assert.IsType<DiagnosticTokenSet>(tokens);
        Assert.True(tokenSet.IsValid(current, UtcNow));
        Assert.True(tokenSet.IsValid(previous, expires));
        Assert.False(tokenSet.IsValid(previous, expires.AddSeconds(1)));
        Assert.False(tokenSet.IsValid("short", UtcNow));
        Assert.False(tokenSet.IsValid(new string('a', 257), UtcNow));

        Assert.False(DiagnosticTokenSet.TryParse($"{current},{previous}", UtcNow, out _));
        Assert.False(DiagnosticTokenSet.TryParse($"{current},{previous}@2026-09-15", UtcNow, out _));
        Assert.False(DiagnosticTokenSet.TryParse($"{current},{previous}@2026-09-15T14:00:00+01:00", UtcNow, out _));
        Assert.False(DiagnosticTokenSet.TryParse($"{current},{previous}@2026-09-17T12:00:01Z", UtcNow, out _));
        Assert.False(DiagnosticTokenSet.TryParse($"{current},{current}@2026-09-15T13:00:00Z", UtcNow, out _));
        Assert.False(DiagnosticTokenSet.TryParse($"{current},{previous}@2026-09-15T13:00:00Z,abcdefghijklmnopqrstuvwxyz0123456789-third@2026-09-15T13:00:00Z,abcdefghijklmnopqrstuvwxyz0123456789-fourth@2026-09-15T13:00:00Z", UtcNow, out _));
        Assert.False(DiagnosticTokenSet.TryParse($"{current},{previous}@2026-09-15T13:00:00Z", UtcNow.ToOffset(TimeSpan.FromHours(1)), out _));
    }

    private static SanitizedDiagnosticEvent CreateEvent(
        DiagnosticEventKind kind = DiagnosticEventKind.RecognitionStage,
        CorrelationId correlationId = default,
        DateTimeOffset? occurredUtc = null,
        DiagnosticOutcome outcome = DiagnosticOutcome.Failed,
        RuntimeFailureKind failureKind = RuntimeFailureKind.Unexpected,
        int? durationMilliseconds = null,
        int? attempt = null) =>
        new(
            kind,
            correlationId.IsDefined ? correlationId : CorrelationId.New(),
            occurredUtc ?? UtcNow,
            outcome,
            failureKind,
            durationMilliseconds,
            attempt);
}
