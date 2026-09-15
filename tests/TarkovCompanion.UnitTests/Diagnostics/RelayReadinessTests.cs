using TarkovCompanion.GroupServer.Diagnostics;

namespace TarkovCompanion.UnitTests.Diagnostics;

public sealed class RelayReadinessTests
{
    [Fact]
    public void HealthyProbeCoversEveryOperationalReadinessDimension()
    {
        var result = RelayReadiness.Evaluate(CreateProbe());

        Assert.Equal(RelayReadinessStatus.Ready, result.Status);
        Assert.Equal(9, result.Checks.Count);
        Assert.All(result.Checks, check => Assert.True(check.IsReady));
    }

    [Fact]
    public void EachReadinessCheckDegradesOnlyItsOwnProbeDimension()
    {
        AssertCheck(CreateProbe(storageAvailable: false), RelayReadinessCheck.Storage);
        AssertCheck(CreateProbe(freeDiskMegabytes: RelayReadiness.MinimumFreeDiskMegabytes - 1), RelayReadinessCheck.Disk);
        AssertCheck(CreateProbe(clockDriftSeconds: RelayReadiness.MaximumClockDriftSeconds + 1), RelayReadinessCheck.Clock);
        AssertCheck(CreateProbe(buildKnown: false), RelayReadinessCheck.Build);
        AssertCheck(CreateProbe(updateAgeMinutes: RelayReadiness.MaximumUpdateAgeMinutes + 1), RelayReadinessCheck.UpdateAge);
        AssertCheck(CreateProbe(latencyMilliseconds: RelayReadiness.MaximumLatencyMilliseconds + 1), RelayReadinessCheck.Latency);
        AssertCheck(CreateProbe(consecutiveFailures: RelayReadiness.MaximumConsecutiveFailures), RelayReadinessCheck.Failures);
        AssertCheck(
            CreateProbe(rateLimitRejectionsInObservationWindow: RelayReadiness.MaximumRateLimitRejectionsInObservationWindow + 1),
            RelayReadinessCheck.RateLimits);
        AssertCheck(
            CreateProbe(rejectedInputCountInObservationWindow: RelayReadiness.MaximumRejectedInputsInObservationWindow + 1),
            RelayReadinessCheck.RejectedInput);
    }

    [Fact]
    public void SaturatedClockDriftDegradesInsteadOfOverflowing()
    {
        var result = RelayReadiness.Evaluate(CreateProbe(clockDriftSeconds: int.MinValue));

        Assert.Equal(RelayReadinessStatus.Degraded, result.Status);
        Assert.False(Check(result, RelayReadinessCheck.Clock));
    }

    [Fact]
    public void ConstructorNamesTheInvalidBoundedInput()
    {
        Assert.Equal("freeDiskMegabytes", Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateProbe(freeDiskMegabytes: -1)).ParamName);
        Assert.Equal("updateAgeMinutes", Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateProbe(updateAgeMinutes: -1)).ParamName);
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
    public void ReadinessSurfaceHasNoSensitiveFreeText()
    {
        var resultProperties = typeof(RelayReadinessResult).GetProperties();
        var probeProperties = typeof(RelayReadinessProbe).GetProperties();

        Assert.DoesNotContain(resultProperties.Concat(probeProperties), property => property.PropertyType == typeof(string));
        Assert.DoesNotContain(probeProperties, property =>
            property.Name.Contains("Path", StringComparison.Ordinal) ||
            property.Name.Contains("Room", StringComparison.Ordinal) ||
            property.Name.Contains("Name", StringComparison.Ordinal));
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

    private static RelayReadinessProbe CreateProbe(
        bool storageAvailable = true,
        int freeDiskMegabytes = 1_024,
        int clockDriftSeconds = 2,
        bool buildKnown = true,
        int updateAgeMinutes = 30,
        int latencyMilliseconds = 10,
        int consecutiveFailures = 0,
        int rateLimitRejectionsInObservationWindow = 0,
        int rejectedInputCountInObservationWindow = 0) =>
        new(
            storageAvailable,
            freeDiskMegabytes,
            clockDriftSeconds,
            buildKnown,
            updateAgeMinutes,
            latencyMilliseconds,
            consecutiveFailures,
            rateLimitRejectionsInObservationWindow,
            rejectedInputCountInObservationWindow);
}
