using System.Globalization;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Infrastructure.Workspaces;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// [#902 P2] One saved switch per route. Each map is built the way the Raid cockpit builds one:
/// the route layers always declared, the remembered choices laid over the requested view, the real
/// assembler and renderer, and the renderer's changes applied and saved the way
/// RaidCockpitViewModel.ViewChangeRequested does it.
/// </summary>
public sealed class RouteSwitchTests : IDisposable
{
    private static readonly MapSceneRendererPresentation Presentation =
        MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"route-switch-{Guid.NewGuid():N}");
    private long _revision;

    private string LayoutPath => Path.Combine(_directory, "workspace-layout.json");

    [Fact]
    public void A_hidden_objective_route_stays_hidden_when_opened_again_and_after_a_restart_and_is_drawn_when_shown()
    {
        var store = new JsonFileWorkspaceLayoutStore(LayoutPath);
        var setting = new MapLayerVisibilitySetting(store);
        var maps = new ObjectiveRouteMaps(store);
        maps.Add("customs");
        var renderer = Host(Build("customs", WithObjectiveRoute(), setting), setting);
        Assert.True(Drawn(renderer, RouteLayerSwitch.Objective));

        // "Show route" on the card, or Plan's "Route" chip: both flip the layer itself.
        RouteLayerSwitch.Set(renderer, setting, RouteLayerSwitch.Objective, false);
        Assert.False(Drawn(renderer, RouteLayerSwitch.Objective));
        Assert.False(Row(renderer, RouteLayerSwitch.Objective).IsVisible);

        // "Open in Raid" again: remembers the map and rebuilds the scene; nothing turns it back on.
        maps.Add("customs");
        renderer.Present(Build("customs", WithObjectiveRoute(), setting));
        Assert.False(RouteLayerSwitch.IsShown(renderer, setting, RouteLayerSwitch.Objective));
        Assert.False(Drawn(renderer, RouteLayerSwitch.Objective));

        // A restart: the choice and the opened map were both saved.
        var restartedStore = new JsonFileWorkspaceLayoutStore(LayoutPath);
        var restarted = new MapLayerVisibilitySetting(restartedStore);
        Assert.True(new ObjectiveRouteMaps(restartedStore).Contains("Customs"));
        var afterRestart = Host(Build("customs", WithObjectiveRoute(), restarted), restarted);
        Assert.False(RouteLayerSwitch.IsShown(afterRestart, restarted, RouteLayerSwitch.Objective));
        Assert.False(Drawn(afterRestart, RouteLayerSwitch.Objective));

        RouteLayerSwitch.Set(afterRestart, restarted, RouteLayerSwitch.Objective, true);

        Assert.True(Drawn(afterRestart, RouteLayerSwitch.Objective));
        Assert.True(new MapLayerVisibilitySetting(new JsonFileWorkspaceLayoutStore(LayoutPath)).Get(RouteLayerSwitch.Objective));
    }

    [Fact]
    public void With_no_route_the_route_rows_stay_in_layers_and_read_off_when_off()
    {
        var setting = new MapLayerVisibilitySetting(new JsonFileWorkspaceLayoutStore(LayoutPath));
        setting.Set(RouteLayerSwitch.Objective, false);
        setting.Set(RouteLayerSwitch.Suggested, false);

        var renderer = Host(Build("woods", EmptyRoutes(), setting), setting);

        var routes = Assert.Single(renderer.LayerGroups, group => group.Key == "Routes");
        Assert.Equal(
            Ids.Select(id => id.Value).Order(),
            routes.Layers.Select(layer => layer.Layer.Id.Value).Order());
        Assert.False(Row(renderer, RouteLayerSwitch.Objective).IsVisible);
        Assert.False(Row(renderer, RouteLayerSwitch.Suggested).IsVisible);
        Assert.True(Row(renderer, RouteLayerSwitch.Direct).IsVisible);
        Row(renderer, RouteLayerSwitch.Objective).ToggleCommand.Execute(null);
        Assert.True(RouteLayerSwitch.IsShown(renderer, setting, RouteLayerSwitch.Objective));
    }

    [Fact]
    public void A_route_arriving_on_a_layer_declared_empty_is_counted_in_its_row()
    {
        var setting = new MapLayerVisibilitySetting(null);
        var renderer = Host(Build("customs", EmptyRoutes(), setting), setting);
        Assert.True(Row(renderer, RouteLayerSwitch.Objective).HasNothingToShow);

        renderer.Present(Build("customs", WithObjectiveRoute(), setting));

        Assert.False(Row(renderer, RouteLayerSwitch.Objective).HasNothingToShow);
        Assert.Equal(
            ["objective-route", "traffic-routes", "traffic-route-direct"],
            renderer.LayerGroups.Single(group => group.Key == "Routes").Layers.Select(layer => layer.Layer.Id.Value));
    }

    [Fact]
    public void The_direct_line_has_its_own_switch_apart_from_the_suggested_extract_route()
    {
        var setting = new MapLayerVisibilitySetting(new JsonFileWorkspaceLayoutStore(LayoutPath));
        var renderer = Host(Build("customs", WithExtractRoutes(), setting), setting);

        // "Show on map" on the route card.
        RouteLayerSwitch.Set(renderer, setting, RouteLayerSwitch.Suggested, false);

        Assert.False(Drawn(renderer, RouteLayerSwitch.Suggested));
        Assert.True(Drawn(renderer, RouteLayerSwitch.Direct));

        // A restart keeps it off; switching it back on draws it again.
        var restarted = new MapLayerVisibilitySetting(new JsonFileWorkspaceLayoutStore(LayoutPath));
        var afterRestart = Host(Build("customs", WithExtractRoutes(), restarted), restarted);
        Assert.False(Drawn(afterRestart, RouteLayerSwitch.Suggested));
        RouteLayerSwitch.Set(afterRestart, restarted, RouteLayerSwitch.Suggested, true);
        Assert.True(Drawn(afterRestart, RouteLayerSwitch.Suggested));
    }

    [Fact]
    public void Before_any_scene_exists_a_route_switch_is_saved_straight_away()
    {
        var setting = new MapLayerVisibilitySetting(new JsonFileWorkspaceLayoutStore(LayoutPath));

        Assert.True(RouteLayerSwitch.Set(null, setting, RouteLayerSwitch.Objective, false));

        Assert.False(RouteLayerSwitch.IsShown(null, setting, RouteLayerSwitch.Objective));
        Assert.False(new MapLayerVisibilitySetting(new JsonFileWorkspaceLayoutStore(LayoutPath)).Get(RouteLayerSwitch.Objective));
        Assert.True(RouteLayerSwitch.IsShown(null, setting, RouteLayerSwitch.Suggested));
    }

    [Fact]
    public void Plans_preview_draws_the_route_line_only_while_the_route_is_shown()
    {
        var route = ObjectiveRoute();

        Assert.Contains(PlanWorkspaceViewModel.PreviewObjects(QuestObjectiveScene.Empty, route), item => item.Kind == MapSceneObjectKind.Route);
        Assert.Empty(PlanWorkspaceViewModel.PreviewObjects(QuestObjectiveScene.Empty, null));
    }

    [Fact]
    public void The_maps_a_route_was_opened_on_are_kept_most_recent_last_and_bad_ids_are_dropped()
    {
        var layout = new MemoryLayout();
        layout.Set(WorkspaceLayoutKeys.RaidObjectiveRouteMaps, "customs, woods,,bad id,customs");
        var maps = new ObjectiveRouteMaps(layout);

        Assert.True(maps.Contains("woods"));
        Assert.False(maps.Contains("bad id"));
        maps.Add("customs");

        Assert.Equal("woods,customs", layout.Get(WorkspaceLayoutKeys.RaidObjectiveRouteMaps));
    }

    [Theory]
    [InlineData("objective-route")]
    [InlineData("traffic-routes")]
    [InlineData("traffic-route-direct")]
    public void Every_route_sits_under_routes(string layerId) =>
        Assert.Equal("Routes", MapLayerGroups.KeyOf(new(layerId)));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static readonly MapSceneLayerId[] Ids = [RouteLayerSwitch.Objective, RouteLayerSwitch.Suggested, RouteLayerSwitch.Direct];

    private static readonly DataProvenance Provenance = new("test", DateTimeOffset.UnixEpoch);

    private static ObjectiveRouteScene ObjectiveRoute()
    {
        var bundle = ObjectiveRoutePlanner.Plan(
            new(10, 10),
            "the selected spawn",
            [new("objective-a", "Find the thing", new(40, 40)), new("objective-b", "Mark the car", new(70, 20))],
            1);
        return ObjectiveRouteSceneBuilder.Build(
            bundle,
            new(10, 10),
            DateTimeOffset.UnixEpoch,
            new("Objective route", "Objective visit order", "Straight-line plan", _ => "reason"));
    }

    /// <summary>What the cockpit hands the assembler: every route layer, and an objective route on this map.</summary>
    private static (IReadOnlyList<MapSceneLayer> Layers, IReadOnlyList<MapSceneObject> Objects) WithObjectiveRoute()
    {
        var route = ObjectiveRoute();
        return ([route.Layer, .. ExtractRouteLayers()], route.Objects);
    }

    private static (IReadOnlyList<MapSceneLayer> Layers, IReadOnlyList<MapSceneObject> Objects) EmptyRoutes() =>
        ([new(RouteLayerSwitch.Objective, "Objective route", 62, true), .. ExtractRouteLayers()], []);

    private static (IReadOnlyList<MapSceneLayer> Layers, IReadOnlyList<MapSceneObject> Objects) WithExtractRoutes() =>
        ([new(RouteLayerSwitch.Objective, "Objective route", 62, true), .. ExtractRouteLayers()],
        [Line("traffic-route:lower-contact", RouteLayerSwitch.Suggested), Line("traffic-route:direct", RouteLayerSwitch.Direct)]);

    private static MapSceneLayer[] ExtractRouteLayers() =>
    [
        new(RouteLayerSwitch.Suggested, "Suggested extract route", 65, true),
        new(RouteLayerSwitch.Direct, "Direct line", 64, true),
    ];

    private static MapSceneObject Line(string id, MapSceneLayerId layerId) => new(
        new(id),
        layerId,
        MapSceneObjectKind.Route,
        // The cockpit's lines are historical estimates; the switch does not care which truth a line is.
        MapSceneTruthKind.PersonalPlan,
        id,
        "estimated",
        new(MapSceneGeometryKind.Line, [new(10, 10), new(60, 60)]),
        [],
        Provenance);

    private static bool Drawn(MapSceneRendererViewModel renderer, MapSceneLayerId layerId) =>
        renderer.GeometryObjects.Any(item => item.SceneObject.LayerId == layerId && item.SceneObject.Kind == MapSceneObjectKind.Route);

    private static MapSceneRendererLayerViewModel Row(MapSceneRendererViewModel renderer, MapSceneLayerId id) =>
        renderer.LayerGroups.SelectMany(group => group.Layers).Single(layer => layer.Layer.Id == id);

    private static MapSceneRendererViewModel Host(MapSceneSnapshot scene, MapLayerVisibilitySetting setting)
    {
        var renderer = new MapSceneRendererViewModel(scene, Presentation, Guid.NewGuid);
        renderer.ViewChangeRequested += change =>
        {
            var result = MapSceneViewReducer.Apply(renderer.Scene, change);
            renderer.Present(result.Scene);
            setting.Record(change, result.Status, renderer.IsDispatchingLootFocus);
        };
        return renderer;
    }

    private MapSceneSnapshot Build(
        string locationId,
        (IReadOnlyList<MapSceneLayer> Layers, IReadOnlyList<MapSceneObject> Objects) routes,
        MapLayerVisibilitySetting setting)
    {
        var result = new MapSceneAssembler().Build(new(
            Interlocked.Increment(ref _revision),
            Model(locationId),
            new(0, 0, 100, 100),
            "maps-json:b",
            new(MapSceneMode.Flat2D, "lower", new(50, 50, 1, 0, 0), setting.Apply([])),
            [],
            routes.Layers,
            routes.Objects,
            [new(
                new("asset:plan"),
                MapSceneAssetKind.Background2D,
                new("https://example.test/plan.asset"),
                new("https://example.test/licence"),
                new string('a', 64),
                "Example author",
                "map-1",
                "game-1",
                MapSceneAssetReviewStatus.Reviewed,
                DateTimeOffset.UnixEpoch)]));
        return Assert.IsType<MapSceneSnapshot>(result.Scene);
    }

    private static MapRenderModel Model(string locationId)
    {
        var location = new MapLocation(locationId, null, locationId, null, null, []);
        var floors = new[] { new MapFloorDefinition("lower", "Lower", null, null, true, []) };
        var variant = new MapVariant(
            location.Id, $"{locationId}-plan", MapProjectionKind.TwoDimensional, "2D", null, null,
            new("https://example.test/plan.svg"), null, 256, null, null,
            new(new(0, 0), new(100, 100)), new(new(0, 0), new(100, 100)), new(1, 0, 1, 0, 0),
            null, null, null, "Example author", new("https://example.test/author"), [], floors, []);
        return new MapPresentationService().Create(location, variant, "/cache/plan.svg");
    }

    private sealed class MemoryLayout : IWorkspaceLayoutStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string value) => _values[key] = value;
    }
}
