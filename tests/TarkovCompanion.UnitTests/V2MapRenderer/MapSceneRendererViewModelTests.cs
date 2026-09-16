using System.Globalization;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Core.Common;
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

        var floorStack = renderer.Modes.Single(item => item.Mode == MapSceneMode.FloorStack2D);
        var interior = renderer.Modes.Single(item => item.Mode == MapSceneMode.Interior3D);
        Assert.False(floorStack.IsAvailable);
        Assert.False(interior.IsAvailable);
        Assert.Contains("floor", floorStack.UnavailableReason, StringComparison.OrdinalIgnoreCase);

        renderer.RequestMode(MapSceneMode.FloorStack2D);

        Assert.Empty(published);
        Assert.True(renderer.HasRendererNotice);
        Assert.Contains("unavailable", renderer.RendererNotice, StringComparison.OrdinalIgnoreCase);

        renderer.Layers.Single(layer => layer.Layer.Id == new MapSceneLayerId("loot"))
            .ToggleCommand.Execute(null);

        var change = Assert.Single(published);
        Assert.Equal(scene.Revision, change.ExpectedRevision);
        Assert.Equal(MapSceneViewChangeKind.SetLayerVisibility, change.Kind);
        Assert.Equal("loot", change.LayerId?.Value);
        Assert.True(change.IsVisible.GetValueOrDefault());
        Assert.Same(scene, renderer.Scene);
    }

    [Fact]
    public void Canonical_floor_stack_state_gets_an_honest_flat_fallback()
    {
        var renderer = Renderer(Scene(
            supportsFloorStack: true,
            mode: MapSceneMode.FloorStack2D));

        Assert.True(renderer.ShowsModeFallback);
        Assert.Contains("floor-filtered 2D plan", renderer.ModeFallbackNotice, StringComparison.OrdinalIgnoreCase);
        Assert.True(renderer.Modes.Single(item => item.Mode == MapSceneMode.Flat2D).IsSelected);
        Assert.False(renderer.Modes.Single(item => item.Mode == MapSceneMode.FloorStack2D).IsAvailable);
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
        Assert.Equal(("P/S", "?"), (shared.FactionGlyph, shared.OfferGlyph));
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

    private static MapSceneRendererViewModel Renderer(MapSceneSnapshot scene, Func<Guid>? nextChangeId = null) =>
        new(scene, Presentation, nextChangeId);

    private static MapSceneSnapshot Scene(
        long revision = 7,
        bool supportsFloorStack = false,
        bool interiorAvailable = false,
        MapSceneMode mode = MapSceneMode.Flat2D,
        IReadOnlyList<MapSceneObject>? firstFloorObjects = null)
    {
        var extracts = new MapSceneLayer(new("extracts"), "Extracts", 10, true);
        var loot = new MapSceneLayer(new("loot"), "Loot", 20, false);
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
            new(mode, "first", new(50, 50, 1, 0, 0), [new(extracts.Id, true), new(loot.Id, false)]),
            [extracts, loot],
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
}
