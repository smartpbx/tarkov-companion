using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Covers the case that actually ships: a map publishing BOTH a tile pyramid and a drawing.
/// </summary>
/// <remarks>
/// The existing coverage deleted <c>TilePath</c> before asserting, so the only configuration
/// where the two artworks can disagree was the one configuration never tested. That is how a
/// render model claiming tiles while displaying a drawing went unnoticed, and with it every
/// marker being projected through tile pixel space onto the drawing.
/// </remarks>
public sealed class MapArtworkChoiceTests
{
    [Fact]
    public void ChoosingTheDrawingReportsTheDrawing()
    {
        var model = new MapPresentationService().Create(
            Location(),
            BothArtworks(),
            cachedAssetPath: "/cache/customs.svg",
            artwork: MapBackgroundKind.Svg);

        // The kind is what the coordinate mapper reads to decide which projection to use, so
        // this assertion is the whole bug.
        Assert.Equal(MapBackgroundKind.Svg, model.Background?.Kind);
        Assert.Equal(BothArtworks().SvgPath, model.Background?.SourceUri);
    }

    [Fact]
    public void ChoosingTilesReportsTiles()
    {
        var model = new MapPresentationService().Create(
            Location(),
            BothArtworks(),
            artwork: MapBackgroundKind.TileTemplate);

        Assert.Equal(MapBackgroundKind.TileTemplate, model.Background?.Kind);
        Assert.Equal(BothArtworks().TilePath, model.Background?.SourceUri);
    }

    [Fact]
    public void AskingForArtworkTheVariantDoesNotPublishFallsBackRatherThanBlanking()
    {
        // A blank map is worse than the wrong one. Most maps publish a single artwork, and a
        // request they cannot honour must not leave them with nothing.
        var tilesOnly = BothArtworks() with { SvgPath = null };

        var model = new MapPresentationService().Create(Location(), tilesOnly, artwork: MapBackgroundKind.Svg);

        Assert.Equal(MapBackgroundKind.TileTemplate, model.Background?.Kind);
    }

    [Fact]
    public void SayingNothingKeepsTheOldOrder()
    {
        var model = new MapPresentationService().Create(Location(), BothArtworks());

        Assert.Equal(MapBackgroundKind.TileTemplate, model.Background?.Kind);
    }

    private static MapLocation Location() => new(
        Id: "customs",
        SourceId: "customs",
        Name: "Customs",
        Description: null,
        PrimaryPath: null,
        Variants: [BothArtworks()]);

    /// <summary>A variant shaped like the seven real maps that publish both artworks.</summary>
    private static MapVariant BothArtworks() => new(
        LocationId: "customs",
        Key: "customs-interactive",
        Projection: MapProjectionKind.Interactive,
        ProjectionName: "Interactive",
        Orientation: null,
        Specific: null,
        SvgPath: new Uri("https://example.invalid/customs.svg"),
        TilePath: new Uri("https://example.invalid/tiles/{z}/{x}/{y}.png"),
        TileSize: 256,
        MinimumZoom: 0,
        MaximumZoom: 4,
        Bounds: null,
        SvgBounds: null,
        Transform: null,
        SvgLayer: null,
        MinimumHeight: null,
        MaximumHeight: null,
        Author: "tarkov.dev",
        AuthorLink: null,
        AlternateLocationIds: [],
        Floors: [],
        Labels: []);
}
