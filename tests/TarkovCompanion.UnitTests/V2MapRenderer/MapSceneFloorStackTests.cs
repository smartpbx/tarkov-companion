using Avalonia;
using Avalonia.Media;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [V2 rough package 39] The stacked floors: the renderer draws the map's floors one above
/// another from the per-floor artwork the scene declares, keeping the floor being read in
/// exactly the rectangle every marker is projected into.
/// </summary>
public sealed class MapSceneFloorStackTests
{
    private static readonly MapSceneRendererPresentation Presentation =
        MapSceneRendererPresentation.English(
            System.Globalization.CultureInfo.InvariantCulture,
            TimeZoneInfo.Utc);

    [Fact]
    public void Every_plate_draws_into_the_rectangle_the_markers_project_into()
    {
        var renderer = Stacked();

        Assert.True(renderer.HasFloorStack);
        Assert.Equal(3, renderer.FloorLayers.Count);
        foreach (var plate in renderer.FloorLayers)
        {
            // #413's contract, kept on every floor: one rectangle for the artwork and the
            // objects on it. Only the vertical offset differs between plates.
            Assert.Equal(renderer.MapLeft, plate.Left, 6);
            Assert.Equal(renderer.MapTop, plate.Top, 6);
            Assert.Equal(renderer.MapWidth, plate.Width, 6);
            Assert.Equal(renderer.MapHeight, plate.Height, 6);
        }
    }

    [Fact]
    public void The_floor_being_read_sits_at_zero_and_the_others_around_it()
    {
        var renderer = Stacked(selectedFloor: "first");

        var read = Assert.Single(renderer.FloorLayers, plate => plate.IsSelected);
        Assert.Equal("first", read.FloorId);
        Assert.Equal(0, read.Offset);
        Assert.Equal(0, read.Translate);
        Assert.Equal(1, read.Opacity);

        // Lowest first, so "second" is above the floor being read and "basement" below it.
        Assert.Equal(["basement", "first", "second"], renderer.FloorLayers.Select(plate => plate.FloorId));
        Assert.True(renderer.FloorLayers[0].Offset < 0, "the floor below should sit below the one being read");
        Assert.True(renderer.FloorLayers[2].Offset > 0, "the floor above should sit above the one being read");
        Assert.All(
            renderer.FloorLayers.Where(plate => !plate.IsSelected),
            plate => Assert.True(plate.Opacity < 1, "a floor that is not being read is context, not content"));
    }

    [Fact]
    public void The_stack_is_ordered_by_how_high_each_floor_sits_not_by_how_the_catalog_listed_them()
    {
        // The scene lists them first, second, basement — the order the catalog happened to use.
        var renderer = Stacked();

        Assert.Equal(["basement", "first", "second"], renderer.FloorLayers.Select(plate => plate.FloorId));
        // The ladder reads the other way: top floor first, the way a lift's buttons do.
        Assert.Equal(["second", "first", "basement"], renderer.Floors.Select(floor => floor.Id));
    }

    [Fact]
    public void Stepping_up_and_down_moves_one_floor_and_stops_at_the_ends()
    {
        var renderer = Stacked(selectedFloor: "second");

        Assert.False(renderer.CanGoUpAFloor);
        Assert.True(renderer.CanGoDownAFloor);
        renderer.FloorDownCommand.Execute(null);

        var change = Assert.IsType<MapSceneViewChange>(renderer.LastRequestedChange);
        Assert.Equal(MapSceneViewChangeKind.SelectFloor, change.Kind);
        Assert.Equal("first", change.FloorId);
    }

    [Fact]
    public void A_floor_whose_artwork_never_arrived_is_left_out_and_the_stack_says_so()
    {
        var renderer = Stacked(missingArtworkFor: "basement");

        Assert.Equal(2, renderer.FloorLayers.Count);
        Assert.DoesNotContain(renderer.FloorLayers, plate => plate.FloorId == "basement");
        Assert.Contains("2 of 3", renderer.StackStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flat_scene_draws_its_one_picture_and_no_stack_at_all()
    {
        var renderer = Stacked(mode: MapSceneMode.Flat2D);

        Assert.False(renderer.IsStacked);
        Assert.False(renderer.HasFloorStack);
        Assert.Empty(renderer.FloorLayers);
        Assert.Equal(string.Empty, renderer.StackStatus);
        Assert.True(renderer.ShowsFlatBackground);
    }

    [Fact]
    public void The_one_flat_picture_gives_way_to_the_stack_so_the_two_are_never_drawn_together()
    {
        Assert.False(Stacked().ShowsFlatBackground);
    }

    [Fact]
    public void Resizing_the_card_moves_the_plates_with_the_plan()
    {
        var renderer = Stacked();
        var before = renderer.FloorLayers.Select(plate => plate.Offset).ToArray();

        renderer.SetViewportSize(1920, 1080);

        Assert.Equal(renderer.MapWidth, renderer.FloorLayers[0].Width, 6);
        Assert.Equal(renderer.MapHeight, renderer.FloorLayers[0].Height, 6);
        Assert.NotEqual(before, renderer.FloorLayers.Select(plate => plate.Offset).ToArray());
    }

    [Fact]
    public void In_the_stack_the_traffic_switch_says_the_heatmap_is_flat_view_only()
    {
        // [#902] The heat picture is drawn on the flat plan only; in the stack the switch read on
        // and counted in "Layers · N on" while nothing was drawn.
        static string TrafficLabel(MapSceneRendererViewModel renderer) =>
            renderer.Layers.Single(layer => layer.Layer.Id == MapSceneRendererViewModel.TrafficHeatLayerId).Label;

        var stacked = Stacked();
        Assert.True(stacked.HasFloorStack);
        Assert.Contains("flat view only", TrafficLabel(stacked), StringComparison.Ordinal);

        var flat = Stacked(mode: MapSceneMode.Flat2D);
        Assert.False(flat.HasFloorStack);
        Assert.DoesNotContain("flat view only", TrafficLabel(flat), StringComparison.Ordinal);
    }

    private static MapSceneRendererViewModel Stacked(
        string selectedFloor = "first",
        string? missingArtworkFor = null,
        MapSceneMode mode = MapSceneMode.FloorStack2D)
    {
        var elevations = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["first"] = 0,
            ["second"] = 5,
            ["basement"] = -4,
        };
        var scene = new MapSceneSnapshot(
            3,
            "interchange",
            "interchange-interactive",
            "transform-1",
            new(0, 0, 200, 140),
            ["first", "second", "basement"],
            new(MapSceneCapability.Available, MapSceneCapability.Available, MapSceneCapability.Unavailable("No interior model.")),
            new(mode, selectedFloor, new(100, 70, 1, 0, 0), []),
            [new MapSceneLayer(new("extracts"), "Extracts", 10, true), new MapSceneLayer(MapSceneRendererViewModel.TrafficHeatLayerId, "Modelled traffic", 5, true)],
            [Extract()],
            [Background(), .. elevations.Keys.Select(FloorAsset)]);

        return new MapSceneRendererViewModel(
            scene,
            Presentation,
            reviewedAssetResolver: asset => asset.FloorId is { } floorId &&
                !string.Equals(floorId, missingArtworkFor, StringComparison.OrdinalIgnoreCase)
                    ? new TestArtwork(new Size(400, 280))
                    : asset.Kind == MapSceneAssetKind.Background2D
                        ? new TestArtwork(new Size(400, 280))
                        : null,
            floorNameResolver: id => id,
            floorElevationResolver: id => elevations.TryGetValue(id, out var value) ? value : null);
    }

    private static MapSceneObject Extract() => new(
        new("extract:emercom"),
        new("extracts"),
        MapSceneObjectKind.Extract,
        MapSceneTruthKind.StaticReference,
        "Emercom Checkpoint",
        null,
        MapSceneGeometry.At(new(50, 40)),
        ["first"],
        new DataProvenance("fixture", new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero)));

    private static MapSceneAsset Background() => new(
        new("asset:interchange:plan"),
        MapSceneAssetKind.Background2D,
        new("https://example.test/maps/interchange.svg"),
        new("https://example.test/licence"),
        new string('a', 64),
        "Example map author",
        "map-1",
        "game-1",
        MapSceneAssetReviewStatus.Reviewed,
        new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero));

    private static MapSceneAsset FloorAsset(string floorId) => new(
        new($"asset:interchange:floor:{floorId}"),
        MapSceneAssetKind.Floor2D,
        new("https://example.test/maps/interchange.svg"),
        new("https://example.test/licence"),
        new string('b', 64),
        "Example map author",
        "map-1",
        "game-1",
        MapSceneAssetReviewStatus.Reviewed,
        new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero),
        floorId: floorId);

    private sealed class TestArtwork(Size size) : IImage
    {
        public Size Size { get; } = size;

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }
    }
}
