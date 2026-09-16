namespace TarkovCompanion.GroupServer.Diagnostics;

/// <summary>
/// Builds an admin-only readiness result from bounded, relay-owned probes. It deliberately has
/// no paths, room identifiers, report bodies, group keys, names, or positions in its model.
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
    public const int MaximumUpdaterCheckAgeMinutes = 90;
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
            new RelayReadinessCheckResult(RelayReadinessCheck.Storage, probe.StorageAvailable),
            new RelayReadinessCheckResult(RelayReadinessCheck.Disk, probe.FreeDiskMegabytes >= MinimumFreeDiskMegabytes),
            new RelayReadinessCheckResult(
                RelayReadinessCheck.Clock,
                Math.Abs((long)probe.ClockDriftSeconds) <= MaximumClockDriftSeconds),
            new RelayReadinessCheckResult(RelayReadinessCheck.Build, probe.BuildKnown),
            new RelayReadinessCheckResult(
                RelayReadinessCheck.UpdaterCheck,
                probe.LastSuccessfulUpdaterCheckAgeMinutes <= MaximumUpdaterCheckAgeMinutes),
            new RelayReadinessCheckResult(
                RelayReadinessCheck.Latency,
                probe.LatencyMilliseconds <= MaximumLatencyMilliseconds),
            new RelayReadinessCheckResult(
                RelayReadinessCheck.Failures,
                probe.ConsecutiveFailures < MaximumConsecutiveFailures),
        };
        var signals = new[]
        {
            new RelayOperationalSignalResult(
                RelayOperationalSignal.RateLimitPressure,
                probe.RateLimitRejectionsInObservationWindow > MaximumRateLimitRejectionsInObservationWindow),
            new RelayOperationalSignalResult(
                RelayOperationalSignal.RejectedInputAbuse,
                probe.RejectedInputCountInObservationWindow > MaximumRejectedInputsInObservationWindow),
        };

        return new(
            checks.All(check => check.IsReady) ? RelayReadinessStatus.Ready : RelayReadinessStatus.Degraded,
            probe.ObservedUtc,
            probe.ObservationWindowStartedUtc,
            checks,
            signals);
    }
}

public sealed record RelayReadinessProbe
{
    public RelayReadinessProbe(
        DateTimeOffset observedUtc,
        DateTimeOffset observationWindowStartedUtc,
        RelayReadinessProbeProvenance provenance,
        bool storageAvailable,
        int freeDiskMegabytes,
        int clockDriftSeconds,
        bool buildKnown,
        int lastSuccessfulUpdaterCheckAgeMinutes,
        int latencyMilliseconds,
        int consecutiveFailures,
        int rateLimitRejectionsInObservationWindow,
        int rejectedInputCountInObservationWindow)
    {
        if (observedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The readiness observation time must be UTC.", nameof(observedUtc));
        }

        if (observationWindowStartedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The readiness observation window must start in UTC.",
                nameof(observationWindowStartedUtc));
        }

        if (observedUtc < DateTimeOffset.MinValue.AddMinutes(RelayReadiness.ObservationWindowMinutes) ||
            observationWindowStartedUtc != observedUtc.AddMinutes(-RelayReadiness.ObservationWindowMinutes))
        {
            throw new ArgumentException(
                $"The readiness counter window must be exactly {RelayReadiness.ObservationWindowMinutes} minutes.",
                nameof(observationWindowStartedUtc));
        }

        if (provenance != RelayReadinessProbeProvenance.Complete)
        {
            throw new ArgumentException(
                "Readiness requires relay-owned probes, verified updater state, and relay-owned fixed-window counters.",
                nameof(provenance));
        }

        if (freeDiskMegabytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(freeDiskMegabytes));
        }

        if (lastSuccessfulUpdaterCheckAgeMinutes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lastSuccessfulUpdaterCheckAgeMinutes));
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

        ObservedUtc = observedUtc;
        ObservationWindowStartedUtc = observationWindowStartedUtc;
        Provenance = provenance;
        StorageAvailable = storageAvailable;
        FreeDiskMegabytes = freeDiskMegabytes;
        ClockDriftSeconds = clockDriftSeconds;
        BuildKnown = buildKnown;
        LastSuccessfulUpdaterCheckAgeMinutes = lastSuccessfulUpdaterCheckAgeMinutes;
        LatencyMilliseconds = latencyMilliseconds;
        ConsecutiveFailures = consecutiveFailures;
        RateLimitRejectionsInObservationWindow = rateLimitRejectionsInObservationWindow;
        RejectedInputCountInObservationWindow = rejectedInputCountInObservationWindow;
    }

    public DateTimeOffset ObservedUtc { get; }

    public DateTimeOffset ObservationWindowStartedUtc { get; }

    public RelayReadinessProbeProvenance Provenance { get; }

    public bool StorageAvailable { get; }

    public int FreeDiskMegabytes { get; }

    public int ClockDriftSeconds { get; }

    public bool BuildKnown { get; }

    /// <summary>
    /// Age of the last successful updater check, not the age of the installed or published
    /// commit. A quiet release is healthy; an updater that has stopped checking is not.
    /// </summary>
    public int LastSuccessfulUpdaterCheckAgeMinutes { get; }

    public int LatencyMilliseconds { get; }

    public int ConsecutiveFailures { get; }

    /// <summary>
    /// Rejections recorded by relay-owned rate-limit accounting during the exact preceding
    /// <see cref="RelayReadiness.ObservationWindowMinutes"/> minutes.
    /// </summary>
    public int RateLimitRejectionsInObservationWindow { get; }

    /// <summary>
    /// Rejected inputs recorded by relay-owned validation accounting during the same exact
    /// observation window.
    /// </summary>
    public int RejectedInputCountInObservationWindow { get; }
}

[Flags]
public enum RelayReadinessProbeProvenance
{
    None = 0,
    RelayOwnedOperationalProbes = 1,
    VerifiedUpdaterState = 2,
    RelayOwnedFixedWindowCounters = 4,
    Complete = RelayOwnedOperationalProbes | VerifiedUpdaterState | RelayOwnedFixedWindowCounters,
}

public enum RelayReadinessStatus
{
    Ready = 1,
    Degraded = 2,
}

public enum RelayReadinessCheck
{
    Storage = 1,
    Disk = 2,
    Clock = 3,
    Build = 4,
    UpdaterCheck = 5,
    Latency = 6,
    Failures = 7,
}

public enum RelayOperationalSignal
{
    RateLimitPressure = 1,
    RejectedInputAbuse = 2,
}

public sealed record RelayReadinessCheckResult
{
    public RelayReadinessCheckResult(RelayReadinessCheck check, bool isReady)
    {
        if (!Enum.IsDefined(check))
        {
            throw new ArgumentOutOfRangeException(nameof(check));
        }

        Check = check;
        IsReady = isReady;
    }

    public RelayReadinessCheck Check { get; }

    public bool IsReady { get; }
}

public sealed record RelayOperationalSignalResult
{
    public RelayOperationalSignalResult(RelayOperationalSignal signal, bool isElevated)
    {
        if (!Enum.IsDefined(signal))
        {
            throw new ArgumentOutOfRangeException(nameof(signal));
        }

        Signal = signal;
        IsElevated = isElevated;
    }

    public RelayOperationalSignal Signal { get; }

    public bool IsElevated { get; }
}

public sealed record RelayReadinessResult
{
    internal RelayReadinessResult(
        RelayReadinessStatus status,
        DateTimeOffset observedUtc,
        DateTimeOffset observationWindowStartedUtc,
        IEnumerable<RelayReadinessCheckResult> checks,
        IEnumerable<RelayOperationalSignalResult> signals)
    {
        Status = status;
        ObservedUtc = observedUtc;
        ObservationWindowStartedUtc = observationWindowStartedUtc;
        Checks = Array.AsReadOnly(checks.ToArray());
        Signals = Array.AsReadOnly(signals.ToArray());
    }

    public RelayReadinessStatus Status { get; }

    public DateTimeOffset ObservedUtc { get; }

    public DateTimeOffset ObservationWindowStartedUtc { get; }

    public IReadOnlyList<RelayReadinessCheckResult> Checks { get; }

    /// <summary>
    /// Abuse and rejection pressure is visible to an authenticated operator but does not make a
    /// correctly rejecting relay unready.
    /// </summary>
    public IReadOnlyList<RelayOperationalSignalResult> Signals { get; }
}
