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

    /// <summary>
    /// A turned map is fitted to the shape it is drawn in, not the shape it is stored in.
    /// </summary>
    /// <remarks>
    /// Reported as the fit being "a little zoomed out" after rotating. A quarter turn renders
    /// the surface as height by width — which is why the viewport measurement already swaps
    /// them — and fitting the unturned box solves for the wrong rectangle, so the scale that
    /// came out was the one that would have fitted the map the way round it was.
    ///
    /// A tall map on a wide panel is the case the turn exists for and the case where the two
    /// answers differ most, which is why it showed up on Shoreline.
    /// </remarks>
    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void ATurnedMapIsFittedToTheShapeItIsDrawnIn(int degrees)
    {
        // A tall map, a wide panel: 400 by 1000 into 1600 by 900.
        var upright = MapViewModel.FitScaleForTest(1600, 900, 400, 1000, rotationDegrees: 0);
        var turned = MapViewModel.FitScaleForTest(1600, 900, 400, 1000, degrees);

        // Upright, height constrains: 900/1000. Turned, the map is 1000 wide and 400 tall, so
        // height constrains at 900/400 — more than twice as large, which is the whole report.
        Assert.Equal(0.9, upright, 6);
        Assert.Equal(1.6, turned, 6);
    }

    [Fact]
    public void AHalfTurnLeavesTheMapTheSameShapeAndMustNotChangeTheFit()
    {
        // 180 degrees is not a quarter turn. Treating it as one would shrink every upside-down
        // map for no reason at all.
        Assert.Equal(
            MapViewModel.FitScaleForTest(1600, 900, 400, 1000, rotationDegrees: 0),
            MapViewModel.FitScaleForTest(1600, 900, 400, 1000, rotationDegrees: 180),
            6);
    }

    [Fact]
    public void ASquareMapFitsTheSameEitherWayRound()
    {
        // The sanity check on the swap: a square has nothing to swap.
        Assert.Equal(
            MapViewModel.FitScaleForTest(1600, 900, 800, 800, rotationDegrees: 0),
            MapViewModel.FitScaleForTest(1600, 900, 800, 800, rotationDegrees: 90),
            6);
    }

    /// <summary>A rectangle with no extent is not a smaller rectangle.</summary>
    [Theory]
    [InlineData(0, 40)]
    [InlineData(40, 0)]
    public void AnEmptyBackgroundNeverWins(double width, double height) =>
        Assert.Equal(WholeMap, MapFitBounds.Choose(WholeMap, new Rect(10, 10, width, height)));
}
