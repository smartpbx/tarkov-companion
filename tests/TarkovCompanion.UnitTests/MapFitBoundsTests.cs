using Avalonia;
using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What Fit frames when a map is made of tiles, a picture, or both.
/// </summary>
/// <remarks>
/// Reported on Shoreline's third floor: the building sat as a thumbnail in the middle of an
/// empty panel, fitted at 109%, which is the whole map scaled to the window. The floor layer
/// had replaced the tiles' artwork without removing the tiles, so the grid still reported the
/// extent of the whole map.
/// </remarks>
public sealed class MapFitBoundsTests
{
    private static readonly Rect WholeMap = new(0, 0, 1200, 900);
    private static readonly Rect OneBuilding = new(540, 300, 190, 60);

    [Fact]
    public void AFloorLayerInsideAMapIsWhatGetsFramed() =>
        Assert.Equal(OneBuilding, MapFitBounds.Choose(WholeMap, OneBuilding));

    [Fact]
    public void TilesWinWhenTheBackgroundCoversTheSameGround() =>
        Assert.Equal(WholeMap, MapFitBounds.Choose(WholeMap, WholeMap));

    [Fact]
    public void ABackgroundLargerThanTheTilesDoesNotTakeOver() =>
        Assert.Equal(WholeMap, MapFitBounds.Choose(WholeMap, new Rect(0, 0, 1600, 1200)));

    [Fact]
    public void ATiledMapWithNothingLoadedFallsBackToThePicture() =>
        Assert.Equal(OneBuilding, MapFitBounds.Choose(default, OneBuilding));

    [Fact]
    public void APictureOnlyMapFramesThePicture() =>
        Assert.Equal(WholeMap, MapFitBounds.Choose(default, WholeMap));

    [Fact]
    public void NothingDrawnFramesNothing() =>
        Assert.Equal(default, MapFitBounds.Choose(default, default));

    /// <summary>A rectangle with no extent is not a smaller rectangle.</summary>
    [Theory]
    [InlineData(0, 40)]
    [InlineData(40, 0)]
    public void AnEmptyBackgroundNeverWins(double width, double height) =>
        Assert.Equal(WholeMap, MapFitBounds.Choose(WholeMap, new Rect(10, 10, width, height)));
}
