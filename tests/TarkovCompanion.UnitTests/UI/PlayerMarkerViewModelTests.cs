using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.UnitTests.UI;

/// <summary>
/// The marker that answers "where am I", drawn from the player's own screenshot.
/// </summary>
public sealed class PlayerMarkerViewModelTests
{
    [Fact]
    public void CentresTheDotOnTheProjectedPoint()
    {
        var marker = new PlayerMarkerViewModel("Your last screenshot position", 200, 140, 0, IsStale: false);

        Assert.Equal(200 - (marker.Size / 2), marker.Left, 6);
        Assert.Equal(140 - (marker.Size / 2), marker.Top, 6);
        Assert.Equal(marker.Size / 2, marker.CornerRadius, 6);
    }

    /// <summary>
    /// A screenshot ages out of being useful, and the marker has to say so rather than keep
    /// presenting a position the player has long since walked away from.
    /// </summary>
    [Fact]
    public void FadesOnceTheScreenshotIsOld()
    {
        var fresh = new PlayerMarkerViewModel("fresh", 0, 0, 0, IsStale: false);
        var stale = fresh with { IsStale = true };

        Assert.NotEqual(fresh.FillColor, stale.FillColor);
        Assert.NotEqual(fresh.BorderColor, stale.BorderColor);
    }
}
