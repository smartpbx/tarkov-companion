using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Strategy;

namespace TarkovCompanion.Application.Services.Strategy;

public sealed class RoutePlanner : IRoutePlanner
{
    public PlannedRoute Plan(RouteGraph graph, string startNodeId, string endNodeId, RouteMode mode, RaidPhase phase)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(startNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endNodeId);
        var nodes = graph.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        if (!nodes.TryGetValue(startNodeId, out var start) || !nodes.ContainsKey(endNodeId))
        {
            return Unavailable(mode, "The requested start or destination is absent from the navigation graph.");
        }

        var costs = nodes.Keys.ToDictionary(key => key, _ => double.PositiveInfinity, StringComparer.OrdinalIgnoreCase);
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var queue = new PriorityQueue<string, double>();
        costs[start.Id] = 0;
        queue.Enqueue(start.Id, 0);

        while (queue.TryDequeue(out var currentId, out var dequeuedCost))
        {
            if (dequeuedCost > costs[currentId])
            {
                continue;
            }

            if (string.Equals(currentId, endNodeId, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            foreach (var edge in graph.Edges.Where(edge =>
                         string.Equals(edge.FromNodeId, currentId, StringComparison.OrdinalIgnoreCase)
                         && edge.Phases.Contains(phase)
                         && nodes.ContainsKey(edge.ToNodeId)))
            {
                var edgeCost = Cost(edge, nodes[edge.ToNodeId], mode);
                if (!double.IsFinite(edgeCost) || edgeCost < 0)
                {
                    continue;
                }

                var candidateCost = dequeuedCost + edgeCost;
                if (candidateCost >= costs[edge.ToNodeId])
                {
                    continue;
                }

                costs[edge.ToNodeId] = candidateCost;
                previous[edge.ToNodeId] = currentId;
                queue.Enqueue(edge.ToNodeId, candidateCost);
            }
        }

        if (double.IsPositiveInfinity(costs[endNodeId]))
        {
            var scope = graph.IsComplete ? "No traversable route exists for this raid phase." : "The incomplete graph cannot connect these points.";
            return new(mode, [start], 0, false, $"{scope} Use the map and in-game judgment; no route is guessed.", new Confidence(0.15));
        }

        var path = Reconstruct(nodes, previous, start.Id, endNodeId);
        var precise = graph.IsComplete;
        var guidance = precise
            ? $"{mode} route calculated from the static navigation graph."
            : $"{mode} route uses only known graph segments; missing paths or hazards may change the best route.";
        return new(
            mode,
            path,
            costs[endNodeId],
            precise,
            guidance,
            precise ? new Confidence(0.80) : new Confidence(0.45));
    }

    private static double Cost(RouteEdge edge, RouteNode destination, RouteMode mode)
    {
        if (edge.TravelCost < 0 || edge.RiskCost < 0 || edge.LootUtility < 0)
        {
            return double.NaN;
        }

        var destinationKind = destination.Kind;
        return mode switch
        {
            RouteMode.Fastest => edge.TravelCost,
            RouteMode.Safest => edge.TravelCost + (3 * edge.RiskCost),
            RouteMode.Quest => Math.Max(0.01, edge.TravelCost + edge.RiskCost - (destinationKind.Equals("quest", StringComparison.OrdinalIgnoreCase) ? 1 : 0)),
            RouteMode.Loot => Math.Max(0.01, edge.TravelCost + edge.RiskCost - (0.50 * edge.LootUtility)),
            RouteMode.AvoidPvp => edge.TravelCost + (5 * edge.RiskCost),
            _ => edge.TravelCost,
        };
    }

    private static IReadOnlyList<RouteNode> Reconstruct(
        IReadOnlyDictionary<string, RouteNode> nodes,
        IReadOnlyDictionary<string, string> previous,
        string startNodeId,
        string endNodeId)
    {
        var path = new List<RouteNode> { nodes[endNodeId] };
        var current = endNodeId;
        while (!string.Equals(current, startNodeId, StringComparison.OrdinalIgnoreCase))
        {
            current = previous[current];
            path.Add(nodes[current]);
        }

        path.Reverse();
        return path;
    }

    private static PlannedRoute Unavailable(RouteMode mode, string guidance) =>
        new(mode, [], 0, false, guidance, Confidence.Unknown);
}
