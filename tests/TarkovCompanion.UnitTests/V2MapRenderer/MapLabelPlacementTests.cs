using System.Diagnostics;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [#931] Interchange's mall wrote thirty-odd shop names on top of each other, some in Russian,
/// on the ground floor in Stack view. Names now give way by rank, come back as the map zooms in,
/// stay on their own floor, and read in English where the sign says what the place is.
/// </summary>
public sealed class MapLabelPlacementTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_two_drawn_names_overlap_however_dense_the_plan()
    {
        var random = new Random(931);
        var boxes = Enumerable.Range(0, 300)
            .Select(_ => new MapLabelBox(random.NextDouble() * 900, random.NextDouble() * 600, 30 + (random.NextDouble() * 120), 21))
            .ToArray();
        var ranks = boxes.Select((_, index) => (MapLabelRank)(index % 3)).ToArray();
        var shown = Place(boxes, ranks);

        Assert.InRange(shown.Count(value => value), 20, 299);
        AssertNoOverlap(boxes, shown);
    }

    [Fact]
    public void An_area_name_wins_over_a_shop_written_on_top_of_it_whichever_comes_first()
    {
        MapLabelBox[] boxes = [new(100, 100, 80, 21), new(110, 104, 80, 21)];

        var shopFirst = Place(boxes, [MapLabelRank.Room, MapLabelRank.Area]);
        var areaFirst = Place(boxes.Reverse().ToArray(), [MapLabelRank.Area, MapLabelRank.Room]);

        Assert.Equal([false, true], shopFirst);
        Assert.Equal([true, false], areaFirst);
    }

    [Fact]
    public void Ranks_run_area_then_building_then_shop()
    {
        // Three names on the same spot: only the most important is written.
        MapLabelBox[] boxes = [new(50, 50, 60, 21), new(52, 50, 60, 21), new(54, 50, 60, 21)];

        Assert.Equal([false, true, false], Place(boxes, [MapLabelRank.Room, MapLabelRank.Area, MapLabelRank.Building]));
        Assert.Equal([false, false, true], Place(boxes, [MapLabelRank.Room, MapLabelRank.Room, MapLabelRank.Building]));
        Assert.Equal(MapLabelRank.Area, MapLabelPlacer.RankFor(100));
        Assert.Equal(MapLabelRank.Area, MapLabelPlacer.RankFor(90));
        Assert.Equal(MapLabelRank.Building, MapLabelPlacer.RankFor(80));
        Assert.Equal(MapLabelRank.Room, MapLabelPlacer.RankFor(65));
        Assert.Equal(MapLabelRank.Pinned, MapLabelPlacer.RankFor(null));
    }

    [Fact]
    public void A_squadmate_name_is_always_written_even_over_a_place_name()
    {
        MapLabelBox[] boxes = [new(100, 100, 80, 21), new(100, 100, 40, 21)];

        Assert.Equal([true, true], Place(boxes, [MapLabelRank.Pinned, MapLabelRank.Pinned]));
        Assert.Equal([true, false], Place(boxes, [MapLabelRank.Pinned, MapLabelRank.Area]));
    }

    [Fact]
    public void A_name_gives_way_to_an_extract_or_a_player_marker()
    {
        MapLabelBox[] boxes = [new(100, 100, 80, 21), new(300, 100, 80, 21)];
        var shown = new bool[2];
        new MapLabelPlacer().Place(
            boxes,
            [MapLabelRank.Area, MapLabelRank.Area],
            MapLabelPlacer.PriorityOrder([MapLabelRank.Area, MapLabelRank.Area], [100, 100], ["a", "b"]),
            [new MapLabelDisc(130, 108, 10)],
            shown);

        Assert.Equal([false, true], shown);
    }

    [Fact]
    public void Zooming_in_brings_hidden_names_back()
    {
        // Interchange's second floor, shop names at their catalog positions (metres) and sizes.
        var shops = InterchangeSecondFloorShops();
        var ranks = shops.Select(shop => MapLabelPlacer.RankFor(shop.Size)).ToArray();
        var counts = new List<int>();
        foreach (var zoom in new[] { 1.0, 2.0, 4.0, 8.0 })
        {
            var boxes = shops
                .Select(shop => new MapLabelBox(shop.X * zoom, shop.Y * zoom, MapSceneRendererLabelViewModel.EstimateTextWidth(shop.Text) + 6, 21))
                .ToArray();
            var shown = Place(boxes, ranks);
            AssertNoOverlap(boxes, shown);
            counts.Add(shown.Count(value => value));
        }

        output.WriteLine("Shown at 1x/2x/4x/8x: " + string.Join(", ", counts));
        Assert.True(counts[0] < counts[^1], string.Join(", ", counts));
        Assert.Equal(counts.Order().ToArray(), counts.ToArray());
        Assert.Equal(shops.Length, counts[^1]);
    }

    [Fact]
    public void Placing_again_at_a_new_zoom_allocates_nothing()
    {
        var shops = InterchangeSecondFloorShops();
        var ranks = shops.Select(shop => MapLabelPlacer.RankFor(shop.Size)).ToArray();
        var order = MapLabelPlacer.PriorityOrder(ranks, shops.Select(shop => shop.Size).ToArray(), shops.Select(shop => shop.Text).ToArray());
        var boxes = new MapLabelBox[shops.Length];
        var shown = new bool[shops.Length];
        MapLabelDisc[] discs = [new(10, 10, 12), new(200, -150, 12)];
        var placer = new MapLabelPlacer();

        void Step(double zoom)
        {
            for (var index = 0; index < shops.Length; index++)
            {
                boxes[index] = new(shops[index].X * zoom, shops[index].Y * zoom, 60, 21);
            }

            placer.Place(boxes, ranks, order, discs, shown);
        }

        // Warm up at the densest zoom, which grows every buffer to its largest.
        Step(8);
        Step(1);
        var clock = Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        const int steps = 1000;
        for (var step = 0; step < steps; step++)
        {
            Step(1 + (step % 8));
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        clock.Stop();
        output.WriteLine($"{shops.Length} names: {clock.Elapsed.TotalMicroseconds / steps:0.0} µs per zoom step, {allocated} bytes allocated over {steps} steps");
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void The_renderer_hides_colliding_names_at_the_fitted_zoom_and_shows_them_zoomed_in()
    {
        var renderer = MapScenePresentReuseTests.Renderer(MallScene(1, zoom: 1));
        var fitted = renderer.LabelObjects.Count(label => label.IsShown);
        AssertNoOverlap(renderer, zoom: 1);
        Assert.True(renderer.LabelObjects.Single(label => label.Text == "Mall").IsShown);

        renderer.Present(MallScene(2, zoom: 8));
        var zoomed = renderer.LabelObjects.Count(label => label.IsShown);
        AssertNoOverlap(renderer, zoom: 8);

        output.WriteLine($"Shown fitted {fitted}, zoomed {zoomed} of {renderer.LabelObjects.Count}");
        Assert.True(fitted < zoomed, $"{fitted} then {zoomed}");
        Assert.Equal(renderer.LabelObjects.Count, zoomed);

        // And back out: the same names give way again, the same ones every time.
        renderer.Present(MallScene(3, zoom: 1));
        Assert.Equal(fitted, renderer.LabelObjects.Count(label => label.IsShown));
    }

    [Fact]
    public void A_zoom_step_on_an_interchange_sized_plan_is_measured()
    {
        var renderer = MapScenePresentReuseTests.Renderer(MallScene(1, zoom: 1));
        var clock = Stopwatch.StartNew();
        const int steps = 200;
        for (var step = 0; step < steps; step++)
        {
            renderer.Present(MallScene(2 + step, zoom: 1 + (step % 8)));
        }

        clock.Stop();
        output.WriteLine($"{renderer.LabelObjects.Count} names, {renderer.SpatialObjects.Count} markers: " +
            $"{clock.Elapsed.TotalMilliseconds / steps:0.000} ms per camera present");
    }

    [Fact]
    public void A_shop_name_is_on_its_own_floor_and_not_on_the_ground_plan()
    {
        // Interchange: no height range for the base floor, 2nd floor from 25 m, 3rd from 34 m.
        MapFloorDefinition[] floors =
        [
            new("base", "Base", null, null, true, [new(null, null, [])]),
            new("floor-1", "2nd Floor", null, null, false, [new(25, 34, [])]),
            new("floor-2", "3rd Floor", null, null, false, [new(34, 1000, [])]),
        ];

        Assert.Equal(["floor-1"], MapSceneAssembler.PlaceNameFloors(Label("Nortex", 25, 33), floors));
        Assert.Equal(["floor-2"], MapSceneAssembler.PlaceNameFloors(Label("Pharmacy", 34, 999), floors));
        Assert.Equal(["base"], MapSceneAssembler.PlaceNameFloors(Label("Garage A", -1000, 24), floors));
        Assert.Equal(["base"], MapSceneAssembler.PlaceNameFloors(Label("Power Station", -1, 10), floors));
        Assert.Empty(MapSceneAssembler.PlaceNameFloors(Label("Scav Camp", null, null), floors));
    }

    [Fact]
    public void A_base_floor_with_its_own_range_keeps_a_name_that_spans_it()
    {
        // Shoreline publishes a base range, and "West Wing" spans every floor of the resort.
        MapFloorDefinition[] floors =
        [
            new("base", "Base", null, null, true, [new(-1000, -1, [])]),
            new("floor-1", "2nd Floor", null, null, false, [new(-1, 2, [])]),
            new("floor-2", "3rd Floor", null, null, false, [new(2, 1000, [])]),
        ];

        Assert.Equal(["base", "floor-1", "floor-2"], MapSceneAssembler.PlaceNameFloors(Label("West Wing", -100, 100), floors));
    }

    [Theory]
    [InlineData("АПТЕКА", "Pharmacy")]
    [InlineData("МУЗЕЙ ИСТОРИИ", "History Museum")]
    [InlineData("ЗАКРЫТО НА РЕМОНТ", "Closed for repair")]
    [InlineData("СКОРО ОТКРЫТИЕ", "Opening soon")]
    [InlineData("МЕБЕЛЬ МК", "MK Furniture")]
    public void A_sign_that_says_what_the_place_is_reads_in_english(string catalog, string english) =>
        Assert.Equal(english, MapPlaceNameText.For(catalog));

    [Theory]
    [InlineData("ТАРЗДРАВ")]
    [InlineData("ПУШКИН")]
    [InlineData("Fashion Store")]
    [InlineData("Power Station")]
    public void A_brand_or_an_english_name_is_kept_as_the_catalog_writes_it(string catalog) =>
        Assert.Equal(catalog, MapPlaceNameText.For(catalog));

    private static bool[] Place(MapLabelBox[] boxes, MapLabelRank[] ranks)
    {
        var shown = new bool[boxes.Length];
        var order = MapLabelPlacer.PriorityOrder(
            ranks,
            ranks.Select(rank => (double)rank).ToArray(),
            boxes.Select((_, index) => index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        new MapLabelPlacer().Place(boxes, ranks, order, [], shown);
        return shown;
    }

    private static void AssertNoOverlap(MapLabelBox[] boxes, bool[] shown)
    {
        for (var first = 0; first < boxes.Length; first++)
        {
            for (var second = first + 1; second < boxes.Length; second++)
            {
                if (shown[first] && shown[second])
                {
                    Assert.False(Overlap(boxes[first], boxes[second]), $"{first} and {second} are both drawn and overlap");
                }
            }
        }
    }

    private static void AssertNoOverlap(MapSceneRendererViewModel renderer, double zoom)
    {
        var drawn = renderer.LabelObjects
            .Where(label => label.IsShown)
            .Select(label => (label.Text, Box: new MapLabelBox(
                (label.AnchorLeft + (label.Width / 2)) * zoom,
                (label.AnchorTop + 9) * zoom,
                MapSceneRendererLabelViewModel.EstimateTextWidth(label.Text),
                MapSceneRendererLabelViewModel.ScreenHeight - 2)))
            .ToArray();
        for (var first = 0; first < drawn.Length; first++)
        {
            for (var second = first + 1; second < drawn.Length; second++)
            {
                Assert.False(Overlap(drawn[first].Box, drawn[second].Box), $"'{drawn[first].Text}' and '{drawn[second].Text}' overlap at {zoom}x");
            }
        }
    }

    private static bool Overlap(MapLabelBox left, MapLabelBox right) =>
        Math.Abs(left.CenterX - right.CenterX) < (left.Width + right.Width) / 2 &&
        Math.Abs(left.CenterY - right.CenterY) < (left.Height + right.Height) / 2;

    private static MapOverlayElement Label(string text, double? bottom, double? top) =>
        new(MapOverlayKind.Labels, new MapPoint(0, 0), text, SizePercent: 65, MinimumHeight: bottom, MaximumHeight: top);

    private static (string Text, double X, double Y, double Size)[] InterchangeSecondFloorShops() =>
    [
        ("Nortex", 87, -165, 65), ("TRend", 60, -152, 65), ("Mode7", 69.5, -134, 65), ("TTS", 19, -129, 65),
        ("Book Store", -38, -129, 65), ("Dino Clothes", 91, -119, 65), ("EMERCOM", 18, -103, 65),
        ("Kostin", -28, -103, 65), ("Bizarro", -65, -103, 65), ("Spiel", 92, -87, 65), ("Voyage", -18, -87, 65),
        ("Viking", 57, -66, 65), ("Mantis", 13, -66, 65), ("German", -18, -72, 65), ("The National", 57, -32, 65),
        ("Brutal", 13, -32, 65), ("Kiba", -18, -25, 65), ("Pretty Lights", -34, -20, 65), ("Telespot", 92, -18, 65),
        ("Revis", 62, -12, 65), ("ADIK", 19, -6, 65), ("Generic", -28, 0.5, 65), ("Top Brand", 92, 15, 65),
        ("Sports", 61, 15, 65), ("Yushka", 70, 32, 65), ("Rasmussen", 19.5, 26, 65), ("Avokado", -37, 26, 65),
        ("Boots 4 Life", 91, 55, 65), ("Texho", 61, 50, 65), ("Dom", 6, 49, 65), ("IDEA", -34, -235, 80),
        ("Goshan", -115, -45, 80), ("OLI", -28, 140, 80),
    ];

    /// <summary>The same shops on a 1000-unit plan, the whole mall about 250 units across, plus one area name.</summary>
    private static MapSceneSnapshot MallScene(long revision, double zoom)
    {
        var names = new MapSceneLayer(new("labels"), "Labels", 15, true);
        var extracts = new MapSceneLayer(new("extracts"), "Extracts", 10, true);
        var provenance = new DataProvenance("fixture", T0, Confidence: new Confidence(1));
        var objects = InterchangeSecondFloorShops()
            .Select((shop, index) => new MapSceneObject(
                new($"object:shop{index}"),
                names.Id,
                MapSceneObjectKind.Label,
                MapSceneTruthKind.StaticReference,
                shop.Text,
                null,
                MapSceneGeometry.At(new(500 + shop.X, 500 + shop.Y)),
                [],
                provenance) { PlaceNameSize = shop.Size })
            .Append(new MapSceneObject(
                new("object:mall"),
                names.Id,
                MapSceneObjectKind.Label,
                MapSceneTruthKind.StaticReference,
                "Mall",
                null,
                MapSceneGeometry.At(new(540, 400)),
                [],
                provenance) { PlaceNameSize = 100 })
            .Concat(Enumerable.Range(0, 40).Select(index => new MapSceneObject(
                new($"object:extract{index}"),
                extracts.Id,
                MapSceneObjectKind.Extract,
                MapSceneTruthKind.StaticReference,
                $"Extract {index}",
                null,
                MapSceneGeometry.At(new(50 + (index * 23 % 900), 50 + (index * 37 % 900))),
                [],
                provenance)))
            .ToArray();
        return new(
            revision,
            "interchange",
            "interchange-plan",
            "transform-1",
            new(0, 0, 1000, 1000),
            ["base"],
            new(MapSceneCapability.Available, MapSceneCapability.Unavailable("No floor stack."), MapSceneCapability.Unavailable("No interior.")),
            new(MapSceneMode.Flat2D, "base", new(500, 500, zoom, 0, 0), [new(names.Id, true), new(extracts.Id, true)]),
            [extracts, names],
            objects,
            []);
    }
}
