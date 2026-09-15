namespace TarkovCompanion.GroupServer.Diagnostics;

/// <summary>
/// Builds an admin-only readiness result from bounded probes. It deliberately has no paths,
/// room identifiers, report bodies, group keys, names, or positions in its model.
/// </summary>
/// <remarks>
/// The public health route remains a minimal liveness response. Program composition publishes
/// this richer result only behind relay administration; wiring that route is owned by the
/// integration owner because <c>Program.cs</c> is shared composition.
/// </remarks>
public static class RelayReadiness
{
    public const int MinimumFreeDiskMegabytes = 256;
    public const int MaximumClockDriftSeconds = 60;
    public const int MaximumUpdateAgeMinutes = 90;
    public const int MaximumLatencyMilliseconds = 2_000;
    public const int MaximumConsecutiveFailures = 3;

    public static RelayReadinessResult Evaluate(RelayReadinessProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var checks = new[]
        {
            Check(RelayReadinessCheck.Storage, probe.StorageAvailable),
            Check(RelayReadinessCheck.Disk, probe.FreeDiskMegabytes >= MinimumFreeDiskMegabytes),
            Check(RelayReadinessCheck.Clock, Math.Abs(probe.ClockDriftSeconds) <= MaximumClockDriftSeconds),
            Check(RelayReadinessCheck.Build, probe.BuildKnown),
            Check(RelayReadinessCheck.UpdateAge, probe.UpdateAgeMinutes <= MaximumUpdateAgeMinutes),
            Check(RelayReadinessCheck.Latency, probe.LatencyMilliseconds <= MaximumLatencyMilliseconds),
            Check(RelayReadinessCheck.Failures, probe.ConsecutiveFailures < MaximumConsecutiveFailures),
            Check(RelayReadinessCheck.RateLimits, probe.RateLimitRejections == 0),
            Check(RelayReadinessCheck.RejectedInput, probe.RejectedInputCount == 0),
        };
        return new(checks.All(check => check.IsReady) ? RelayReadinessStatus.Ready : RelayReadinessStatus.Degraded, checks);
    }

    private static RelayReadinessCheckResult Check(RelayReadinessCheck check, bool isReady) => new(check, isReady);
}

public sealed record RelayReadinessProbe(
    bool StorageAvailable,
    int FreeDiskMegabytes,
    int ClockDriftSeconds,
    bool BuildKnown,
    int UpdateAgeMinutes,
    int LatencyMilliseconds,
    int ConsecutiveFailures,
    int RateLimitRejections,
    int RejectedInputCount)
{
    public RelayReadinessProbe
    {
        if (FreeDiskMegabytes < 0 || UpdateAgeMinutes < 0 || LatencyMilliseconds < 0 ||
            ConsecutiveFailures < 0 || RateLimitRejections < 0 || RejectedInputCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(FreeDiskMegabytes));
        }
    }
}

public enum RelayReadinessStatus { Ready, Degraded }

public enum RelayReadinessCheck
{
    Storage,
    Disk,
    Clock,
    Build,
    UpdateAge,
    Latency,
    Failures,
    RateLimits,
    RejectedInput,
}

public sealed record RelayReadinessCheckResult(RelayReadinessCheck Check, bool IsReady);

public sealed record RelayReadinessResult(
    RelayReadinessStatus Status,
    IReadOnlyList<RelayReadinessCheckResult> Checks);
