namespace TarkovCompanion.GroupServer.Diagnostics;

/// <summary>
/// Builds an admin-only readiness result from bounded probes. It deliberately has no paths,
/// room identifiers, report bodies, group keys, names, or positions in its model.
/// </summary>
/// <remarks>
/// The current public health route also exposes aggregate counts; this richer result is not
/// published anywhere yet. Future composition must place it behind relay administration, and is
/// owned by the integration owner because <c>Program.cs</c> is shared composition.
/// </remarks>
public static class RelayReadiness
{
    public const int MinimumFreeDiskMegabytes = 256;
    public const int MaximumClockDriftSeconds = 60;
    public const int MaximumUpdateAgeMinutes = 90;
    public const int MaximumLatencyMilliseconds = 2_000;
    public const int MaximumConsecutiveFailures = 3;
    public const int ObservationWindowMinutes = 5;
    public const int MaximumRateLimitRejectionsInObservationWindow = 3;
    public const int MaximumRejectedInputsInObservationWindow = 3;

    public static RelayReadinessResult Evaluate(RelayReadinessProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var checks = new[]
        {
            Check(RelayReadinessCheck.Storage, probe.StorageAvailable),
            Check(RelayReadinessCheck.Disk, probe.FreeDiskMegabytes >= MinimumFreeDiskMegabytes),
            Check(RelayReadinessCheck.Clock, Math.Abs((long)probe.ClockDriftSeconds) <= MaximumClockDriftSeconds),
            Check(RelayReadinessCheck.Build, probe.BuildKnown),
            Check(RelayReadinessCheck.UpdateAge, probe.UpdateAgeMinutes <= MaximumUpdateAgeMinutes),
            Check(RelayReadinessCheck.Latency, probe.LatencyMilliseconds <= MaximumLatencyMilliseconds),
            Check(RelayReadinessCheck.Failures, probe.ConsecutiveFailures < MaximumConsecutiveFailures),
            Check(
                RelayReadinessCheck.RateLimits,
                probe.RateLimitRejectionsInObservationWindow <= MaximumRateLimitRejectionsInObservationWindow),
            Check(
                RelayReadinessCheck.RejectedInput,
                probe.RejectedInputCountInObservationWindow <= MaximumRejectedInputsInObservationWindow),
        };
        return new(checks.All(check => check.IsReady) ? RelayReadinessStatus.Ready : RelayReadinessStatus.Degraded, checks);
    }

    private static RelayReadinessCheckResult Check(RelayReadinessCheck check, bool isReady) => new(check, isReady);
}

public sealed record RelayReadinessProbe
{
    public RelayReadinessProbe(
        bool storageAvailable,
        int freeDiskMegabytes,
        int clockDriftSeconds,
        bool buildKnown,
        int updateAgeMinutes,
        int latencyMilliseconds,
        int consecutiveFailures,
        int rateLimitRejectionsInObservationWindow,
        int rejectedInputCountInObservationWindow)
    {
        if (freeDiskMegabytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(freeDiskMegabytes));
        }

        if (updateAgeMinutes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(updateAgeMinutes));
        }

        if (latencyMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(latencyMilliseconds));
        }

        if (consecutiveFailures < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(consecutiveFailures));
        }

        if (rateLimitRejectionsInObservationWindow < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rateLimitRejectionsInObservationWindow));
        }

        if (rejectedInputCountInObservationWindow < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rejectedInputCountInObservationWindow));
        }

        StorageAvailable = storageAvailable;
        FreeDiskMegabytes = freeDiskMegabytes;
        ClockDriftSeconds = clockDriftSeconds;
        BuildKnown = buildKnown;
        UpdateAgeMinutes = updateAgeMinutes;
        LatencyMilliseconds = latencyMilliseconds;
        ConsecutiveFailures = consecutiveFailures;
        RateLimitRejectionsInObservationWindow = rateLimitRejectionsInObservationWindow;
        RejectedInputCountInObservationWindow = rejectedInputCountInObservationWindow;
    }

    public bool StorageAvailable { get; }

    public int FreeDiskMegabytes { get; }

    public int ClockDriftSeconds { get; }

    public bool BuildKnown { get; }

    public int UpdateAgeMinutes { get; }

    public int LatencyMilliseconds { get; }

    public int ConsecutiveFailures { get; }

    /// <summary>
    /// Rejections recorded by relay-owned rate-limit accounting during the preceding fixed
    /// <see cref="RelayReadiness.ObservationWindowMinutes"/> minutes; it is never a cumulative
    /// request counter or a value supplied by an unauthenticated request.
    /// </summary>
    public int RateLimitRejectionsInObservationWindow { get; }

    /// <summary>
    /// Rejected inputs recorded by relay-owned validation accounting during the same fixed
    /// observation window; it is never a cumulative counter or untrusted client field.
    /// </summary>
    public int RejectedInputCountInObservationWindow { get; }
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
