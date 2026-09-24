using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.Application.Services.Planning;

/// <summary>
/// Orders exactly placed objectives by nearest neighbour from a player-selected origin, then
/// removes avoidable detours with 2-opt. Distances are straight lines over the reviewed map
/// transform; walls, terrain, safety and live conditions are deliberately outside this planner.
/// </summary>
public static class ObjectiveRoutePlanner
{
    private const double ImprovementEpsilon = 1e-9;

    public static ObjectiveRouteBundle Plan(
        MapScenePoint start,
        string startLabel,
        IEnumerable<ObjectiveRouteStop> stops,
        double unitsPerMetre)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startLabel);
        ArgumentNullException.ThrowIfNull(stops);
        if (!double.IsFinite(unitsPerMetre) || unitsPerMetre <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitsPerMetre), "Map units per metre must be finite and positive.");
        }

        var remaining = stops.OrderBy(stop => stop.ObjectiveId, StringComparer.Ordinal).ToList();
        if (remaining.Any(stop => string.IsNullOrWhiteSpace(stop.ObjectiveId) || string.IsNullOrWhiteSpace(stop.Label)))
        {
            throw new ArgumentException("Every route stop requires an objective id and label.", nameof(stops));
        }

        if (remaining.Select(stop => stop.ObjectiveId).Distinct(StringComparer.Ordinal).Count() != remaining.Count)
        {
            throw new ArgumentException("A route cannot contain the same objective twice.", nameof(stops));
        }

        var nearest = new List<ObjectiveRouteStop>(remaining.Count);
        var at = start;
        while (remaining.Count > 0)
        {
            var next = remaining
                .OrderBy(stop => DistanceSquared(at, stop.At))
                .ThenBy(stop => stop.ObjectiveId, StringComparer.Ordinal)
                .First();
            nearest.Add(next);
            remaining.Remove(next);
            at = next.At;
        }

        var order = nearest.ToList();
        var improved = Improve(start, order);
        var seedPositions = nearest
            .Select((stop, index) => (stop.ObjectiveId, index))
            .ToDictionary(pair => pair.ObjectiveId, pair => pair.index, StringComparer.Ordinal);
        var steps = new List<ObjectiveRouteStep>(order.Count);
        at = start;
        var total = 0d;
        for (var index = 0; index < order.Count; index++)
        {
            var stop = order[index];
            var leg = Math.Sqrt(DistanceSquared(at, stop.At)) / unitsPerMetre;
            total += leg;
            var previous = index == 0 ? startLabel : order[index - 1].Label;
            var seedIndex = seedPositions[stop.ObjectiveId];
            var keepsNearestNeighbourLeg = seedIndex == 0
                ? index == 0
                : index > 0 && order[index - 1].ObjectiveId == nearest[seedIndex - 1].ObjectiveId;
            // A 2-opt reversal changes the neighbours of stops that keep their step number, so
            // "moved" is claimed only for a stop whose number really changed.
            var reason = keepsNearestNeighbourLeg
                ? $"Nearest unvisited objective from {previous}"
                : seedIndex != index
                    ? $"2-opt moved it from step {seedIndex + 1} to shorten the whole route"
                    : $"Still step {index + 1}; 2-opt reordered the stops before it";
            steps.Add(new(index + 1, stop.ObjectiveId, stop.Label, stop.At, leg, reason)
            {
                FloorIds = stop.FloorIds,
            });
            at = stop.At;
        }

        return new(startLabel, total, steps, improved) { PlannerVersion = PlannerVersions.ObjectiveRoute };
    }

    private static bool Improve(MapScenePoint start, List<ObjectiveRouteStop> order)
    {
        var improved = false;
        while (true)
        {
            var changed = false;
            for (var first = 0; first < order.Count - 1 && !changed; first++)
            {
                for (var last = first + 1; last < order.Count; last++)
                {
                    var before = first == 0 ? start : order[first - 1].At;
                    var current = Distance(before, order[first].At);
                    var proposed = Distance(before, order[last].At);
                    if (last + 1 < order.Count)
                    {
                        current += Distance(order[last].At, order[last + 1].At);
                        proposed += Distance(order[first].At, order[last + 1].At);
                    }

                    if (proposed + ImprovementEpsilon >= current)
                    {
                        continue;
                    }

                    order.Reverse(first, last - first + 1);
                    changed = true;
                    improved = true;
                    break;
                }
            }

            if (!changed)
            {
                return improved;
            }
        }
    }

    private static double Distance(MapScenePoint left, MapScenePoint right) =>
        Math.Sqrt(DistanceSquared(left, right));

    private static double DistanceSquared(MapScenePoint left, MapScenePoint right) =>
        Math.Pow(right.X - left.X, 2) + Math.Pow(right.Y - left.Y, 2);
}
