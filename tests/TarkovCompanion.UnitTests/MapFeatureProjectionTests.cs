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
    /// An extract a player cannot use is worse than no marker, so the side always survives.
    /// </summary>
    /// <remarks>
    /// It used to survive by being appended to the label in words, because every marker was
    /// drawn identically and the text was the only way to tell them apart. It now survives as
    /// a value the map draws with, which is why this asserts the field rather than the
    /// sentence. The requirement did not change; the way it is met did.
    /// </remarks>
    [Fact]
    public void CarriesWhichSideAnExtractIsFor()
    {
        var elements = MapFeatureProjection.Project(Variant(), [
            new(MapFeatureKind.Extract, "Smugglers' Boat", new(10, 2, 20), "scav"),
        ]);

        var element = Assert.Single(elements);
        Assert.Equal(MapFeatureFaction.Scav, element.Faction);
        Assert.Equal("Smugglers' Boat", element.Label);
    }

    /// <summary>
    /// A spawn either side can use says so, and the two spellings mean the same thing.
    /// </summary>
    /// <remarks>
    /// The feed writes an exit's side as one lower-case word and a spawn's as its list of
    /// sides joined together, so "Pmc, Scav" is the same claim "shared" makes for an exit.
    /// </remarks>
    [Fact]
    public void ReadsBothSpellingsOfEitherSide()
    {
        var elements = MapFeatureProjection.Project(Variant(), [
            new(MapFeatureKind.Spawn, "ZoneScav", new(10, 2, 20), "Pmc, Scav"),
            new(MapFeatureKind.Extract, "Crossroads", new(12, 2, 22), "shared"),
        ]);

        Assert.All(elements, element => Assert.Equal(MapFeatureFaction.Shared, element.Faction));
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
