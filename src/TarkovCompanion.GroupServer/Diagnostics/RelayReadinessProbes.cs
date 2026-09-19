using System.Diagnostics;

namespace TarkovCompanion.GroupServer.Diagnostics;

/// <summary>
/// The relay's own measurements of itself, fed to <see cref="RelayReadiness"/>.
/// </summary>
/// <remarks>
/// Every input here is measured by this process or read from a directory this process cannot write:
/// storage by writing and reading back a probe file, disk by asking the volume, the updater by the
/// modification time of a stamp only root writes, the rest from <see cref="RelayOperationalCounters"/>.
/// Nothing is asked of the network, so readiness cannot be made to fail by an upstream being slow.
///
/// Two states are reported as not applicable rather than failed, because a fixed threshold would
/// make every developer's relay and every test relay permanently degraded: a relay with no state
/// directory keeps everything in memory (storage is then trivially available), and one with no
/// updater reporting to it has no updater check to be late. A relay that <em>has</em> an updater and
/// has never had a check recorded is reported as never checked, which is degraded.
/// </remarks>
public sealed class RelayReadinessProbes(
    TimeProvider clock,
    string? stateDirectory,
    RelayUpdate update,
    RelayOperationalCounters counters,
    bool buildKnown)
{
    private const long BytesPerMegabyte = 1024 * 1024;

    /// <summary>Older than any real check, for an updater that has never recorded one.</summary>
    private const int NeverChecked = 60 * 24 * 365;

    public RelayReadinessReport Capture()
    {
        var observed = clock.GetUtcNow();
        var (storageAvailable, latencyMilliseconds) = ProbeStorage();
        var updater = update.ReadLastVerifiedCheck();
        var updaterAgeMinutes = !updater.Configured
            ? 0
            : updater.LastVerifiedUtc is { } last
                ? (int)Math.Clamp((observed - last).TotalMinutes, 0, NeverChecked)
                : NeverChecked;
        var window = counters.Snapshot(observed);
        var probe = new RelayReadinessProbe(
            observed,
            observed.AddMinutes(-RelayReadiness.ObservationWindowMinutes),
            RelayReadinessProbeProvenance.Complete,
            storageAvailable,
            FreeDiskMegabytes(),
            counters.ClockDriftSeconds(),
            buildKnown,
            updaterAgeMinutes,
            latencyMilliseconds,
            window.ConsecutiveFailures,
            window.RateLimited,
            window.RejectedInput);
        return new(RelayReadiness.Evaluate(probe), probe, updater.Configured, stateDirectory is not null);
    }

    private (bool Available, int LatencyMilliseconds) ProbeStorage()
    {
        if (stateDirectory is null)
        {
            return (true, 0);
        }

        var started = Stopwatch.GetTimestamp();
        var path = Path.Combine(stateDirectory, $".readiness-{Guid.NewGuid():N}.probe");
        try
        {
            Directory.CreateDirectory(stateDirectory);
            var expected = Guid.NewGuid().ToByteArray();
            File.WriteAllBytes(path, expected);
            var read = File.ReadAllBytes(path);
            return (read.AsSpan().SequenceEqual(expected), ElapsedMilliseconds(started));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (false, ElapsedMilliseconds(started));
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A probe file that cannot be removed is what the storage check above just said.
            }
        }
    }

    private int FreeDiskMegabytes()
    {
        try
        {
            var free = new DriveInfo(Path.GetFullPath(stateDirectory ?? AppContext.BaseDirectory)).AvailableFreeSpace;
            return (int)Math.Min(int.MaxValue, free / BytesPerMegabyte);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unknown is not plenty: report none, which the check treats as short.
            return 0;
        }
    }

    private static int ElapsedMilliseconds(long started) =>
        (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
}

/// <summary>One capture: the verdict, the measurements behind it, and what could not be measured.</summary>
public sealed record RelayReadinessReport(
    RelayReadinessResult Result,
    RelayReadinessProbe Probe,
    bool UpdaterConfigured,
    bool StorageIsPersistent);
