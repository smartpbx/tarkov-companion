using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.UnitTests.UI;

/// <summary>
/// The marker that answers "where am I", drawn from the player's own screenshot.
/// </summary>
public sealed class PlayerMarkerViewModelTests
{
    [Fact]
    public void CentresTheWholeMarkerOnTheProjectedPoint()
    {
        var marker = new PlayerMarkerViewModel("Your last screenshot position", 200, 140, 0, IsStale: false);

        // The square is placed by its corner and rotated about its middle, so the middle is
        // what has to land on the position.
        Assert.Equal(200 - (marker.Extent / 2), marker.Left, 6);
        Assert.Equal(140 - (marker.Extent / 2), marker.Top, 6);
    }

    /// <summary>
    /// The cone has to fit inside the square that carries it, or rotation clips it.
    /// </summary>
    [Fact]
    public void KeepsTheFacingConeInsideTheMarkerSquare()
    {
        var marker = new PlayerMarkerViewModel("facing", 0, 0, 0, IsStale: false);
        var numbers = System.Text.RegularExpressions.Regex
            .Matches(marker.ConeGeometry, @"-?\d+(?:\.\d+)?")
            .Select(match => double.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture));

        Assert.All(numbers, value => Assert.InRange(value, 0, marker.Extent));
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
        Assert.NotEqual(fresh.ConeColor, stale.ConeColor);
        // The outline is what keeps it visible on pale artwork, so it does not fade with age.
        Assert.Equal(fresh.OutlineColor, stale.OutlineColor);
    }
}
