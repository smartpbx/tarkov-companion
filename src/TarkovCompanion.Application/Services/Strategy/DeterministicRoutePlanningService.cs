using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Strategy;

namespace TarkovCompanion.Application.Services.Strategy;

/// <summary>
/// Produces bounded, deterministic route choices from static topology and already-governed
/// corridor pressure. Missing model or topology evidence stays missing; it is never scored as a
/// quiet corridor or promoted into a connected path.
/// </summary>
public sealed class DeterministicRoutePlanningService
{
    public const string PolicyVersion = "route-planner-v2.1";

    // Unknown must sort behind the maximum known pressure. A value of one would tie with a
    // measured maximum and let corridor-id order make missing evidence look equally supported.
    private const double UnknownPressureRankingPenalty = 1.25;
    private const double LowerContactPressureWeight = 180;
    private const double ObjectivePressureWeight = 25;
    private const double TeamPressureWeight = 10;
    private const double QuestTravelDiscount = 0.65;
    private const double LootTravelDiscount = 0.65;
    private const double TeamTravelDiscount = 0.70;

    private static readonly RoutePlanningMode[] ModeOrder =
    [
        RoutePlanningMode.Fastest,
        RoutePlanningMode.LowerExpectedContact,
        RoutePlanningMode.Quest,
        RoutePlanningMode.Loot,
        RoutePlanningMode.TeamRegroup,
    ];

    public RoutePlanBundle Plan(RoutePlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var nodes = request.Topology.Waypoints.ToDictionary(
            waypoint => waypoint.WaypointId,
            StringComparer.Ordinal);
        var adjacency = request.Topology.Corridors
            .Where(corridor => corridor.AvailablePhases.Contains(request.Phase))
            .OrderBy(corridor => corridor.FromWaypointId, StringComparer.Ordinal)
            .ThenBy(corridor => corridor.ToWaypointId, StringComparer.Ordinal)
            .ThenBy(corridor => corridor.CorridorId, StringComparer.Ordinal)
            .GroupBy(corridor => corridor.FromWaypointId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        var pressures = request.CorridorPressures
            .Where(intelligence => intelligence.Estimate.Value is { } pressure && pressure.Phase == request.Phase)
            .ToDictionary(
                intelligence => intelligence.Estimate.Value!.CorridorId,
                StringComparer.Ordinal);

        var calculations = ModeOrder
            .Select(mode => FindPath(request, nodes, adjacency, pressures, mode))
            .ToArray();
        var fastest = calculations[0];
        var alternatives = calculations
            .Select(calculation => BuildAlternative(request, pressures, calculation, fastest))
            .ToArray();

        return new RoutePlanBundle(
            request.Topology.MapId,
            request.StartWaypointId,
            request.DestinationWaypointId,
            request.Phase,
            request.PlannedUtc,
            PolicyVersion,
            request.Topology.Provenance,
            alternatives,
            BuildWarnings(request, calculations, alternatives));
    }

    private static PathCalculation FindPath(
        RoutePlanningRequest request,
        IReadOnlyDictionary<string, RoutePlanningWaypoint> nodes,
        IReadOnlyDictionary<string, RoutePlanningCorridor[]> adjacency,
        IReadOnlyDictionary<string, ModelledIntelligence<RouteCorridorPressure>> pressures,
        RoutePlanningMode mode)
    {
        nodes.TryGetValue(request.StartWaypointId, out var start);
        nodes.TryGetValue(request.DestinationWaypointId, out var destination);
        if (start is null || destination is null)
        {
            var anchors = new List<RoutePlanningWaypoint>(2);
            if (start is not null)
            {
                anchors.Add(start);
            }

            if (destination is not null && !anchors.Contains(destination))
            {
                anchors.Add(destination);
            }

            return new(mode, false, anchors, [], PathFailure.MissingAnchor);
        }

        if (string.Equals(start.WaypointId, destination.WaypointId, StringComparison.Ordinal))
        {
            return new(mode, true, [start], [], PathFailure.None);
        }

        var costs = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [start.WaypointId] = 0,
        };
        var hops = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [start.WaypointId] = 0,
        };
        var previous = new Dictionary<string, RoutePlanningCorridor>(StringComparer.Ordinal);
        var queue = new PriorityQueue<RouteVisit, RouteQueuePriority>();
        long sequence = 0;
        queue.Enqueue(
            new RouteVisit(start.WaypointId, 0, 0),
            new RouteQueuePriority(0, 0, sequence++));

        while (queue.TryDequeue(out var visit, out _))
        {
            if (!costs.TryGetValue(visit.WaypointId, out var bestCost) ||
                !hops.TryGetValue(visit.WaypointId, out var bestHops) ||
                visit.Cost != bestCost ||
                visit.Hops != bestHops)
            {
                continue;
            }

            if (string.Equals(visit.WaypointId, destination.WaypointId, StringComparison.Ordinal))
            {
                break;
            }

            if (!adjacency.TryGetValue(visit.WaypointId, out var outgoing))
            {
                continue;
            }

            foreach (var corridor in outgoing)
            {
                var destinationNode = nodes[corridor.ToWaypointId];
                var pressure = pressures.TryGetValue(corridor.CorridorId, out var intelligence)
                    ? intelligence.Estimate.Value!.RelativePressure
                    : UnknownPressureRankingPenalty;
                var candidateCost = visit.Cost + Score(corridor, destinationNode, pressure, mode);
                var candidateHops = visit.Hops + 1;
                if (costs.TryGetValue(corridor.ToWaypointId, out var knownCost) &&
                    (candidateCost > knownCost ||
                     (candidateCost == knownCost && candidateHops >= hops[corridor.ToWaypointId])))
                {
                    continue;
                }

                costs[corridor.ToWaypointId] = candidateCost;
                hops[corridor.ToWaypointId] = candidateHops;
                previous[corridor.ToWaypointId] = corridor;
                queue.Enqueue(
                    new RouteVisit(corridor.ToWaypointId, candidateCost, candidateHops),
                    new RouteQueuePriority(candidateCost, candidateHops, sequence++));
            }
        }

        if (!costs.ContainsKey(destination.WaypointId))
        {
            return new(mode, false, [start, destination], [], PathFailure.Disconnected);
        }

        var routeNodes = new List<RoutePlanningWaypoint> { destination };
        var routeCorridors = new List<RoutePlanningCorridor>();
        var current = destination.WaypointId;
        while (!string.Equals(current, start.WaypointId, StringComparison.Ordinal))
        {
            if (!previous.TryGetValue(current, out var corridor) ||
                routeCorridors.Count >= RoutePlanningBounds.MaximumCorridors)
            {
                return new(mode, false, [start, destination], [], PathFailure.Disconnected);
            }

            routeCorridors.Add(corridor);
            current = corridor.FromWaypointId;
            routeNodes.Add(nodes[current]);
        }

        routeNodes.Reverse();
        routeCorridors.Reverse();
        return new(mode, true, routeNodes, routeCorridors, PathFailure.None);
    }

    private static double Score(
        RoutePlanningCorridor corridor,
        RoutePlanningWaypoint destination,
        double pressure,
        RoutePlanningMode mode) => mode switch
        {
            RoutePlanningMode.Fastest => corridor.EstimatedTravelSeconds,
            RoutePlanningMode.LowerExpectedContact =>
                corridor.EstimatedTravelSeconds + (LowerContactPressureWeight * pressure),
            RoutePlanningMode.Quest =>
                ObjectiveCost(corridor.EstimatedTravelSeconds, destination.QuestOpportunity, QuestTravelDiscount) +
                (ObjectivePressureWeight * pressure),
            RoutePlanningMode.Loot =>
                ObjectiveCost(corridor.EstimatedTravelSeconds, destination.LootOpportunity, LootTravelDiscount) +
                (ObjectivePressureWeight * pressure),
            RoutePlanningMode.TeamRegroup =>
                ObjectiveCost(corridor.EstimatedTravelSeconds, destination.TeamRegroupOpportunity, TeamTravelDiscount) +
                (TeamPressureWeight * pressure),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    // A discount rather than a negative reward keeps every edge positive, so an objective-rich
    // cycle cannot beat a simple route or invalidate Dijkstra's settled-node guarantee.
    private static double ObjectiveCost(double travelSeconds, double opportunity, double discount) =>
        Math.Max(0.001, travelSeconds * (1 - (discount * opportunity)));

    private static RouteAlternative BuildAlternative(
        RoutePlanningRequest request,
        IReadOnlyDictionary<string, ModelledIntelligence<RouteCorridorPressure>> pressures,
        PathCalculation calculation,
        PathCalculation fastest)
    {
        var shape = Shape(request.Topology, calculation);
        var tradeoffs = BuildTradeoffs(request.Topology, calculation, pressures);
        var evidence = calculation.Corridors
            .Where(corridor => pressures.ContainsKey(corridor.CorridorId))
            .Select(corridor => pressures[corridor.CorridorId])
            .ToArray();
        string[] renderableCorridors = shape == RoutePlanShape.PrecisePath
            ? calculation.Corridors.Select(corridor => corridor.CorridorId).ToArray()
            : [];

        return new RouteAlternative(
            calculation.Mode,
            shape,
            calculation.Nodes,
            renderableCorridors,
            tradeoffs,
            evidence,
            TradeoffSummary(tradeoffs),
            WhyDifferent(calculation, tradeoffs, fastest, BuildTradeoffs(request.Topology, fastest, pressures)),
            Guidance(request, calculation, shape));
    }

    private static RoutePlanShape Shape(RoutePlanningTopology topology, PathCalculation calculation)
    {
        if (calculation.Found && topology.IsComplete)
        {
            return RoutePlanShape.PrecisePath;
        }

        if (!topology.IsComplete && calculation.Nodes.Count > 0)
        {
            return RoutePlanShape.WaypointsOnly;
        }

        return RoutePlanShape.StrategyOnly;
    }

    private static RouteTradeoffs BuildTradeoffs(
        RoutePlanningTopology topology,
        PathCalculation calculation,
        IReadOnlyDictionary<string, ModelledIntelligence<RouteCorridorPressure>> pressures)
    {
        if (!calculation.Found)
        {
            return new(
                null,
                null,
                0,
                0,
                0,
                null,
                0,
                0,
                0,
                topology.IsComplete,
                topology.CoverageFraction,
                []);
        }

        var travelSeconds = calculation.Corridors.Sum(corridor => corridor.EstimatedTravelSeconds);
        var pressureEvidence = calculation.Corridors
            .Where(corridor => pressures.ContainsKey(corridor.CorridorId))
            .Select(corridor => (Corridor: corridor, Intelligence: pressures[corridor.CorridorId]))
            .ToArray();
        var missing = calculation.Corridors
            .Where(corridor => !pressures.ContainsKey(corridor.CorridorId))
            .Select(corridor => corridor.CorridorId)
            .ToArray();
        var evaluatedCount = calculation.Corridors.Count;
        var coveredCount = pressureEvidence.Length;
        var pressureCoverage = evaluatedCount == 0 ? 0 : coveredCount / (double)evaluatedCount;
        double? expectedPressure = evaluatedCount > 0 && coveredCount == evaluatedCount
            ? pressureEvidence.Sum(item =>
                    item.Intelligence.Estimate.Value!.RelativePressure * item.Corridor.EstimatedTravelSeconds) /
                travelSeconds
            : null;
        double? modelCoverage = coveredCount > 0 &&
                                pressureEvidence.All(item => item.Intelligence.Estimate.Provenance.Coverage?.Fraction is not null)
            ? pressureEvidence.Min(item => item.Intelligence.Estimate.Provenance.Coverage!.Fraction!.Value)
            : null;

        return new(
            travelSeconds,
            expectedPressure,
            evaluatedCount,
            coveredCount,
            pressureCoverage,
            modelCoverage,
            Opportunity(calculation.Nodes, waypoint => waypoint.QuestOpportunity),
            Opportunity(calculation.Nodes, waypoint => waypoint.LootOpportunity),
            Opportunity(calculation.Nodes, waypoint => waypoint.TeamRegroupOpportunity),
            topology.IsComplete,
            topology.CoverageFraction,
            missing);
    }

    private static double Opportunity(
        IReadOnlyList<RoutePlanningWaypoint> nodes,
        Func<RoutePlanningWaypoint, double> selector) =>
        nodes.Count == 0 ? 0 : nodes.Max(selector);

    private static string TradeoffSummary(RouteTradeoffs tradeoffs)
    {
        var time = tradeoffs.EstimatedTravelSeconds is { } seconds
            ? FormattableString.Invariant($"{seconds:0.#} seconds estimated")
            : "unknown";
        var pressure = tradeoffs.ExpectedContactPressure is { } expected
            ? FormattableString.Invariant($"{expected:P0} relative pressure")
            : tradeoffs.EvaluatedCorridorCount == 0
                ? "unavailable because no corridor was evaluated"
                : "unknown because one or more route corridors lack evidence";
        var modelCoverage = tradeoffs.MinimumModelCoverageFraction is { } coverage
            ? FormattableString.Invariant($"model floor {coverage:P0}")
            : tradeoffs.PressureCoveredCorridorCount == 0
                ? "no pressure model used"
                : "model coverage unknown";
        var topologyCoverage = tradeoffs.TopologyCoverageFraction is { } topology
            ? FormattableString.Invariant($"topology {topology:P0}")
            : "topology coverage unknown";

        return FormattableString.Invariant(
            $"Time: {time}. Expected contact: {pressure}. Coverage: {tradeoffs.PressureCoveredCorridorCount}/{tradeoffs.EvaluatedCorridorCount} corridors ({tradeoffs.CorridorPressureCoverageFraction:P0}); {modelCoverage}; {topologyCoverage}. Opportunities: quest {tradeoffs.QuestOpportunity:P0}, loot {tradeoffs.LootOpportunity:P0}, team regroup {tradeoffs.TeamRegroupOpportunity:P0}.");
    }

    private static string WhyDifferent(
        PathCalculation calculation,
        RouteTradeoffs tradeoffs,
        PathCalculation fastest,
        RouteTradeoffs fastestTradeoffs)
    {
        if (calculation.Mode == RoutePlanningMode.Fastest)
        {
            return calculation.Found
                ? "Baseline alternative: it minimizes estimated travel time across the known topology."
                : "Baseline alternative: the known topology cannot connect the requested anchors.";
        }

        if (!calculation.Found)
        {
            return $"{ModeName(calculation.Mode)} has no connected candidate; it remains strategy guidance rather than a guessed path.";
        }

        var samePath = calculation.Corridors
            .Select(corridor => corridor.CorridorId)
            .SequenceEqual(fastest.Corridors.Select(corridor => corridor.CorridorId), StringComparer.Ordinal);
        if (samePath)
        {
            return $"It matches the fastest known route because no alternative improves {Preference(calculation.Mode)} enough to justify extra estimated time.";
        }

        var timeTradeoff = tradeoffs.EstimatedTravelSeconds is { } currentTime &&
                           fastestTradeoffs.EstimatedTravelSeconds is { } fastestTime
            ? FormattableString.Invariant($"{Math.Abs(currentTime - fastestTime):0.#} seconds {(currentTime >= fastestTime ? "more" : "less")} estimated travel")
            : "an unknown time tradeoff";
        var pressureTradeoff = tradeoffs.ExpectedContactPressure is { } currentPressure &&
                               fastestTradeoffs.ExpectedContactPressure is { } fastestPressure
            ? FormattableString.Invariant(
                $"{Math.Abs(currentPressure - fastestPressure):P0} {(currentPressure <= fastestPressure ? "lower" : "higher")} relative contact pressure")
            : "contact pressure that cannot be compared with complete coverage";
        return $"It differs from fastest by accepting {timeTradeoff} for {pressureTradeoff} and greater {Preference(calculation.Mode)}.";
    }

    private static string Guidance(
        RoutePlanningRequest request,
        PathCalculation calculation,
        RoutePlanShape shape) => shape switch
        {
            RoutePlanShape.PrecisePath =>
                $"Use the ordered static-topology path for {ModeName(calculation.Mode)}; it contains {calculation.Corridors.Count} corridors for the {request.Phase} phase.",
            RoutePlanShape.WaypointsOnly when calculation.Found =>
                $"Topology is incomplete. Show the {calculation.Nodes.Count} ordered waypoints as anchors only; do not connect them into a claimed path.",
            RoutePlanShape.WaypointsOnly =>
                $"Topology is incomplete and does not connect the anchors. Keep the available waypoints and use {Preference(calculation.Mode)} strategy; no path is asserted.",
            _ when calculation.Failure == PathFailure.MissingAnchor =>
                "The requested start or destination is absent from the topology. Keep any known anchor and request map guidance only.",
            _ =>
                $"No traversable corridor connects the anchors in the {request.Phase} phase. Keep the destination as {Preference(calculation.Mode)} strategy guidance.",
        };

    private static IReadOnlyList<string> BuildWarnings(
        RoutePlanningRequest request,
        IReadOnlyList<PathCalculation> calculations,
        IReadOnlyList<RouteAlternative> alternatives)
    {
        var warnings = new List<string>();
        if (!request.Topology.IsComplete)
        {
            warnings.Add(request.Topology.CoverageFraction is { } coverage
                ? FormattableString.Invariant($"Topology is {coverage:P0} covered; alternatives are waypoint guidance only.")
                : "Topology coverage is unknown; alternatives are waypoint guidance only.");
        }

        if (calculations.Any(calculation => calculation.Failure == PathFailure.MissingAnchor))
        {
            warnings.Add("At least one requested route anchor is absent from the topology.");
        }
        else if (calculations.All(calculation => !calculation.Found))
        {
            warnings.Add("The selected phase has no known connected route between the requested anchors.");
        }

        if (alternatives.Any(alternative =>
                alternative.Tradeoffs.EvaluatedCorridorCount > 0 &&
                alternative.Tradeoffs.CorridorPressureCoverageFraction < 1))
        {
            warnings.Add("Expected-contact coverage is partial; unknown corridors are not treated as quiet.");
        }

        if (alternatives.SelectMany(alternative => alternative.PressureEvidence)
            .Any(intelligence => intelligence.Estimate.Provenance.Coverage?.Fraction is null))
        {
            warnings.Add("At least one used pressure model does not publish a coverage fraction.");
        }

        return warnings.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string ModeName(RoutePlanningMode mode) => mode switch
    {
        RoutePlanningMode.Fastest => "Fastest",
        RoutePlanningMode.LowerExpectedContact => "Lower expected contact",
        RoutePlanningMode.Quest => "Quest",
        RoutePlanningMode.Loot => "Loot",
        RoutePlanningMode.TeamRegroup => "Team regroup",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static string Preference(RoutePlanningMode mode) => mode switch
    {
        RoutePlanningMode.LowerExpectedContact => "lower expected contact",
        RoutePlanningMode.Quest => "quest opportunity",
        RoutePlanningMode.Loot => "loot opportunity",
        RoutePlanningMode.TeamRegroup => "team-regroup utility",
        _ => "travel-time efficiency",
    };

    private enum PathFailure
    {
        None,
        MissingAnchor,
        Disconnected,
    }

    private sealed record PathCalculation(
        RoutePlanningMode Mode,
        bool Found,
        IReadOnlyList<RoutePlanningWaypoint> Nodes,
        IReadOnlyList<RoutePlanningCorridor> Corridors,
        PathFailure Failure);

    private readonly record struct RouteVisit(string WaypointId, double Cost, int Hops);

    private readonly record struct RouteQueuePriority(double Cost, int Hops, long Sequence) : IComparable<RouteQueuePriority>
    {
        public int CompareTo(RouteQueuePriority other)
        {
            var cost = Cost.CompareTo(other.Cost);
            if (cost != 0)
            {
                return cost;
            }

            var hops = Hops.CompareTo(other.Hops);
            return hops != 0 ? hops : Sequence.CompareTo(other.Sequence);
        }
    }
}
