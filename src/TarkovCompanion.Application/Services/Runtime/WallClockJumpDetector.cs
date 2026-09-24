using Microsoft.Extensions.Logging;

namespace TarkovCompanion.Application.Services.Runtime;

/// <summary>
/// Notices the PC's clock being set while the companion runs, and says by how much.
/// </summary>
/// <remarks>
/// [#799] Clayton's Windows clock started four hours fast (the dual-boot RTC of #704) and was
/// corrected while the companion ran. Everything that had stamped a wall time before the
/// correction and compared it with a wall time after it went wrong at once: the group publish
/// loop waited four hours for its 300 ms rate bound, every new screenshot was refused as older
/// than the last one, and the loot layer's cache read as written in the future. The squad, the
/// player's own marker and the pings were gone until a restart.
///
/// Durations are measured on a monotonic clock where that is possible. What cannot be, because
/// it is a wall time on purpose — a screenshot's own time, the raid's start, a mark's creation —
/// is moved by the jump this reports, so the next wall time read compares against the same frame.
///
/// The jump is the difference between how far the wall clock moved and how far the monotonic
/// clock moved since the last look. Ordinary drift and NTP slewing are milliseconds per check;
/// anything past <see cref="Threshold"/> is somebody (or Windows) setting the time.
/// </remarks>
public sealed class WallClockJumpDetector : IDisposable
{
    /// <summary>How far the wall clock must move against the monotonic one to count as set.</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(30);

    /// <summary>How often to look. A jump is noticed within this, which is well inside a ping.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _clock;
    private readonly ILogger<WallClockJumpDetector>? _logger;
    private readonly Lock _gate = new();
    private DateTimeOffset _wall;
    private long _monotonic;
    private ITimer? _timer;
    private bool _disposed;

    public WallClockJumpDetector(TimeProvider? clock = null, ILogger<WallClockJumpDetector>? logger = null)
    {
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
        _wall = _clock.GetUtcNow();
        _monotonic = _clock.GetTimestamp();
    }

    /// <summary>Raised with the jump (new wall time minus what it should have been) once per jump.</summary>
    /// <remarks>Subscribers run on whichever thread noticed; each one is isolated from the others.</remarks>
    public event Action<TimeSpan>? Jumped;

    /// <summary>Starts looking on a timer. Safe to call more than once.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null)
            {
                return;
            }

            _timer = _clock.CreateTimer(static state => ((WallClockJumpDetector)state!).Check(), this, CheckEvery, CheckEvery);
        }
    }

    /// <summary>Looks now, raising <see cref="Jumped"/> when the wall clock was set since the last look.</summary>
    /// <returns>The jump, or null when there was none.</returns>
    public TimeSpan? Check()
    {
        TimeSpan jump;
        lock (_gate)
        {
            var wall = _clock.GetUtcNow();
            var monotonic = _clock.GetTimestamp();
            jump = (wall - _wall) - _clock.GetElapsedTime(_monotonic, monotonic);
            _wall = wall;
            _monotonic = monotonic;
            if (jump.Duration() < Threshold)
            {
                return null;
            }
        }

        _logger?.LogWarning(
            "The PC clock was set by {Jump} while running; in-memory times are moved by the same amount.",
            jump);
        foreach (var handler in Jumped?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<TimeSpan>)handler)(jump);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // One subscriber failing to re-stamp must not stop the others doing so.
                _logger?.LogWarning(exception, "A clock-jump subscriber failed.");
            }
        }

        return jump;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
