using Microsoft.Extensions.Logging;

namespace TarkovCompanion.Application.Services.Quests;

public sealed record QuestScreenshotBurstOffer(
    long BurstId,
    IReadOnlyList<string> Paths,
    DateTimeOffset FirstCapturedUtc,
    DateTimeOffset LastCapturedUtc,
    bool IsDismissed)
{
    public int ScreenshotCount => Paths.Count;

    public bool IsVisible => BurstId > 0 && Paths.Count > 0 && !IsDismissed;

    public static QuestScreenshotBurstOffer Empty { get; } = new(
        0,
        [],
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        false);
}

/// <summary>Groups passively recognised TASKS screenshots into one review offer.</summary>
/// <remarks>
/// The screenshot watcher scans files concurrently, so completion order is not capture order.
/// The file's capture time defines the burst and paths are sorted before a review receives them.
/// Dismissal belongs to the burst, not the current count: another frame from the same scroll must
/// not raise the offer again after the player already answered it.
/// </remarks>
public sealed class QuestScreenshotBurstCollector
{
    public static readonly TimeSpan DefaultBurstWindow = TimeSpan.FromMinutes(2);
    private const int MaximumScreenshots = 24;

    private readonly object _gate = new();
    private readonly TimeSpan _burstWindow;
    private readonly ILogger<QuestScreenshotBurstCollector>? _logger;
    private readonly List<(string Path, DateTimeOffset CapturedUtc)> _screenshots = [];
    private long _nextBurstId;
    private long _burstId;
    private bool _dismissed;

    public QuestScreenshotBurstCollector(
        TimeSpan? burstWindow = null,
        ILogger<QuestScreenshotBurstCollector>? logger = null)
    {
        _burstWindow = burstWindow ?? DefaultBurstWindow;
        _logger = logger;
        if (_burstWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(burstWindow));
        }
    }

    public event EventHandler? Changed;

    public QuestScreenshotBurstOffer Current
    {
        get
        {
            lock (_gate)
            {
                return SnapshotUnsafe();
            }
        }
    }

    public QuestScreenshotBurstOffer Observe(
        string path,
        DateTimeOffset capturedUtc,
        bool raidActive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (raidActive)
        {
            return Current;
        }

        QuestScreenshotBurstOffer snapshot;
        var changed = false;
        lock (_gate)
        {
            capturedUtc = capturedUtc.ToUniversalTime();
            var latest = _screenshots.Count == 0
                ? (DateTimeOffset?)null
                : _screenshots.Max(entry => entry.CapturedUtc);
            var earliest = _screenshots.Count == 0
                ? (DateTimeOffset?)null
                : _screenshots.Min(entry => entry.CapturedUtc);
            if (latest is null || capturedUtc - latest > _burstWindow)
            {
                _screenshots.Clear();
                _burstId = ++_nextBurstId;
                _dismissed = false;
            }
            else if (earliest - capturedUtc > _burstWindow)
            {
                // A slow OCR completion from the preceding burst must not join or replace the
                // newer offer that already exists.
                return SnapshotUnsafe();
            }

            if (!_screenshots.Any(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                _screenshots.Add((path, capturedUtc));
                _screenshots.Sort(static (left, right) => left.CapturedUtc.CompareTo(right.CapturedUtc));
                if (_screenshots.Count > MaximumScreenshots)
                {
                    _screenshots.RemoveRange(0, _screenshots.Count - MaximumScreenshots);
                }

                changed = true;
            }

            snapshot = SnapshotUnsafe();
        }

        if (changed)
        {
            NotifyChanged();
        }

        return snapshot;
    }

    public QuestScreenshotBurstOffer TakeForReview() => Dismiss(takePaths: true);

    public void DismissCurrent() => _ = Dismiss(takePaths: false);

    private QuestScreenshotBurstOffer Dismiss(bool takePaths)
    {
        QuestScreenshotBurstOffer snapshot;
        var changed = false;
        lock (_gate)
        {
            snapshot = SnapshotUnsafe();
            if (snapshot.IsVisible)
            {
                _dismissed = true;
                changed = true;
            }

            if (!takePaths)
            {
                snapshot = SnapshotUnsafe();
            }
        }

        if (changed)
        {
            NotifyChanged();
        }

        return snapshot;
    }

    private void NotifyChanged()
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                // A banner subscriber is presentation-only. It must never stop the passive
                // watcher from publishing the scan and HUD evidence it already produced.
                _logger?.LogWarning(exception, "A quest screenshot offer subscriber failed.");
            }
        }
    }

    private QuestScreenshotBurstOffer SnapshotUnsafe()
    {
        if (_screenshots.Count == 0)
        {
            return QuestScreenshotBurstOffer.Empty;
        }

        return new(
            _burstId,
            _screenshots.Select(entry => entry.Path).ToArray(),
            _screenshots[0].CapturedUtc,
            _screenshots[^1].CapturedUtc,
            _dismissed);
    }
}
