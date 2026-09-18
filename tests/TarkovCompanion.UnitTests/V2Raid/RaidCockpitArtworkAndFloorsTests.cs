using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// [V2 rough package 39] The artwork chooser and the floor readout: the two rules the Raid
/// cockpit adds around V1's own map state, checked without standing up a map.
/// </summary>
public sealed class RaidCockpitArtworkAndFloorsTests
{
    [Fact]
    public void Every_reviewed_variant_is_offered_with_the_interactive_one_first()
    {
        var rows = RaidCockpitViewModel.BuildArtworkVariants(
            [Variant("lighthouse-2d", MapProjectionKind.TwoDimensional, "2D", svg: true),
             Variant("lighthouse-interactive", MapProjectionKind.Interactive, "Interactive", svg: true, tiles: true),
             Variant("lighthouse-3d", MapProjectionKind.ThreeDimensional, "3D", tiles: true)],
            selectedKey: "lighthouse-interactive",
            select: _ => Task.CompletedTask);

        Assert.Equal(
            ["lighthouse-interactive", "lighthouse-2d", "lighthouse-3d"],
            rows.Select(row => row.Key));
        Assert.True(rows[0].IsSelected);
        Assert.All(rows.Skip(1), row => Assert.False(row.IsSelected));
    }

    [Fact]
    public void A_variant_with_no_artwork_at_all_is_not_offered()
    {
        // "Tile-only variants must draw, or not be offered": a tile-only variant does draw (the
        // cockpit composes V1's loaded grid into one picture), and a variant with neither a
        // drawing nor tiles cannot, so it never reaches the chooser.
        var rows = RaidCockpitViewModel.BuildArtworkVariants(
            [Variant("photo", MapProjectionKind.TwoDimensional, "2D", tiles: true),
             Variant("nothing", MapProjectionKind.TwoDimensional, "2D · empty")],
            selectedKey: null,
            select: _ => Task.CompletedTask);

        Assert.Equal(["photo"], rows.Select(row => row.Key));
    }

    [Fact]
    public void A_row_says_what_kind_of_picture_it_is_and_whether_it_has_floors()
    {
        var rows = RaidCockpitViewModel.BuildArtworkVariants(
            [Variant("photo", MapProjectionKind.TwoDimensional, "2D", tiles: true),
             Variant("drawing", MapProjectionKind.Interactive, "Interactive", svg: true, floors: 4)],
            selectedKey: null,
            select: _ => Task.CompletedTask);

        Assert.Equal("Drawing · 4 floors", rows.Single(row => row.Key == "drawing").Detail);
        Assert.Equal("Photo", rows.Single(row => row.Key == "photo").Detail);
    }

    [Fact]
    public void Choosing_a_row_asks_for_that_variant_by_key()
    {
        var chosen = new List<string>();
        var rows = RaidCockpitViewModel.BuildArtworkVariants(
            [Variant("a", MapProjectionKind.TwoDimensional, "2D", svg: true),
             Variant("b", MapProjectionKind.TwoDimensional, "2D · night", svg: true)],
            selectedKey: "a",
            select: key =>
            {
                chosen.Add(key);
                return Task.CompletedTask;
            });

        rows.Single(row => row.Key == "b").SelectCommand.Execute(null);

        Assert.Equal(["b"], chosen);
    }

    [Fact]
    public void Automatic_floor_selection_says_what_it_did_or_why_it_could_not()
    {
        Assert.Equal(
            string.Empty,
            MapViewModel.DescribeFloorSource(following: false, hasPosition: true, matched: Floor()));
        Assert.Contains(
            "No screenshot yet",
            MapViewModel.DescribeFloorSource(following: true, hasPosition: false, matched: null),
            StringComparison.Ordinal);
        Assert.Contains(
            "matches no floor",
            MapViewModel.DescribeFloorSource(following: true, hasPosition: true, matched: null),
            StringComparison.Ordinal);
        Assert.Contains(
            "Second floor",
            MapViewModel.DescribeFloorSource(following: true, hasPosition: true, matched: Floor()),
            StringComparison.Ordinal);
    }

    private static MapFloorDefinition Floor() => new(
        Id: "second",
        Name: "Second floor",
        SvgLayer: "second",
        TilePath: null,
        IsVisibleByDefault: false,
        Extents: []);

    private static MapVariant Variant(
        string key,
        MapProjectionKind projection,
        string projectionName,
        bool svg = false,
        bool tiles = false,
        int floors = 0) => new(
        LocationId: "lighthouse",
        Key: key,
        Projection: projection,
        ProjectionName: projectionName,
        Orientation: null,
        Specific: null,
        SvgPath: svg ? new Uri("https://example.test/maps/plan.svg") : null,
        TilePath: tiles ? new Uri("https://example.test/maps/{z}/{x}/{y}.png") : null,
        TileSize: 256,
        MinimumZoom: null,
        MaximumZoom: null,
        Bounds: null,
        SvgBounds: null,
        Transform: null,
        SvgLayer: null,
        MinimumHeight: null,
        MaximumHeight: null,
        Author: "Example author",
        AuthorLink: null,
        AlternateLocationIds: [],
        Floors: [.. Enumerable.Range(0, floors).Select(index => new MapFloorDefinition(
            Id: $"floor-{index}",
            Name: $"Floor {index}",
            SvgLayer: $"floor-{index}",
            TilePath: null,
            IsVisibleByDefault: index == 0,
            Extents: []))],
        Labels: []);
}
