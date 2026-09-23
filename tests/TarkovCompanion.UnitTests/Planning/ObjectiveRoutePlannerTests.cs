using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.UnitTests.Planning;

public sealed class ObjectiveRoutePlannerTests
{
    [Fact]
    public void NearestNeighbourStartsAtTheChosenOriginAndExplainsEveryLeg()
    {
        var route = ObjectiveRoutePlanner.Plan(
            new(0, 0),
            "selected spawn",
            [Stop("far", 9, 0), Stop("near", 2, 0), Stop("middle", 5, 0)],
            unitsPerMetre: 1);

        Assert.Equal(["near", "middle", "far"], route.Steps.Select(step => step.ObjectiveId));
        Assert.Equal([2d, 3d, 4d], route.Steps.Select(step => step.LegDistanceMetres));
        Assert.Equal(9, route.TotalDistanceMetres);
        Assert.All(route.Steps, step => Assert.StartsWith("Nearest unvisited objective from", step.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void TwoOptShortensTheNearestNeighbourSeedWithoutClaimingOptimality()
    {
        ObjectiveRouteStop[] stops =
        [
            Stop("A", 5, 0),
            Stop("B", 5, 9),
            Stop("C", 4, 4),
            Stop("D", 8, 3),
        ];

        var route = ObjectiveRoutePlanner.Plan(new(0, 0), "player position", stops, unitsPerMetre: 1);

        Assert.True(route.ImprovedByTwoOpt);
        Assert.Equal(["A", "D", "C", "B"], route.Steps.Select(step => step.ObjectiveId));
        Assert.Equal(18.4648, route.TotalDistanceMetres, precision: 4);
        Assert.Contains(route.Steps, step => step.Reason.StartsWith("2-opt moved", StringComparison.Ordinal));
    }

    [Fact]
    public void TiesAndInputOrderProduceTheSameRoute()
    {
        var left = ObjectiveRoutePlanner.Plan(
            new(0, 0),
            "spawn",
            [Stop("b", 1, 0), Stop("a", 0, 1)],
            unitsPerMetre: 2);
        var right = ObjectiveRoutePlanner.Plan(
            new(0, 0),
            "spawn",
            [Stop("a", 0, 1), Stop("b", 1, 0)],
            unitsPerMetre: 2);

        Assert.Equal(left.TotalDistanceMetres, right.TotalDistanceMetres);
        Assert.Equal(left.Steps.ToArray(), right.Steps.ToArray());
        Assert.Equal("a", left.Steps[0].ObjectiveId);
        Assert.Equal(0.5, left.Steps[0].LegDistanceMetres);
    }

    private static ObjectiveRouteStop Stop(string id, double x, double y) =>
        new(id, id, new MapScenePoint(x, y));
}
