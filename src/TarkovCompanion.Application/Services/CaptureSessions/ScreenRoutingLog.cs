using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.Application.Services.CaptureSessions;

/// <summary>One frame's routing decision, pixel-free, as the tray and the situation read it.</summary>
public sealed record ScreenRoutingRecord(
    CaptureCorrelationId CorrelationId,
    CaptureSessionId SessionId,
    string ArtifactId,
    DateTimeOffset ObservedUtc,
    ScreenRouting Routing);

/// <summary>
/// Where every routing decision is announced (#712 1-1): the unrecognised tray takes the unsure
/// ones, the situation takes the routed ones for its "because" line.
/// </summary>
/// <remarks>
/// A publisher rather than a return value because the decision is made inside the pipeline, which
/// the coordinator calls and whose result it reduces to a context; the decision's detail (which
/// detector, how sure, who came second) would otherwise be lost there.
/// </remarks>
public sealed class ScreenRoutingLog
{
    private const int Kept = 64;
    private readonly object _gate = new();
    private readonly Queue<ScreenRoutingRecord> _recent = new();

    public event EventHandler<ScreenRoutingRecord>? Recorded;

    public IReadOnlyList<ScreenRoutingRecord> Recent
    {
        get
        {
            lock (_gate)
            {
                return [.. _recent];
            }
        }
    }

    public void Record(ScreenRoutingRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            _recent.Enqueue(record);
            while (_recent.Count > Kept)
            {
                _recent.Dequeue();
            }
        }

        Recorded?.Invoke(this, record);
    }
}
