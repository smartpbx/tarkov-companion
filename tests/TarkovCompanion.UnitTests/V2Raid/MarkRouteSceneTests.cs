using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>#290: a tablet route is one line through its stops, in step order, on its own map only.</summary>
public sealed class MarkRouteSceneTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_route_is_one_line_through_its_stops_in_step_order()
    {
        var route = Guid.NewGuid();
        RaidMark[] marks =
        [
            Stop(route, 3, 30, 30),
            Stop(route, 1, 10, 10),
            Stop(route, 2, 20, 10),
            Stop(Guid.NewGuid(), 1, 50, 50), // a route of one stop is not a line
            Stop(Guid.NewGuid(), 1, 5, 5, "factory"),
            new(Guid.NewGuid(), RaidMarkKind.Waypoint, new MapMarkState("customs", null, 70, 70, null, null), NowUtc),
        ];

        var (objects, routes) = RaidCockpitViewModel.BuildMarkRoutes(marks, "customs", NowUtc);

        Assert.Equal(1, routes);
        var line = Assert.Single(objects);
        Assert.Equal(MapSceneObjectKind.Route, line.Kind);
        Assert.Equal(MapSceneGeometryKind.Line, line.Geometry.Kind);
        Assert.Equal([(10d, 10d), (20d, 10d), (30d, 30d)], line.Geometry.Points.Select(point => (point.X, point.Y)));
    }

    private static RaidMark Stop(Guid route, int step, double x, double y, string mapId = "customs") =>
        new(Guid.NewGuid(), RaidMarkKind.Waypoint, new MapMarkState(mapId, null, x, y, null, null), NowUtc)
        {
            Route = new RaidMarkRoute(route, step),
        };
}
