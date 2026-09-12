using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Placing a map's extracts, spawns and locked doors.
/// </summary>
public sealed class MapFeatureProjectionTests
{
    private static readonly MapCatalogTransform Transform = new(
        ScaleX: 1,
        OffsetX: 500,
        ScaleY: 1,
        OffsetY: 500,
        RotationDegrees: 0);

    [Fact]
    public void SendsEachKindToItsOwnLayer()
    {
        var elements = MapFeatureProjection.Project(Variant(), [
            new(MapFeatureKind.Extract, "Klimov Street", new(10, 2, 20), "pmc"),
            new(MapFeatureKind.Transit, "Ground Zero", new(30, 2, 40)),
            new(MapFeatureKind.Spawn, "Sawmill", new(50, 2, 60), "pmc"),
            new(MapFeatureKind.Lock, "Needs Blue Key", new(70, 2, 80)),
        ]);

        Assert.Equal(4, elements.Count);
        Assert.Equal(MapOverlayKind.Extracts, elements[0].Layer);
        Assert.Equal(MapOverlayKind.Extracts, elements[1].Layer);
        Assert.Equal(MapOverlayKind.Spawns, elements[2].Layer);
        Assert.Equal(MapOverlayKind.Keys, elements[3].Layer);
    }

    /// <summary>
    /// An extract a player cannot use is worse than no marker, so the side is on the label.
    /// </summary>
    [Fact]
    public void NamesWhichSideAnExtractIsFor()
    {
        var elements = MapFeatureProjection.Project(Variant(), [
            new(MapFeatureKind.Extract, "Smugglers' Boat", new(10, 2, 20), "scav"),
        ]);

        Assert.Contains("scav", Assert.Single(elements).Label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Smugglers' Boat", elements[0].Label, StringComparison.Ordinal);
    }

    /// <summary>
    /// A marker that cannot be placed is left out rather than dropped at the origin.
    /// </summary>
    /// <remarks>
    /// A marker in the wrong place is worse than a missing one, because it sends somebody
    /// somewhere.
    /// </remarks>
    [Fact]
    public void OmitsEverythingWhenThereIsNoUsableTransform()
    {
        var variant = Variant() with { Transform = null };

        Assert.Empty(MapFeatureProjection.Project(variant, [
            new(MapFeatureKind.Extract, "Klimov Street", new(10, 2, 20), "pmc"),
        ]));
    }

    /// <summary>Extracts are drawn larger than spawns, because they are what is looked for.</summary>
    [Fact]
    public void DrawsExtractsLargerThanSpawns()
    {
        var elements = MapFeatureProjection.Project(Variant(), [
            new(MapFeatureKind.Extract, "Klimov Street", new(10, 2, 20), "pmc"),
            new(MapFeatureKind.Spawn, "Sawmill", new(50, 2, 60)),
        ]);

        Assert.True(elements[0].SizePercent > elements[1].SizePercent);
    }

    private static MapVariant Variant() => new(
        LocationId: "streets-of-tarkov",
        Key: "streets-of-tarkov-interactive",
        Projection: MapProjectionKind.Interactive,
        ProjectionName: "Interactive",
        Orientation: null,
        Specific: null,
        SvgPath: null,
        TilePath: null,
        TileSize: 256,
        MinimumZoom: null,
        MaximumZoom: null,
        Bounds: null,
        SvgBounds: null,
        Transform: Transform,
        SvgLayer: null,
        MinimumHeight: null,
        MaximumHeight: null,
        Author: "tarkov.dev",
        AuthorLink: null,
        AlternateLocationIds: [],
        Floors: [],
        Labels: []);
}
