using Avalonia;
using TarkovCompanion.App.Views.V2.Setup;

namespace TarkovCompanion.UnitTests.V2Setup;

/// <summary>#833: Setup's tab row stops short of the floating What's new banner, and only beside it.</summary>
public sealed class FloatingBannerClearanceTests
{
    private const double ParentWidth = 1720;

    [Fact]
    public void ABannerBesideTheRowTakesItsWidthPlusAGap()
    {
        // At 200% text: banner 560 wide at the parent's right, over a three-line tab row.
        var banner = new Rect(ParentWidth - 560, 8, 560, 54);

        Assert.Equal(560 + FloatingBannerClearance.Gap, FloatingBannerClearance.RightReserve(banner, ParentWidth, 0, 160));
    }

    [Fact]
    public void AHiddenOrEmptyBannerReservesNothing()
    {
        Assert.Equal(0, FloatingBannerClearance.RightReserve(null, ParentWidth, 0, 50));
        Assert.Equal(0, FloatingBannerClearance.RightReserve(new Rect(ParentWidth, 8, 0, 0), ParentWidth, 0, 50));
    }

    [Fact]
    public void ABannerAboveOrBelowTheRowDoesNotNarrowIt()
    {
        var banner = new Rect(ParentWidth - 400, 8, 400, 40);

        Assert.Equal(0, FloatingBannerClearance.RightReserve(banner, ParentWidth, 48, 100));
        Assert.Equal(0, FloatingBannerClearance.RightReserve(new Rect(ParentWidth - 400, 200, 400, 40), ParentWidth, 0, 100));
    }
}
