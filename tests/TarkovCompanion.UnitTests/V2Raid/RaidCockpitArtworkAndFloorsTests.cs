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
    public void A_variant_that_publishes_both_pictures_offers_both_as_rows()
    {
        // The choice nearly every map actually has: upstream gives an asset path for the
        // interactive variant alone, and that variant is the one that carries both a drawing and
        // a tile photograph.
        var rows = RaidCockpitViewModel.BuildArtworkVariants(
            [Variant("customs", MapProjectionKind.Interactive, "Interactive", svg: true, tiles: true, floors: 4)],
            selectedKey: "customs",
            prefersDrawing: false,
            select: (_, _) => Task.CompletedTask);

        Assert.Equal(["Drawing", "Photo"], rows.Select(row => row.Name));
        Assert.False(rows[0].IsSelected);
        Assert.True(rows[1].IsSelected);
        Assert.Equal("Interactive · 4 floors", rows[0].Detail);
        Assert.NotEqual(rows[0].AutomationId, rows[1].AutomationId);
    }

    [Fact]
    public void Every_reviewed_variant_is_offered_with_the_interactive_one_first()
    {
        var rows = RaidCockpitViewModel.BuildArtworkVariants(
            [Variant("lighthouse-2d", MapProjectionKind.TwoDimensional, "2D", svg: true),
             Variant("lighthouse-interactive", MapProjectionKind.Interactive, "Interactive", svg: true),
             Variant("lighthouse-3d", MapProjectionKind.ThreeDimensional, "3D", tiles: true)],
            selectedKey: "lighthouse-interactive",
            prefersDrawing: true,
            select: (_, _) => Task.CompletedTask);

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
            prefersDrawing: false,
            select: (_, _) => Task.CompletedTask);

        Assert.Equal(["photo"], rows.Select(row => row.Key));
        Assert.False(rows[0].PrefersDrawing);
    }

    [Fact]
    public void A_row_says_what_kind_of_picture_it_is_and_whether_it_has_floors()
    {
        var rows = RaidCockpitViewModel.BuildArtworkVariants(
            [Variant("photo", MapProjectionKind.TwoDimensional, "2D", tiles: true),
             Variant("drawing", MapProjectionKind.Interactive, "Interactive", svg: true, floors: 4)],
            selectedKey: null,
            prefersDrawing: false,
            select: (_, _) => Task.CompletedTask);

        Assert.Equal("Drawing · 4 floors", rows.Single(row => row.Key == "drawing").Detail);
        Assert.Equal("Photo", rows.Single(row => row.Key == "photo").Detail);
    }

    [Fact]
    public void Choosing_a_row_asks_for_that_variant_and_that_picture()
    {
        var chosen = new List<(string Key, bool Drawing)>();
        var rows = RaidCockpitViewModel.BuildArtworkVariants(
            [Variant("a", MapProjectionKind.Interactive, "Interactive", svg: true, tiles: true),
             Variant("b", MapProjectionKind.TwoDimensional, "2D · night", svg: true)],
            selectedKey: "a",
            prefersDrawing: true,
            select: (key, drawing) =>
            {
                chosen.Add((key, drawing));
                return Task.CompletedTask;
            });

        rows.Single(row => row.Key == "a" && !row.PrefersDrawing).SelectCommand.Execute(null);
        rows.Single(row => row.Key == "b").SelectCommand.Execute(null);

        Assert.Equal([("a", false), ("b", true)], chosen);
    }

    [Theory]
    // [#923] Stack pressed over the photograph (what Customs and Interchange open on): the stack's
    // plates are the drawing, so the press loads it rather than being undone by the next rebuild.
    [InlineData(true, true, false, null, "customs", "ChooseDrawing")]
    [InlineData(true, true, true, null, "customs", "None")]
    [InlineData(true, false, false, null, "the-lab", "None")]
    // 2D puts back the photograph the stack took away, on the same map only.
    [InlineData(false, true, true, "customs", "customs", "RestorePhoto")]
    [InlineData(false, true, true, "customs", "interchange", "None")]
    // A drawing the player chose for themselves stays.
    [InlineData(false, true, true, null, "customs", "None")]
    [InlineData(false, true, false, "customs", "customs", "None")]
    public void Stack_loads_the_drawing_it_needs_and_2D_puts_the_photograph_back(
        bool wantsStack,
        bool hasChoice,
        bool prefersDrawing,
        string? chosenOn,
        string location,
        string expected) =>
        Assert.Equal(expected, RaidCockpitViewModel.StackArtworkStepFor(wantsStack, hasChoice, prefersDrawing, chosenOn, location).ToString());

    /// <summary>
    /// #938: the drawing Stack borrows belongs to that map and that session alone. It shows on the
    /// map it was borrowed on (however the map's id is cased), and nowhere else.
    /// </summary>
    [Theory]
    [InlineData("customs", "customs", true)]
    [InlineData("Customs", "customs", true)]
    [InlineData("customs", "interchange", false)]
    [InlineData(null, "customs", false)]
    public void The_stacks_borrowed_drawing_shows_on_its_own_map_only(string? borrowedOn, string location, bool expected) =>
        Assert.Equal(expected, MapViewModel.IsStackDrawingOn(borrowedOn, location));

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
