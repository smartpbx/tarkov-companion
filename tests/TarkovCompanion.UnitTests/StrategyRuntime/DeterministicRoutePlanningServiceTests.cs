using TarkovCompanion.Application.Services.Strategy;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Strategy;
using TrafficRaidPhase = TarkovCompanion.Core.Abstractions.V2.RaidPhase;

namespace TarkovCompanion.UnitTests.StrategyRuntime;

public sealed class DeterministicRoutePlanningServiceTests
{
    private const string MapId = "customs";

    private static readonly DateTimeOffset PlannedUtc =
        new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProducesAllFiveModesWithTheirOwnDeterministicObjective()
    {
        var request = Request(CompleteTopology(), AllPressures());

        var result = new DeterministicRoutePlanningService().Plan(request);

        Assert.Equal(DeterministicRoutePlanningService.PolicyVersion, result.PlanningPolicyVersion);
        Assert.Equal(
            [
                RoutePlanningMode.Fastest,
                RoutePlanningMode.LowerExpectedContact,
                RoutePlanningMode.Quest,
                RoutePlanningMode.Loot,
                RoutePlanningMode.TeamRegroup,
            ],
            result.Alternatives.Select(alternative => alternative.Mode));
        Assert.Equal("fast", Middle(result, RoutePlanningMode.Fastest));
        Assert.Equal("quiet", Middle(result, RoutePlanningMode.LowerExpectedContact));
        Assert.Equal("quest", Middle(result, RoutePlanningMode.Quest));
        Assert.Equal("loot", Middle(result, RoutePlanningMode.Loot));
        Assert.Equal("group", Middle(result, RoutePlanningMode.TeamRegroup));
        Assert.All(result.Alternatives, alternative =>
        {
            Assert.Equal(RoutePlanShape.PrecisePath, alternative.Shape);
            Assert.Contains("Time:", alternative.TradeoffSummary, StringComparison.Ordinal);
            Assert.Contains("Expected contact:", alternative.TradeoffSummary, StringComparison.Ordinal);
            Assert.Contains("Coverage:", alternative.TradeoffSummary, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(alternative.WhyDifferent));
        });
    }

    [Fact]
    public void InputOrderCannotChangeRoutesOrExplanations()
    {
        var topology = CompleteTopology();
        var pressures = AllPressures();
        var reversedTopology = new RoutePlanningTopology(
            topology.MapId,
            topology.Waypoints.Reverse().ToArray(),
            topology.Corridors.Reverse().ToArray(),
            topology.IsComplete,
            topology.CoverageFraction,
            topology.Provenance);
        var service = new DeterministicRoutePlanningService();

        var first = service.Plan(Request(topology, pressures));
        var second = service.Plan(Request(reversedTopology, pressures.Reverse().ToArray()));

        Assert.Equal(first.PlannedUtc, second.PlannedUtc);
        Assert.Equal(first.Alternatives.Count, second.Alternatives.Count);
        for (var index = 0; index < first.Alternatives.Count; index++)
        {
            Assert.Equal(first.Alternatives[index].Mode, second.Alternatives[index].Mode);
            Assert.Equal(
                first.Alternatives[index].Waypoints.Select(waypoint => waypoint.WaypointId),
                second.Alternatives[index].Waypoints.Select(waypoint => waypoint.WaypointId));
            Assert.Equal(first.Alternatives[index].CorridorIds, second.Alternatives[index].CorridorIds);
            Assert.Equal(first.Alternatives[index].TradeoffSummary, second.Alternatives[index].TradeoffSummary);
            Assert.Equal(first.Alternatives[index].WhyDifferent, second.Alternatives[index].WhyDifferent);
        }
    }

    [Fact]
    public void IncompleteTopologyReturnsWaypointsAndNeverExposesAConnectedPath()
    {
        var complete = CompleteTopology();
        var partial = new RoutePlanningTopology(
            complete.MapId,
            complete.Waypoints,
            complete.Corridors,
            isComplete: false,
            coverageFraction: 0.55,
            complete.Provenance);

        var result = new DeterministicRoutePlanningService().Plan(Request(partial, AllPressures()));

        Assert.All(result.Alternatives, alternative =>
        {
            Assert.Equal(RoutePlanShape.WaypointsOnly, alternative.Shape);
            Assert.Empty(alternative.CorridorIds);
            Assert.True(alternative.Waypoints.Count >= 2);
            Assert.Contains("anchors only", alternative.Guidance, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(result.Warnings, warning => warning.Contains("55", StringComparison.Ordinal));
    }

    [Fact]
    public void DisconnectedPartialTopologyKeepsOnlyIndependentAnchorsAndUnknownTradeoffs()
    {
        var topology = new RoutePlanningTopology(
            MapId,
            [Waypoint("start"), Waypoint("end")],
            [],
            isComplete: false,
            coverageFraction: null,
            TopologyProvenance());

        var result = new DeterministicRoutePlanningService().Plan(Request(topology, []));

        Assert.All(result.Alternatives, alternative =>
        {
            Assert.Equal(RoutePlanShape.WaypointsOnly, alternative.Shape);
            Assert.Equal(["start", "end"], alternative.Waypoints.Select(waypoint => waypoint.WaypointId));
            Assert.Empty(alternative.CorridorIds);
            Assert.Null(alternative.Tradeoffs.EstimatedTravelSeconds);
            Assert.Null(alternative.Tradeoffs.ExpectedContactPressure);
            Assert.Contains("no path is asserted", alternative.Guidance, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(result.Warnings, warning => warning.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ACompleteButDisconnectedGraphReturnsStrategyInsteadOfInventingACorridor()
    {
        var topology = new RoutePlanningTopology(
            MapId,
            [Waypoint("start"), Waypoint("end")],
            [],
            isComplete: true,
            coverageFraction: 1,
            TopologyProvenance());

        var result = new DeterministicRoutePlanningService().Plan(Request(topology, []));

        Assert.All(result.Alternatives, alternative =>
        {
            Assert.Equal(RoutePlanShape.StrategyOnly, alternative.Shape);
            Assert.Empty(alternative.CorridorIds);
            Assert.Null(alternative.Tradeoffs.EstimatedTravelSeconds);
        });
    }

    [Fact]
    public void MissingPressureIsNotRankedAsZeroContact()
    {
        var topology = TwoRouteTopology();
        var knownQuietPressures = new[]
        {
            Pressure("quiet-a", 0.10),
            Pressure("quiet-b", 0.10),
        };

        var result = new DeterministicRoutePlanningService().Plan(Request(topology, knownQuietPressures));
        var fastest = Alternative(result, RoutePlanningMode.Fastest);
        var lowerContact = Alternative(result, RoutePlanningMode.LowerExpectedContact);

        Assert.Equal("fast", fastest.Waypoints[1].WaypointId);
        Assert.Null(fastest.Tradeoffs.ExpectedContactPressure);
        Assert.Equal(0, fastest.Tradeoffs.CorridorPressureCoverageFraction);
        Assert.Equal("quiet", lowerContact.Waypoints[1].WaypointId);
        Assert.Equal(0.10, lowerContact.Tradeoffs.ExpectedContactPressure!.Value, 6);
        Assert.Equal(1, lowerContact.Tradeoffs.CorridorPressureCoverageFraction);
        Assert.Contains(result.Warnings, warning => warning.Contains("not treated as quiet", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PartialPressureCoverageSuppressesTheRouteRiskNumber()
    {
        var topology = SingleRouteTopology(isComplete: true, coverageFraction: 1);
        var result = new DeterministicRoutePlanningService().Plan(Request(
            topology,
            [Pressure("only-a", 0.25)]));

        var fastest = Alternative(result, RoutePlanningMode.Fastest);

        Assert.Equal(0.5, fastest.Tradeoffs.CorridorPressureCoverageFraction, 6);
        Assert.Null(fastest.Tradeoffs.ExpectedContactPressure);
        Assert.Equal(["only-b"], fastest.Tradeoffs.MissingPressureCorridorIds);
        Assert.Contains("unknown", fastest.TradeoffSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownModelCoverageStaysUnknownWhileExactEvidenceIsRetained()
    {
        var first = Pressure("only-a", 0.25, modelCoverageFraction: null);
        var second = Pressure("only-b", 0.50, modelCoverageFraction: null);
        var result = new DeterministicRoutePlanningService().Plan(Request(
            SingleRouteTopology(isComplete: true, coverageFraction: 1),
            [first, second]));

        var route = Alternative(result, RoutePlanningMode.Fastest);

        Assert.Null(route.Tradeoffs.MinimumModelCoverageFraction);
        Assert.NotNull(route.Tradeoffs.ExpectedContactPressure);
        Assert.Contains("model coverage unknown", route.TradeoffSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Same(first, route.PressureEvidence[0]);
        Assert.Same(second, route.PressureEvidence[1]);
        Assert.Equal("traffic-model-7", route.PressureEvidence[0].Estimate.Provenance.Producer.ModelVersion);
        Assert.Equal(PlannedUtc.AddDays(-2), route.PressureEvidence[0].Estimate.Provenance.DataThroughUtc);
        Assert.Equal(0.73, route.PressureEvidence[0].Estimate.Provenance.Confidence.Score!.Value, 6);
    }

    [Fact]
    public void PlanningUsesOnlyPressureFromTheRequestedRaidPhase()
    {
        var topology = SingleRouteTopology(isComplete: true, coverageFraction: 1);
        var result = new DeterministicRoutePlanningService().Plan(Request(
            topology,
            [
                Pressure("only-a", 0.10, phase: TrafficRaidPhase.Early),
                Pressure("only-b", 0.10, phase: TrafficRaidPhase.Early),
                Pressure("only-a", 0.80, phase: TrafficRaidPhase.Mid),
                Pressure("only-b", 0.80, phase: TrafficRaidPhase.Mid),
            ],
            TrafficRaidPhase.Mid));

        var route = Alternative(result, RoutePlanningMode.Fastest);

        Assert.Equal(0.80, route.Tradeoffs.ExpectedContactPressure!.Value, 6);
        Assert.All(
            route.PressureEvidence,
            intelligence => Assert.Equal(TrafficRaidPhase.Mid, intelligence.Estimate.Value!.Phase));
    }

    [Fact]
    public void RequestRejectsConflictingOrMismatchedPressureInputs()
    {
        var topology = SingleRouteTopology(isComplete: true, coverageFraction: 1);

        Assert.Throws<ArgumentException>(() => Request(
            topology,
            [Pressure("only-a", 0.2), Pressure("only-a", 0.4)]));
        Assert.Throws<ArgumentException>(() => Request(
            topology,
            [Pressure("missing", 0.2)]));
        Assert.Throws<ArgumentException>(() => Request(
            topology,
            [Pressure("only-a", 0.2, mapId: "woods")]));
    }

    [Fact]
    public void ContractsRejectHostileBoundsAndContradictoryCoverage()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RoutePlanningWaypoint("bad", "Bad", new MapPoint(double.NaN, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RoutePlanningCorridor("bad", "start", "end", double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RoutePlanningWaypoint("bad", "Bad", new MapPoint(0, 0), lootOpportunity: 1.01));
        Assert.Throws<ArgumentException>(() => new RoutePlanningTopology(
            MapId,
            [Waypoint("start"), Waypoint("end")],
            [],
            isComplete: true,
            coverageFraction: null,
            TopologyProvenance()));
        Assert.Throws<ArgumentException>(() => new RoutePlanningTopology(
            MapId,
            [Waypoint("start"), Waypoint("end")],
            [],
            isComplete: false,
            coverageFraction: 1,
            TopologyProvenance()));

        var tooMany = Enumerable.Range(0, RoutePlanningBounds.MaximumWaypoints + 1)
            .Select(index => Waypoint($"node-{index}"))
            .ToArray();
        Assert.Throws<ArgumentException>(() => new RoutePlanningTopology(
            MapId,
            tooMany,
            [],
            isComplete: false,
            coverageFraction: null,
            TopologyProvenance()));
    }

    [Fact]
    public void ContractCollectionsCannotBeMutatedAfterValidation()
    {
        var waypoints = new[] { Waypoint("start"), Waypoint("end") };
        var corridors = new[] { Corridor("only", "start", "end", 30) };
        var pressures = new[] { Pressure("only", 0.2) };
        var topology = new RoutePlanningTopology(
            MapId,
            waypoints,
            corridors,
            isComplete: true,
            coverageFraction: 1,
            TopologyProvenance());
        var request = Request(topology, pressures);

        waypoints[0] = Waypoint("changed");
        corridors[0] = Corridor("changed", "start", "end", 40);
        pressures[0] = Pressure("only", 0.9);

        Assert.Equal("start", topology.Waypoints[0].WaypointId);
        Assert.Equal("only", topology.Corridors[0].CorridorId);
        Assert.Equal(0.2, request.CorridorPressures[0].Estimate.Value!.RelativePressure, 6);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<RoutePlanningWaypoint>)topology.Waypoints)[0] = Waypoint("mutated"));
    }

    private static RouteAlternative Alternative(RoutePlanBundle bundle, RoutePlanningMode mode) =>
        Assert.Single(bundle.Alternatives, alternative => alternative.Mode == mode);

    private static string Middle(RoutePlanBundle bundle, RoutePlanningMode mode) =>
        Alternative(bundle, mode).Waypoints[1].WaypointId;

    private static RoutePlanningRequest Request(
        RoutePlanningTopology topology,
        IReadOnlyList<ModelledIntelligence<RouteCorridorPressure>> pressures,
        TrafficRaidPhase phase = TrafficRaidPhase.Mid) => new(
        topology,
        "start",
        "end",
        phase,
        pressures,
        PlannedUtc);

    private static RoutePlanningTopology CompleteTopology()
    {
        var waypoints = new[]
        {
            Waypoint("start"),
            Waypoint("fast"),
            Waypoint("quiet"),
            Waypoint("quest", quest: 1),
            Waypoint("loot", loot: 1),
            Waypoint("group", regroup: 1),
            Waypoint("end"),
        };
        var corridors = new[]
        {
            Corridor("fast-a", "start", "fast", 30),
            Corridor("fast-b", "fast", "end", 30),
            Corridor("quiet-a", "start", "quiet", 50),
            Corridor("quiet-b", "quiet", "end", 50),
            Corridor("quest-a", "start", "quest", 45),
            Corridor("quest-b", "quest", "end", 45),
            Corridor("loot-a", "start", "loot", 46),
            Corridor("loot-b", "loot", "end", 46),
            Corridor("group-a", "start", "group", 44),
            Corridor("group-b", "group", "end", 44),
        };
        return new(MapId, waypoints, corridors, true, 1, TopologyProvenance());
    }

    private static RoutePlanningTopology TwoRouteTopology() => new(
        MapId,
        [Waypoint("start"), Waypoint("fast"), Waypoint("quiet"), Waypoint("end")],
        [
            Corridor("fast-a", "start", "fast", 30),
            Corridor("fast-b", "fast", "end", 30),
            Corridor("quiet-a", "start", "quiet", 50),
            Corridor("quiet-b", "quiet", "end", 50),
        ],
        true,
        1,
        TopologyProvenance());

    private static RoutePlanningTopology SingleRouteTopology(bool isComplete, double? coverageFraction) => new(
        MapId,
        [Waypoint("start"), Waypoint("middle"), Waypoint("end")],
        [
            Corridor("only-a", "start", "middle", 30),
            Corridor("only-b", "middle", "end", 30),
        ],
        isComplete,
        coverageFraction,
        TopologyProvenance());

    private static IReadOnlyList<ModelledIntelligence<RouteCorridorPressure>> AllPressures() =>
    [
        Pressure("fast-a", 0.90),
        Pressure("fast-b", 0.90),
        Pressure("quiet-a", 0.10),
        Pressure("quiet-b", 0.10),
        Pressure("quest-a", 0.40),
        Pressure("quest-b", 0.40),
        Pressure("loot-a", 0.40),
        Pressure("loot-b", 0.40),
        Pressure("group-a", 0.40),
        Pressure("group-b", 0.40),
    ];

    private static RoutePlanningWaypoint Waypoint(
        string id,
        double quest = 0,
        double loot = 0,
        double regroup = 0) => new(
        id,
        id,
        new MapPoint(id.Length, id.Length * 2),
        quest,
        loot,
        regroup);

    private static RoutePlanningCorridor Corridor(
        string id,
        string from,
        string to,
        double travelSeconds) => new(id, from, to, travelSeconds);

    private static ModelledIntelligence<RouteCorridorPressure> Pressure(
        string corridorId,
        double pressure,
        double? modelCoverageFraction = 0.80,
        string mapId = MapId,
        TrafficRaidPhase phase = TrafficRaidPhase.Mid)
    {
        var inputProvenance = new EvidenceProvenance(
            EvidenceSourceClass.HistoricalAggregate,
            "fixture://traffic-aggregate",
            PlannedUtc.AddDays(-2),
            new EvidenceConfidence(EvidenceConfidenceKind.CalibratedEstimate, 0.78, "aggregate-calibration-3"),
            new ProducerIdentity("fixture-aggregate", "3.0", "aggregate-3"),
            PlannedUtc.AddDays(-3),
            PlannedUtc.AddDays(-2),
            new EvidenceCoverage(400, 0.85, "Four hundred governed historical samples"));
        var input = new IntelligenceInputReference(
            $"traffic-aggregate-{corridorId}",
            IntelligenceInputKind.HistoricalAggregate,
            inputProvenance);
        var modelProvenance = new EvidenceProvenance(
            EvidenceSourceClass.ModelledEstimate,
            "fixture://traffic-model",
            PlannedUtc.AddDays(-1),
            new EvidenceConfidence(EvidenceConfidenceKind.CalibratedEstimate, 0.73, "traffic-calibration-7"),
            new ProducerIdentity("fixture-traffic-model", "7.0", "traffic-model-7"),
            PlannedUtc.AddDays(-2),
            PlannedUtc.AddDays(-1),
            new EvidenceCoverage(320, modelCoverageFraction, "Governed corridor model coverage"),
            inputs: [inputProvenance]);
        var value = new EvidencedValue<RouteCorridorPressure>(
            $"traffic.pressure.{corridorId}",
            new RouteCorridorPressure(mapId, corridorId, phase, pressure),
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current),
            modelProvenance);
        return new(
            $"traffic-{mapId}-{corridorId}-{phase}",
            value,
            [input],
            "Historical relative pressure for a static map corridor.");
    }

    private static EvidenceProvenance TopologyProvenance() => new(
        EvidenceSourceClass.PublicStructuredData,
        "fixture://map-topology",
        PlannedUtc.AddDays(-4),
        EvidenceConfidence.Certain,
        new ProducerIdentity("fixture-map-catalog", "2.0"));
}
