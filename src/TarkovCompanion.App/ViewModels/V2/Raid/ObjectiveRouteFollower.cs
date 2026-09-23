using System.Globalization;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>A recomputed route for one map, from one origin.</summary>
internal sealed record ObjectiveRouteFollowerResult(string LocationId, ObjectiveRouteOrigin Origin, ObjectiveRouteBundle Route);

/// <summary>
/// [#307] Keeps the Raid map's objective route following the plan once it has been opened there.
/// </summary>
/// <remarks>
/// The route used to be handed over once, on "Open in Raid", and then stood still while the player
/// took screenshots or changed the plan. Every input that decides it (the plan's stops for the map,
/// and where the route starts) is fed in here; a change starts a short debounce, the planner runs
/// off the interface thread, and the result is posted back only if nothing changed meanwhile. An
/// input that is the same as the last one published does nothing, which is what keeps the raid
/// rebuild (that feeds the origin) and the Plan refresh it triggers (that feeds the stops) from
/// looping.
/// </remarks>
internal sealed class ObjectiveRouteFollower : IDisposable
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _debounce;
    private readonly Action<ObjectiveRouteFollowerResult?> _publish;
    private readonly SynchronizationContext? _context;
    private readonly object _gate = new();
    private string? _locationId;
    private IReadOnlyList<ObjectiveRouteStop> _stops = [];
    private ObjectiveRouteOrigin? _origin;
    private bool _isEnabled;
    private string? _publishedSignature;
    private ITimer? _timer;
    private int _computeCount;
    private bool _disposed;

    public ObjectiveRouteFollower(
        TimeProvider time,
        TimeSpan debounce,
        Action<ObjectiveRouteFollowerResult?> publish,
        SynchronizationContext? context)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _debounce = debounce;
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _context = context;
    }

    /// <summary>How many times the planner has run; the unit tests count recomputes with it.</summary>
    public int ComputeCount => Volatile.Read(ref _computeCount);

    /// <summary>Whether a recompute is waiting for its debounce.</summary>
    public bool IsPending
    {
        get
        {
            lock (_gate)
            {
                return _timer is not null;
            }
        }
    }

    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _isEnabled;
            }
        }
    }

    public string? LocationId
    {
        get
        {
            lock (_gate)
            {
                return _locationId;
            }
        }
    }

    /// <summary>Turns following on (the route is shown) or off (hidden: nothing is computed).</summary>
    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _isEnabled = enabled;
            if (!enabled)
            {
                CancelPendingUnsafe();
                _publishedSignature = null;
                return;
            }

            ScheduleIfChangedUnsafe();
        }
    }

    /// <summary>The plan's exactly placed objectives for one map, as Plan last ordered them.</summary>
    public void SetStops(string locationId, IReadOnlyList<ObjectiveRouteStop> stops)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        ArgumentNullException.ThrowIfNull(stops);
        lock (_gate)
        {
            _locationId = locationId;
            _stops = [.. stops];
            ScheduleIfChangedUnsafe();
        }
    }

    /// <summary>Where the route starts now: the last screenshot, else the selected spawn.</summary>
    public void SetOrigin(ObjectiveRouteOrigin? origin)
    {
        lock (_gate)
        {
            _origin = origin;
            ScheduleIfChangedUnsafe();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CancelPendingUnsafe();
        }
    }

    private string SignatureUnsafe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{_locationId}|{(_origin is { } origin ? $"{origin.At.X:R},{origin.At.Y:R},{origin.Label},{origin.UnitsPerMetre:R}" : "none")}|{string.Join(';', _stops.Select(stop => $"{stop.ObjectiveId}@{stop.At.X:R},{stop.At.Y:R}"))}");

    private void ScheduleIfChangedUnsafe()
    {
        if (_disposed || !_isEnabled || _locationId is null)
        {
            return;
        }

        if (_timer is null && SignatureUnsafe() == _publishedSignature)
        {
            return;
        }

        // A burst of changes (a screenshot, then the floor it switches to, then the Plan refresh
        // that follows) restarts the wait, so the planner runs once for the last of them.
        _timer?.Dispose();
        _timer = _time.CreateTimer(_ => Fire(), null, _debounce, Timeout.InfiniteTimeSpan);
    }

    private void CancelPendingUnsafe()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Fire()
    {
        string signature;
        string locationId;
        ObjectiveRouteOrigin? origin;
        IReadOnlyList<ObjectiveRouteStop> stops;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_disposed || !_isEnabled || _locationId is null)
            {
                return;
            }

            signature = SignatureUnsafe();
            locationId = _locationId;
            origin = _origin;
            stops = _stops;
        }

        _ = Task.Run(() =>
        {
            Interlocked.Increment(ref _computeCount);
            ObjectiveRouteFollowerResult? result = origin is { } start && stops.Count > 0
                ? new(locationId, start, ObjectiveRoutePlanner.Plan(start.At, start.Label, stops, start.UnitsPerMetre))
                : null;
            Post(() =>
            {
                lock (_gate)
                {
                    // Something changed while the planner ran: its own timer is already pending
                    // and will publish the newer answer.
                    if (_disposed || !_isEnabled || signature != SignatureUnsafe())
                    {
                        return;
                    }

                    _publishedSignature = signature;
                }

                _publish(result);
            });
        });
    }

    private void Post(Action action)
    {
        if (_context is null)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }
}
