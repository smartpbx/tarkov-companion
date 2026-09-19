using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace TarkovCompanion.GroupServer.Diagnostics;

/// <summary>
/// Relay-owned counters for what <see cref="RelayReadiness"/> needs and no probe can read: how
/// many requests were refused for being too fast or too malformed in the last five minutes, how
/// many answers in a row were the relay's own fault, and whether the wall clock has moved under it.
/// </summary>
/// <remarks>
/// Counted from the status a route already chose, never from a request: no path, header, key, room
/// or address is read here, so there is nothing in this type that could leak one. Rejections are
/// not failures. A relay that correctly answers a hostile caller with 400s is doing its job, which
/// is why <see cref="RelayReadiness"/> treats them as signals and only server faults as readiness.
/// </remarks>
public sealed class RelayOperationalCounters
{
    /// <summary>Bounds memory under a flood; the readiness thresholds are single digits.</summary>
    public const int MaximumTrackedObservations = 4096;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(RelayReadiness.ObservationWindowMinutes);

    private readonly TimeProvider _clock;
    private readonly long _startedTimestamp;
    private readonly Lock _gate = new();
    private readonly Queue<DateTimeOffset> _rateLimited = new();
    private readonly Queue<DateTimeOffset> _rejectedInput = new();
    private int _consecutiveFailures;

    public RelayOperationalCounters(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        StartedUtc = clock.GetUtcNow();
        _startedTimestamp = clock.GetTimestamp();
    }

    public DateTimeOffset StartedUtc { get; }

    /// <summary>Records the status an answer was sent with.</summary>
    public void Observe(int statusCode)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (statusCode == StatusCodes.Status429TooManyRequests)
            {
                Track(_rateLimited, now);
            }
            else if (statusCode is 400 or 411 or 413 or 414 or 415 or 422 or 431)
            {
                Track(_rejectedInput, now);
            }

            _consecutiveFailures = statusCode >= 500 ? _consecutiveFailures + 1 : 0;
        }
    }

    /// <summary>What happened in the exact five minutes before <paramref name="observedUtc"/>.</summary>
    public RelayCounterWindow Snapshot(DateTimeOffset observedUtc)
    {
        lock (_gate)
        {
            var since = observedUtc - Window;
            return new(Count(_rateLimited, since), Count(_rejectedInput, since), _consecutiveFailures);
        }
    }

    /// <summary>
    /// How far the wall clock has moved against the monotonic one since this process started.
    /// </summary>
    /// <remarks>
    /// The relay has no time source of its own to compare with, and asking one would make readiness
    /// depend on a network. A wall clock that jumps (a container resumed from a snapshot, a bad NTP
    /// step) disagrees with the monotonic clock by exactly the jump, so that is what is measured.
    /// </remarks>
    public int ClockDriftSeconds()
    {
        var wall = _clock.GetUtcNow() - StartedUtc;
        var monotonic = _clock.GetElapsedTime(_startedTimestamp);
        return (int)Math.Min(int.MaxValue, Math.Round(Math.Abs((wall - monotonic).TotalSeconds)));
    }

    private static void Track(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        queue.Enqueue(now);
        while (queue.Count > MaximumTrackedObservations)
        {
            queue.Dequeue();
        }
    }

    private static int Count(Queue<DateTimeOffset> queue, DateTimeOffset since)
    {
        while (queue.Count > 0 && queue.Peek() <= since)
        {
            queue.Dequeue();
        }

        return queue.Count;
    }
}

public readonly record struct RelayCounterWindow(int RateLimited, int RejectedInput, int ConsecutiveFailures);

public static class RelayOperationalCountersMiddleware
{
    /// <summary>Observes every answer. Placed after the security middleware, so its refusals count too.</summary>
    public static IApplicationBuilder UseRelayOperationalCounters(
        this IApplicationBuilder app,
        RelayOperationalCounters counters)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(counters);
        return app.Use(async (context, next) =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
            {
                // Nothing above this turns a fault into a 500 before the pipeline unwinds to here.
                counters.Observe(StatusCodes.Status500InternalServerError);
                throw;
            }

            counters.Observe(context.Response.StatusCode);
        });
    }
}
