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

    [Fact]
    public void UnnamedWaypointsAreNumberedInPlacementOrderAndPingsAreSkippedInTheCount()
    {
        var first = new RaidMark(Guid.NewGuid(), RaidMarkKind.Waypoint, new("factory", null, 1, 1, null, null), NowUtc);
        var ping = new RaidMark(Guid.NewGuid(), RaidMarkKind.Ping, new("factory", null, 2, 2, null, null), NowUtc.AddSeconds(1));
        var second = new RaidMark(Guid.NewGuid(), RaidMarkKind.Waypoint, new("factory", null, 3, 3, null, null), NowUtc.AddSeconds(2));
        // Placed out of order to prove the pass sorts by when a mark was made, not by the order
        // it happens to be enumerated in.
        var marks = new[] { second, ping, first };

        var labeled = RaidCockpitViewModel.LabelMarksForMap(marks, "factory");

        Assert.Equal(
            [(first.Id, "1"), (ping.Id, "Ping"), (second.Id, "2")],
            labeled.Select(item => (item.Mark.Id, item.Label)));
    }

    [Fact]
    public void TheMapObjectAndTheListRowAlwaysAgreeOnTheSameLabel()
    {
        var marks = new[]
        {
            new RaidMark(Guid.NewGuid(), RaidMarkKind.Waypoint, new("factory", null, 1, 1, null, null), NowUtc),
            new RaidMark(Guid.NewGuid(), RaidMarkKind.Waypoint, new("factory", null, 2, 2, "Loot room", null), NowUtc.AddSeconds(1)),
            new RaidMark(Guid.NewGuid(), RaidMarkKind.Waypoint, new("factory", null, 3, 3, null, null), NowUtc.AddSeconds(2)),
        };

        var (_, objects) = RaidCockpitViewModel.BuildMarksLayer(marks, "factory", NowUtc);
        var labeled = RaidCockpitViewModel.LabelMarksForMap(marks, "factory");

        foreach (var (mark, label) in labeled)
        {
            var sceneObject = Assert.Single(objects, item => item.Id.Value == $"mark:{mark.Id}");
            Assert.Equal(label, sceneObject.Label);
        }

        Assert.Equal(["1", "Loot room", "3"], labeled.Select(item => item.Label));
    }

    [Fact]
    public void APingNeverShowsACustomNameEvenIfOneIsStoredOnIt()
    {
        // Renaming is not offered for a ping in the UI (RaidMarkRowViewModel.CanRename), but the
        // label computation itself must not trust a stored label either — belt and suspenders
        // for "a ping never shows text on the map".
        var ping = new RaidMark(Guid.NewGuid(), RaidMarkKind.Ping, new("factory", null, 1, 1, "Somebody's old name", null), NowUtc);

        var labeled = RaidCockpitViewModel.LabelMarksForMap([ping], "factory");
        var (_, objects) = RaidCockpitViewModel.BuildMarksLayer([ping], "factory", NowUtc);

        Assert.Equal("Ping", Assert.Single(labeled).Label);
        Assert.Equal("Ping", Assert.Single(objects).Label);
    }

    [Fact]
    public void APingsExpiryCarriesThroughToItsSceneObjectAndAWaypointsStaysNull()
    {
        // Issue 584: BuildMarksLayer is the one seam between the store (which now stamps a
        // ping's ExpiresUtc) and the map (which needs it to know when to stop drawing one).
        var expires = NowUtc.AddSeconds(30);
        var pingId = Guid.NewGuid();
        var waypointId = Guid.NewGuid();
        var marks = new[]
        {
            new RaidMark(pingId, RaidMarkKind.Ping, new("factory", null, 1, 1, null, expires), NowUtc),
            new RaidMark(waypointId, RaidMarkKind.Waypoint, new("factory", null, 2, 2, null, null), NowUtc),
        };

        var (_, objects) = RaidCockpitViewModel.BuildMarksLayer(marks, "factory", NowUtc);

        Assert.Equal(expires, Assert.Single(objects, item => item.Id.Value == $"mark:{pingId}").ExpiresUtc);
        Assert.Null(Assert.Single(objects, item => item.Id.Value == $"mark:{waypointId}").ExpiresUtc);
    }

    [Fact]
    public void RightClickingAPingsSceneObjectResolvesBackToThatPingsId()
    {
        var pingId = Guid.NewGuid();
        var marks = new[]
        {
            new RaidMark(pingId, RaidMarkKind.Ping, new("factory", null, 20, 30, null, null), NowUtc),
        };
        var (_, objects) = RaidCockpitViewModel.BuildMarksLayer(marks, "factory", NowUtc);
        var pingObjectId = Assert.Single(objects).Id;

        Assert.True(RaidCockpitViewModel.TryParseMarkId(pingObjectId, out var resolved));
        Assert.Equal(pingId, resolved);
    }

    [Fact]
    public void RightClickingAWaypointsSceneObjectResolvesBackToThatWaypointsId()
    {
        var waypointId = Guid.NewGuid();
        var marks = new[]
        {
            new RaidMark(waypointId, RaidMarkKind.Waypoint, new("factory", null, 20, 30, null, null), NowUtc),
        };
        var (_, objects) = RaidCockpitViewModel.BuildMarksLayer(marks, "factory", NowUtc);
        var waypointObjectId = Assert.Single(objects).Id;

        Assert.True(RaidCockpitViewModel.TryParseMarkId(waypointObjectId, out var resolved));
        Assert.Equal(waypointId, resolved);
    }

    [Fact]
    public void RightClickingSomethingThatIsNotAMarkOfOursNeverResolves()
    {
        // An extract, a loot spawn, or anything else the assembler placed — a right-click on
        // one of those must not be mistaken for "remove this mark".
        Assert.False(RaidCockpitViewModel.TryParseMarkId(new("catalog:deadbeef"), out _));
        Assert.False(RaidCockpitViewModel.TryParseMarkId(new("mark:not-a-guid"), out _));
    }
}
