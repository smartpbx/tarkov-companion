using TarkovCompanion.Application.Services.Strategy;
using TarkovCompanion.Application.Services.Strategy.Prior;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Strategy;

namespace TarkovCompanion.UnitTests.StrategyRuntime;

public sealed class TrafficRoutePlannerTests
{
    private const int Cells = 32;
    private const double Cell = 10;

    private static readonly TrafficRoutePlanner Planner = new(new RoutePlanner());

    [Fact]
    public void TheLowerContactRouteGoesRoundAHotspotTheDirectLineCrosses()
    {
        var field = Field((column, row) => column is >= 12 and <= 19 && row is >= 10 and <= 21 ? 1 : 0);
        var hotspot = new TrafficHotspot(new(160, 160), 1, "Dorms", ["high-value loot"], 60);
        var graph = Planner.BuildGraph("test-map", field, unitsPerMetre: 1);

        var plan = Planner.Plan(graph, new(15, 165), new(305, 165), [hotspot], RaidPhase.Early)!;

        Assert.NotNull(plan.Direct);
        Assert.Equal(1, plan.Direct.PeakTraffic, 6);
        Assert.Equal(0, plan.LowerContact.PeakTraffic, 6);
        Assert.True(plan.LowerContact.Metres > plan.Direct.Metres, "The detour is the longer of the two.");
        Assert.True(plan.LowerContact.MeanTraffic < plan.Direct.MeanTraffic);
        Assert.Contains(plan.Reasons, reason => reason is { Kind: TrafficRouteReasonKind.AvoidsPeak, Place: "Dorms" });
        Assert.Contains(plan.Reasons, reason => reason.Kind == TrafficRouteReasonKind.Longer && reason.Metres > reason.OtherMetres);
        // The planner V1 shipped calls a graph with no walls in it imprecise, and so does this.
        Assert.Equal(0.45, plan.Confidence.Value, 6);
    }

    [Fact]
    public void WithNothingToAvoidThereIsOneRouteAndItSaysSo()
    {
        var graph = Planner.BuildGraph("test-map", Field((_, _) => 0.1), unitsPerMetre: 1);

        var plan = Planner.Plan(graph, new(15, 165), new(305, 165), [], RaidPhase.Mid)!;

        Assert.Null(plan.Direct);
        Assert.Equal(TrafficRouteReasonKind.DirectIsLowest, plan.Reasons[0].Kind);
        Assert.DoesNotContain(plan.Reasons, reason => reason.Kind == TrafficRouteReasonKind.AvoidsPeak);
    }

    [Fact]
    public void EstimatesScaleWithDistance()
    {
        var graph = Planner.BuildGraph("test-map", Field((_, _) => 0), unitsPerMetre: 1);

        var near = Planner.Plan(graph, new(15, 165), new(155, 165), [], RaidPhase.Early)!.LowerContact;
        var far = Planner.Plan(graph, new(15, 165), new(295, 165), [], RaidPhase.Early)!.LowerContact;

        // A little over the straight line: the path runs through the centres of the cells it crosses.
        Assert.InRange(near.Metres, 140, 145);
        Assert.InRange(far.Metres / near.Metres, 1.9, 2.1);
        Assert.True(far.MinutesHigh > near.MinutesHigh);
        Assert.True(far.MinutesLow >= near.MinutesLow && far.MinutesHigh > far.MinutesLow);
    }

    [Fact]
    public void MetresFollowTheMapsScaleNotItsUnits()
    {
        var field = Field((_, _) => 0);

        var oneToOne = Planner.Plan(Planner.BuildGraph("test-map", field, 1), new(15, 165), new(295, 165), [], RaidPhase.Early)!;
        var twoUnitsAMetre = Planner.Plan(Planner.BuildGraph("test-map", field, 2), new(15, 165), new(295, 165), [], RaidPhase.Early)!;

        Assert.Equal(oneToOne.LowerContact.Metres / 2, twoUnitsAMetre.LowerContact.Metres, 3);
    }

    [Fact]
    public void AnEndOffThePlanHasNoRoute()
    {
        var graph = Planner.BuildGraph("test-map", Field((_, _) => 0), unitsPerMetre: 1);

        Assert.Null(Planner.Plan(graph, new(-50, 165), new(295, 165), [], RaidPhase.Early));
        Assert.Null(Planner.Plan(graph, new(15, 165), new(295, 5000), [], RaidPhase.Early));
    }

    [Fact]
    public void ARouteStaysOnThePlanAndEndsWhereItWasAsked()
    {
        var graph = Planner.BuildGraph("test-map", Field((column, _) => column > 28 ? 1 : 0), unitsPerMetre: 1);

        var route = Planner.Plan(graph, new(3, 3), new(318, 318), [], RaidPhase.Late)!.LowerContact;

        Assert.Equal(new MapPoint(3, 3), route.Points[0]);
        Assert.Equal(new MapPoint(318, 318), route.Points[^1]);
        Assert.All(route.Points, point => Assert.True(point.X is >= 0 and <= 320 && point.Y is >= 0 and <= 320));
    }

    private static TrafficField Field(Func<int, int, double> value) => new(
        0,
        0,
        Cell,
        Cells,
        Cells,
        [.. Enumerable.Range(0, Cells * Cells).Select(index => value(index % Cells, index / Cells))]);
}
