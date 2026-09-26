using TarkovCompanion.Application.Services.CaptureSessions;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>One screenshot no detector was sure of, and its best guesses.</summary>
public sealed record UnrecognisedScreen(
    string ArtifactId,
    CaptureCorrelationId CorrelationId,
    DateTimeOffset ObservedUtc,
    IReadOnlyList<ScreenVote> Guesses,
    string Because);

/// <summary>
/// The unrecognised tray (#712 T2): a quiet list of the screenshots nobody could place.
/// </summary>
/// <remarks>
/// It never opens anything. It replaced the "what is this?" prompt, which could not be answered
/// mid-raid and which a guessed answer would have made worse (decision 9: never wrong). Each row
/// offers "Read as…" while its frame is still held in memory; the frame itself is never kept here.
/// A world-view screenshot is not unrecognised (the detectors say so), and is never listed.
/// </remarks>
public sealed class UnrecognisedScreenTray : IDisposable
{
    /// <summary>How many rows are kept; older ones fall off, newest first.</summary>
    public const int Capacity = 8;

    private readonly object _gate = new();
    private readonly ScreenRoutingLog _log;
    private readonly List<UnrecognisedScreen> _items = [];

    public UnrecognisedScreenTray(ScreenRoutingLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _log.Recorded += OnRecorded;
    }

    public event EventHandler? Changed;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<UnrecognisedScreen> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    /// <summary>A row the player has answered ("Read as…") leaves the tray.</summary>
    public void Remove(string artifactId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _items.RemoveAll(item => string.Equals(item.ArtifactId, artifactId, StringComparison.Ordinal)) > 0;
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose() => _log.Recorded -= OnRecorded;

    private void OnRecorded(object? sender, ScreenRoutingRecord record)
    {
        if (!record.Routing.IsUnsure)
        {
            return;
        }

        lock (_gate)
        {
            _items.RemoveAll(item => string.Equals(item.ArtifactId, record.ArtifactId, StringComparison.Ordinal));
            _items.Insert(0, new(record.ArtifactId, record.CorrelationId, record.ObservedUtc, record.Routing.Guesses, record.Routing.Because));
            if (_items.Count > Capacity)
            {
                _items.RemoveRange(Capacity, _items.Count - Capacity);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
