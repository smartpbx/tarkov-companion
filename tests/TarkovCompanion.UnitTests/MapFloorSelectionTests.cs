using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Which floor a position belongs to.
/// </summary>
/// <remarks>
/// This function was written, was correct, and had no caller anywhere in the application. So
/// the map knew the player's height, knew which floor that height belonged to, and drew the
/// wrong one until somebody changed it by hand. These assert the behaviour the automatic
/// following now depends on.
/// </remarks>
public sealed class MapFloorSelectionTests
{
    [Fact]
    public void PicksTheFloorWhoseHeightBandContainsThePlayer()
    {
        var floor = new MapPresentationService().SelectFloor(Variant(), new(0, 12, 0));

        Assert.Equal("second", floor?.Id);
    }

    [Fact]
    public void PrefersARealFloorOverTheBaseLayer()
    {
        // The base layer's extents often cover the whole map, so it matches everything. If it
        // won, the map would never leave it and the feature would do nothing.
        var floor = new MapPresentationService().SelectFloor(Variant(), new(0, 2, 0));

        Assert.Equal("ground", floor?.Id);
    }

    [Fact]
    public void APositionInsideOneBuildingDoesNotMatchAnotherAtTheSameHeight()
    {
        // Extents carry X and Z bounds as well as a height band, which is what makes an
        // interior layer for a single building expressible rather than just a storey.
        var floor = new MapPresentationService().SelectFloor(Variant(), new(500, 12, 500));

        Assert.NotEqual("second", floor?.Id);
    }

    private static MapVariant Variant() => new(
        LocationId: "customs",
        Key: "customs-interactive",
        Projection: MapProjectionKind.Interactive,
        ProjectionName: "Interactive",
        Orientation: null,
        Specific: null,
        SvgPath: new Uri("https://example.invalid/customs.svg"),
        TilePath: null,
        TileSize: 256,
        MinimumZoom: 0,
        MaximumZoom: 2,
        Bounds: new MapCatalogBounds(new MapCatalogPoint(-600, -600), new MapCatalogPoint(600, 600)),
        SvgBounds: null,
        Transform: new MapCatalogTransform(1, 0, 1, 0, 0),
        SvgLayer: null,
        MinimumHeight: null,
        MaximumHeight: null,
        Author: "tarkov.dev",
        AuthorLink: null,
        AlternateLocationIds: [],
        Floors:
        [
            new("base", "Base", null, null, true, [new(null, null, [])]),
            new("ground", "Ground", "ground", null, false, [new(0, 8, [new(new(-100, -100), new(100, 100))])]),
            new("second", "Second", "second", null, false, [new(8, 20, [new(new(-100, -100), new(100, 100))])]),
        ],
        Labels: []);
}
