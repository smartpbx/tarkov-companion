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
        Assert.All(route.Steps, step => Assert.Equal(ObjectiveRouteReasonKind.NearestFrom, step.Reason.Kind));
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
        Assert.Equal(PlannerVersions.ObjectiveRoute, route.PlannerVersion);
        Assert.Equal(["A", "D", "C", "B"], route.Steps.Select(step => step.ObjectiveId));
        Assert.Equal(18.4648, route.TotalDistanceMetres, precision: 4);
        Assert.Equal(
            [
                "Nearest unvisited objective from player position",
                "2-opt moved it from step 3 to shorten the whole route",
                "2-opt moved it from step 2 to shorten the whole route",
                "Still step 4; 2-opt reordered the stops before it",
            ],
            route.Steps.Select(step => English(step.Reason)));
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

    [Fact]
    public void TwoOptNeverLengthensTheRouteAndVisitsEveryStopOnce()
    {
        var random = new Random(307);
        for (var trial = 0; trial < 50; trial++)
        {
            var stops = Enumerable.Range(0, 12)
                .Select(index => Stop($"s{index:D2}", random.Next(0, 500), random.Next(0, 500)))
                .ToArray();

            var route = ObjectiveRoutePlanner.Plan(new(250, 250), "spawn", stops, unitsPerMetre: 1);
            var seed = NearestNeighbourLength(new(250, 250), stops);

            Assert.Equal(stops.Select(stop => stop.ObjectiveId).Order(), route.Steps.Select(step => step.ObjectiveId).Order());
            Assert.Equal(Enumerable.Range(1, 12), route.Steps.Select(step => step.Number));
            Assert.True(route.TotalDistanceMetres <= seed + 1e-6);
            Assert.Equal(route.Steps.Sum(step => step.LegDistanceMetres), route.TotalDistanceMetres, precision: 6);
        }
    }

    private static double NearestNeighbourLength(MapScenePoint start, IReadOnlyList<ObjectiveRouteStop> stops)
    {
        var remaining = stops.OrderBy(stop => stop.ObjectiveId, StringComparer.Ordinal).ToList();
        var at = start;
        var total = 0d;
        while (remaining.Count > 0)
        {
            var next = remaining.MinBy(stop => Math.Pow(stop.At.X - at.X, 2) + Math.Pow(stop.At.Y - at.Y, 2))!;
            total += Math.Sqrt(Math.Pow(next.At.X - at.X, 2) + Math.Pow(next.At.Y - at.Y, 2));
            remaining.Remove(next);
            at = next.At;
        }

        return total;
    }

    private static ObjectiveRouteStop Stop(string id, double x, double y) =>
        new(id, id, new MapScenePoint(x, y));

    /// <summary>The reason as the Plan page says it in English.</summary>
    private static string English(ObjectiveRouteReason reason)
    {
        using var scope = TarkovCompanion.App.Localization.UiText.Scope(TarkovCompanion.App.Localization.UiText.Create("en", _ => { }));
        return TarkovCompanion.App.Localization.PlanText.ObjectiveRouteReason(reason);
    }
}
