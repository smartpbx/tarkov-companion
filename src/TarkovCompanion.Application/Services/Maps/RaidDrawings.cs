using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>
/// [#286] A freehand line the player drew on the Raid map, in plan (Leaflet) units.
/// </summary>
/// <remarks>
/// Plan units rather than screen pixels, so a line stays where it was drawn when the map is
/// panned, zoomed or turned, and a floor rather than the current view, so it shows on the floor it
/// was drawn on and on no other. The player's own annotation on the companion's map only: nothing
/// here reads or draws anything in the game.
/// </remarks>
public sealed record RaidDrawing(
    Guid Id,
    string MapId,
    string? FloorId,
    IReadOnlyList<MapPoint> Points,
    DateTimeOffset CreatedUtc,
    RaidMarkScope Scope,
    RaidMarkLifetime Lifetime,
    DateTimeOffset? ExpiresUtc);

/// <summary>The bounds every drawing is held to, on this machine and on the relay.</summary>
public static class RaidDrawingLimits
{
    /// <summary>The most points one line keeps after simplifying, and the most the relay accepts.</summary>
    public const int MaximumPoints = 200;

    /// <summary>The most lines one member shares, and the most the relay accepts from one member.</summary>
    public const int MaximumSharedStrokes = 20;

    /// <summary>
    /// The most points one member shares across all its lines.
    /// </summary>
    /// <remarks>
    /// Twenty lines of two hundred points is 4,000 points, about 56 KB of JSON, and the relay
    /// refuses any publish over 32 KB — the member's position included. A thousand points is
    /// about 14 KB, which leaves the rest of a full publish room; past it the oldest lines stay
    /// on this map and are not sent.
    /// </remarks>
    public const int MaximumSharedPoints = 1000;

    /// <summary>The most lines kept here; past it the oldest goes, like the relay's marks.</summary>
    public const int MaximumStoredStrokes = 40;

    /// <summary>
    /// How far, in plan units, a simplified line may stray from what the hand drew. Plan units
    /// are Leaflet units on a map a few hundred across, so this is well under a marker's width.
    /// </summary>
    public const double SimplifyTolerance = 0.75;
}

/// <summary>Ramer–Douglas–Peucker, with a hard cap on what is left.</summary>
public static class StrokeSimplifier
{
    /// <summary>
    /// The drawn line with the points that add nothing taken out, and never more than
    /// <paramref name="maximumPoints"/>.
    /// </summary>
    /// <remarks>
    /// A pointer reports a hundred moves a second, so a three-second scribble is three hundred
    /// points, nearly all of them on the straight parts. Douglas–Peucker keeps the corners. When
    /// even that is too many (a long wiggly line), the tolerance doubles until it fits; the ends
    /// are always kept, so a line never gets shorter than the player drew it.
    /// </remarks>
    public static IReadOnlyList<MapPoint> Simplify(IReadOnlyList<MapPoint> points, double tolerance, int maximumPoints)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPoints, 2);
        var finite = Deduplicate(points);
        if (finite.Count <= 2)
        {
            return finite;
        }

        var epsilon = Math.Max(0, tolerance);
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var kept = DouglasPeucker(finite, epsilon);
            if (kept.Count <= maximumPoints)
            {
                return kept;
            }

            epsilon = epsilon <= 0 ? 0.01 : epsilon * 2;
        }

        // Unreachable for a finite line (a large enough tolerance keeps only the ends), but a
        // bound is a bound: evenly spaced, ends kept.
        var step = (finite.Count - 1) / (double)(maximumPoints - 1);
        return [.. Enumerable.Range(0, maximumPoints).Select(index => finite[(int)Math.Round(index * step)])];
    }

    private static List<MapPoint> Deduplicate(IReadOnlyList<MapPoint> points)
    {
        var result = new List<MapPoint>(points.Count);
        foreach (var point in points)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                continue;
            }

            if (result.Count == 0 || result[^1] != point)
            {
                result.Add(point);
            }
        }

        return result;
    }

    private static List<MapPoint> DouglasPeucker(List<MapPoint> points, double epsilon)
    {
        var keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;
        // An explicit stack, not recursion: a spiral of thousands of points would recurse that deep.
        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            var farthest = -1;
            var farthestDistance = epsilon;
            for (var index = first + 1; index < last; index++)
            {
                var distance = DistanceToSegment(points[index], points[first], points[last]);
                if (distance > farthestDistance)
                {
                    farthest = index;
                    farthestDistance = distance;
                }
            }

            if (farthest < 0)
            {
                continue;
            }

            keep[farthest] = true;
            stack.Push((first, farthest));
            stack.Push((farthest, last));
        }

        var result = new List<MapPoint>();
        for (var index = 0; index < points.Count; index++)
        {
            if (keep[index])
            {
                result.Add(points[index]);
            }
        }

        return result;
    }

    internal static double DistanceToSegment(MapPoint point, MapPoint start, MapPoint end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared == 0)
        {
            return Math.Sqrt(Square(point.X - start.X) + Square(point.Y - start.Y));
        }

        var t = Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared, 0, 1);
        return Math.Sqrt(Square(point.X - (start.X + t * dx)) + Square(point.Y - (start.Y + t * dy)));
    }

    private static double Square(double value) => value * value;
}

/// <summary>
/// [#286] The lines drawn on this machine's Raid map, for this session.
/// </summary>
/// <remarks>
/// Kept in memory, not on disk: a drawing is tonight's plan scribbled over tonight's map, and the
/// lifetimes a player picks for one ("this raid", minutes) are all shorter than a session.
/// "Until removed" means until removed or the companion closes. The scope and lifetime model is
/// the marks' own (#289), so "Just me" and "Squad" mean the same thing on a line as on a pin.
/// </remarks>
public sealed class RaidDrawingStore
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private List<RaidDrawing> _drawings = [];

    public RaidDrawingStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Every line, oldest first.</summary>
    public IReadOnlyList<RaidDrawing> Drawings
    {
        get
        {
            lock (_gate)
            {
                return _drawings;
            }
        }
    }

    /// <summary>Raised after a line is added, removed, changed or expires.</summary>
    public event Action? Changed;

    /// <summary>
    /// Keeps a drawn line, simplified and bounded; null when there is not a line to keep (a click,
    /// or points that are not numbers).
    /// </summary>
    public RaidDrawing? Add(
        string mapId,
        string? floorId,
        IReadOnlyList<MapPoint> points,
        RaidMarkScope scope,
        RaidMarkLifetime lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        ArgumentNullException.ThrowIfNull(points);
        var simplified = StrokeSimplifier.Simplify(points, RaidDrawingLimits.SimplifyTolerance, RaidDrawingLimits.MaximumPoints);
        if (simplified.Count < 2)
        {
            return null;
        }

        var now = _clock.GetUtcNow();
        var drawing = new RaidDrawing(
            Guid.NewGuid(),
            mapId,
            floorId,
            simplified,
            now,
            scope,
            lifetime,
            RaidMarkLifetimes.ExpiresUtc(lifetime, now));
        Mutate(list =>
        {
            list.Add(drawing);
            if (list.Count > RaidDrawingLimits.MaximumStoredStrokes)
            {
                list.RemoveRange(0, list.Count - RaidDrawingLimits.MaximumStoredStrokes);
            }

            return true;
        });
        return drawing;
    }

    public bool Remove(Guid id) => Mutate(list => list.RemoveAll(drawing => drawing.Id == id) > 0);

    /// <summary>"Clear my drawings": every line on one map, or on every map when null.</summary>
    public int Clear(string? mapId)
    {
        var removed = 0;
        Mutate(list =>
        {
            removed = list.RemoveAll(drawing => mapId is null || string.Equals(drawing.MapId, mapId, StringComparison.OrdinalIgnoreCase));
            return removed > 0;
        });
        return removed;
    }

    /// <summary>Changes who sees a line and how long it lasts; a new lifetime counts from now.</summary>
    public bool SetOptions(Guid id, RaidMarkScope scope, RaidMarkLifetime lifetime) => Mutate(list =>
    {
        var index = list.FindIndex(drawing => drawing.Id == id);
        if (index < 0 || (list[index].Scope == scope && list[index].Lifetime == lifetime))
        {
            return false;
        }

        var current = list[index];
        list[index] = current with
        {
            Scope = scope,
            Lifetime = lifetime,
            ExpiresUtc = current.Lifetime == lifetime ? current.ExpiresUtc : RaidMarkLifetimes.ExpiresUtc(lifetime, _clock.GetUtcNow()),
        };
        return true;
    });

    /// <summary>Removes every "this raid" line: the raid they belonged to is over.</summary>
    public bool EndRaid() => Mutate(list => list.RemoveAll(drawing => drawing.Lifetime == RaidMarkLifetime.ThisRaid) > 0);

    /// <summary>Removes the lines whose time is up; the host calls it on its own clock.</summary>
    public bool Expire() => Mutate(list =>
    {
        var now = _clock.GetUtcNow();
        return list.RemoveAll(drawing => drawing.ExpiresUtc is { } expires && expires <= now) > 0;
    });

    /// <summary>The soonest a line expires, so the host can wake then and not on a tick.</summary>
    public DateTimeOffset? NextExpiryUtc
    {
        get
        {
            lock (_gate)
            {
                return _drawings.Where(drawing => drawing.ExpiresUtc is not null).Min(drawing => drawing.ExpiresUtc);
            }
        }
    }

    /// <summary>
    /// The lines the squad should see: "Squad" ones, newest first up to the relay's bounds, each
    /// handed to <paramref name="locate"/> for its world points (null drops the line).
    /// </summary>
    /// <remarks>
    /// Newest first because a squad reading a map wants the line drawn a moment ago, and a line
    /// that does not fit the budget is kept here rather than cut short on the wire.
    /// </remarks>
    public static IReadOnlyList<T> SelectShared<T>(
        IReadOnlyList<RaidDrawing> drawings,
        Func<RaidDrawing, T?> locate,
        Func<T, int> pointCount)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(drawings);
        ArgumentNullException.ThrowIfNull(locate);
        ArgumentNullException.ThrowIfNull(pointCount);
        var result = new List<T>();
        var budget = RaidDrawingLimits.MaximumSharedPoints;
        foreach (var drawing in drawings.Where(drawing => drawing.Scope == RaidMarkScope.Squad).OrderByDescending(drawing => drawing.CreatedUtc))
        {
            if (result.Count >= RaidDrawingLimits.MaximumSharedStrokes)
            {
                break;
            }

            if (locate(drawing) is not { } shared)
            {
                continue;
            }

            var points = pointCount(shared);
            if (points < 2 || points > RaidDrawingLimits.MaximumPoints || points > budget)
            {
                continue;
            }

            budget -= points;
            result.Add(shared);
        }

        // Oldest first on the wire, the order they were drawn in.
        result.Reverse();
        return result;
    }

    private bool Mutate(Func<List<RaidDrawing>, bool> change)
    {
        bool changed;
        lock (_gate)
        {
            var copy = new List<RaidDrawing>(_drawings);
            changed = change(copy);
            if (changed)
            {
                _drawings = copy;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return changed;
    }
}
