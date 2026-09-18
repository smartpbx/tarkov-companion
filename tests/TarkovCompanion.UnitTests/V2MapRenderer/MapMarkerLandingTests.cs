using System.Globalization;
using Avalonia;
using Avalonia.Media;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [V2 rough package 23] Everything drawn on a map lands on the artwork.
/// </summary>
/// <remarks>
/// #393 checked this proportionally: a scene point at (25, 75) in a 0–100 box lands a quarter
/// across and three quarters down the drawn rectangle. That held while the fault was upstream of
/// it — the cockpit declared a 0–100 box as the plan's bounds and then filled the scene with
/// Leaflet map units from the variant's own transform, so the box and the coordinates in it
/// described different rectangles and Customs' extracts piled up against the plan's edges
/// instead of sitting on its exits.
///
/// These tests therefore start from world coordinates and a catalog transform, run the real
/// adapter (<see cref="RaidCockpitViewModel.PlanBoundsFor"/>, the assembler, the renderer) and
/// check the marker against where the artwork itself puts that world position — computed here
/// from the variant's bounds corners, independently of the code under test. They are run on a
/// wide map and a tall one, at two card sizes, turned and unturned, because the fault was
/// invisible on any map whose projected bounds happened to resemble a 0–100 square.
///
/// The extract tests' world coordinates are fixture values: the transforms are shaped like the
/// catalog's (a non-unit scale, an offset, a coordinate rotation, bounds well away from 0–100)
/// rather than quoted from it.
///
/// [Package 35] The quest objective tests below are the other kind, and deliberately so. They run
/// the real Customs, Lighthouse and Interchange definitions (parsed by the production catalog
/// parser) and twenty objectives with their zones as json.tarkov.dev listed them, through the real
/// quest projection and the scene builder both screens use, and then ask the renderer where each
/// objective is against where the artwork puts the same world coordinates. Nothing in them is
/// shaped like the data; it is the data.
/// </remarks>
public sealed class MapMarkerLandingTests
{
    private static readonly MapSceneRendererPresentation Presentation =
        MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc);

    private static readonly DateTimeOffset NowUtc = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Where three known Customs features are, in world coordinates.</summary>
    private static readonly (string Name, WorldPosition World)[] CustomsFeatures =
    [
        ("Old Gas Station", new(-120, 0, -60)),
        ("Crossroads", new(-340, 0, 90)),
        ("RUAF Roadblock", new(480, 0, -250)),
    ];

    private static readonly (string Name, WorldPosition World)[] LighthouseFeatures =
    [
        ("Northern Road", new(-760, 0, 180)),
        ("Path to Lighthouse", new(240, 0, -95)),
        ("V-Ex", new(700, 0, 220)),
    ];

    public static TheoryData<int, int, double> Cards => new()
    {
        { 1920, 1080, 0 },
        { 1920, 1080, 90 },
        { 3840, 1080, 0 },
        { 3840, 1080, 270 },
    };

    [Theory]
    [MemberData(nameof(Cards))]
    public void Customs_extracts_land_on_the_artwork(int width, int height, double bearingDegrees)
    {
        AssertFeaturesLand(Customs(), CustomsFeatures, width, height, bearingDegrees);
    }

    [Theory]
    [MemberData(nameof(Cards))]
    public void Lighthouse_extracts_land_on_the_artwork(int width, int height, double bearingDegrees)
    {
        AssertFeaturesLand(Lighthouse(), LighthouseFeatures, width, height, bearingDegrees);
    }

    public static TheoryData<string, int, int, double> RealMapCards()
    {
        var data = new TheoryData<string, int, int, double>();
        foreach (var map in new[] { "customs", "lighthouse", "interchange" })
        {
            foreach (var (width, height, bearing) in new[] { (1920, 1080, 0d), (1920, 1080, 90d), (3840, 1080, 0d), (3840, 1080, 270d) })
            {
                data.Add(map, width, height, bearing);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RealMapCards))]
    public void Quest_objectives_from_the_feed_land_on_the_artwork(string map, int width, int height, double bearingDegrees)
    {
        var zones = RealQuestZones.Load();
        AssertObjectivesLand(zones, map, zones.Model(map), width, height, bearingDegrees);
    }

    [Theory]
    [InlineData("customs", 1920, 1080, 0)]
    [InlineData("customs", 3840, 1080, 270)]
    [InlineData("interchange", 1920, 1080, 90)]
    [InlineData("interchange", 3840, 1080, 0)]
    public void Quest_objectives_land_on_the_tile_artwork_too(string map, int width, int height, double bearingDegrees)
    {
        // The cockpit opens on the tile pyramid where a map publishes one, and the plan rectangle
        // is then the tile grid snapped outwards, which is a different rectangle from the drawing's.
        var zones = RealQuestZones.Load();
        var model = zones.Model(map, MapBackgroundKind.TileTemplate);
        model = model with { Background = model.Background! with { TileZoom = model.Variant.MinimumZoom } };
        AssertObjectivesLand(zones, map, model, width, height, bearingDegrees);
    }

    [Fact]
    public void The_map_as_tarkov_dev_publishes_it_carries_no_game_id_so_the_quest_query_needs_one()
    {
        // The reason MapViewModel asks the synced maps table for the game's id before it reads the
        // quests of a map: json.tarkov.dev names a zone's map by that id, the map catalog names
        // the map only by its slug, and a location asked for by slug alone matches no objective.
        var zones = RealQuestZones.Load();
        foreach (var map in new[] { "customs", "lighthouse", "interchange" })
        {
            var published = zones.PublishedLocation(map);
            Assert.Null(published.SourceId);
            Assert.DoesNotContain(
                zones.GameMapId(map),
                QuestMapProjectionService.CompatibleMapIds(published, zones.Variant(map)));
            Assert.Contains(
                zones.GameMapId(map),
                QuestMapProjectionService.CompatibleMapIds(zones.Location(map), zones.Variant(map)));
        }
    }

    [Fact]
    public void Objectives_on_a_map_with_floors_are_on_the_floor_their_height_and_place_say()
    {
        var zones = RealQuestZones.Load();
        var model = zones.Model("interchange");
        var upper2 = model.Floors.Single(floor => floor.Name == "2nd Floor");
        var upper3 = model.Floors.Single(floor => floor.Name == "3rd Floor");
        var scene = new QuestObjectiveSceneBuilder().Build(zones.Project("interchange", model), model.Floors, null, NowUtc);

        QuestObjectiveEntry Entry(string objectiveStart) => scene.Entries.Single(entry =>
            entry.ObjectiveId.StartsWith(objectiveStart, StringComparison.Ordinal));

        // Inside the mall's bounds and inside the 2nd floor's heights (25 to 34): its own floor.
        Assert.Equal(new[] { "2nd Floor" }, Entry("5ae4511d").FloorNames);
        Assert.Equal(new[] { "2nd Floor" }, Entry("5ae450db").FloorNames);
        // The mall, but at street level (y 22.8): the ground plan, which is not a named floor.
        Assert.Empty(Entry("5b478daf").FloorNames);
        // Height 28.7 like the mall's 2nd floor, but at x 429, far outside the mall's bounds: a
        // floor whose extents name an area does not claim what merely stands as high.
        Assert.Empty(Entry("5ae452de").FloorNames);
        // A tall trigger volume (6 to 36 m) outside the mall overlaps every floor's heights and
        // is still not "on the 2nd and 3rd floors".
        Assert.Empty(Entry("6a60968c").FloorNames);
        // Candidate spots at 36.6 to 38.5 m inside the mall: the 3rd floor, all nine.
        var console = Entry("667a958e");
        Assert.Equal(new[] { "3rd Floor" }, console.FloorNames);
        Assert.Equal(QuestObjectivePlacement.Candidates, console.Placement);
        Assert.Equal("One of 9 places", console.PlacementLabel);

        foreach (var (floor, visible, hidden) in new[]
                 {
                     (model.Floors.Single(item => item.Id == "base"), new[] { "5ae4511d", "5ae450db", "5b478daf", "5ae452de", "6a60968c", "667a958e" }, Array.Empty<string>()),
                     (upper2, new[] { "5ae4511d", "5ae450db" }, new[] { "667a958e", "5b478daf", "5ae452de", "6a60968c" }),
                     (upper3, new[] { "667a958e" }, new[] { "5ae4511d", "5ae450db", "5b478daf", "5ae452de", "6a60968c" }),
                 })
        {
            var renderer = Render(model, [], 1920, 1080, 0, extraObjects: scene.Objects, floorId: floor.Id);
            foreach (var objective in visible)
            {
                Assert.Contains(renderer.SpatialObjects, marker => IsOf(marker, Entry(objective)));
            }

            foreach (var objective in hidden)
            {
                Assert.DoesNotContain(renderer.SpatialObjects, marker => IsOf(marker, Entry(objective)));
                Assert.DoesNotContain(renderer.GeometryObjects, item => item.SceneObject.Id.Value.Contains(Entry(objective).ObjectiveId, StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void Customs_dorm_room_214_is_upstairs_and_room_114_is_not()
    {
        var zones = RealQuestZones.Load();
        var model = zones.Model("customs");
        var scene = new QuestObjectiveSceneBuilder().Build(zones.Project("customs", model), model.Floors, null, NowUtc);

        Assert.Equal(new[] { "2nd Floor" }, scene.Entries.Single(entry => entry.ObjectiveId.StartsWith("5a3fc032", StringComparison.Ordinal)).FloorNames);
        Assert.Empty(scene.Entries.Single(entry => entry.ObjectiveId.StartsWith("5a3fb922", StringComparison.Ordinal)).FloorNames);
        Assert.Empty(scene.Entries.Single(entry => entry.ObjectiveId.StartsWith("5a3fb8f6", StringComparison.Ordinal)).FloorNames);
    }

    [Fact]
    public void The_selected_objective_is_drawn_above_the_ones_it_overlaps()
    {
        // Two objectives can stand on the same helicopter; picking one must not leave it underneath.
        var zones = RealQuestZones.Load();
        var model = zones.Model("lighthouse");
        var scene = new QuestObjectiveSceneBuilder().Build(zones.Project("lighthouse", model), model.Floors, null, NowUtc);
        var renderer = Render(model, [], 1920, 1080, 0, extraObjects: scene.Objects);
        var first = scene.Entries.First(entry => entry.IsPlaced);
        var id = scene.Objects.First(item => first.ObjectIds.Contains(item.Id) && item.Geometry.Kind == MapSceneGeometryKind.Point).Id;
        var marker = renderer.SpatialObjects.Single(item => item.SceneObject!.Id == id);
        Assert.Equal(0, marker.ZOrder);

        renderer.SelectObject(id);

        Assert.True(marker.ZOrder > renderer.SpatialObjects.Where(item => item != marker).Max(item => item.ZOrder));
    }

    private static bool IsOf(MapSceneRendererObjectViewModel marker, QuestObjectiveEntry entry) =>
        marker.SceneObject is { } item && entry.ObjectIds.Contains(item.Id);

    private static void AssertObjectivesLand(
        RealQuestZones zones,
        string map,
        MapRenderModel model,
        int width,
        int height,
        double bearingDegrees)
    {
        var scene = new QuestObjectiveSceneBuilder().Build(zones.Project(map, model), model.Floors, null, NowUtc);
        var renderer = Render(model, [], width, height, bearingDegrees, extraObjects: scene.Objects);
        var placedAnything = false;
        foreach (var objective in zones.Objectives(map))
        {
            var entry = scene.Entries.Single(item => item.ObjectiveId == objective.Id);
            if (objective.HasNoZones)
            {
                // Nothing to put on the map: listed as having no location, and no object drawn.
                Assert.Equal(QuestObjectivePlacement.NoLocation, entry.Placement);
                Assert.Equal("No location", entry.PlacementLabel);
                Assert.Empty(entry.ObjectIds);
                continue;
            }

            placedAnything = true;
            var drawn = scene.Objects.Where(item => item.Id.Value.StartsWith($"quest:{objective.Id}:", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(drawn);

            var areas = objective.DistinctZones.Where(zone => zone.IsArea).ToArray();
            var spots = objective.DistinctZones.Where(zone => !zone.IsArea).ToArray();
            var geometry = renderer.GeometryObjects
                .Where(item => item.SceneObject.Id.Value.StartsWith($"quest:{objective.Id}:", StringComparison.Ordinal))
                .ToArray();
            var markers = renderer.SpatialObjects
                .Where(item => item.SceneObject?.Id.Value.StartsWith($"quest:{objective.Id}:", StringComparison.Ordinal) == true)
                .ToArray();

            // An area is drawn as the area: one polygon per distinct zone, each vertex where the
            // artwork puts that world coordinate.
            Assert.Equal(areas.Length, geometry.Length);
            foreach (var area in areas)
            {
                var expected = area.Outline.Select(point => OnArtwork(model, renderer, point)).ToArray();
                Assert.Contains(geometry, item => item.Points.Count == expected.Length &&
                    item.Points.Zip(expected).All(pair =>
                        Math.Abs(pair.First.X - pair.Second.X) < 0.05 && Math.Abs(pair.First.Y - pair.Second.Y) < 0.05));

                // Its number sits inside it, not beside it.
                var polygon = expected;
                Assert.Contains(markers.Where(item => item.SceneObject!.Id.Value.EndsWith(":label", StringComparison.Ordinal)), item =>
                    Inside(polygon, item.AnchorLeft + (MapSceneRendererViewModel.MarkerExtent / 2), item.AnchorTop + (MapSceneRendererViewModel.MarkerExtent / 2)));
            }

            Assert.Equal(areas.Length, markers.Count(item => item.SceneObject!.Id.Value.EndsWith(":label", StringComparison.Ordinal)));

            // A spot, and each candidate of a possibleLocations objective, is one marker at the
            // place the artwork gives its coordinates: as many markers as distinct places.
            var spotMarkers = markers.Where(item => item.SceneObject!.Id.Value.EndsWith(":spot", StringComparison.Ordinal)).ToArray();
            Assert.Equal(spots.Length, spotMarkers.Length);
            foreach (var spot in spots)
            {
                var expected = OnArtwork(model, renderer, spot.Position!.Value);
                Assert.Contains(spotMarkers, item =>
                    Math.Abs(item.AnchorLeft + (MapSceneRendererViewModel.MarkerExtent / 2) - expected.X) < 0.05 &&
                    Math.Abs(item.AnchorTop + (MapSceneRendererViewModel.MarkerExtent / 2) - expected.Y) < 0.05);
            }

            if (objective.IsCandidate && spots.Length > 1)
            {
                Assert.Equal(QuestObjectivePlacement.Candidates, entry.Placement);
                Assert.Equal($"One of {spots.Length} places", entry.PlacementLabel);
            }
        }

        Assert.True(placedAnything, $"No objective on {map} was placed; the fixture or the projection is not being exercised.");

        // Nothing is piled against an edge, which is what a mismatched rectangle looks like.
        foreach (var marker in renderer.SpatialObjects)
        {
            var centreX = marker.AnchorLeft + (MapSceneRendererViewModel.MarkerExtent / 2);
            var centreY = marker.AnchorTop + (MapSceneRendererViewModel.MarkerExtent / 2);
            Assert.InRange(centreX, renderer.MapLeft, renderer.MapLeft + renderer.MapWidth);
            Assert.InRange(centreY, renderer.MapTop, renderer.MapTop + renderer.MapHeight);
        }
    }

    private static bool Inside(IReadOnlyList<(double X, double Y)> polygon, double x, double y)
    {
        var inside = false;
        for (int index = 0, previous = polygon.Count - 1; index < polygon.Count; previous = index++)
        {
            var a = polygon[index];
            var b = polygon[previous];
            if ((a.Y > y) != (b.Y > y) && x < ((b.X - a.X) * (y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    [Fact]
    public void The_plan_rectangle_keeps_each_maps_own_shape()
    {
        // The scene's bounds are the artwork's rectangle, so they say what shape the map is —
        // which is the whole reason a wide map is no longer squashed into a square. Customs is
        // wider than it is tall and Lighthouse is the other way round.
        var customs = RaidCockpitViewModel.PlanBoundsFor(Customs());
        var lighthouse = RaidCockpitViewModel.PlanBoundsFor(Lighthouse());

        Assert.True(customs.Width > customs.Height);
        Assert.True(lighthouse.Height > lighthouse.Width);
        // And neither is the 0-100 box that used to stand in for both.
        Assert.NotEqual(new MapSceneBounds(0, 0, 100, 100), customs);
        Assert.NotEqual(new MapSceneBounds(0, 0, 100, 100), lighthouse);
    }

    [Fact]
    public void Nothing_on_a_placeable_map_is_reported_as_off_plan()
    {
        // The "24 off-plan" chip counts objects whose geometry leaves the plan. Every feature
        // here is inside the map's own reviewed bounds, so the honest count is none: the chip
        // was counting the mismatch between the coordinates and the box, not the map.
        var renderer = Render(Customs(), CustomsFeatures, 1920, 1080, bearingDegrees: 0);

        Assert.DoesNotContain("off-plan", renderer.DenseSceneChip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outside", renderer.DenseSceneNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_player_their_trail_and_a_squadmate_land_on_the_artwork_too()
    {
        // If the extracts are wrong they are all wrong, because everything on the plan is
        // projected the same way. The live layers go through the cockpit's own builder.
        var model = Customs();
        var player = CustomsFeatures[0].World;
        var mate = CustomsFeatures[2].World;
        var live = RaidCockpitViewModel.BuildLiveLayers(
            new(
                new ScreenshotPosition(NowUtc.AddSeconds(-5), player, default, 90, null, null, "demo.png"),
                [
                    new ScreenshotPosition(NowUtc.AddSeconds(-60), CustomsFeatures[1].World, default, 0, null, null, "a.png"),
                    new ScreenshotPosition(NowUtc.AddSeconds(-5), player, default, 90, null, null, "b.png"),
                ],
                [
                    new GroupMemberView(
                        "Geo", model.Location.Id, RaidLifecycleState.InRaid, "PMC", mate, 0,
                        TimeSpan.FromSeconds(5), [], []),
                ],
                _ => true,
                _ => "#FF00FF00",
                false,
                [],
                false),
            model,
            NowUtc);

        var renderer = Render(model, [], 1920, 1080, bearingDegrees: 0, live.Layers, live.Objects);
        var expectedPlayer = OnArtwork(model, renderer, player);
        var marker = Assert.Single(renderer.SpatialObjects, item =>
            item.SceneObject?.Kind == MapSceneObjectKind.LastKnownPosition);
        AssertAnchoredAt(renderer, marker, expectedPlayer);

        var mateMarker = Assert.Single(renderer.SpatialObjects, item =>
            item.SceneObject?.Kind == MapSceneObjectKind.TeammateLastKnown);
        AssertAnchoredAt(renderer, mateMarker, OnArtwork(model, renderer, mate));

        // The trail is drawn as geometry rather than a marker; its last point is where the
        // player is standing, so it has to agree with the player's own dot.
        var trail = Assert.Single(renderer.GeometryObjects, item => item.SceneObject.Kind == MapSceneObjectKind.Route);
        var last = trail.Points[^1];
        Assert.Equal(expectedPlayer.X, last.X, 1);
        Assert.Equal(expectedPlayer.Y, last.Y, 1);
    }

    private static void AssertFeaturesLand(
        MapRenderModel model,
        IReadOnlyList<(string Name, WorldPosition World)> features,
        int width,
        int height,
        double bearingDegrees)
    {
        var renderer = Render(model, features, width, height, bearingDegrees);

        foreach (var (name, world) in features)
        {
            var marker = Assert.Single(renderer.SpatialObjects, item => item.Label == name);
            AssertAnchoredAt(renderer, marker, OnArtwork(model, renderer, world));
        }

        // And none of them is piled against an edge of the plan, which is what a mismatched
        // projection looks like on screen.
        foreach (var marker in renderer.SpatialObjects)
        {
            var centreX = marker.AnchorLeft + (MapSceneRendererViewModel.MarkerExtent / 2);
            var centreY = marker.AnchorTop + (MapSceneRendererViewModel.MarkerExtent / 2);
            Assert.InRange(centreX, renderer.MapLeft, renderer.MapLeft + renderer.MapWidth);
            Assert.InRange(centreY, renderer.MapTop, renderer.MapTop + renderer.MapHeight);
        }
    }

    private static void AssertAnchoredAt(
        MapSceneRendererViewModel renderer,
        MapSceneRendererObjectViewModel marker,
        (double X, double Y) expected)
    {
        var centreX = marker.AnchorLeft + (MapSceneRendererViewModel.MarkerExtent / 2);
        var centreY = marker.AnchorTop + (MapSceneRendererViewModel.MarkerExtent / 2);
        Assert.Equal(expected.X, centreX, 1);
        Assert.Equal(expected.Y, centreY, 1);
    }

    /// <summary>
    /// Where the artwork puts a world position, worked out from the variant alone.
    /// </summary>
    /// <remarks>
    /// Deliberately not through <see cref="MapPlanProjection"/>: the drawn plan is the variant's
    /// projected bounds stretched across MapLeft/MapTop/MapWidth/MapHeight (see
    /// MapSceneRendererView.axaml), so this rederives that from the catalog transform and asks
    /// the renderer to agree with it.
    /// </remarks>
    private static (double X, double Y) OnArtwork(
        MapRenderModel model,
        MapSceneRendererViewModel renderer,
        WorldPosition world)
    {
        var transform = model.Variant.Transform!;
        // What the drawing covers is the SVG's bounds, and what the tile pyramid covers is the
        // reviewed bounds snapped outwards to whole tiles at the zoom that was loaded. Both are
        // worked out here from the variant alone.
        var tiled = model.Background?.Kind == MapBackgroundKind.TileTemplate;
        var bounds = tiled ? model.Variant.Bounds! : model.Variant.SvgBounds ?? model.Variant.Bounds!;
        var scale = tiled ? Math.Pow(2, model.Background!.TileZoom ?? model.Variant.MinimumZoom!.Value) : 1;
        var corners = new[]
        {
            new WorldPosition(bounds.First.X, 0, bounds.First.Y),
            new WorldPosition(bounds.First.X, 0, bounds.Second.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.First.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.Second.Y),
        };
        var projected = corners.Select(corner =>
        {
            transform.TryProject(corner, out var point);
            return point;
        }).ToArray();
        var minimumX = projected.Min(point => point.X);
        var maximumX = projected.Max(point => point.X);
        var minimumY = projected.Min(point => point.Y);
        var maximumY = projected.Max(point => point.Y);
        if (tiled)
        {
            double Snap(double value, bool up) => (up ? Math.Floor(value * scale / model.Variant.TileSize) + 1 : Math.Floor(value * scale / model.Variant.TileSize))
                * model.Variant.TileSize / scale;
            (minimumX, maximumX, minimumY, maximumY) = (Snap(minimumX, false), Snap(maximumX, true), Snap(minimumY, false), Snap(maximumY, true));
        }

        Assert.True(transform.TryProject(world, out var here));
        return (
            renderer.MapLeft + ((here.X - minimumX) / (maximumX - minimumX) * renderer.MapWidth),
            renderer.MapTop + ((here.Y - minimumY) / (maximumY - minimumY) * renderer.MapHeight));
    }

    private static MapSceneRendererViewModel Render(
        MapRenderModel model,
        IReadOnlyList<(string Name, WorldPosition World)> features,
        int width,
        int height,
        double bearingDegrees,
        IReadOnlyList<MapSceneLayer>? extraLayers = null,
        IReadOnlyList<MapSceneObject>? extraObjects = null,
        string? floorId = null)
    {
        var elements = features
            .Select(feature =>
            {
                Assert.True(model.Variant.Transform!.TryProject(feature.World, out var point));
                return new MapSceneLegacyElement(
                    new MapOverlayElement(MapOverlayKind.Extracts, point, feature.Name),
                    new DataProvenance("fixture", NowUtc));
            })
            .ToArray();
        var bounds = RaidCockpitViewModel.PlanBoundsFor(model);
        var centre = new MapSceneCamera(
            bounds.MinimumX + (bounds.Width / 2),
            bounds.MinimumY + (bounds.Height / 2),
            1,
            bearingDegrees,
            0);
        var result = new MapSceneAssembler().Build(new(
            1,
            model,
            bounds,
            model.Variant.Key,
            new(MapSceneMode.Flat2D, floorId ?? model.SelectedFloor?.Id, centre, []),
            elements,
            extraLayers ?? [],
            extraObjects ?? [],
            [Asset()]));
        var scene = Assert.IsType<MapSceneSnapshot>(result.Scene);
        var renderer = new MapSceneRendererViewModel(
            scene,
            Presentation,
            reviewedAssetResolver: _ => new TestArtwork(new(2048, 2048 / bounds.Width * bounds.Height)));
        renderer.SetViewportSize(width, height);
        return renderer;
    }

    /// <summary>
    /// A wide map with a rotated transform and bounds nowhere near a 0-100 box.
    /// </summary>
    private static MapRenderModel Customs() => Model(
        "customs",
        "Customs",
        new MapCatalogBounds(new(-390, -380), new(570, 180)),
        new MapCatalogTransform(0.0619, 89.5, 0.0619, 128, 180));

    /// <summary>A tall map, turned a quarter of the way round by its own transform.</summary>
    private static MapRenderModel Lighthouse() => Model(
        "lighthouse",
        "Lighthouse",
        new MapCatalogBounds(new(-900, -300), new(900, 300)),
        new MapCatalogTransform(0.05, 60, 0.05, -20, 90));

    private static MapRenderModel Model(
        string id,
        string name,
        MapCatalogBounds bounds,
        MapCatalogTransform transform)
    {
        var location = new MapLocation(id, null, name, null, null, []);
        var floors = new[] { new MapFloorDefinition("ground", "Ground", null, null, true, []) };
        var variant = new MapVariant(
            location.Id,
            $"{id}-interactive",
            MapProjectionKind.Interactive,
            "2D",
            null,
            null,
            new($"https://example.test/{id}.svg"),
            null,
            256,
            1,
            5,
            bounds,
            bounds,
            transform,
            null,
            null,
            null,
            "Example author",
            new("https://example.test/author"),
            [],
            floors,
            []);
        var overlays = Enum.GetValues<MapOverlayKind>()
            .Select(kind => new MapOverlayLayer(kind, kind.ToString(), true, false))
            .ToArray();
        return new(
            location,
            variant,
            new(MapBackgroundKind.Svg, variant.SvgPath!, $"/cache/{id}.svg", MapAssetAvailability.Available, null),
            MapTransformAvailability.Valid,
            "Validated transform.",
            overlays,
            [],
            floors,
            floors[0],
            "Example attribution",
            new("https://example.test/licence"));
    }

    private static MapSceneAsset Asset() => new(
        new("asset:fixture"),
        MapSceneAssetKind.Background2D,
        new("https://example.test/maps/plan.svg"),
        new("https://example.test/licence"),
        new string('b', 64),
        "Example map author",
        "map-1",
        "game-1",
        MapSceneAssetReviewStatus.Reviewed,
        NowUtc);

    private sealed class TestArtwork(Size size) : IImage
    {
        public Size Size { get; } = size;

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }
    }
}
