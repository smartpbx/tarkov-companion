using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Covers which level of the tile pyramid a map is drawn at.
/// </summary>
/// <remarks>
/// The map used to fetch the coarsest level a pyramid publishes and then magnify it with a
/// scale transform, so zooming in never produced anything sharper. That is why one map was
/// reported as "pretty flat and brown, hard to tell what anything is". Nothing was broken in
/// a way a test would have caught, because the loader and the coordinate mapper made the same
/// wrong assumption independently and therefore agreed with each other.
/// </remarks>
public sealed class MapTileZoomTests
{
    [Fact]
    public void TheSharpestAffordableLevelIsChosen()
    {
        // Four levels published. The coarsest is one tile; each step up quadruples that, so
        // level 3 needs 64 and fits a budget of 256 comfortably.
        var zoom = MapCanvasCoordinateMapper.ChooseTileZoom(Variant(minimum: 0, maximum: 3), budget: 256);

        Assert.Equal(3, zoom);
    }

    [Fact]
    public void ABudgetTooSmallForAnyStepFallsBackToTheCoarsestLevel()
    {
        // A map still has to draw. Refusing to pick a level would leave it blank, which is
        // worse than the blur this change exists to remove.
        var zoom = MapCanvasCoordinateMapper.ChooseTileZoom(Variant(minimum: 0, maximum: 6), budget: 1);

        Assert.Equal(0, zoom);
    }

    [Fact]
    public void AMapPublishingOneLevelStaysOnIt()
    {
        Assert.Equal(2, MapCanvasCoordinateMapper.ChooseTileZoom(Variant(minimum: 2, maximum: 2), budget: 256));
    }

    [Fact]
    public void TheChosenLevelIsSharperThanItUsedToBe()
    {
        // The regression this guards: any map publishing more than one level must now be
        // drawn above its minimum, which is precisely what was not happening.
        var variant = Variant(minimum: 0, maximum: 4);

        Assert.True(MapCanvasCoordinateMapper.ChooseTileZoom(variant, budget: 256) > (variant.MinimumZoom ?? 0));
    }

    private static MapVariant Variant(int minimum, int maximum) => new(
        LocationId: "customs",
        Key: "customs-interactive",
        Projection: MapProjectionKind.Interactive,
        ProjectionName: "Interactive",
        Orientation: null,
        Specific: null,
        SvgPath: null,
        TilePath: new Uri("https://example.invalid/tiles/{z}/{x}/{y}.png"),
        TileSize: 256,
        MinimumZoom: minimum,
        MaximumZoom: maximum,
        Bounds: new MapCatalogBounds(new MapCatalogPoint(-128, -128), new MapCatalogPoint(128, 128)),
        SvgBounds: null,
        Transform: new MapCatalogTransform(1, 0, 1, 0, 0),
        SvgLayer: null,
        MinimumHeight: null,
        MaximumHeight: null,
        Author: "tarkov.dev",
        AuthorLink: null,
        AlternateLocationIds: [],
        Floors: [],
        Labels: []);
}
