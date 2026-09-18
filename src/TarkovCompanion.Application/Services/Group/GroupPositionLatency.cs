using System.Globalization;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>
/// How long a squadmate's screenshot takes to reach this map, measured rather than claimed.
/// </summary>
/// <remarks>
/// The claim was "about five seconds" and the truth was seven to twelve, which nobody could
/// have known from inside the application because nothing anywhere timed it. So it is timed,
/// and the number is in the diagnostics report beside everything else that can be checked.
///
/// Measured without trusting two machines to agree about the time. Each leg is a duration
/// reported by the one clock that can see both ends of it: the sender says how old its position
/// was when it published, which is its own clock twice, and the relay says how long ago that
/// arrived, which is the relay's clock twice. Their sum is everything but the last hop from the
/// relay to this screen, so the number reads slightly low and never invents precision it does
/// not have.
/// </remarks>
public sealed class GroupPositionLatency
{
    /// <summary>How many recent deliveries are kept, which is enough for a median to mean something.</summary>
    private const int MaximumSamples = 64;

    /// <summary>
    /// Past this, a position is a member this companion has only just met rather than a delivery.
    /// </summary>
    /// <remarks>
    /// Joining a room hands over everybody's last known position, which may be minutes old. That
    /// is not a measurement of anything and would drag a median it has no business being in.
    /// </remarks>
    private static readonly TimeSpan LongestCredibleDelivery = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Queue<TimeSpan> _samples = new(MaximumSamples);
    private readonly Dictionary<string, DateTimeOffset> _lastOrigin = new(StringComparer.OrdinalIgnoreCase);
    private long _delivered;

    /// <summary>
    /// Records a squadmate's position, and times it when it is one this companion has not seen.
    /// </summary>
    /// <param name="member">Who it describes.</param>
    /// <param name="age">How old their position was when they published it.</param>
    /// <param name="since">How long ago the relay heard from them.</param>
    /// <param name="nowUtc">Now, for working out which screenshot this is.</param>
    public void Observe(string member, TimeSpan? age, TimeSpan? since, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(member) || age is not { } taken || since is not { } arrived)
        {
            return;
        }

        var latency = taken + arrived;
        if (latency < TimeSpan.Zero || latency > LongestCredibleDelivery)
        {
            return;
        }

        // Which screenshot this is, rather than which exchange carried it. The same position
        // comes back on every exchange until they take another one, and its age grows exactly
        // as fast as the clock does, so the moment it was taken stays put and the moment they
        // take a new one it moves.
        var origin = nowUtc - latency;
        lock (_gate)
        {
            if (_lastOrigin.TryGetValue(member, out var previous)
                && (origin - previous).Duration() < TimeSpan.FromSeconds(1))
            {
                return;
            }

            if (_lastOrigin.Count >= 32 && !_lastOrigin.ContainsKey(member))
            {
                // A room holds sixteen. Past that somebody is inventing names, and the tally is
                // worth less than the memory it would take to keep.
                _lastOrigin.Clear();
            }

            _lastOrigin[member] = origin;
            _samples.Enqueue(latency);
            if (_samples.Count > MaximumSamples)
            {
                _samples.Dequeue();
            }

            _delivered++;
        }
    }

    /// <summary>Forgets what it has seen, for a member list that is no longer the same room.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _samples.Clear();
            _lastOrigin.Clear();
            _delivered = 0;
        }
    }

    /// <summary>What the recent deliveries measured, or nothing where none have arrived.</summary>
    public GroupPositionLatencySnapshot Current
    {
        get
        {
            lock (_gate)
            {
                if (_samples.Count == 0)
                {
                    return GroupPositionLatencySnapshot.None;
                }

                var ordered = _samples.OrderBy(sample => sample).ToArray();
                return new(
                    _delivered,
                    ordered.Length,
                    Percentile(ordered, 0.50),
                    Percentile(ordered, 0.95),
                    _samples.Last());
            }
        }
    }

    /// <summary>Nearest rank, because there are tens of samples rather than thousands.</summary>
    private static TimeSpan Percentile(IReadOnlyList<TimeSpan> ordered, double fraction)
    {
        var rank = (int)Math.Ceiling(fraction * ordered.Count) - 1;
        return ordered[Math.Clamp(rank, 0, ordered.Count - 1)];
    }
}

/// <summary>What the last few squadmate positions took to arrive.</summary>
/// <param name="Delivered">How many distinct screenshots have been timed this session.</param>
/// <param name="SampleCount">How many of them the median and the tail were taken from.</param>
public sealed record GroupPositionLatencySnapshot(
    long Delivered,
    int SampleCount,
    TimeSpan Median,
    TimeSpan Slowest95,
    TimeSpan Last)
{
    public static GroupPositionLatencySnapshot None { get; } =
        new(0, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

    public bool HasSamples => SampleCount > 0;

    /// <summary>The measurement in one line, for diagnostics.</summary>
    public string Describe() => HasSamples
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"{Seconds(Median)}s median, {Seconds(Slowest95)}s at p95, over {SampleCount} of {Delivered}")
        : "no squadmate positions timed yet";

    private static string Seconds(TimeSpan elapsed) =>
        elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);
}
