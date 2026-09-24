using TarkovCompanion.Application.Services.Strategy.Prior;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>One stop of a route the player is clicking out: its step, where, and the mark it became.</summary>
public sealed record PlannedRouteStop(int Step, MapPoint At)
{
    /// <summary>The waypoint this stop was placed as, once the mark store has answered.</summary>
    public Guid? MarkId { get; init; }
}

/// <summary>
/// [#286] Route mode's route: stops clicked on the Raid map, in order, at most twelve.
/// </summary>
/// <remarks>
/// The same twelve the tablet's "Draw route" allows (#787), because both become waypoints that
/// carry one route id and a step (<see cref="RaidMarkRoute"/>), and one dashed line joins them.
/// A stop the player removes some other way (the Marks card, "This raid" ending) leaves the route
/// too, through <see cref="Forget"/>, so Undo never reaches for a mark that is already gone.
/// </remarks>
public sealed class PlannedRoute
{
    public const int MaximumStops = 12;

    private readonly List<PlannedRouteStop> _stops = [];

    public Guid RouteId { get; private set; } = Guid.NewGuid();

    public IReadOnlyList<PlannedRouteStop> Stops => _stops;

    public bool IsFull => _stops.Count >= MaximumStops;

    /// <summary>Adds a stop after the last; null once the route has <see cref="MaximumStops"/>.</summary>
    public PlannedRouteStop? Add(MapPoint at)
    {
        if (IsFull)
        {
            return null;
        }

        var stop = new PlannedRouteStop(_stops.Count == 0 ? 1 : _stops[^1].Step + 1, at);
        _stops.Add(stop);
        return stop;
    }

    /// <summary>Records which mark a stop became.</summary>
    public void Bind(int step, Guid markId)
    {
        var index = _stops.FindIndex(stop => stop.Step == step);
        if (index >= 0)
        {
            _stops[index] = _stops[index] with { MarkId = markId };
        }
    }

    /// <summary>Takes the last stop off, and returns it so its mark can go too.</summary>
    public PlannedRouteStop? Undo()
    {
        if (_stops.Count == 0)
        {
            return null;
        }

        var last = _stops[^1];
        _stops.RemoveAt(_stops.Count - 1);
        return last;
    }

    /// <summary>Empties the route; the next stop starts a new one, with a new id.</summary>
    public IReadOnlyList<PlannedRouteStop> Clear()
    {
        var removed = _stops.ToArray();
        _stops.Clear();
        RouteId = Guid.NewGuid();
        return removed;
    }

    /// <summary>Drops stops whose mark no longer exists; a stop still waiting for its mark stays.</summary>
    /// <returns>Whether anything was dropped.</returns>
    public bool Forget(Func<Guid, bool> markExists)
    {
        ArgumentNullException.ThrowIfNull(markExists);
        return _stops.RemoveAll(stop => stop.MarkId is { } id && !markExists(id)) > 0;
    }

    /// <summary>
    /// The route's length in metres, leg by leg, through a plan-to-world conversion; null when any
    /// stop cannot be placed in the world (no transform), because a partial sum would be a lie.
    /// </summary>
    public double? Metres(Func<MapPoint, (double X, double Z)?> toWorld)
    {
        ArgumentNullException.ThrowIfNull(toWorld);
        return PathMetres(_stops.Select(stop => stop.At).ToArray(), toWorld);
    }

    /// <summary>
    /// "~3–5 min" as numbers: the suggested routes' own walking estimate
    /// (<see cref="TrafficRoute.MinutesFor"/>), so one distance never gets two answers.
    /// </summary>
    public (int Low, int High)? Minutes(Func<MapPoint, (double X, double Z)?> toWorld) =>
        _stops.Count >= 2 && Metres(toWorld) is { } metres ? TrafficRoute.MinutesFor(metres) : null;

    public static double? PathMetres(IReadOnlyList<MapPoint> points, Func<MapPoint, (double X, double Z)?> toWorld)
    {
        double total = 0;
        (double X, double Z)? previous = null;
        foreach (var point in points)
        {
            if (toWorld(point) is not { } world)
            {
                return null;
            }

            if (previous is { } last)
            {
                total += Math.Sqrt(Math.Pow(world.X - last.X, 2) + Math.Pow(world.Z - last.Z, 2));
            }

            previous = world;
        }

        return total;
    }
}
