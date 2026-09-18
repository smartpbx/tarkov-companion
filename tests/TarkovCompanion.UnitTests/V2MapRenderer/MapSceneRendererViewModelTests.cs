using System.Globalization;
using Avalonia;
using Avalonia.Media;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.Views.V2.MapRenderer;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

public sealed class MapSceneRendererViewModelTests
{
    private static readonly MapSceneRendererPresentation Presentation =
        MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc);

    [Fact]
    public void Renderer_disables_presentations_it_cannot_render_and_emits_supported_changes()
    {
        var scene = Scene(revision: 41, supportsFloorStack: true, interiorAvailable: true);
        var renderer = Renderer(scene, () => Guid.Parse("20000000-0000-0000-0000-000000000306"));
        var published = new List<MapSceneViewChange>();
        renderer.ViewChangeRequested += published.Add;

        var interior = renderer.Modes.Single(item => item.Mode == MapSceneMode.Interior3D);
        Assert.False(interior.IsAvailable);
        Assert.Contains("3D", interior.UnavailableReason, StringComparison.OrdinalIgnoreCase);

        renderer.RequestMode(MapSceneMode.Interior3D);

        Assert.Empty(published);
        Assert.True(renderer.HasRendererNotice);
        Assert.Contains("unavailable", renderer.RendererNotice, StringComparison.OrdinalIgnoreCase);

        // [V2 rough package 39] A layer with nothing on it no longer switches the map to empty:
        // "Loot" holds no objects in this scene, so its switch says so and does nothing.
        var loot = renderer.Layers.Single(layer => layer.Layer.Id == new MapSceneLayerId("loot"));
        Assert.True(loot.HasNothingToShow);
        loot.ToggleCommand.Execute(null);
        Assert.Empty(published);

        var extracts = renderer.Layers.Single(layer => layer.Layer.Id == new MapSceneLayerId("extracts"));
        Assert.Equal(1, extracts.Count);
        Assert.Contains("1", extracts.Label, StringComparison.Ordinal);
        extracts.ToggleCommand.Execute(null);

        var change = Assert.Single(published);
        Assert.Equal(scene.Revision, change.ExpectedRevision);
        Assert.Equal(MapSceneViewChangeKind.SetLayerVisibility, change.Kind);
        Assert.Equal("extracts", change.LayerId?.Value);
        Assert.False(change.IsVisible.GetValueOrDefault());
        Assert.Same(scene, renderer.Scene);
    }

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(false, 0, false)]
    [InlineData(true, 3, true)]
    [InlineData(false, 3, true)]
    public void An_empty_layer_cannot_be_switched_on_but_one_that_is_showing_can_always_be_switched_off(
        bool isVisible,
        int count,
        bool canToggle)
    {
        // The Windows gallery's offline loot scenario applies the loot preset with no loot-spawn
        // data: High-value loot is on and empty. Disabled outright, it could not be turned off,
        // and UI Automation's Toggle threw on it, which the gallery reported as "no window".
        bool? requested = null;
        var layer = new MapSceneRendererLayerViewModel(
            new MapSceneLayer(new MapSceneLayerId("high-value-loot-spawns"), "High-value loot", 10, isVisible),
            isVisible,
            MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc),
            visible => requested = visible,
            count);

        Assert.Equal(canToggle, layer.CanToggle);
        layer.ToggleCommand.Execute(null);

        Assert.Equal(canToggle ? !isVisible : null, requested);
    }

    [Fact]
    public void Canonical_floor_stack_state_with_no_artwork_says_so_and_draws_one_floor()
    {
        var renderer = Renderer(Scene(
            supportsFloorStack: true,
            mode: MapSceneMode.FloorStack2D));

        // [V2 rough package 39] The mode is offered whenever the map has floors, and the
        // renderer draws it — but only from per-floor artwork the scene actually declares. This
        // scene declares none, so it still falls back to one plan and still says so.
        Assert.True(renderer.Modes.Single(item => item.Mode == MapSceneMode.FloorStack2D).IsAvailable);
        Assert.True(renderer.Modes.Single(item => item.Mode == MapSceneMode.FloorStack2D).IsSelected);
        Assert.Empty(renderer.FloorLayers);
        Assert.False(renderer.HasFloorStack);
        // One line, not a paragraph beside it: the stack's own status is the honest answer and
        // the generic mode fallback would only say the same thing again, longer.
        Assert.False(renderer.ShowsModeFallback);
        Assert.Contains("no floor artwork", renderer.StackStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("showing one floor", renderer.StackStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Renderer_uses_floor_and_layer_filtered_scene_objects_for_map_and_complete_list()
    {
        var scene = Scene(firstFloorObjects:
        [
            Extract("crossroads", "Crossroads", "first", MapSceneOfferState.Offered),
            Extract("dorms", "Dorms", "second", MapSceneOfferState.NotOffered),
        ]);
        var renderer = Renderer(scene, () => Guid.Parse("20000000-0000-0000-0000-000000000308"));

        Assert.Equal(["Crossroads"], renderer.ListItems.Select(item => item.Label));
        Assert.Equal(["Crossroads"], renderer.SpatialObjects.Select(item => item.Label));

        renderer.SelectFloor("second");
        var floorChange = Assert.IsType<MapSceneViewChange>(renderer.LastRequestedChange);
        renderer.Present(MapSceneViewReducer.Apply(scene, floorChange).Scene);

        Assert.Equal(["Dorms"], renderer.ListItems.Select(item => item.Label));
        Assert.Equal(["Dorms"], renderer.SpatialObjects.Select(item => item.Label));
    }

    [Fact]
    public void Semantic_glyphs_and_text_keep_truth_faction_and_offer_states_distinct()
    {
        var renderer = Renderer(Scene(firstFloorObjects:
        [
            Extract("pmc", "PMC offered", "first", MapSceneOfferState.Offered, MapFeatureFaction.Pmc, 10),
            Extract("scav", "Scav not offered", "first", MapSceneOfferState.NotOffered, MapFeatureFaction.Scav, 20),
            Extract("shared", "Shared unknown", "first", MapSceneOfferState.Unknown, MapFeatureFaction.Shared, 30),
            Point("local", "Local record", MapSceneObjectKind.LastKnownPosition, MapSceneTruthKind.LocalLastKnown, 40, 40),
            Point("team", "Team record", MapSceneObjectKind.TeammateLastKnown, MapSceneTruthKind.TeamSharedLastKnown, 50, 50),
            Historical("traffic", "Historical traffic", 60, 60),
        ]));

        var pmc = renderer.SpatialObjects.Single(item => item.Label == "PMC offered");
        var scav = renderer.SpatialObjects.Single(item => item.Label == "Scav not offered");
        var shared = renderer.SpatialObjects.Single(item => item.Label == "Shared unknown");
        var local = renderer.SpatialObjects.Single(item => item.Label == "Local record");
        var team = renderer.SpatialObjects.Single(item => item.Label == "Team record");
        var historical = renderer.SpatialObjects.Single(item => item.Label == "Historical traffic");

        Assert.Equal(("P", "✓"), (pmc.FactionGlyph, pmc.OfferGlyph));
        Assert.Equal(("S", "×"), (scav.FactionGlyph, scav.OfferGlyph));
        // V2 rough package 20: an unknown offer state says nothing rather than drawing a "?" —
        // the marker's own border colour carries "offer unknown", and the words are still in the
        // accessible name below.
        Assert.Equal(("P/S", string.Empty), (shared.FactionGlyph, shared.OfferGlyph));
        Assert.Contains("Unknown", shared.AutomationName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(("◎", "L"), (local.MarkerGlyph, local.TruthGlyph));
        Assert.Equal(("◉", "T"), (team.MarkerGlyph, team.TruthGlyph));
        Assert.Equal(("≈", "H"), (historical.MarkerGlyph, historical.TruthGlyph));
        Assert.Contains("PMC", pmc.AutomationName, StringComparison.Ordinal);
        Assert.Contains("Offered this raid", pmc.AutomationName, StringComparison.Ordinal);
        Assert.Contains("Historical estimate", historical.AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_keeps_sourced_lines_and_regions_as_geometry_and_list_details()
    {
        var route = SceneObject(
            "route",
            MapSceneObjectKind.Route,
            MapSceneTruthKind.PersonalPlan,
            new(MapSceneGeometryKind.Line, [new(10, 20), new(50, 60), new(90, 20)]));
        var risk = SceneObject(
            "risk",
            MapSceneObjectKind.Risk,
            MapSceneTruthKind.StaticReference,
            new(MapSceneGeometryKind.Region, [new(20, 20), new(40, 20), new(30, 40)]));
        var renderer = Renderer(Scene(firstFloorObjects: [route, risk]));

        Assert.Empty(renderer.SpatialObjects);
        Assert.Equal(2, renderer.GeometryObjects.Count);
        Assert.Equal(3, renderer.GeometryObjects.Single(item => item.Kind == MapSceneGeometryKind.Line).Points.Count);
        Assert.Single(renderer.GeometryObjects, item => item.Kind == MapSceneGeometryKind.Region);
        Assert.Equal(2, renderer.ListItems.Count);
    }

    [Fact]
    public void Out_of_bounds_points_are_list_only_instead_of_clamped_to_an_edge()
    {
        var maximumEdge = Point("edge", "Edge", MapSceneObjectKind.Waypoint, MapSceneTruthKind.UserAuthored, 100, 100);
        var outside = Point("outside", "Outside", MapSceneObjectKind.Waypoint, MapSceneTruthKind.UserAuthored, 1_000, 1_000);
        var renderer = Renderer(Scene(firstFloorObjects: [maximumEdge, outside]));

        var marker = Assert.Single(renderer.SpatialObjects);
        Assert.Equal("Edge", marker.Label);
        Assert.InRange(marker.AnchorLeft, 0, renderer.CanvasWidth - MapSceneRendererViewModel.MarkerExtent);
        Assert.InRange(marker.AnchorTop, 0, renderer.CanvasHeight - MapSceneRendererViewModel.MarkerExtent);
        Assert.Equal(2, renderer.FilteredListCount);
    }

    [Fact]
    public void Marker_hit_extent_clears_the_44px_touch_target_floor()
    {
        // V2 rough package 21: the drawn chip is 32px (see MapSceneRendererView's
        // v2-map-marker-chip style), but the button's own hit/automation box — what this
        // constant positions markers and clusters by — must not regress below the touch-target
        // floor the Windows page gallery measures.
        Assert.True(MapSceneRendererViewModel.MarkerExtent >= 44);
    }

    [Fact]
    public void Dense_scenes_offer_cluster_drilldown_search_and_pages_for_every_object()
    {
        var clustered = Enumerable.Range(0, 305)
            .Select(index => Point(
                $"clustered-{index:D3}",
                $"Clustered loot {index:D3}",
                MapSceneObjectKind.LootSpawn,
                MapSceneTruthKind.PotentialSpawn,
                18,
                18))
            .ToArray();
        var remainder = Enumerable.Range(305, 35)
            .Select(index => Point(
                $"loot-{index:D3}",
                $"Loot {index:D3}",
                MapSceneObjectKind.LootSpawn,
                MapSceneTruthKind.PotentialSpawn,
                index % 100,
                80))
            .ToArray();
        var renderer = Renderer(Scene(firstFloorObjects: clustered.Concat(remainder).ToArray()));

        Assert.Equal(340, renderer.FilteredListCount);
        Assert.Equal(7, renderer.ListPageCount);
        Assert.Equal(MapSceneRendererViewModel.ListPageSize, renderer.ListItems.Count);
        Assert.True(renderer.HasDenseSceneNotice);
        Assert.Contains(renderer.SpatialObjects, item => item.IsCluster);
        Assert.All(renderer.PointMarkers, item => Assert.False(item.IsCluster));
        Assert.All(renderer.ClusterMarkers, item => Assert.True(item.IsCluster));
        Assert.Equal(renderer.SpatialObjects.Count, renderer.PointMarkers.Count + renderer.ClusterMarkers.Count);

        renderer.NextPageCommand.Execute(null);
        Assert.Equal(2, renderer.ListPageNumber);

        var cluster = renderer.SpatialObjects.Single(item => item.IsCluster && item.Label.StartsWith("305", StringComparison.Ordinal));
        cluster.SelectCommand.Execute(null);
        Assert.True(renderer.HasClusterFilter);
        Assert.Equal(305, renderer.FilteredListCount);
        Assert.Equal(7, renderer.ListPageCount);

        renderer.ClearClusterCommand.Execute(null);
        renderer.SearchText = "Loot 339";
        var match = Assert.Single(renderer.ListItems);
        Assert.Equal("Loot 339", match.Label);
        Assert.Equal(1, renderer.FilteredListCount);
    }

    [Fact]
    public void Bare_map_hit_testing_ignores_points_hidden_inside_a_cluster()
    {
        var dense = Enumerable.Range(0, 300)
            .Select(index => Point(
                $"loot-{index}",
                $"Loot {index}",
                MapSceneObjectKind.LootSpawn,
                MapSceneTruthKind.PotentialSpawn,
                50,
                50))
            .ToArray();
        var renderer = Renderer(Scene(firstFloorObjects: dense));

        Assert.Single(renderer.SpatialObjects, item => item.IsCluster);
        Assert.False(renderer.TrySelectAt(renderer.CanvasWidth / 2, renderer.CanvasHeight / 2));
        Assert.False(renderer.HasSelection);

        var ordinary = Renderer(Scene(firstFloorObjects:
        [
            Point("waypoint", "Waypoint", MapSceneObjectKind.Waypoint, MapSceneTruthKind.UserAuthored, 50, 50),
        ]));
        var marker = Assert.Single(ordinary.SpatialObjects);
        Assert.True(ordinary.TrySelectAt(
            marker.AnchorLeft + (MapSceneRendererViewModel.MarkerExtent / 2),
            marker.AnchorTop + (MapSceneRendererViewModel.MarkerExtent / 2)));
        Assert.Equal("Waypoint", ordinary.SelectedObject?.Label);
    }

    [Fact]
    public void TryHitObjectAt_finds_the_object_under_a_point_without_selecting_it()
    {
        var renderer = Renderer(Scene(firstFloorObjects:
        [
            Point("waypoint", "Waypoint", MapSceneObjectKind.Waypoint, MapSceneTruthKind.UserAuthored, 50, 50),
        ]));
        var marker = Assert.Single(renderer.SpatialObjects);

        // A right-click asks what it hit — used to decide whether to remove a mark — without
        // the side effect TrySelectAt has of also changing what is selected.
        Assert.True(renderer.TryHitObjectAt(
            marker.AnchorLeft + (MapSceneRendererViewModel.MarkerExtent / 2),
            marker.AnchorTop + (MapSceneRendererViewModel.MarkerExtent / 2),
            out var objectId));
        Assert.Equal("object:waypoint", objectId.Value);
        Assert.False(renderer.HasSelection);

        Assert.False(renderer.TryHitObjectAt(0, 0, out _));
    }

    [Fact]
    public void TryScenePointAt_unprojects_a_viewport_position_into_scene_space()
    {
        var renderer = Renderer(Scene());

        // The canvas center is where the camera is looking, whatever the viewport size: this is
        // the same unprojection TrySelectAt already relies on to hit-test, exposed for a host —
        // the raid cockpit placing a mark, for one — that needs the point without a hit.
        Assert.True(renderer.TryScenePointAt(renderer.CanvasWidth / 2, renderer.CanvasHeight / 2, out var center));
        Assert.Equal(50, center.X, 3);
        Assert.Equal(50, center.Y, 3);

        Assert.True(renderer.TryScenePointAt(0, 0, out var corner));
        Assert.NotEqual(center.X, corner.X);
        Assert.NotEqual(center.Y, corner.Y);
    }

    [Fact]
    public void Selection_clear_and_camera_updates_preserve_control_collections_and_asset_resolution()
    {
        var resolveCalls = 0;
        var ids = new Queue<Guid>([Guid.Parse("20000000-0000-0000-0000-000000000318")]);
        var renderer = new MapSceneRendererViewModel(
            Scene(revision: 18),
            Presentation,
            ids.Dequeue,
            _ =>
            {
                resolveCalls++;
                return null;
            });
        var modes = renderer.Modes;
        var floors = renderer.Floors;
        var layers = renderer.Layers;
        var markers = renderer.SpatialObjects;
        var list = renderer.ListItems;

        renderer.SelectObject(renderer.ListItems[0].Id);
        renderer.ClearSelection();

        Assert.Same(modes, renderer.Modes);
        Assert.Same(floors, renderer.Floors);
        Assert.Same(layers, renderer.Layers);
        Assert.Same(markers, renderer.SpatialObjects);
        Assert.Same(list, renderer.ListItems);
        Assert.Equal(1, resolveCalls);

        renderer.RequestZoom(1);
        renderer.Present(MapSceneViewReducer.Apply(renderer.Scene, renderer.LastRequestedChange!).Scene);

        Assert.Same(modes, renderer.Modes);
        Assert.Same(floors, renderer.Floors);
        Assert.Same(layers, renderer.Layers);
        Assert.Same(markers, renderer.SpatialObjects);
        Assert.Same(list, renderer.ListItems);
        Assert.Equal(1, resolveCalls);
        Assert.Equal(1.25, renderer.CameraZoom);
        Assert.Equal(0.8, renderer.SpatialObjects[0].MarkerInverseZoom, 6);
    }

    [Fact]
    public void Evidence_uses_the_selected_culture_time_zone_and_localized_resource()
    {
        var culture = CultureInfo.GetCultureInfo("fr-FR");
        var strings = new Dictionary<string, string>(MapSceneRendererPresentation.EnglishStrings, StringComparer.Ordinal)
        {
            ["Map.Evidence"] = "SOURCE={0}; WHEN={1}{2}",
        };
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "renderer-plus-two",
            TimeSpan.FromHours(2),
            "renderer-plus-two",
            "renderer-plus-two");
        var renderer = new MapSceneRendererViewModel(
            Scene(firstFloorObjects:
            [
                Point("culture", "Culture", MapSceneObjectKind.Waypoint, MapSceneTruthKind.UserAuthored, 20, 20),
            ]),
            new(culture, zone, strings));

        var evidence = Assert.Single(renderer.ListItems).EvidenceLabel;
        Assert.StartsWith("SOURCE=fixture; WHEN=", evidence, StringComparison.Ordinal);
        Assert.Contains("UTC+02:00", evidence, StringComparison.Ordinal);
        Assert.Contains("2026", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Narrow_viewports_keep_status_text_inside_the_available_width()
    {
        var renderer = Renderer(Scene());

        renderer.SetViewportSize(296, 280);

        Assert.Equal(296, renderer.CanvasWidth);
        Assert.InRange(renderer.MessageWidth, 1, 272);
        Assert.InRange(renderer.EmptyMessageWidth, 1, 272);
        Assert.InRange(renderer.StatusLeft + renderer.MessageWidth, 1, renderer.CanvasWidth);
        Assert.InRange(renderer.EmptyLeft + renderer.EmptyMessageWidth, 1, renderer.CanvasWidth);
    }

    [Fact]
    public void Reviewed_artwork_without_a_resolver_is_reported_as_not_cached()
    {
        var renderer = Renderer(Scene());

        Assert.False(renderer.HasBackgroundImage);
        Assert.True(renderer.HasBackgroundStatus);
        Assert.Contains("not cached", renderer.BackgroundStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Example map author", renderer.ReviewedAssetLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_returns_reviewed_artwork_for_the_scenes_background_asset()
    {
        var scene = Scene();
        var backgroundAsset = scene.Assets.Single(asset => asset.Kind == MapSceneAssetKind.Background2D);
        var artwork = new StubImage();
        var renderer = new MapSceneRendererViewModel(
            scene,
            Presentation,
            reviewedAssetResolver: asset => string.Equals(asset.ContentSha256, backgroundAsset.ContentSha256, StringComparison.Ordinal)
                ? artwork
                : null);

        Assert.True(renderer.HasBackgroundImage);
        Assert.Same(artwork, renderer.BackgroundImage);
        Assert.False(renderer.HasBackgroundStatus);
    }

    [Fact]
    public void Marker_projection_lands_proportionally_within_the_background_plan_rectangle()
    {
        // The rasterized background is stretched across MapLeft/MapTop/MapWidth/MapHeight (see
        // MapSceneRendererView.axaml), the exact rectangle every scene object is projected into
        // through the same MapSceneProjection. A fixture point at (25, 75) in the scene's 0-100
        // plan space must therefore land a quarter of the way across and three quarters down
        // that rectangle — proof the artwork and the markers share one transform.
        var renderer = Renderer(Scene(firstFloorObjects:
        [
            Point("fixture", "Fixture", MapSceneObjectKind.Waypoint, MapSceneTruthKind.UserAuthored, 25, 75),
        ]));

        var marker = Assert.Single(renderer.SpatialObjects);
        var anchorCenterX = marker.AnchorLeft + (MapSceneRendererViewModel.MarkerExtent / 2);
        var anchorCenterY = marker.AnchorTop + (MapSceneRendererViewModel.MarkerExtent / 2);

        Assert.Equal(renderer.MapLeft + (0.25 * renderer.MapWidth), anchorCenterX, 3);
        Assert.Equal(renderer.MapTop + (0.75 * renderer.MapHeight), anchorCenterY, 3);
    }

    [Fact]
    public void FitPlan_frames_the_whole_plan_within_the_viewport_after_zooming_and_panning()
    {
        var renderer = Renderer(Scene());
        renderer.SetViewportSize(640, 480);

        renderer.RequestZoom(1);
        renderer.Present(MapSceneViewReducer.Apply(renderer.Scene, renderer.LastRequestedChange!).Scene);
        renderer.RequestPan(150, -60);
        renderer.Present(MapSceneViewReducer.Apply(renderer.Scene, renderer.LastRequestedChange!).Scene);
        Assert.NotEqual(1, renderer.CameraZoom);

        renderer.FitPlanCommand.Execute(null);
        renderer.Present(MapSceneViewReducer.Apply(renderer.Scene, renderer.LastRequestedChange!).Scene);

        Assert.Equal(1, renderer.CameraZoom, 6);
        Assert.Equal(0, renderer.CameraRotationDegrees, 6);

        var topLeft = OnScreen(renderer, renderer.MapLeft, renderer.MapTop);
        var bottomRight = OnScreen(renderer, renderer.MapLeft + renderer.MapWidth, renderer.MapTop + renderer.MapHeight);

        Assert.InRange(topLeft.X, 0, renderer.CanvasWidth);
        Assert.InRange(topLeft.Y, 0, renderer.CanvasHeight);
        Assert.InRange(bottomRight.X, 0, renderer.CanvasWidth);
        Assert.InRange(bottomRight.Y, 0, renderer.CanvasHeight);
    }

    /// <summary>
    /// Reproduces MapSceneRendererView.axaml's camera render-transform chain (pre-translate,
    /// rotate, scale, post-translate) so the fit test asserts against the same math the view
    /// actually applies, rather than a hand rederivation of it.
    /// </summary>
    private static (double X, double Y) OnScreen(MapSceneRendererViewModel renderer, double planX, double planY)
    {
        var preX = planX + renderer.CameraPreTranslateX;
        var preY = planY + renderer.CameraPreTranslateY;
        var radians = renderer.CameraRotationDegrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var rotatedX = (cosine * preX) - (sine * preY);
        var rotatedY = (sine * preX) + (cosine * preY);
        return (
            (rotatedX * renderer.CameraZoom) + renderer.CameraPostTranslateX,
            (rotatedY * renderer.CameraZoom) + renderer.CameraPostTranslateY);
    }

    private sealed class StubImage : IImage
    {
        public Size Size => new(256, 256);

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }
    }

    [Fact]
    public void High_value_panel_keeps_typed_list_only_unknown_and_floor_states()
    {
        var renderer = MapSceneRendererGalleryViewModel.Create(largeText: false).Renderer;
        var loot = Assert.IsType<HighValueLootLayerViewModel>(renderer.HighValueLoot);

        Assert.Equal(309, loot.AllEntries.Count);
        Assert.Equal(50, loot.Rows.Count);
        Assert.Contains("Potential spawns", loot.Legend, StringComparison.Ordinal);
        Assert.Contains("positioned", loot.CoverageLabel, StringComparison.OrdinalIgnoreCase);

        var mapOnly = loot.Rows.Single(row => row.Entry.Spawn.SpawnId == "map-only-cache");
        Assert.True(mapOnly.IsListOnly);
        Assert.Contains("location unknown", mapOnly.LocationLabel, StringComparison.OrdinalIgnoreCase);
        mapOnly.SelectCommand.Execute(null);

        Assert.True(renderer.HasLootSelection);
        Assert.Null(renderer.SelectedObject);
        Assert.Equal("map-only-cache", renderer.SelectedLootEntry?.Entry.Spawn.SpawnId);

        var floorUnknown = loot.Rows.Single(row => row.Entry.Spawn.SpawnId == "floor-unresolved");
        Assert.True(floorUnknown.IsListOnly);
        Assert.Equal("Floor unknown", floorUnknown.FloorLabel);

        var unknown = loot.Rows.Single(row => row.Entry.Spawn.SpawnId == "profile-only-unknown-value");
        Assert.Contains("unknown", unknown.ValueLabel, StringComparison.OrdinalIgnoreCase);
        Assert.True(unknown.HasProfileRelevance);
        Assert.Contains("expected value not calculated", unknown.ProbabilityLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void High_value_filters_rebuild_the_canonical_result_and_tier_projection()
    {
        var renderer = MapSceneRendererGalleryViewModel.Create(largeText: false).Renderer;
        var camera = renderer.Scene.View.Camera;
        var floor = renderer.Scene.View.SelectedFloorId;
        var loot = Assert.IsType<HighValueLootLayerViewModel>(renderer.HighValueLoot);

        loot.CategoryChoices.Single(choice => choice.Label == "medical").SelectCommand.Execute(null);

        loot = Assert.IsType<HighValueLootLayerViewModel>(renderer.HighValueLoot);
        Assert.Equal(2, loot.AllEntries.Count);
        Assert.All(loot.AllEntries, entry =>
            Assert.All(entry.Spawn.Candidates, candidate => Assert.Equal("medical", candidate.Category)));
        Assert.Equal(camera, renderer.Scene.View.Camera);
        Assert.Equal(floor, renderer.Scene.View.SelectedFloorId);

        loot.CategoryChoices.Single(choice => choice.Label == "All categories").SelectCommand.Execute(null);
        loot.MinimumTierChoices.Single(choice => choice.Label == "Exceptional").SelectCommand.Execute(null);

        loot = Assert.IsType<HighValueLootLayerViewModel>(renderer.HighValueLoot);
        Assert.Equal(2, loot.FilteredCount);
        Assert.Single(loot.VisibleObjectIds);
        Assert.All(loot.Rows, row => Assert.Equal("Exceptional", row.TierLabel));
    }

    [Fact]
    public void High_value_filter_requests_are_bound_to_the_scene_revision_map_and_transform()
    {
        var scene = Scene(revision: 73, includeHighValueLoot: true);
        var result = UnavailableLootResult();
        var changeId = Guid.Parse("20000000-0000-0000-0000-000000000318");
        var renderer = new MapSceneRendererViewModel(
            scene,
            Presentation,
            () => changeId,
            highValueLoot: result);
        HighValueLootFilterRequest? published = null;
        renderer.HighValueLootFilterRequested += request => published = request;

        renderer.HighValueLoot!.ValueBasisChoices
            .Single(choice => choice.Label == "Flea gross")
            .SelectCommand.Execute(null);

        var request = Assert.IsType<HighValueLootFilterRequest>(published);
        Assert.Equal(changeId, request.ChangeId);
        Assert.Equal(scene.Revision, request.ExpectedRevision);
        Assert.Equal(scene.LocationId, request.LocationId);
        Assert.Equal(scene.TransformVersion, request.TransformVersion);
        Assert.Equal(LootSpawnValueBasis.FleaGross, request.State.Filter.ValueBasis);
    }

    [Fact]
    public void Typed_map_only_loot_cannot_cross_scene_map_or_transform_boundaries()
    {
        var scene = Scene(includeHighValueLoot: true);

        var wrongMap = Assert.Throws<ArgumentException>(() => new MapSceneRendererViewModel(
            scene,
            Presentation,
            highValueLoot: MapOnlyLootResult("shoreline", scene.TransformVersion)));
        var wrongTransform = Assert.Throws<ArgumentException>(() => new MapSceneRendererViewModel(
            scene,
            Presentation,
            highValueLoot: MapOnlyLootResult(scene.LocationId, "different-transform")));

        Assert.Contains("map and transform", wrongMap.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("map and transform", wrongTransform.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Typed_loot_result_cannot_be_presented_under_a_different_filter_state()
    {
        var result = UnavailableLootResult();
        var otherFilter = new HighValueLootFilter(
            LootSpawnValueBasis.FleaGross,
            LootSpawnValueThresholds.Default,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromDays(90),
            0.5);

        var mismatch = Assert.Throws<ArgumentException>(() => new MapSceneRendererViewModel(
            Scene(includeHighValueLoot: true),
            Presentation,
            highValueLoot: result,
            highValueLootFilterState: new(otherFilter, LootSpawnValueTier.Qualifying)));

        Assert.Contains("filter state", mismatch.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void High_value_value_profile_and_floor_controls_use_the_existing_filter_contract()
    {
        var renderer = MapSceneRendererGalleryViewModel.Create(largeText: false).Renderer;
        var loot = Assert.IsType<HighValueLootLayerViewModel>(renderer.HighValueLoot);

        loot.ValueBasisChoices.Single(choice => choice.Label == "Profile utility").SelectCommand.Execute(null);

        loot = Assert.IsType<HighValueLootLayerViewModel>(renderer.HighValueLoot);
        var profileOnly = Assert.Single(loot.AllEntries);
        Assert.Equal("profile-only-unknown-value", profileOnly.Spawn.SpawnId);
        var profileRow = Assert.Single(loot.Rows);
        Assert.Contains("market value not used", profileRow.ValueLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("possible candidates", profileRow.CandidateLabel, StringComparison.OrdinalIgnoreCase);

        loot.ProfileRelevanceChoices.Single(choice => choice.Label == "Market value only")
            .SelectCommand.Execute(null);
        Assert.Empty(loot.AllEntries);

        renderer = MapSceneRendererGalleryViewModel.Create(largeText: false).Renderer;
        loot = Assert.IsType<HighValueLootLayerViewModel>(renderer.HighValueLoot);
        loot.FloorChoices.Single(choice => choice.Label == "upper").SelectCommand.Execute(null);

        var upper = Assert.Single(loot.AllEntries);
        Assert.Equal("profile-only-unknown-value", upper.Spawn.SpawnId);
        Assert.All(upper.Spawn.Location.FloorIds, floor => Assert.Equal("upper", floor));

        renderer = MapSceneRendererGalleryViewModel.Create(largeText: false).Renderer;
        loot = Assert.IsType<HighValueLootLayerViewModel>(renderer.HighValueLoot);
        loot.ValueBasisChoices.Single(choice => choice.Label == "Value per square").SelectCommand.Execute(null);

        var perSquare = loot.Rows.First(row => row.Entry.Spawn.SpawnId.StartsWith("dense-", StringComparison.Ordinal));
        Assert.Contains("₽/square", perSquare.ValueLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void Independent_high_value_layer_toggle_hides_both_markers_and_typed_rows()
    {
        var renderer = MapSceneRendererGalleryViewModel.Create(largeText: false).Renderer;
        var layer = renderer.Layers.Single(item => item.Layer.Id == HighValueLootLayerService.LayerId);

        layer.ToggleCommand.Execute(null);

        var loot = Assert.IsType<HighValueLootLayerViewModel>(renderer.HighValueLoot);
        Assert.Empty(loot.Rows);
        Assert.Empty(loot.VisibleObjectIds);
        Assert.DoesNotContain(renderer.SpatialObjects, item =>
            item.SceneObject?.LayerId == HighValueLootLayerService.LayerId);
        Assert.Contains("off", loot.StateMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void High_value_preset_keeps_orientation_camera_and_selected_spawn()
    {
        var renderer = MapSceneRendererGalleryViewModel.Create(largeText: false).Renderer;
        var camera = renderer.Scene.View.Camera;
        var floor = renderer.Scene.View.SelectedFloorId;
        var loot = Assert.IsType<HighValueLootLayerViewModel>(renderer.HighValueLoot);
        loot.Rows.Single(row => row.Entry.Spawn.SpawnId == "medical-exceptional")
            .SelectCommand.Execute(null);

        renderer.HighValueLootPresetCommand.Execute(null);

        Assert.Equal(camera, renderer.Scene.View.Camera);
        Assert.Equal(floor, renderer.Scene.View.SelectedFloorId);
        Assert.Equal("medical-exceptional", renderer.SelectedLootEntry?.Entry.Spawn.SpawnId);
        Assert.True(IsVisible(renderer, HighValueLootLayerService.LayerId));
        Assert.True(IsVisible(renderer, new("extracts")));
        Assert.True(IsVisible(renderer, new("companion-markers")));
        Assert.True(IsVisible(renderer, new("hazards")));
        Assert.False(IsVisible(renderer, new("estimates")));
    }

    [Fact]
    public void High_value_preset_keeps_a_visible_host_selected_context_layer()
    {
        var result = new HighValueLootLayerResult(
            HighValueLootLayerService.Layer,
            "customs",
            "transform-1",
            HighValueLootFilter.Default,
            new(ResultCompleteness.Unavailable, FreshnessState.Unknown, "fixture.unavailable"),
            "Potential spawns · Data unavailable",
            null,
            null,
            [],
            [],
            []);
        var original = Scene(includeHighValueLoot: true);
        var safety = new MapSceneLayer(new("host-safety"), "Host safety", 35, true);
        var scene = new MapSceneSnapshot(
            original.Revision,
            original.LocationId,
            original.VariantKey,
            original.TransformVersion,
            original.Bounds,
            original.FloorIds,
            original.Capabilities,
            new(
                original.View.Mode,
                original.View.SelectedFloorId,
                original.View.Camera,
                original.View.Layers.Append(new MapSceneLayerState(safety.Id, true)).ToArray()),
            original.Layers.Append(safety).ToArray(),
            original.Objects,
            original.Assets);
        var renderer = new MapSceneRendererViewModel(
            scene,
            Presentation,
            highValueLoot: result,
            highValueLootPresetPreservedLayers: [safety.Id]);
        renderer.ViewChangeRequested += change =>
            renderer.Present(MapSceneViewReducer.Apply(renderer.Scene, change).Scene);

        renderer.HighValueLootPresetCommand.Execute(null);

        Assert.True(IsVisible(renderer, safety.Id));
        Assert.True(IsVisible(renderer, new("hazards")));
        Assert.True(IsVisible(renderer, HighValueLootLayerService.LayerId));
        Assert.False(IsVisible(renderer, new("estimates")));
    }

    [Fact]
    public void High_value_preset_stops_after_an_owner_republishes_the_rejected_revision()
    {
        var result = new HighValueLootLayerResult(
            HighValueLootLayerService.Layer,
            "customs",
            "transform-1",
            HighValueLootFilter.Default,
            new(ResultCompleteness.Unavailable, FreshnessState.Unknown, "fixture.unavailable"),
            "Potential spawns · Data unavailable",
            null,
            null,
            [],
            [],
            []);
        var scene = Scene(includeHighValueLoot: true);
        var renderer = new MapSceneRendererViewModel(
            scene,
            Presentation,
            () => Guid.Parse("20000000-0000-0000-0000-000000000319"),
            highValueLoot: result);
        var requestCount = 0;
        renderer.ViewChangeRequested += _ =>
        {
            requestCount++;
            renderer.Present(scene);
        };

        renderer.HighValueLootPresetCommand.Execute(null);

        Assert.Equal(1, requestCount);
        Assert.Same(scene, renderer.Scene);
        Assert.Contains("changed", renderer.RendererNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void High_value_preset_keeps_earlier_steps_when_a_later_step_is_rejected()
    {
        var result = new HighValueLootLayerResult(
            HighValueLootLayerService.Layer,
            "customs",
            "transform-1",
            HighValueLootFilter.Default,
            new(ResultCompleteness.Unavailable, FreshnessState.Unknown, "fixture.unavailable"),
            "Potential spawns · Data unavailable",
            null,
            null,
            [],
            [],
            []);
        var renderer = new MapSceneRendererViewModel(
            Scene(includeHighValueLoot: true),
            Presentation,
            highValueLoot: result);
        var requestCount = 0;
        renderer.ViewChangeRequested += change =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                renderer.Present(MapSceneViewReducer.Apply(renderer.Scene, change).Scene);
                return;
            }

            renderer.Present(renderer.Scene);
        };

        renderer.HighValueLootPresetCommand.Execute(null);

        Assert.Equal(2, requestCount);
        Assert.False(IsVisible(renderer, new("estimates")));
        Assert.False(IsVisible(renderer, HighValueLootLayerService.LayerId));
        Assert.True(IsVisible(renderer, new("hazards")));
        Assert.Contains("Earlier layer changes remain applied", renderer.RendererNotice,
            StringComparison.Ordinal);
    }

    [Fact]
    public void High_value_filter_choices_bound_untrusted_host_enumeration_and_rendering()
    {
        var result = new HighValueLootLayerResult(
            HighValueLootLayerService.Layer,
            "customs",
            "transform-1",
            HighValueLootFilter.Default,
            new(ResultCompleteness.Unavailable, FreshnessState.Unknown, "fixture.unavailable"),
            "Potential spawns · Data unavailable",
            null,
            null,
            [],
            [],
            []);
        var categories = new LyingOptionList("category");
        var floors = new LyingOptionList("floor");

        var loot = new HighValueLootLayerViewModel(
            result,
            HighValueLootLayerFilterState.Default,
            categories,
            floors,
            true,
            Presentation,
            _ => { },
            _ => { });

        Assert.Equal(HighValueLootLayerViewModel.MaximumRenderedFilterOptions + 1, loot.CategoryChoices.Count);
        Assert.Equal(HighValueLootLayerViewModel.MaximumRenderedFilterOptions + 1, loot.FloorChoices.Count);
        Assert.True(loot.HasCategoryOptionsNotice);
        Assert.True(loot.HasFloorOptionsNotice);
        Assert.InRange(categories.EnumeratedCount, 1, 257);
        Assert.InRange(floors.EnumeratedCount, 1, 257);
    }

    [Fact]
    public void High_value_filter_cap_keeps_the_active_choice_visible_and_discloses_multi_category_state()
    {
        var result = UnavailableLootResult();
        var active = Filter(categories: ["selected-beyond-cap"]);
        var loot = new HighValueLootLayerViewModel(
            result,
            new(active, LootSpawnValueTier.Qualifying),
            Enumerable.Range(0, 20).Select(index => $"category-{index:D2}").ToArray(),
            ["first"],
            true,
            Presentation,
            _ => { },
            _ => { });

        Assert.Contains(loot.CategoryChoices, choice =>
            choice.Label == "selected-beyond-cap" && choice.IsSelected);
        Assert.True(loot.HasCategoryOptionsNotice);

        loot.Present(
            result,
            new(Filter(categories: ["medical", "electronics"]), LootSpawnValueTier.Qualifying),
            null,
            null);

        Assert.True(loot.HasMultipleCategoryFilter);
        Assert.Contains("2", loot.MultipleCategoryFilterMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FreshnessState.Stale, "last-known")]
    [InlineData(FreshnessState.Unknown, "could not be verified")]
    public void High_value_panel_exposes_non_current_freshness_with_recovery_guidance(
        FreshnessState freshness,
        string expected)
    {
        var result = new HighValueLootLayerResult(
            HighValueLootLayerService.Layer,
            "customs",
            "transform-1",
            HighValueLootFilter.Default,
            new(ResultCompleteness.Complete, freshness),
            "Potential spawns",
            null,
            null,
            [],
            [],
            []);
        var loot = new HighValueLootLayerViewModel(
            result,
            HighValueLootLayerFilterState.Default,
            [],
            [],
            true,
            Presentation,
            _ => { },
            _ => { });

        Assert.True(loot.HasFreshnessMessage);
        Assert.Contains(expected, loot.FreshnessMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void High_value_selection_discloses_versioned_source_timing_confidence_and_coverage()
    {
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.PublicStructuredData,
            "fixture://versioned-source",
            new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero),
            new(EvidenceConfidenceKind.CalibratedEstimate, 0.80, "fixture-calibration-v2"),
            new("loot-importer", "2.1", "ranking-v3"),
            dataThroughUtc: new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
            generatedUtc: new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero),
            coverage: new(100, 0.75, "13 of 17 supported maps"));
        var loot = new HighValueLootLayerViewModel(
            MapOnlyLootResult("customs", "transform-1", provenance),
            HighValueLootLayerFilterState.Default,
            [],
            [],
            true,
            Presentation,
            _ => { },
            _ => { });

        var row = Assert.Single(loot.Rows);
        Assert.Contains("fixture-dataset", row.DatasetLabel, StringComparison.Ordinal);
        Assert.Contains("transform-1", row.DatasetLabel, StringComparison.Ordinal);
        Assert.Contains("Public structured data", row.SourceLabel, StringComparison.Ordinal);
        Assert.Contains("fixture://versioned-source", row.SourceLabel, StringComparison.Ordinal);
        Assert.Contains("ranking-v3", row.ProducerLabel, StringComparison.Ordinal);
        Assert.Contains("generated", row.EvidenceTimeLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Calibrated estimate", row.ConfidenceLabel, StringComparison.Ordinal);
        Assert.Contains("fixture-calibration-v2", row.ConfidenceLabel, StringComparison.Ordinal);
        Assert.Contains("75", row.SourceCoverageLabel, StringComparison.Ordinal);
        Assert.Contains("sample 100", row.SourceCoverageLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("13 of 17", row.SourceCoverageLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void High_value_offline_gallery_has_an_explicit_unavailable_empty_state()
    {
        var renderer = MapSceneRendererGalleryViewModel.Create(largeText: false, lootOffline: true).Renderer;
        var loot = Assert.IsType<HighValueLootLayerViewModel>(renderer.HighValueLoot);

        Assert.Empty(loot.AllEntries);
        Assert.True(loot.ShowsEmpty);
        Assert.Contains("Offline", loot.Legend, StringComparison.Ordinal);
        Assert.Contains("unavailable", loot.StateMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(renderer.Layers, layer => layer.Layer.Id == HighValueLootLayerService.LayerId);
    }

    private static HighValueLootLayerResult UnavailableLootResult() => new(
        HighValueLootLayerService.Layer,
        "customs",
        "transform-1",
        HighValueLootFilter.Default,
        new(ResultCompleteness.Unavailable, FreshnessState.Unknown, "fixture.unavailable"),
        "Potential spawns · Data unavailable",
        null,
        null,
        [],
        [],
        []);

    private static HighValueLootLayerResult MapOnlyLootResult(
        string mapId,
        string transformVersion,
        EvidenceProvenance? sourceProvenance = null)
    {
        var provenance = sourceProvenance ?? LootProvenance();
        var status = new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current);
        var candidate = new LootSpawnCandidate(
            "fixture-item",
            "Fixture item",
            "fixture-category",
            new("gross", 100_000L, status, provenance),
            new("net", 90_000L, status, provenance),
            new("trader", 50_000L, status, provenance),
            new("squares", 1, status, provenance));
        var spawn = new LootSpawnRecord(
            "map-only",
            mapId,
            "Map-only fixture",
            new(LootSpawnPrecision.MapOnly, null),
            LootSpawnPoolKind.SingleKnownItem,
            [candidate],
            new EvidencedValue<double?>(
                "probability",
                null,
                new(ResultCompleteness.Unknown, FreshnessState.Current),
                provenance),
            new EvidencedValue<string?>(
                "respawn",
                null,
                new(ResultCompleteness.Unknown, FreshnessState.Current),
                provenance),
            "fixture-dataset",
            transformVersion,
            status,
            provenance);
        var entry = new HighValueLootEntry(
            spawn,
            LootSpawnValueTier.Moderate,
            90_000,
            90_000,
            90_000,
            90_000,
            1,
            1,
            1,
            true,
            "best net",
            "Potential fixture item",
            [],
            [],
            [],
            null,
            null,
            null);
        return new(
            HighValueLootLayerService.Layer,
            mapId,
            transformVersion,
            HighValueLootFilter.Default,
            status,
            "Potential spawns",
            provenance.EvidenceThroughUtc,
            new(1, 0, 0, 1),
            [],
            [entry],
            []);
    }

    private static HighValueLootFilter Filter(IReadOnlyList<string>? categories = null) => new(
        LootSpawnValueBasis.BestNet,
        LootSpawnValueThresholds.Default,
        TimeSpan.FromMinutes(30),
        TimeSpan.FromDays(90),
        0.5,
        categories: categories);

    private static EvidenceProvenance LootProvenance() => new(
        EvidenceSourceClass.PublicStructuredData,
        "fixture://map-renderer",
        new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero),
        new(EvidenceConfidenceKind.ProviderScore, 0.95),
        new("map-renderer-tests", "1"));

    private static bool IsVisible(MapSceneRendererViewModel renderer, MapSceneLayerId layerId) =>
        renderer.Scene.View.Layers.Single(state => state.LayerId == layerId).IsVisible;

    // ---- V2 rough package 20 ----

    [Fact]
    public void Plan_keeps_the_artwork_shape_instead_of_being_stretched_to_the_scene_box()
    {
        // Every scene uses the same normalized 0-100 box, so without the artwork's own shape a
        // wide map and a tall one both drew as the same square.
        var renderer = Renderer(Scene(), artwork: new Size(1600, 400));
        renderer.SetViewportSize(1200, 800);

        Assert.Equal(4d, renderer.MapWidth / renderer.MapHeight, 3);
        Assert.True(renderer.MapWidth <= 1200, "The plan is contained, never cropped by the fit.");
        Assert.True(renderer.MapHeight <= 800, "The plan is contained, never cropped by the fit.");
        // Centred in the viewport, both ways.
        Assert.Equal(renderer.MapLeft, 1200 - renderer.MapLeft - renderer.MapWidth, 3);
        Assert.Equal(renderer.MapTop, 800 - renderer.MapTop - renderer.MapHeight, 3);
    }

    [Theory]
    [InlineData(1600, 400)]
    [InlineData(400, 1600)]
    [InlineData(1024, 1024)]
    public void Plan_fits_inside_every_viewport_it_is_given_at_its_own_aspect(double artworkWidth, double artworkHeight)
    {
        var renderer = Renderer(Scene(), artwork: new Size(artworkWidth, artworkHeight));
        foreach (var (width, height) in new[] { (1200d, 700d), (3200d, 800d), (620d, 900d) })
        {
            renderer.SetViewportSize(width, height);

            Assert.Equal(artworkWidth / artworkHeight, renderer.MapWidth / renderer.MapHeight, 3);
            Assert.True(renderer.MapWidth <= width + 0.001);
            Assert.True(renderer.MapHeight <= height + 0.001);
        }
    }

    [Fact]
    public void Dragging_moves_the_plan_one_to_one_and_commits_one_camera_when_it_ends()
    {
        var renderer = Renderer(Scene(), () => Guid.Parse("20000000-0000-0000-0000-000000000320"));
        renderer.SetViewportSize(1200, 800);
        // Zoomed in first, because there is nothing to drag at the fit: a plan that already fits
        // the card stays centred in it rather than sliding to one side (see Clamp).
        ZoomTo(renderer, 3);
        var published = new List<MapSceneViewChange>();
        renderer.ViewChangeRequested += published.Add;
        var restX = renderer.CameraPostTranslateX;
        var restY = renderer.CameraPostTranslateY;

        renderer.BeginPan();
        renderer.UpdatePan(40, -25);

        // The plan tracks the pointer exactly, and nothing has reached the scene yet.
        Assert.Equal(restX + 40, renderer.CameraPostTranslateX, 3);
        Assert.Equal(restY - 25, renderer.CameraPostTranslateY, 3);
        Assert.True(renderer.IsPanning);
        Assert.Empty(published);

        renderer.UpdatePan(80, -50);
        Assert.Equal(restX + 80, renderer.CameraPostTranslateX, 3);
        Assert.Empty(published);

        renderer.CommitPan();

        var change = Assert.Single(published);
        Assert.Equal(MapSceneViewChangeKind.SetCamera, change.Kind);
        Assert.False(renderer.IsPanning);
        Assert.Equal(restX, renderer.CameraPostTranslateX, 3);
        Assert.Equal(restY, renderer.CameraPostTranslateY, 3);
    }

    [Fact]
    public void An_abandoned_drag_puts_the_plan_back_and_changes_no_camera()
    {
        var renderer = Renderer(Scene(), () => Guid.Parse("20000000-0000-0000-0000-000000000321"));
        renderer.SetViewportSize(1200, 800);
        ZoomTo(renderer, 3);
        var published = new List<MapSceneViewChange>();
        renderer.ViewChangeRequested += published.Add;
        var restX = renderer.CameraPostTranslateX;

        renderer.BeginPan();
        renderer.UpdatePan(120, 0);
        renderer.CancelPan();

        Assert.Empty(published);
        Assert.Equal(restX, renderer.CameraPostTranslateX, 3);
    }

    [Fact]
    public void Wheel_zoom_keeps_the_place_under_the_pointer_under_the_pointer()
    {
        var renderer = Renderer(Scene(), () => Guid.Parse("20000000-0000-0000-0000-000000000322"));
        renderer.SetViewportSize(1200, 800);
        // Far enough in that the plan is larger than the card: only then is there anywhere for
        // the point under the pointer to stay, rather than the plan sitting centred in the card.
        ZoomTo(renderer, 3);
        MapSceneViewChange? published = null;
        renderer.ViewChangeRequested += change => published = change;
        Assert.True(renderer.TryScenePointAt(900, 250, out var before));

        renderer.RequestZoomAt(1, 900, 250);

        var camera = Assert.IsType<MapSceneViewChange>(published).Camera;
        Assert.NotNull(camera);
        Assert.True(camera.Value.Zoom > renderer.Scene.View.Camera.Zoom);
        // Apply the requested camera and ask again where that viewport point is now.
        var applied = MapSceneViewReducer.Apply(renderer.Scene, published!);
        renderer.Present(applied.Scene);
        Assert.True(renderer.TryScenePointAt(900, 250, out var after));
        Assert.Equal(before.X, after.X, 1);
        Assert.Equal(before.Y, after.Y, 1);
    }

    [Fact]
    public void A_camera_change_is_never_refused_for_an_in_flight_change()
    {
        // The host applies nothing here, so the revision never moves: the guard that refuses a
        // second change while one is in flight used to stall every later pan and zoom.
        var renderer = Renderer(Scene(), () => Guid.NewGuid());
        renderer.SetViewportSize(1200, 800);
        var published = new List<MapSceneViewChange>();
        renderer.ViewChangeRequested += published.Add;

        renderer.RequestZoom(1);
        renderer.RequestZoom(1);
        renderer.RequestPan(30, 30);

        Assert.Equal(3, published.Count);
        Assert.False(renderer.HasRendererNotice);
    }

    [Fact]
    public void No_marker_ever_renders_a_question_mark_for_something_unknown()
    {
        var renderer = Renderer(Scene(firstFloorObjects:
        [
            Extract("unknown-offer", "Unknown", "first", MapSceneOfferState.Unknown, MapFeatureFaction.Unknown),
        ]));

        var marker = Assert.Single(renderer.PointMarkers);
        Assert.Equal(MapSceneMarkerIcon.Extract, marker.Icon);
        Assert.True(marker.IsExtractIcon);
        Assert.DoesNotContain("?", marker.MarkerGlyph, StringComparison.Ordinal);
        Assert.DoesNotContain("?", marker.TruthGlyph, StringComparison.Ordinal);
        Assert.DoesNotContain("?", marker.FactionGlyph, StringComparison.Ordinal);
        Assert.DoesNotContain("?", marker.OfferGlyph, StringComparison.Ordinal);
        // Unknown still reaches assistive technology, as words rather than a glyph.
        Assert.False(string.IsNullOrWhiteSpace(marker.AutomationName));
    }

    [Fact]
    public void A_numbered_waypoint_draws_its_number_and_everything_else_draws_an_icon()
    {
        var renderer = Renderer(Scene(firstFloorObjects:
        [
            Point("wp", "3", MapSceneObjectKind.Waypoint, MapSceneTruthKind.UserAuthored, 30, 40),
            Point("ping", "Ping", MapSceneObjectKind.Ping, MapSceneTruthKind.UserAuthored, 60, 40),
        ]));

        var waypoint = renderer.PointMarkers.Single(item => item.Key == "object:wp");
        var ping = renderer.PointMarkers.Single(item => item.Key == "object:ping");
        Assert.True(waypoint.HasMarkerNumber);
        Assert.False(waypoint.ShowsMarkerIcon);
        Assert.Equal("3", waypoint.MarkerGlyph);
        Assert.True(ping.ShowsMarkerIcon);
        Assert.True(ping.IsPingIcon);
    }

    [Fact]
    public void The_dense_scene_notice_is_a_chip_with_the_wording_behind_it()
    {
        var renderer = Renderer(Scene(firstFloorObjects:
        [
            // Outside the scene's own 0-100 bounds, which is what makes this list-only.
            Point("far", "Far", MapSceneObjectKind.Lock, MapSceneTruthKind.StaticReference, 140, 50),
        ]));

        Assert.True(renderer.HasDenseSceneNotice);
        Assert.Equal("1 off-plan", renderer.DenseSceneChip);
        Assert.Contains("outside reviewed bounds", renderer.DenseSceneNotice, StringComparison.Ordinal);
        Assert.True(renderer.DenseSceneChip.Length < renderer.DenseSceneNotice.Length / 4);
    }

    /// <summary>Presents the same scene with the camera at a given zoom, as the host would.</summary>
    private static void ZoomTo(MapSceneRendererViewModel renderer, double zoom)
    {
        var camera = renderer.Scene.View.Camera;
        var scene = renderer.Scene;
        renderer.Present(new(
            scene.Revision + 1,
            scene.LocationId,
            scene.VariantKey,
            scene.TransformVersion,
            scene.Bounds,
            scene.FloorIds,
            scene.Capabilities,
            scene.View with { Camera = new(camera.CenterX, camera.CenterY, zoom, camera.BearingDegrees, camera.PitchDegrees) },
            scene.Layers,
            scene.Objects,
            scene.Assets));
    }

    private static MapSceneRendererViewModel Renderer(
        MapSceneSnapshot scene,
        Func<Guid>? nextChangeId = null,
        Size? artwork = null) => artwork is { } size
        ? new(scene, Presentation, nextChangeId, _ => new TestArtwork(size))
        : new(scene, Presentation, nextChangeId);

    /// <summary>Stands in for decoded map artwork; only its shape matters to the projection.</summary>
    private sealed class TestArtwork(Size size) : IImage
    {
        public Size Size { get; } = size;

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }
    }

    private static MapSceneSnapshot Scene(
        long revision = 7,
        bool supportsFloorStack = false,
        bool interiorAvailable = false,
        MapSceneMode mode = MapSceneMode.Flat2D,
        IReadOnlyList<MapSceneObject>? firstFloorObjects = null,
        bool includeHighValueLoot = false)
    {
        var extracts = new MapSceneLayer(new("extracts"), "Extracts", 10, true);
        var loot = new MapSceneLayer(new("loot"), "Loot", 20, false);
        var hazards = new MapSceneLayer(new("hazards"), "Hazards", 25, true);
        var estimates = new MapSceneLayer(new("estimates"), "Historical estimates", 30, true);
        IReadOnlyList<MapSceneLayer> layers = includeHighValueLoot
            ? [extracts, loot, hazards, estimates, HighValueLootLayerService.Layer]
            : [extracts, loot];
        IReadOnlyList<MapSceneLayerState> layerStates = includeHighValueLoot
            ? [
                new(extracts.Id, true),
                new(loot.Id, false),
                new(hazards.Id, true),
                new(estimates.Id, true),
                new(HighValueLootLayerService.LayerId, false),
            ]
            : [new(extracts.Id, true), new(loot.Id, false)];
        var objects = firstFloorObjects ?? [Extract("crossroads", "Crossroads", "first", MapSceneOfferState.Unknown)];
        return new(
            revision,
            "customs",
            "customs-plan",
            "transform-1",
            new(0, 0, 100, 100),
            ["first", "second"],
            new(
                MapSceneCapability.Available,
                supportsFloorStack ? MapSceneCapability.Available : MapSceneCapability.Unavailable("No reviewed floor stack."),
                interiorAvailable ? MapSceneCapability.Available : MapSceneCapability.Unavailable("No reviewed interior model.")),
            new(mode, "first", new(50, 50, 1, 0, 0), layerStates),
            layers,
            objects,
            [Asset()]);
    }

    private static MapSceneObject Extract(
        string id,
        string label,
        string floor,
        MapSceneOfferState offerState,
        MapFeatureFaction faction = MapFeatureFaction.Pmc,
        double x = 25) => new(
            new($"extract:{id}"),
            new("extracts"),
            MapSceneObjectKind.Extract,
            MapSceneTruthKind.StaticReference,
            label,
            $"{label} details",
            MapSceneGeometry.At(new(x, 50)),
            [floor],
            Provenance(),
            faction: faction,
            offerState: offerState);

    private static MapSceneObject Point(
        string id,
        string label,
        MapSceneObjectKind kind,
        MapSceneTruthKind truth,
        double x,
        double y) => SceneObject(id, kind, truth, MapSceneGeometry.At(new(x, y)), label);

    private static MapSceneObject Historical(string id, string label, double x, double y) => new(
        new($"object:{id}"),
        new("extracts"),
        MapSceneObjectKind.Traffic,
        MapSceneTruthKind.HistoricalEstimate,
        label,
        $"{label} details",
        MapSceneGeometry.At(new(x, y)),
        ["first"],
        Provenance(0.7),
        new(
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero),
            "catalog sample",
            "uncalibrated",
            "transform-1",
            "model-1"));

    private static MapSceneObject SceneObject(
        string id,
        MapSceneObjectKind kind,
        MapSceneTruthKind truth,
        MapSceneGeometry geometry,
        string? label = null) => new(
            new($"object:{id}"),
            new("extracts"),
            kind,
            truth,
            label ?? char.ToUpperInvariant(id[0]) + id[1..],
            $"{id} details",
            geometry,
            ["first"],
            Provenance(),
            faction: MapFeatureFaction.Unknown);

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

    private static DataProvenance Provenance(double confidence = 1) => new(
        "fixture",
        new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero),
        Confidence: new Confidence(confidence));

    private sealed class LyingOptionList(string prefix) : IReadOnlyList<string>
    {
        public int EnumeratedCount { get; private set; }

        public int Count => 0;

        public string this[int index] => $"{prefix}-{index:D4}";

        public IEnumerator<string> GetEnumerator()
        {
            for (var index = 0; ; index++)
            {
                EnumeratedCount++;
                yield return this[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
