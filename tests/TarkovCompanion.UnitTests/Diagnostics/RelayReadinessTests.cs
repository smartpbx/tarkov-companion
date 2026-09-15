using TarkovCompanion.GroupServer.Diagnostics;

namespace TarkovCompanion.UnitTests.Diagnostics;

public sealed class RelayReadinessTests
{
    [Fact]
    public void HealthyProbeCoversEveryOperationalReadinessDimension()
    {
        var result = RelayReadiness.Evaluate(new(
            StorageAvailable: true,
            FreeDiskMegabytes: 1_024,
            ClockDriftSeconds: 2,
            BuildKnown: true,
            UpdateAgeMinutes: 30,
            LatencyMilliseconds: 10,
            ConsecutiveFailures: 0,
            RateLimitRejections: 0,
            RejectedInputCount: 0));

        Assert.Equal(RelayReadinessStatus.Ready, result.Status);
        Assert.Equal(9, result.Checks.Count);
        Assert.All(result.Checks, check => Assert.True(check.IsReady));
    }

    [Fact]
    public void StaleAndRejectedRelayIsDegradedWithoutPublishingSensitiveValues()
    {
        var result = RelayReadiness.Evaluate(new(
            StorageAvailable: false,
            FreeDiskMegabytes: 10,
            ClockDriftSeconds: 61,
            BuildKnown: false,
            UpdateAgeMinutes: 91,
            LatencyMilliseconds: 2_001,
            ConsecutiveFailures: 3,
            RateLimitRejections: 1,
            RejectedInputCount: 1));

        Assert.Equal(RelayReadinessStatus.Degraded, result.Status);
        Assert.All(result.Checks, check => Assert.False(check.IsReady));
        Assert.DoesNotContain(typeof(RelayReadinessProbe).GetProperties(), property =>
            property.Name.Contains("Path", StringComparison.Ordinal) ||
            property.Name.Contains("Room", StringComparison.Ordinal) ||
            property.Name.Contains("Name", StringComparison.Ordinal));
    }
}
