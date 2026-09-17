using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.UnitTests.Maps;

/// <summary>Covers the map-marker rule Clayton asked for: a ping never draws text on the map.</summary>
public sealed class GroupMarkViewModelTests
{
    [Fact]
    public void APingNeverHasALabelEvenWhenOneIsSet()
    {
        var ping = new GroupMarkViewModel(1, 0, 0, "Somebody's old place name", "detail", IsPing: true, IsReached: false);

        Assert.False(ping.HasLabel);
    }

    [Fact]
    public void ANumberedWaypointStillHasALabel()
    {
        var waypoint = new GroupMarkViewModel(1, 0, 0, "3", "detail", IsPing: false, IsReached: false);

        Assert.True(waypoint.HasLabel);
    }

    [Fact]
    public void AWaypointWithNoLabelTextHasNoLabel()
    {
        var waypoint = new GroupMarkViewModel(1, 0, 0, string.Empty, "detail", IsPing: false, IsReached: false);

        Assert.False(waypoint.HasLabel);
    }
}
