using System.Globalization;
using Avalonia;
using Avalonia.Media;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [V2 rough package 22] The presentation the canonical renderer grew so the V2 Raid cockpit can
/// show what the V1 map page showed: a person with a facing, place names as text, human floor
/// names, and a camera a host can point at somebody.
/// </summary>
public sealed class MapSceneRendererParityTests
{
    private static readonly MapSceneRendererPresentation Presentation =
        MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc);

    [Fact]
    public void A_position_with_a_heading_is_drawn_as_a_person_with_a_cone()
    {
        var renderer = Renderer(Scene([Player(headingDegrees: 90)]));

        var marker = Assert.Single(renderer.PointMarkers);
        Assert.True(marker.IsPersonIcon);
        Assert.True(marker.IsPlayerIcon);
        Assert.False(marker.ShowsGlyphIcon);
        Assert.True(marker.HasHeading);
        Assert.Equal(90, marker.ConeDegrees);
    }

    [Fact]
    public void A_position_without_a_heading_still_draws_but_points_nowhere()
    {
        var renderer = Renderer(Scene([Player(headingDegrees: null)]));

        var marker = Assert.Single(renderer.PointMarkers);
        Assert.True(marker.IsPersonIcon);
        Assert.False(marker.HasHeading);
        Assert.Equal(0, marker.ConeDegrees);
    }

    [Fact]
    public void Turning_the_map_turns_the_cone_with_the_plan_and_not_with_the_upright_marker()
    {
        // A marker is turned back by the camera bearing so its icon stays upright, so a facing
        // recorded in plan degrees has to be turned by the bearing or the cone points where the
        // player was looking before the map was turned.
        var renderer = Renderer(Scene([Player(headingDegrees: 90)], bearingDegrees: 270));

        var marker = Assert.Single(renderer.PointMarkers);
        Assert.Equal(270, marker.MarkerUprightDegrees);
        Assert.Equal(180, marker.ConeDegrees);
    }

    [Fact]
    public void A_camera_only_change_keeps_the_cone_and_the_place_names_correct()
    {
        var scene = Scene([Player(headingDegrees: 90), Label("dorms", "Dorms")]);
        var renderer = Renderer(scene);
        Assert.Equal(90, Assert.Single(renderer.PointMarkers).ConeDegrees);

        renderer.Present(With(scene, scene.View with { Camera = new(50, 50, 2, 90, 0) }));

        Assert.Equal(0, Assert.Single(renderer.PointMarkers).ConeDegrees);
        var label = Assert.Single(renderer.LabelObjects);
        Assert.Equal(0.5, label.InverseZoom);
        Assert.Equal(90, label.UprightDegrees);
    }

    [Fact]
    public void Place_names_are_text_rather_than_markers_and_never_compete_for_the_marker_budget()
    {
        var renderer = Renderer(Scene([Player(headingDegrees: 0), Label("dorms", "Dorms"), Label("gas", "Gas station")]));

        Assert.Single(renderer.PointMarkers);
        Assert.True(renderer.HasLabelObjects);
        Assert.Equal(["Dorms", "Gas station"], renderer.LabelObjects.Select(item => item.Text).Order());
    }

    [Fact]
    public void A_new_scene_tells_the_view_its_place_names_changed()
    {
        // Without this the plan kept drawing the previous map's street names, pinned at the
        // pixels the previous map's projection put them at: a render of Lighthouse showed
        // Customs' "Old Gas" sitting off the edge of the plan.
        var scene = Scene([Label("dorms", "Dorms")]);
        var renderer = Renderer(scene);
        var changed = new List<string?>();
        renderer.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        renderer.Present(new(
            scene.Revision + 1,
            "streets",
            scene.VariantKey,
            scene.TransformVersion,
            scene.Bounds,
            scene.FloorIds,
            scene.Capabilities,
            scene.View,
            scene.Layers,
            [Label("kolokol", "Kolokol")],
            scene.Assets));

        Assert.Contains(nameof(MapSceneRendererViewModel.LabelObjects), changed);
        Assert.Equal("Kolokol", Assert.Single(renderer.LabelObjects).Text);
    }

    [Fact]
    public void A_hidden_labels_layer_hides_its_place_names()
    {
        var scene = Scene([Label("dorms", "Dorms")]);
        var renderer = Renderer(With(scene, scene.View with { Layers = [.. scene.View.Layers.Select(state =>
            state.LayerId == new MapSceneLayerId("labels") ? state with { IsVisible = false } : state)] }));

        Assert.False(renderer.HasLabelObjects);
    }

    [Fact]
    public void The_host_names_the_floors_and_the_renderer_falls_back_to_the_id()
    {
        var renderer = new MapSceneRendererViewModel(
            Scene([Player(headingDegrees: 0)]),
            Presentation,
            floorNameResolver: id => id == "first" ? "Ground floor" : id);

        // [V2 rough package 39] Top floor first, the way a lift's buttons read. With no host
        // opinion on how high each floor sits, the scene's own order is simply read downwards.
        Assert.Equal(["second", "Ground floor"], renderer.Floors.Select(floor => floor.Name));
    }

    [Fact]
    public void The_host_styles_an_object_the_scene_contract_deliberately_says_nothing_about()
    {
        var renderer = new MapSceneRendererViewModel(
            Scene([Player(headingDegrees: 0), Trail()]),
            Presentation,
            styleResolver: item => item.Kind == MapSceneObjectKind.Route
                ? new MapSceneObjectStyle("#FF112233", LineThickness: 4, Opacity: 0.5)
                : null);

        var line = Assert.Single(renderer.GeometryObjects);
        Assert.Equal("#FF112233", line.ColorHint);
        Assert.Equal(4, line.ThicknessHint);
        Assert.Equal(0.5, line.OpacityHint);
        Assert.Null(Assert.Single(renderer.PointMarkers).ColorHint);
    }

    [Fact]
    public void Following_somebody_centres_on_them_and_only_ever_zooms_in()
    {
        var scene = Scene([Player(headingDegrees: 0)]);
        var renderer = Renderer(scene, () => Guid.Parse("20000000-0000-0000-0000-000000000022"));
        var published = new List<MapSceneViewChange>();
        renderer.ViewChangeRequested += published.Add;

        renderer.FocusOn(new(30, 40), minimumZoom: 3);

        var change = Assert.Single(published);
        Assert.Equal(MapSceneViewChangeKind.SetCamera, change.Kind);
        Assert.Equal(30, change.Camera!.Value.CenterX);
        Assert.Equal(40, change.Camera!.Value.CenterY);
        Assert.Equal(3, change.Camera!.Value.Zoom);

        published.Clear();
        renderer.Present(MapSceneViewReducer.Apply(renderer.Scene, change).Scene);
        renderer.FocusOn(new(10, 10), minimumZoom: 2);

        // Already further in than the follow zoom asks for: the view moves, the zoom does not.
        Assert.Equal(3, Assert.Single(published).Camera!.Value.Zoom);
    }

    [Fact]
    public void A_fit_keeps_which_way_round_the_map_is_turned()
    {
        var scene = Scene([Player(headingDegrees: 0)], bearingDegrees: 90);
        var renderer = Renderer(scene, () => Guid.Parse("20000000-0000-0000-0000-000000000023"));
        var published = new List<MapSceneViewChange>();
        renderer.ViewChangeRequested += published.Add;

        renderer.FitPlanCommand.Execute(null);

        var camera = Assert.Single(published).Camera!.Value;
        Assert.Equal(90, camera.BearingDegrees);
        // [Issue 551] Fitted the way round it now lies: this plan is drawn 1000 wide and 700 tall,
        // so a quarter turn stands it 1000 tall in a 700 card, and the whole of it is 0.7 out.
        // It used to be zoom one whatever the bearing, which cropped this one and left a tall
        // map lying along a wide card half its size.
        Assert.Equal(0.7, camera.Zoom, 6);
        Assert.Equal(50, camera.CenterX, 6);
    }

    [Fact]
    public void A_renderer_that_fills_its_card_never_fits_out_to_a_letterbox()
    {
        // [#961] Team's squad map: the same quarter-turned plan that fits at 0.7 above stays at
        // zoom 1, which in a filling renderer already covers the card edge to edge.
        var scene = Scene([Player(headingDegrees: 0)], bearingDegrees: 90);
        var renderer = new MapSceneRendererViewModel(
            scene,
            Presentation,
            () => Guid.Parse("20000000-0000-0000-0000-000000000025"),
            _ => new TestArtwork(new(1000, 700)),
            fillsViewport: true);
        var published = new List<MapSceneViewChange>();
        renderer.ViewChangeRequested += published.Add;

        renderer.FitPlanCommand.Execute(null);

        Assert.Equal(1, Assert.Single(published).Camera!.Value.Zoom, 6);
    }

    [Fact]
    public void Setting_a_bearing_normalises_it_and_says_nothing_when_it_has_not_moved()
    {
        var renderer = Renderer(Scene([Player(headingDegrees: 0)]), () => Guid.Parse("20000000-0000-0000-0000-000000000024"));
        var published = new List<MapSceneViewChange>();
        renderer.ViewChangeRequested += published.Add;

        renderer.SetBearing(0);
        Assert.Empty(published);

        renderer.SetBearing(-90);
        Assert.Equal(270, Assert.Single(published).Camera!.Value.BearingDegrees);
    }

    /// <summary>The same scene at the next revision with a different view; the snapshot is not a
    /// positional record, so there is nothing to <c>with</c>.</summary>
    private static MapSceneSnapshot With(MapSceneSnapshot scene, MapSceneViewState view) => new(
        scene.Revision + 1,
        scene.LocationId,
        scene.VariantKey,
        scene.TransformVersion,
        scene.Bounds,
        scene.FloorIds,
        scene.Capabilities,
        view,
        scene.Layers,
        scene.Objects,
        scene.Assets);

    private static MapSceneRendererViewModel Renderer(MapSceneSnapshot scene, Func<Guid>? nextChangeId = null) =>
        new(scene, Presentation, nextChangeId, _ => new TestArtwork(new(1000, 700)));

    private sealed class TestArtwork(Size size) : IImage
    {
        public Size Size { get; } = size;

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }
    }

    private static MapSceneSnapshot Scene(IReadOnlyList<MapSceneObject> objects, double bearingDegrees = 0)
    {
        var you = new MapSceneLayer(new("you"), "You", 70, true);
        var labels = new MapSceneLayer(new("labels"), "Place names", 5, true);
        return new(
            7,
            "customs",
            "customs-plan",
            "transform-1",
            new(0, 0, 100, 100),
            ["first", "second"],
            new(
                MapSceneCapability.Available,
                MapSceneCapability.Unavailable("No reviewed floor stack."),
                MapSceneCapability.Unavailable("No reviewed interior model.")),
            new(MapSceneMode.Flat2D, null, new(50, 50, 1, bearingDegrees, 0), [new(you.Id, true), new(labels.Id, true)]),
            [you, labels],
            objects,
            [Asset()]);
    }

    private static MapSceneObject Player(double? headingDegrees) => new(
        new("you:position"),
        new("you"),
        MapSceneObjectKind.LastKnownPosition,
        MapSceneTruthKind.LocalLastKnown,
        "You",
        null,
        MapSceneGeometry.At(new(50, 50)),
        [],
        Provenance(),
        headingDegrees: headingDegrees);

    private static MapSceneObject Trail() => new(
        new("you:trail"),
        new("you"),
        MapSceneObjectKind.Route,
        MapSceneTruthKind.LocalLastKnown,
        "Your path this raid",
        null,
        new(MapSceneGeometryKind.Line, [new(20, 20), new(50, 50)]),
        [],
        Provenance());

    private static MapSceneObject Label(string id, string text) => new(
        new($"label:{id}"),
        new("labels"),
        MapSceneObjectKind.Label,
        MapSceneTruthKind.StaticReference,
        text,
        null,
        MapSceneGeometry.At(new(40, 60)),
        [],
        Provenance());

    private static MapSceneAsset Asset() => new(
        new("asset:customs-plan"),
        MapSceneAssetKind.Background2D,
        new("https://example.test/maps/customs.svg"),
        new("https://example.test/licence"),
        new string('a', 64),
        "Example map author",
        "map-1",
        "game-1",
        MapSceneAssetReviewStatus.Reviewed,
        new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));

    private static DataProvenance Provenance() => new(
        "fixture",
        new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
}
