using System.Globalization;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// A scene rebuilt from the same inputs draws the same plan, so presenting it must leave every
/// marker, line and label alone. It did not: scene objects hold their floor ids and geometry in
/// arrays that a record compares by reference, so the renderer's "did anything change" test said
/// yes every time, and a log line, a group exchange or a clock tick recreated the whole plan.
/// </summary>
public sealed class MapScenePresentReuseTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Two_builds_of_the_same_object_are_not_equal_as_records_but_are_the_same_display()
    {
        var first = Marker("a", "Alpha", 10, 10, T0);
        var second = Marker("a", "Alpha", 10, 10, T0.AddMinutes(5));

        // The trap this guards against, written down: record equality cannot answer the question.
        Assert.NotEqual(first, second);
        Assert.True(first.HasSameDisplayAs(second));
    }

    [Fact]
    public void Anything_that_is_drawn_or_described_differently_is_a_different_display()
    {
        var baseline = Marker("a", "Alpha", 10, 10, T0);

        Assert.False(baseline.HasSameDisplayAs(Marker("a", "Beta", 10, 10, T0)));
        Assert.False(baseline.HasSameDisplayAs(Marker("a", "Alpha", 11, 10, T0)));
        Assert.False(baseline.HasSameDisplayAs(Marker("a", "Alpha", 10, 10, T0, detail: "changed")));
        Assert.False(baseline.HasSameDisplayAs(Marker("a", "Alpha", 10, 10, T0, floor: "second")));
        Assert.False(baseline.HasSameDisplayAs(Marker("a", "Alpha", 10, 10, T0, heading: 90)));
        Assert.False(baseline.HasSameDisplayAs(Marker("b", "Alpha", 10, 10, T0)));
        Assert.False(baseline.HasSameDisplayAs(null));
    }

    [Fact]
    public void A_line_is_compared_point_by_point()
    {
        Assert.True(Line("r", [(0, 0), (10, 10)]).HasSameDisplayAs(Line("r", [(0, 0), (10, 10)])));
        Assert.False(Line("r", [(0, 0), (10, 10)]).HasSameDisplayAs(Line("r", [(0, 0), (10, 11)])));
        Assert.False(Line("r", [(0, 0), (10, 10)]).HasSameDisplayAs(Line("r", [(0, 0), (10, 10), (20, 20)])));
    }

    [Fact]
    public void Presenting_a_rebuilt_scene_with_the_same_content_recreates_nothing()
    {
        var renderer = Renderer(FullScene(1, T0));
        var spatial = renderer.SpatialObjects;
        var geometry = renderer.GeometryObjects;
        var labels = renderer.LabelObjects;
        Assert.NotEmpty(spatial);
        Assert.NotEmpty(geometry);
        Assert.NotEmpty(labels);
        var raised = new List<string?>();
        renderer.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        renderer.Present(FullScene(2, T0.AddSeconds(30)));

        Assert.Same(spatial, renderer.SpatialObjects);
        Assert.Same(geometry, renderer.GeometryObjects);
        Assert.Same(labels, renderer.LabelObjects);
        Assert.DoesNotContain(nameof(MapSceneRendererViewModel.SpatialObjects), raised);
        Assert.DoesNotContain(nameof(MapSceneRendererViewModel.GeometryObjects), raised);
        Assert.DoesNotContain(nameof(MapSceneRendererViewModel.LabelObjects), raised);
        Assert.Equal(2, renderer.Scene.Revision);
    }

    [Fact]
    public void Presenting_a_scene_where_one_thing_moved_recreates_the_plan()
    {
        var renderer = Renderer(FullScene(1, T0));
        var spatial = renderer.SpatialObjects;

        renderer.Present(FullScene(2, T0, movedMarkerX: 44));

        Assert.NotSame(spatial, renderer.SpatialObjects);
    }

    [Fact]
    public void Presenting_a_scene_with_a_new_label_replaces_that_label_and_keeps_the_rest()
    {
        // [#453] The list the view holds stays the same list; only the renamed entry is new.
        var renderer = Renderer(FullScene(1, T0));
        var labels = renderer.LabelObjects;
        var before = labels.ToArray();

        renderer.Present(FullScene(2, T0, firstLabel: "Renamed"));

        Assert.Same(labels, renderer.LabelObjects);
        var renamed = Assert.Single(renderer.LabelObjects, label => label.Text == "Renamed");
        Assert.DoesNotContain(renamed, before);
        Assert.All(renderer.LabelObjects.Where(label => label != renamed), label => Assert.Contains(label, before));
    }

    [Fact]
    public void A_new_style_for_an_unchanged_object_recreates_the_plan()
    {
        // Styles are resolved when a view model is made, so an unchanged object that is now meant
        // to look different (a squadmate joining reassigns every colour) must not be reused.
        var color = "#ff0000";
        var renderer = Renderer(FullScene(1, T0), item => new MapSceneObjectStyle(color));
        var spatial = renderer.SpatialObjects;

        renderer.Present(FullScene(2, T0));
        Assert.Same(spatial, renderer.SpatialObjects);

        color = "#00ff00";
        renderer.Present(FullScene(3, T0));
        Assert.NotSame(spatial, renderer.SpatialObjects);
    }

    internal static MapSceneRendererViewModel Renderer(
        MapSceneSnapshot scene,
        Func<MapSceneObject, MapSceneObjectStyle?>? styles = null)
    {
        var renderer = new MapSceneRendererViewModel(
            scene,
            MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc),
            nextChangeId: null,
            styleResolver: styles);
        renderer.SetViewportSize(1200, 800);
        return renderer;
    }

    /// <summary>A plan with points, lines and labels, built afresh on every call.</summary>
    internal static MapSceneSnapshot FullScene(
        long revision,
        DateTimeOffset observed,
        double movedMarkerX = 20,
        string firstLabel = "Alpha",
        int markers = 40,
        int labels = 10)
    {
        var extracts = new MapSceneLayer(new("extracts"), "Extracts", 10, true);
        var names = new MapSceneLayer(new("labels"), "Labels", 15, true);
        var routes = new MapSceneLayer(new("routes"), "Routes", 20, true);
        var objects = new List<MapSceneObject>();
        for (var index = 0; index < markers; index++)
        {
            objects.Add(Marker($"m{index}", $"Marker {index}", index == 0 ? movedMarkerX : 5 + (index * 2 % 90), 5 + (index * 7 % 90), observed, layer: extracts.Id));
        }

        for (var index = 0; index < labels; index++)
        {
            objects.Add(Marker($"l{index}", index == 0 ? firstLabel : $"Place {index}", 8 + (index * 9 % 84), 12 + (index * 5 % 80), observed, MapSceneObjectKind.Label, names.Id));
        }

        objects.Add(Line("route", [(5, 5), (30, 40), (60, 45)], routes.Id, observed));
        return new(
            revision,
            "customs",
            "customs-plan",
            "transform-1",
            new(0, 0, 100, 100),
            ["first", "second"],
            new(MapSceneCapability.Available, MapSceneCapability.Unavailable("No floor stack."), MapSceneCapability.Unavailable("No interior.")),
            new(MapSceneMode.Flat2D, "first", new(50, 50, 1, 0, 0), [new(extracts.Id, true), new(names.Id, true), new(routes.Id, true)]),
            [extracts, names, routes],
            objects,
            [new MapSceneAsset(
                new("asset:customs-plan"),
                MapSceneAssetKind.Background2D,
                new("https://example.test/maps/customs.svg"),
                new("https://example.test/licence"),
                new string('a', 64),
                "Example map author",
                "map-1",
                "game-1",
                MapSceneAssetReviewStatus.Reviewed,
                T0)]);
    }

    internal static MapSceneObject Marker(
        string id,
        string label,
        double x,
        double y,
        DateTimeOffset observed,
        MapSceneObjectKind kind = MapSceneObjectKind.Extract,
        MapSceneLayerId? layer = null,
        string? detail = null,
        string floor = "first",
        double? heading = null) => new(
            new($"object:{id}"),
            layer ?? new("extracts"),
            kind,
            MapSceneTruthKind.StaticReference,
            label,
            detail ?? $"{label} details",
            MapSceneGeometry.At(new(x, y)),
            [floor],
            new DataProvenance("fixture", observed, Confidence: new Confidence(1)),
            headingDegrees: heading);

    internal static MapSceneObject Line(string id, (double X, double Y)[] points, MapSceneLayerId? layer = null, DateTimeOffset? observed = null) => new(
        new($"object:{id}"),
        layer ?? new("routes"),
        MapSceneObjectKind.Route,
        MapSceneTruthKind.LocalLastKnown,
        id,
        null,
        new(MapSceneGeometryKind.Line, [.. points.Select(point => new MapScenePoint(point.X, point.Y))]),
        ["first"],
        new DataProvenance("fixture", observed ?? T0));
}
