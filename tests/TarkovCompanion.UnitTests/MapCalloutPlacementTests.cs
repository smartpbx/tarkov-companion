using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Where a marker's detail card goes.
/// </summary>
/// <remarks>
/// It always went up and to the right, so a marker near the right edge put its card off the
/// map, where the surface clipped it. Reported as the popup being cut off by the bound of the
/// map.
/// </remarks>
public sealed class MapCalloutPlacementTests
{
    [Fact]
    public void ACardWithRoomGoesTheUsualWay()
    {
        var callout = Callout(centerX: 500, centerY: 500);

        Assert.False(callout.PrefersLeft);
        Assert.False(callout.PrefersDown);
    }

    [Fact]
    public void AMarkerNearTheRightEdgePutsItsCardOnTheLeft()
    {
        var callout = Callout(centerX: 1980, centerY: 500);

        Assert.True(callout.PrefersLeft);
        // The leader has to follow the card, or it points at nothing.
        Assert.True(callout.LeaderEnd.X < callout.LeaderStart.X);
    }

    [Fact]
    public void AMarkerNearTheTopPutsItsCardBelow()
    {
        var callout = Callout(centerX: 500, centerY: 10);

        Assert.True(callout.PrefersDown);
        Assert.True(callout.LeaderEnd.Y > callout.LeaderStart.Y);
    }

    [Fact]
    public void AMarkerInTheCornerFlipsBothWays()
    {
        var callout = Callout(centerX: 1980, centerY: 10);

        Assert.True(callout.PrefersLeft);
        Assert.True(callout.PrefersDown);
    }

    [Fact]
    public void TheCardIsPinnedByTheOppositeEdgeWhenItFlips()
    {
        // A margin only pins the edges it sets, so a flipped card has to set the other pair or
        // it stays where it was and the leader points away from it.
        var right = Callout(centerX: 500, centerY: 500).CardInset;
        var left = Callout(centerX: 1980, centerY: 500).CardInset;

        Assert.True(right.Left > 0 && right.Right == 0);
        Assert.True(left.Right > 0 && left.Left == 0);
    }

    [Fact]
    public void RoomIsMeasuredAtTheMapsScale()
    {
        // The card is drawn at the map's own scale, so zooming in shrinks the room it needs.
        // A marker that had to flip when zoomed out may not need to when zoomed in.
        var zoomedOut = Callout(centerX: 1800, centerY: 500, zoom: 1);
        var zoomedIn = Callout(centerX: 1800, centerY: 500, zoom: 4);

        Assert.True(zoomedOut.PrefersLeft);
        Assert.False(zoomedIn.PrefersLeft);
    }

    /// <summary>
    /// A callout at a point, on a map at a given zoom.
    /// </summary>
    /// <remarks>
    /// The scale is driven by Follow rather than set, because MapMarkerScale is shared and
    /// notifies: every marker on the map reads the same instance, so it has one way in.
    /// Follow takes a zoom and Inverse is one over it.
    /// </remarks>
    private static MapMarkerSelectionViewModel Callout(double centerX, double centerY, double zoom = 1)
    {
        var scale = new MapMarkerScale();
        scale.Follow(zoom);
        return new("Dorms V-Ex", "Extract", centerX, centerY)
        {
            CanvasWidth = 2000,
            CanvasHeight = 1000,
            Scale = scale,
        };
    }
}
