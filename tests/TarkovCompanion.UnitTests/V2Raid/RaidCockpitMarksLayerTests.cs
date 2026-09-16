using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2Raid;

public sealed class RaidCockpitMarksLayerTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoMarksForTheCurrentMapProducesNoLayer()
    {
        var marks = new[]
        {
            new RaidMark(Guid.NewGuid(), RaidMarkKind.Ping, new("other-map", null, 10, 10, null, null), NowUtc),
        };

        var (layer, objects) = RaidCockpitViewModel.BuildMarksLayer(marks, "factory", NowUtc);

        Assert.Null(layer);
        Assert.Empty(objects);
    }

    [Fact]
    public void MarksForTheCurrentMapBecomePingAndWaypointSceneObjects()
    {
        var pingId = Guid.NewGuid();
        var waypointId = Guid.NewGuid();
        var marks = new[]
        {
            new RaidMark(pingId, RaidMarkKind.Ping, new("factory", "ground", 20, 30, null, null), NowUtc),
            new RaidMark(waypointId, RaidMarkKind.Waypoint, new("factory", null, 40, 50, "Regroup here", null), NowUtc),
            new RaidMark(Guid.NewGuid(), RaidMarkKind.Ping, new("other-map", null, 1, 1, null, null), NowUtc),
        };

        var (layer, objects) = RaidCockpitViewModel.BuildMarksLayer(marks, "factory", NowUtc);

        Assert.NotNull(layer);
        Assert.Equal(2, objects.Count);

        var ping = Assert.Single(objects, item => item.Id.Value == $"mark:{pingId}");
        Assert.Equal(MapSceneObjectKind.Ping, ping.Kind);
        Assert.Equal(MapSceneTruthKind.UserAuthored, ping.Truth);
        Assert.Equal(layer!.Id, ping.LayerId);
        Assert.Equal("Ping", ping.Label);
        Assert.Equal(20, ping.Geometry.Points[0].X);
        Assert.Equal(["ground"], ping.FloorIds);

        var waypoint = Assert.Single(objects, item => item.Id.Value == $"mark:{waypointId}");
        Assert.Equal(MapSceneObjectKind.Waypoint, waypoint.Kind);
        Assert.Equal("Regroup here", waypoint.Label);
        Assert.Empty(waypoint.FloorIds);
    }
}
