using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TarkovCompanion.Application.Services.CaptureSessions;

/// <summary>One stage's measured duration within a single scan's timeline.</summary>
public readonly record struct CaptureStageTiming(string Stage, double ElapsedMilliseconds);

/// <summary>Every stage measured for one scan, file seen to first paint.</summary>
/// <remarks>
/// <see cref="Stages"/> are durations, marked by whichever stage measured itself (the Loot page's
/// progress line reads them). <see cref="Milestones"/> (#712 0-12) are points on one monotonic
/// clock, in milliseconds since the file's name was first seen: settled, decoded, classified,
/// recognised, shown. They are what the per-kind p50/p95 in Setup are made of, because a duration
/// cannot show time spent queued between two stages and a milestone can.
/// </remarks>
public sealed record CaptureStageSummary(
    CaptureCorrelationId CorrelationId,
    DateTimeOffset FileSeenUtc,
    IReadOnlyList<CaptureStageTiming> Stages,
    double TotalMilliseconds)
{
    /// <summary>What the screenshot was read as ("Loot", "Stash", "Position", "Tasks"...); null when nothing said.</summary>
    public string? Kind { get; init; }

    /// <summary>Milestones in the order reached, each as milliseconds since the file was first seen.</summary>
    public IReadOnlyList<CaptureStageTiming> Milestones { get; init; } = [];

    /// <summary>The monotonic moment the file was first seen, for a milestone added after completion.</summary>
    public long? SeenTimestamp { get; init; }

    /// <summary>A Loot scan, or a timeline nobody classified (every caller before #712 0-12).</summary>
    public bool IsLoot => Kind is null || string.Equals(Kind, CaptureTimelineKinds.Loot, StringComparison.Ordinal);
}

/// <summary>The kinds and milestones the pipeline records, so the words are said in one place.</summary>
public static class CaptureTimelineKinds
{
    public const string Loot = "Loot";
    public const string Position = "Position";
    public const string Item = "Item";

    /// <summary>A capture that ended with no answer: an unknown screen, a cancelled review.</summary>
    public const string Unread = "Unread";

    /// <summary>The name-path position: parsed from the filename, applied to the raid, sent to the relay.</summary>
    public const string Applied = "applied";
    public const string Published = "published";

    /// <summary>The pixel path, in the order a screenshot reaches them.</summary>
    public const string Settled = "settled";
    public const string Dequeued = "dequeued";
    public const string Decoded = "decoded";
    public const string Classified = "classified";
    public const string Recognised = "recognised";
    public const string Shown = "shown";

    /// <summary>Milestone order for the Setup table; anything else sorts after these.</summary>
    public static readonly IReadOnlyList<string> Order =
        [Settled, Dequeued, Decoded, Classified, Recognised, Shown, Applied, Published];

    /// <summary>The kind a capture handed to a workspace counts under: its intent's name.</summary>
    public static string For(TarkovCompanion.Core.Abstractions.V2.ScanIntent intent) => intent.ToString();
}

/// <summary>One kind's milestone over the session: how many, and the p50 and p95 since file seen.</summary>
public sealed record CaptureStageStatistic(string Kind, string Milestone, int Count, double P50Milliseconds, double P95Milliseconds);

/// <summary>
/// One step of a scan in flight, as the Loot page's progress line reads it (#572).
/// </summary>
/// <param name="LastStage">The stage that just finished, or null when the scan only began.</param>
/// <param name="Summary">The whole timeline once <see cref="ICaptureStageTimeline.Complete"/> ran; null before.</param>
public sealed record CaptureStageStep(
    CaptureCorrelationId CorrelationId,
    string? LastStage,
    CaptureStageSummary? Summary)
{
    public bool IsComplete => Summary is not null;
}

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

    /// <summary>
    /// Starts timing with the monotonic moment the file's name was first seen (#712 0-12), so
    /// milestones count the settle wait too, and optionally the kind when it is known up front.
    /// A timeline begun with a kind other than Loot raises no <see cref="Progressed"/> steps.
    /// </summary>
    void Begin(CaptureCorrelationId correlationId, DateTimeOffset fileSeenUtc, long? seenTimestamp, string? kind);

    /// <summary>
    /// Records that a milestone was reached, at <paramref name="timestamp"/> (a
    /// <see cref="TimeProvider.GetTimestamp"/> value) or now. The first time per milestone wins.
    /// </summary>
    void Reached(CaptureCorrelationId correlationId, string milestone, long? timestamp = null);

    /// <summary>Says what the screenshot was read as. A later call replaces an earlier one.</summary>
    void Classify(CaptureCorrelationId correlationId, string kind);

    /// <summary>Records one stage's duration. A no-op when nothing began this id.</summary>
    void Mark(CaptureCorrelationId correlationId, string stage, TimeSpan elapsed);

    /// <summary>
    /// Ends timing, logs one structured line naming every stage and the total, keeps the summary
    /// as <see cref="LastCompleted"/>, and returns it. Null when nothing began this id (already
    /// completed, swept as abandoned, or an intent this pass does not time).
    /// </summary>
    CaptureStageSummary? Complete(CaptureCorrelationId correlationId, DateTimeOffset finishedUtc);

    /// <summary>As <see cref="Complete(CaptureCorrelationId, DateTimeOffset)"/>, with the kind to use when nothing classified it.</summary>
    CaptureStageSummary? Complete(CaptureCorrelationId correlationId, DateTimeOffset finishedUtc, string? fallbackKind);

    /// <summary>
    /// The newest position whose change a relay exchange just carried (#712 0-12): adds
    /// <see cref="CaptureTimelineKinds.Published"/> to the latest completed Position timeline that
    /// has none. <paramref name="sentTimestamp"/> is when the request went out.
    /// </summary>
    void PositionPublished(long sentTimestamp);

    /// <summary>The most recently completed Loot scan's timeline - Setup &gt; Diagnostics' last-scan detail.</summary>
    CaptureStageSummary? LastCompleted { get; }

    /// <summary>The last completed timelines of every kind, oldest first, bounded.</summary>
    IReadOnlyList<CaptureStageSummary> Recent { get; }

    /// <summary>p50 and p95 per kind per milestone over <see cref="Recent"/>.</summary>
    IReadOnlyList<CaptureStageStatistic> Statistics();

    /// <summary>Raised after a timeline completes or a position is published, outside the lock.</summary>
    event Action? Recorded;

    /// <summary>
    /// Raised after every begin, mark and complete, outside the lock, on whichever thread
    /// recorded it: the Loot page's per-stage progress line. A handler marshals itself.
    /// </summary>
    event Action<CaptureStageStep>? Progressed;
}

public sealed class CaptureStageTimeline(
    ILogger<CaptureStageTimeline>? logger = null,
    // #712 0-12: the monotonic clock milestones are read from. Optional, like the logger.
    TimeProvider? timeProvider = null) : ICaptureStageTimeline
{
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(2);

    /// <summary>How many completed timelines the session keeps for its p50/p95: a few raids' worth.</summary>
    public const int RecentCapacity = 256;

    private readonly ILogger<CaptureStageTimeline> _logger = logger ?? NullLogger<CaptureStageTimeline>.Instance;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<CaptureCorrelationId, Entry> _inFlight = [];
    private readonly Queue<CaptureStageSummary> _recent = new();
    private CaptureStageSummary? _lastCompleted;

    public event Action<CaptureStageStep>? Progressed;

    public event Action? Recorded;

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

    public IReadOnlyList<CaptureStageSummary> Recent
    {
        get
        {
            lock (_gate)
            {
                return [.. _recent];
            }
        }
    }

    public void Begin(CaptureCorrelationId correlationId, DateTimeOffset fileSeenUtc) =>
        Begin(correlationId, fileSeenUtc, null, null);

    public void Begin(CaptureCorrelationId correlationId, DateTimeOffset fileSeenUtc, long? seenTimestamp, string? kind)
    {
        bool begun;
        lock (_gate)
        {
            SweepAbandonedUnsafe(fileSeenUtc);
            begun = _inFlight.TryAdd(correlationId, new Entry(fileSeenUtc, seenTimestamp ?? _clock.GetTimestamp()) { Kind = kind });
        }

        if (begun && ProgressesLoot(kind))
        {
            Progressed?.Invoke(new(correlationId, null, null));
        }
    }

    public void Mark(CaptureCorrelationId correlationId, string stage, TimeSpan elapsed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        bool marked;
        string? kind = null;
        lock (_gate)
        {
            marked = _inFlight.TryGetValue(correlationId, out var entry);
            entry?.Stages.Add(new(stage, Math.Round(elapsed.TotalMilliseconds, 1)));
            kind = entry?.Kind;
        }

        if (marked && ProgressesLoot(kind))
        {
            Progressed?.Invoke(new(correlationId, stage, null));
        }
    }

    public void Reached(CaptureCorrelationId correlationId, string milestone, long? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(milestone);
        var at = timestamp ?? _clock.GetTimestamp();
        lock (_gate)
        {
            if (_inFlight.TryGetValue(correlationId, out var entry)
                && !entry.Milestones.Exists(existing => string.Equals(existing.Stage, milestone, StringComparison.Ordinal)))
            {
                entry.Milestones.Add(new(milestone, Offset(entry.SeenTimestamp, at)));
            }
        }
    }

    public void Classify(CaptureCorrelationId correlationId, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        lock (_gate)
        {
            if (_inFlight.TryGetValue(correlationId, out var entry))
            {
                entry.Kind = kind;
            }
        }
    }

    public CaptureStageSummary? Complete(CaptureCorrelationId correlationId, DateTimeOffset finishedUtc) =>
        Complete(correlationId, finishedUtc, null);

    public CaptureStageSummary? Complete(CaptureCorrelationId correlationId, DateTimeOffset finishedUtc, string? fallbackKind)
    {
        CaptureStageSummary summary;
        lock (_gate)
        {
            if (!_inFlight.Remove(correlationId, out var entry))
            {
                return null;
            }

            var total = Math.Round((finishedUtc - entry.FileSeenUtc).TotalMilliseconds, 1);
            summary = new(correlationId, entry.FileSeenUtc, [.. entry.Stages], total)
            {
                Kind = entry.Kind ?? fallbackKind,
                Milestones = [.. entry.Milestones],
                SeenTimestamp = entry.SeenTimestamp,
            };
            if (summary.IsLoot)
            {
                _lastCompleted = summary;
            }

            RememberUnsafe(summary);
        }

        // One line per screenshot, whatever it was read as: the stage durations the pipeline
        // marked, then every milestone as time since the file was first seen.
        _logger.LogInformation(
            "Screenshot {Kind} {Correlation} timing: {Stages}; milestones {Milestones}; total {TotalMs} ms.",
            summary.Kind ?? "unclassified",
            summary.CorrelationId,
            Describe(summary.Stages, "no stages recorded"),
            Describe(summary.Milestones, "none"),
            summary.TotalMilliseconds);
        if (!string.Equals(summary.Kind, CaptureTimelineKinds.Position, StringComparison.Ordinal))
        {
            // Raised for every pixel capture, so the Loot page's progress line can let go of a
            // screenshot that turned out to be something else; the view models read the kind.
            Progressed?.Invoke(new(correlationId, summary.Stages.Count == 0 ? null : summary.Stages[^1].Stage, summary));
        }

        Recorded?.Invoke();
        return summary;
    }

    public void PositionPublished(long sentTimestamp)
    {
        CaptureStageSummary? published = null;
        CaptureStageSummary? replaced = null;
        lock (_gate)
        {
            // The newest Position only: an exchange carries the latest position, and an older one
            // it replaced was never sent.
            replaced = _recent.LastOrDefault(summary =>
                string.Equals(summary.Kind, CaptureTimelineKinds.Position, StringComparison.Ordinal));
            if (replaced is null
                || replaced.Milestones.Any(milestone => milestone.Stage == CaptureTimelineKinds.Published)
                || replaced.SeenTimestamp is not { } seen)
            {
                return;
            }

            published = replaced with
            {
                Milestones = [.. replaced.Milestones, new(CaptureTimelineKinds.Published, Offset(seen, sentTimestamp))],
            };
            var kept = _recent.ToArray();
            _recent.Clear();
            foreach (var summary in kept)
            {
                _recent.Enqueue(ReferenceEquals(summary, replaced) ? published : summary);
            }
        }

        _logger.LogInformation(
            "Screenshot Position {Correlation} sent to the relay {PublishedMs} ms after the file was seen.",
            published.CorrelationId,
            published.Milestones[^1].ElapsedMilliseconds);
        Recorded?.Invoke();
    }

    public IReadOnlyList<CaptureStageStatistic> Statistics()
    {
        CaptureStageSummary[] recent;
        lock (_gate)
        {
            recent = [.. _recent];
        }

        return Summarise(recent);
    }

    /// <summary>p50 and p95 by kind and milestone; nearest-rank, so every value is one that was measured.</summary>
    public static IReadOnlyList<CaptureStageStatistic> Summarise(IEnumerable<CaptureStageSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        return
        [
            .. summaries
                .Where(summary => summary.Kind is not null)
                .SelectMany(summary => summary.Milestones.Select(milestone => (Kind: summary.Kind!, milestone.Stage, milestone.ElapsedMilliseconds)))
                .GroupBy(row => (row.Kind, row.Stage))
                .Select(group =>
                {
                    var sorted = group.Select(row => row.ElapsedMilliseconds).Order().ToArray();
                    return new CaptureStageStatistic(group.Key.Kind, group.Key.Stage, sorted.Length, Rank(sorted, 0.50), Rank(sorted, 0.95));
                })
                .OrderBy(statistic => statistic.Kind, StringComparer.Ordinal)
                .ThenBy(statistic => MilestoneRank(statistic.Milestone))
                .ThenBy(statistic => statistic.Milestone, StringComparer.Ordinal),
        ];
    }

    private static int MilestoneRank(string milestone)
    {
        for (var index = 0; index < CaptureTimelineKinds.Order.Count; index++)
        {
            if (string.Equals(CaptureTimelineKinds.Order[index], milestone, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return CaptureTimelineKinds.Order.Count;
    }

    private static double Rank(double[] sorted, double percentile) =>
        sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];

    private double Offset(long from, long to) =>
        Math.Round(Math.Max(0, _clock.GetElapsedTime(from, to).TotalMilliseconds), 1);

    private static string Describe(IReadOnlyList<CaptureStageTiming> timings, string none) =>
        timings.Count == 0
            ? none
            : string.Join(", ", timings.Select(timing => $"{timing.Stage}={timing.ElapsedMilliseconds}ms"));

    /// <summary>A timeline begun without a kind is a pixel capture that may yet be a Loot scan.</summary>
    private static bool ProgressesLoot(string? kind) =>
        kind is null || string.Equals(kind, CaptureTimelineKinds.Loot, StringComparison.Ordinal);

    private void RememberUnsafe(CaptureStageSummary summary)
    {
        _recent.Enqueue(summary);
        while (_recent.Count > RecentCapacity)
        {
            _recent.Dequeue();
        }
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

    private sealed class Entry(DateTimeOffset fileSeenUtc, long seenTimestamp)
    {
        public DateTimeOffset FileSeenUtc { get; } = fileSeenUtc;

        public long SeenTimestamp { get; } = seenTimestamp;

        public string? Kind { get; set; }

        public List<CaptureStageTiming> Stages { get; } = [];

        public List<CaptureStageTiming> Milestones { get; } = [];
    }
}
