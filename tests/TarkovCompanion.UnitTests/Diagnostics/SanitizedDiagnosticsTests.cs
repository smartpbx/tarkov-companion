using TarkovCompanion.Infrastructure.Diagnostics;

namespace TarkovCompanion.UnitTests.Diagnostics;

public sealed class SanitizedDiagnosticsTests
{
    [Fact]
    public void StructuredEventHasNoFreeTextOrSensitiveFieldSurface()
    {
        var diagnosticEvent = new SanitizedDiagnosticEvent(
            DiagnosticEventKind.RecognitionStage,
            DiagnosticCorrelationId.Create(),
            DateTimeOffset.UtcNow,
            DiagnosticOutcome.Failed,
            DiagnosticFailureCode.InvalidInput,
            DurationMilliseconds: 42,
            Attempt: 1);

        var properties = typeof(SanitizedDiagnosticEvent).GetProperties().Select(property => property.Name);

        Assert.DoesNotContain("Message", properties);
        Assert.DoesNotContain("Path", properties);
        Assert.DoesNotContain("Name", properties);
        Assert.DoesNotContain("Coordinates", properties);
        Assert.DoesNotContain("ReportBody", properties);
        Assert.Equal(DiagnosticEventKind.RecognitionStage, diagnosticEvent.Kind);
    }

    [Fact]
    public void CorrelationIsRandomAndNonIdentifying()
    {
        var first = DiagnosticCorrelationId.Create();
        var second = DiagnosticCorrelationId.Create();

        Assert.NotEqual(first, second);
        Assert.Matches("^[0-9a-f]{32}$", first.Value);
        Assert.True(DiagnosticCorrelationId.TryParse(first.Value.ToUpperInvariant(), out var parsed));
        Assert.Equal(first, parsed);
    }

    [Fact]
    public void CrashRecoveryIsBoundedAndKeepsOnlySanitizedEvents()
    {
        var recovery = CrashRecoveryState.Empty;
        for (var index = 0; index < CrashRecoveryState.MaximumEvents + 4; index++)
        {
            recovery = recovery.Add(new(
                DiagnosticEventKind.Failure,
                DiagnosticCorrelationId.Create(),
                DateTimeOffset.UtcNow,
                DiagnosticOutcome.Failed,
                DiagnosticFailureCode.Unexpected));
        }

        Assert.Equal(CrashRecoveryState.MaximumEvents, recovery.Events.Count);
        Assert.NotNull(recovery.PreviousFailure);
    }

    [Fact]
    public void DefaultControlsAreLocalOnlyAndTraceNeedsAnExplicitRuntimeControl()
    {
        var defaults = DiagnosticRuntimeControls.FromEnvironment(_ => null);
        var enabled = DiagnosticRuntimeControls.FromEnvironment(name => name switch
        {
            "TARKOV_COMPANION_DIAGNOSTIC_LOG_LEVEL" => "trace",
            "TARKOV_COMPANION_INTERNAL_TELEMETRY" => "enabled",
            _ => null,
        });

        Assert.False(defaults.InternalTelemetryEnabled);
        Assert.Equal(DiagnosticLogVerbosity.Information, defaults.Verbosity);
        Assert.True(enabled.InternalTelemetryEnabled);
        Assert.Equal(DiagnosticLogVerbosity.Trace, enabled.Verbosity);
    }

    [Fact]
    public void RotatedTokenIsAcceptedOnlyUntilItsExplicitExpiry()
    {
        const string current = "abcdefghijklmnopqrstuvwxyz0123456789-current";
        const string previous = "abcdefghijklmnopqrstuvwxyz0123456789-old-old";
        Assert.True(DiagnosticTokenSet.TryParse(
            $"{current},{previous}@2026-09-16T00:00:00Z",
            out var tokens));

        var tokenSet = Assert.IsType<DiagnosticTokenSet>(tokens);
        Assert.True(tokenSet.IsValid(current, new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero)));
        Assert.True(tokenSet.IsValid(previous, new(2026, 9, 16, 0, 0, 0, TimeSpan.Zero)));
        Assert.False(tokenSet.IsValid(previous, new(2026, 9, 16, 0, 0, 1, TimeSpan.Zero)));
        Assert.False(tokenSet.IsValid("wrong", new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void APreviousTokenWithoutExpiryIsRefused()
    {
        const string first = "abcdefghijklmnopqrstuvwxyz0123456789-first";
        const string second = "abcdefghijklmnopqrstuvwxyz0123456789-second";

        Assert.False(DiagnosticTokenSet.TryParse($"{first},{second}", out _));
    }
}
