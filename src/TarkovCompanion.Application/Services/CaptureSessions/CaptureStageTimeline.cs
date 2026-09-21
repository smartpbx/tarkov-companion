using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TarkovCompanion.Application.Services.CaptureSessions;

/// <summary>One stage's measured duration within a single scan's timeline.</summary>
public readonly record struct CaptureStageTiming(string Stage, double ElapsedMilliseconds);

/// <summary>Every stage measured for one scan, file seen to first paint.</summary>
public sealed record CaptureStageSummary(
    CaptureCorrelationId CorrelationId,
    DateTimeOffset FileSeenUtc,
    IReadOnlyList<CaptureStageTiming> Stages,
    double TotalMilliseconds);

/// <summary>
/// Times one scan end to end (#572), correlated by its <see cref="CaptureCorrelationId"/> rather
/// than threaded as a parameter through every stage's own record type.
/// </summary>
/// <remarks>
/// Before this the log's only stage-shaped line was "recognised context ... candidate(s)", which
/// ScanUseCase emits for the older always-on background scan - a different pipeline from the one
/// that actually produces what the V2 Loot page shows. Everything past that line (grid
/// reconstruction, icon matching against the full catalog, profile lookup, recommendation, the
/// page's own first paint) was invisible.
///
/// A capture crosses a work queue between "file seen" (<see cref="Begin"/>, on the watcher's own
/// thread) and analysis (<see cref="Mark"/>, on whatever thread the coordinator's scheduler runs
/// on), so nothing here can rely on one continuous async call stack the way, say,
/// <c>ScanFrameDeadline</c> does with an <c>AsyncLocal</c>. The correlation id is the one thing
/// that already rides the whole trip - <c>RaidObservationService</c> mints it, it travels inside
/// <c>CaptureHandoffRequest</c> and back out on <c>LootScanResult</c> - so it is the key here too.
///
/// A stage nothing ever begins (every intent but Loot, this pass) records nothing and costs
/// nothing beyond a dictionary miss. An abandoned begin (the capture was skipped, cancelled, or
/// never reached a handoff that calls <see cref="Complete"/>) is swept after two minutes so a
/// quiet session cannot grow this without bound.
/// </remarks>
public interface ICaptureStageTimeline
{
    /// <summary>Starts timing a scan. A second call for the same id is ignored.</summary>
    void Begin(CaptureCorrelationId correlationId, DateTimeOffset fileSeenUtc);

    /// <summary>Records one stage's duration. A no-op when nothing began this id.</summary>
    void Mark(CaptureCorrelationId correlationId, string stage, TimeSpan elapsed);

    /// <summary>
    /// Ends timing, logs one structured line naming every stage and the total, keeps the summary
    /// as <see cref="LastCompleted"/>, and returns it. Null when nothing began this id (already
    /// completed, swept as abandoned, or an intent this pass does not time).
    /// </summary>
    CaptureStageSummary? Complete(CaptureCorrelationId correlationId, DateTimeOffset finishedUtc);

    /// <summary>The most recently completed scan's timeline - Setup &gt; Diagnostics' last-scan detail.</summary>
    CaptureStageSummary? LastCompleted { get; }
}

public sealed class CaptureStageTimeline(ILogger<CaptureStageTimeline>? logger = null) : ICaptureStageTimeline
{
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(2);
    private readonly ILogger<CaptureStageTimeline> _logger = logger ?? NullLogger<CaptureStageTimeline>.Instance;
    private readonly Lock _gate = new();
    private readonly Dictionary<CaptureCorrelationId, Entry> _inFlight = [];
    private CaptureStageSummary? _lastCompleted;

    public CaptureStageSummary? LastCompleted
    {
        get
        {
            lock (_gate)
            {
                return _lastCompleted;
            }
        }
    }

    public void Begin(CaptureCorrelationId correlationId, DateTimeOffset fileSeenUtc)
    {
        lock (_gate)
        {
            SweepAbandonedUnsafe(fileSeenUtc);
            _inFlight.TryAdd(correlationId, new Entry(fileSeenUtc));
        }
    }

    public void Mark(CaptureCorrelationId correlationId, string stage, TimeSpan elapsed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        lock (_gate)
        {
            if (_inFlight.TryGetValue(correlationId, out var entry))
            {
                entry.Stages.Add(new(stage, Math.Round(elapsed.TotalMilliseconds, 1)));
            }
        }
    }

    public CaptureStageSummary? Complete(CaptureCorrelationId correlationId, DateTimeOffset finishedUtc)
    {
        CaptureStageSummary summary;
        lock (_gate)
        {
            if (!_inFlight.Remove(correlationId, out var entry))
            {
                return null;
            }

            var total = Math.Round((finishedUtc - entry.FileSeenUtc).TotalMilliseconds, 1);
            summary = new(correlationId, entry.FileSeenUtc, [.. entry.Stages], total);
            _lastCompleted = summary;
        }

        _logger.LogInformation(
            "Loot scan {Correlation} timing: {Stages}; total {TotalMs} ms.",
            summary.CorrelationId,
            summary.Stages.Count == 0
                ? "no stages recorded"
                : string.Join(", ", summary.Stages.Select(stage => $"{stage.Stage}={stage.ElapsedMilliseconds}ms")),
            summary.TotalMilliseconds);
        return summary;
    }

    /// <summary>Drops a begin nothing ever completed, so a session that skips or cancels scans cannot grow this unbounded.</summary>
    private void SweepAbandonedUnsafe(DateTimeOffset now)
    {
        if (_inFlight.Count == 0)
        {
            return;
        }

        List<CaptureCorrelationId>? stale = null;
        foreach (var (id, entry) in _inFlight)
        {
            if (now - entry.FileSeenUtc > AbandonedAfter)
            {
                (stale ??= []).Add(id);
            }
        }

        if (stale is null)
        {
            return;
        }

        foreach (var id in stale)
        {
            _inFlight.Remove(id);
        }
    }

    private sealed class Entry(DateTimeOffset fileSeenUtc)
    {
        public DateTimeOffset FileSeenUtc { get; } = fileSeenUtc;

        public List<CaptureStageTiming> Stages { get; } = [];
    }
}
