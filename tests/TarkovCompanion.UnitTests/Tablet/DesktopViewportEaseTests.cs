using TarkovCompanion.App.Services.V2;

namespace TarkovCompanion.UnitTests.TabletSurface;

/// <summary>#604: the glide the desktop's map makes toward a tablet's Control move.</summary>
public sealed class DesktopViewportEaseTests
{
    private static readonly EasedCamera From = new(0, 0, 1);
    private static readonly EasedCamera To = new(100, -50, 4);

    [Fact]
    public void TheGlideStartsWhereTheCameraIsAndEndsExactlyOnTheTarget()
    {
        Assert.Equal(From, DesktopViewportEase.At(From, To, 0));
        Assert.Equal(To, DesktopViewportEase.At(From, To, 1));
        Assert.Equal(To, DesktopViewportEase.At(From, To, 3));
        Assert.Equal(To, DesktopViewportEase.At(From, To, double.NaN));
    }

    [Fact]
    public void ItMovesFastFirstThenSettlesAndNeverOvershoots()
    {
        var quarter = DesktopViewportEase.At(From, To, 0.25);
        var half = DesktopViewportEase.At(From, To, 0.5);
        Assert.True(quarter.X > 25, $"eased out: a quarter of the time covers more than a quarter ({quarter.X})");
        Assert.True(half.X > quarter.X && half.X < 100);
        for (var step = 0; step <= 20; step++)
        {
            var at = DesktopViewportEase.At(From, To, step / 20.0);
            Assert.InRange(at.X, 0, 100);
            Assert.InRange(at.Y, -50, 0);
            Assert.InRange(at.Zoom, 1, 4);
        }
    }

    [Fact]
    public void ZoomMovesInProportionSoInAndOutFeelAlike()
    {
        // Halfway along the eased path, zooming 1 -> 4 and 4 -> 1 are mirror images: 2 is the
        // proportional midpoint of both.
        var fraction = 1 - Math.Cbrt(0.5); // where the ease has covered exactly half
        Assert.Equal(2, DesktopViewportEase.At(new(0, 0, 1), new(0, 0, 4), fraction).Zoom, 6);
        Assert.Equal(2, DesktopViewportEase.At(new(0, 0, 4), new(0, 0, 1), fraction).Zoom, 6);
    }
}
