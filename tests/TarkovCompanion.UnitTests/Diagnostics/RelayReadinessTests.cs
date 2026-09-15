using TarkovCompanion.GroupServer.Diagnostics;

namespace TarkovCompanion.UnitTests.Diagnostics;

public sealed class RelayReadinessTests
{
    private static readonly DateTimeOffset ObservedUtc =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HealthyProbeCoversEveryOperationalReadinessDimension()
    {
        var result = RelayReadiness.Evaluate(CreateProbe());

        Assert.Equal(RelayReadinessStatus.Ready, result.Status);
        Assert.Equal(ObservedUtc, result.ObservedUtc);
        Assert.Equal(ObservedUtc.AddMinutes(-RelayReadiness.ObservationWindowMinutes), result.ObservationWindowStartedUtc);
        Assert.Equal(7, result.Checks.Count);
        Assert.All(result.Checks, check => Assert.True(check.IsReady));
        Assert.Equal(2, result.Signals.Count);
        Assert.All(result.Signals, signal => Assert.False(signal.IsElevated));
    }

    [Fact]
    public void EachReadinessCheckDegradesOnlyItsOwnProbeDimension()
    {
        AssertCheck(CreateProbe(storageAvailable: false), RelayReadinessCheck.Storage);
        AssertCheck(CreateProbe(freeDiskMegabytes: RelayReadiness.MinimumFreeDiskMegabytes - 1), RelayReadinessCheck.Disk);
        AssertCheck(CreateProbe(clockDriftSeconds: RelayReadiness.MaximumClockDriftSeconds + 1), RelayReadinessCheck.Clock);
        AssertCheck(CreateProbe(buildKnown: false), RelayReadinessCheck.Build);
        AssertCheck(
            CreateProbe(lastSuccessfulUpdaterCheckAgeMinutes: RelayReadiness.MaximumUpdaterCheckAgeMinutes + 1),
            RelayReadinessCheck.UpdaterCheck);
        AssertCheck(CreateProbe(latencyMilliseconds: RelayReadiness.MaximumLatencyMilliseconds + 1), RelayReadinessCheck.Latency);
        AssertCheck(CreateProbe(consecutiveFailures: RelayReadiness.MaximumConsecutiveFailures), RelayReadinessCheck.Failures);
    }

    [Fact]
    public void RejectionAndRateLimitPressureAreSignalsRatherThanReadinessFailures()
    {
        var result = RelayReadiness.Evaluate(CreateProbe(
            rateLimitRejectionsInObservationWindow:
                RelayReadiness.MaximumRateLimitRejectionsInObservationWindow + 1,
            rejectedInputCountInObservationWindow:
                RelayReadiness.MaximumRejectedInputsInObservationWindow + 1));

        Assert.Equal(RelayReadinessStatus.Ready, result.Status);
        Assert.All(result.Checks, check => Assert.True(check.IsReady));
        Assert.True(Signal(result, RelayOperationalSignal.RateLimitPressure));
        Assert.True(Signal(result, RelayOperationalSignal.RejectedInputAbuse));
    }

    [Fact]
    public void ReadinessAndSignalThresholdsAreInclusive()
    {
        var result = RelayReadiness.Evaluate(CreateProbe(
            freeDiskMegabytes: RelayReadiness.MinimumFreeDiskMegabytes,
            clockDriftSeconds: -RelayReadiness.MaximumClockDriftSeconds,
            lastSuccessfulUpdaterCheckAgeMinutes: RelayReadiness.MaximumUpdaterCheckAgeMinutes,
            latencyMilliseconds: RelayReadiness.MaximumLatencyMilliseconds,
            consecutiveFailures: RelayReadiness.MaximumConsecutiveFailures - 1,
            rateLimitRejectionsInObservationWindow:
                RelayReadiness.MaximumRateLimitRejectionsInObservationWindow,
            rejectedInputCountInObservationWindow:
                RelayReadiness.MaximumRejectedInputsInObservationWindow));

        Assert.Equal(RelayReadinessStatus.Ready, result.Status);
        Assert.All(result.Checks, check => Assert.True(check.IsReady));
        Assert.All(result.Signals, signal => Assert.False(signal.IsElevated));
    }

    [Fact]
    public void SaturatedClockDriftDegradesInsteadOfOverflowing()
    {
        var result = RelayReadiness.Evaluate(CreateProbe(clockDriftSeconds: int.MinValue));

        Assert.Equal(RelayReadinessStatus.Degraded, result.Status);
        Assert.False(Check(result, RelayReadinessCheck.Clock));
    }

    [Fact]
    public void ProbeRequiresExactUtcWindowAndCompleteProvenance()
    {
        Assert.Equal("observedUtc", Assert.Throws<ArgumentException>(() =>
            CreateProbe(observedUtc: ObservedUtc.ToOffset(TimeSpan.FromHours(1)))).ParamName);
        Assert.Equal("observationWindowStartedUtc", Assert.Throws<ArgumentException>(() =>
            CreateProbe(observationWindowStartedUtc: ObservedUtc.AddMinutes(-4))).ParamName);
        Assert.Equal("observationWindowStartedUtc", Assert.Throws<ArgumentException>(() =>
            CreateProbe(observationWindowStartedUtc:
                ObservedUtc.AddMinutes(-RelayReadiness.ObservationWindowMinutes).ToOffset(TimeSpan.FromHours(1)))).ParamName);
        Assert.Equal("provenance", Assert.Throws<ArgumentException>(() =>
            CreateProbe(provenance: RelayReadinessProbeProvenance.RelayOwnedOperationalProbes)).ParamName);
    }

    [Fact]
    public void ConstructorNamesEveryInvalidBoundedInput()
    {
        Assert.Equal("freeDiskMegabytes", Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateProbe(freeDiskMegabytes: -1)).ParamName);
        Assert.Equal("lastSuccessfulUpdaterCheckAgeMinutes", Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateProbe(lastSuccessfulUpdaterCheckAgeMinutes: -1)).ParamName);
        Assert.Equal("latencyMilliseconds", Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateProbe(latencyMilliseconds: -1)).ParamName);
        Assert.Equal("consecutiveFailures", Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateProbe(consecutiveFailures: -1)).ParamName);
        Assert.Equal("rateLimitRejectionsInObservationWindow", Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateProbe(rateLimitRejectionsInObservationWindow: -1)).ParamName);
        Assert.Equal("rejectedInputCountInObservationWindow", Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateProbe(rejectedInputCountInObservationWindow: -1)).ParamName);
    }

    [Fact]
    public void ResultCollectionsAreImmutableCopies()
    {
        var result = RelayReadiness.Evaluate(CreateProbe());
        var checks = Assert.IsAssignableFrom<IList<RelayReadinessCheckResult>>(result.Checks);
        var signals = Assert.IsAssignableFrom<IList<RelayOperationalSignalResult>>(result.Signals);

        Assert.True(checks.IsReadOnly);
        Assert.True(signals.IsReadOnly);
        Assert.Throws<NotSupportedException>(() =>
            checks[0] = new(RelayReadinessCheck.Storage, false));
        Assert.Throws<NotSupportedException>(() =>
            signals[0] = new(RelayOperationalSignal.RateLimitPressure, true));
    }

    [Fact]
    public void ReadinessSurfaceHasAnExactNonStringShape()
    {
        Assert.Equal(
            [
                "BuildKnown",
                "ClockDriftSeconds",
                "ConsecutiveFailures",
                "FreeDiskMegabytes",
                "LastSuccessfulUpdaterCheckAgeMinutes",
                "LatencyMilliseconds",
                "ObservationWindowStartedUtc",
                "ObservedUtc",
                "Provenance",
                "RateLimitRejectionsInObservationWindow",
                "RejectedInputCountInObservationWindow",
                "StorageAvailable",
            ],
            typeof(RelayReadinessProbe).GetProperties().Select(property => property.Name).Order());
        Assert.Equal(
            ["Checks", "ObservationWindowStartedUtc", "ObservedUtc", "Signals", "Status"],
            typeof(RelayReadinessResult).GetProperties().Select(property => property.Name).Order());
        Assert.DoesNotContain(
            typeof(RelayReadinessProbe).GetProperties()
                .Concat(typeof(RelayReadinessResult).GetProperties())
                .Concat(typeof(RelayReadinessCheckResult).GetProperties())
                .Concat(typeof(RelayOperationalSignalResult).GetProperties()),
            property => property.PropertyType == typeof(string));
    }

    private static void AssertCheck(RelayReadinessProbe probe, RelayReadinessCheck expectedFailedCheck)
    {
        var result = RelayReadiness.Evaluate(probe);

        Assert.Equal(RelayReadinessStatus.Degraded, result.Status);
        Assert.False(Check(result, expectedFailedCheck));
        Assert.All(
            result.Checks.Where(check => check.Check != expectedFailedCheck),
            check => Assert.True(check.IsReady));
    }

    private static bool Check(RelayReadinessResult result, RelayReadinessCheck check) =>
        Assert.Single(result.Checks, candidate => candidate.Check == check).IsReady;

    private static bool Signal(RelayReadinessResult result, RelayOperationalSignal signal) =>
        Assert.Single(result.Signals, candidate => candidate.Signal == signal).IsElevated;

    private static RelayReadinessProbe CreateProbe(
        DateTimeOffset? observedUtc = null,
        DateTimeOffset? observationWindowStartedUtc = null,
        RelayReadinessProbeProvenance provenance = RelayReadinessProbeProvenance.Complete,
        bool storageAvailable = true,
        int freeDiskMegabytes = 1_024,
        int clockDriftSeconds = 2,
        bool buildKnown = true,
        int lastSuccessfulUpdaterCheckAgeMinutes = 30,
        int latencyMilliseconds = 10,
        int consecutiveFailures = 0,
        int rateLimitRejectionsInObservationWindow = 0,
        int rejectedInputCountInObservationWindow = 0)
    {
        var observed = observedUtc ?? ObservedUtc;
        return new(
            observed,
            observationWindowStartedUtc ?? observed.AddMinutes(-RelayReadiness.ObservationWindowMinutes),
            provenance,
            storageAvailable,
            freeDiskMegabytes,
            clockDriftSeconds,
            buildKnown,
            lastSuccessfulUpdaterCheckAgeMinutes,
            latencyMilliseconds,
            consecutiveFailures,
            rateLimitRejectionsInObservationWindow,
            rejectedInputCountInObservationWindow);
    }
}
