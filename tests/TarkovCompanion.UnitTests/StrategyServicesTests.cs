using TarkovCompanion.Application.Services.Strategy;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Strategy;

namespace TarkovCompanion.UnitTests;

public sealed class StrategyServicesTests
{
    [Fact]
    public void SpawnInfluenceDecaysAndLateExtractAttractionIncreases()
    {
        var zones = new[]
        {
            Zone("spawn", spawn: 1, extract: 0),
            Zone("extract", spawn: 0, extract: 1),
        };
        var model = new StrategyModel();

        var early = model.Predict(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(40), zones, null, DateTimeOffset.UnixEpoch);
        var late = model.Predict(TimeSpan.FromMinutes(39), TimeSpan.FromMinutes(40), zones, null, DateTimeOffset.UnixEpoch);

        Assert.True(early.Samples[0].Score > late.Samples[0].Score);
        Assert.True(late.Samples[1].Score > early.Samples[1].Score);
        Assert.Equal(RaidPhase.Early, early.Phase);
        Assert.Equal(RaidPhase.Late, late.Phase);
        Assert.Contains("not live player data", late.Disclaimer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RiskPanelAndRotationFlowRetainNonLiveDisclaimer()
    {
        var zones = new[]
        {
            Zone("spawn", spawn: 1, extract: 0),
            new StrategyZone("middle", "middle", new MapPoint(50, 0), 20, 0, 1, 1, 0.5, 0, Provenance()),
            Zone("extract", spawn: 0, extract: 1) with { Position = new MapPoint(100, 0) },
        };
        var prediction = new StrategyModel().Predict(
            TimeSpan.FromMinutes(20),
            TimeSpan.FromMinutes(40),
            zones,
            new MapPoint(50, 0),
            DateTimeOffset.UnixEpoch);

        var panel = TrafficRiskPanelService.Create(prediction, zones);
        var rotations = RotationFlowService.Build(zones);

        Assert.Equal(3, panel.Areas.Count);
        Assert.Contains("not live player data", panel.Disclaimer, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(rotations.Rotations);
        Assert.Contains("not live player data", rotations.Disclaimer, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RouteMode.Fastest, "risk")]
    [InlineData(RouteMode.Safest, "safe")]
    [InlineData(RouteMode.Quest, "risk")]
    [InlineData(RouteMode.Loot, "safe")]
    [InlineData(RouteMode.AvoidPvp, "safe")]
    public void RouteModesApplyTheirDeclaredPreference(RouteMode mode, string expectedMiddle)
    {
        var graph = RouteFixture(isComplete: true);

        var route = new RoutePlanner().Plan(graph, "start", "end", mode, RaidPhase.Mid);

        Assert.Equal(3, route.Nodes.Count);
        Assert.Equal(expectedMiddle, route.Nodes[1].Id);
        Assert.True(route.IsPrecise);
        Assert.Equal(0.8, route.Confidence.Value, 3);
    }

    [Fact]
    public void IncompleteGraphRouteIsExplicitlyImprecise()
    {
        var route = new RoutePlanner().Plan(RouteFixture(isComplete: false), "start", "end", RouteMode.Fastest, RaidPhase.Mid);

        Assert.False(route.IsPrecise);
        Assert.Contains("only known graph segments", route.Guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0.45, route.Confidence.Value, 3);
    }

    [Fact]
    public void DisconnectedIncompleteGraphDoesNotGuess()
    {
        var graph = RouteFixture(isComplete: false) with { Edges = [] };

        var route = new RoutePlanner().Plan(graph, "start", "end", RouteMode.Safest, RaidPhase.Mid);

        Assert.False(route.IsPrecise);
        Assert.Single(route.Nodes);
        Assert.Contains("no route is guessed", route.Guidance, StringComparison.OrdinalIgnoreCase);
    }

    private static RouteGraph RouteFixture(bool isComplete)
    {
        var phases = new HashSet<RaidPhase>(Enum.GetValues<RaidPhase>());
        var nodes = new[]
        {
            new RouteNode("start", "Start", new MapPoint(0, 0), "spawn"),
            new RouteNode("risk", "Quest shortcut", new MapPoint(1, 0), "quest"),
            new RouteNode("safe", "Loot detour", new MapPoint(0, 1), "loot"),
            new RouteNode("end", "End", new MapPoint(2, 0), "extract"),
        };
        var edges = new[]
        {
            new RouteEdge("start", "risk", 1, 0.6, 0, phases),
            new RouteEdge("risk", "end", 1, 0.6, 0, phases),
            new RouteEdge("start", "safe", 2, 0.1, 4, phases),
            new RouteEdge("safe", "end", 2, 0.1, 4, phases),
        };
        return new("fixture", nodes, edges, isComplete);
    }

    private static StrategyZone Zone(string id, double spawn, double extract) =>
        new(id, id, new MapPoint(0, 0), 20, spawn, 0, 0, 0, extract, Provenance());

    private static DataProvenance Provenance() => new("fixture", DateTimeOffset.UnixEpoch);
}
