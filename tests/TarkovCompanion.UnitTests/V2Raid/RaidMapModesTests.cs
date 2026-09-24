using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Strategy.Prior;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>[#286] The Raid map's Navigate / Inspect / Route / Draw modes.</summary>
public sealed class RaidMapModesTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    // ---- Route building ----

    [Fact]
    public void A_route_takes_twelve_stops_in_order_and_no_more()
    {
        var route = new PlannedRoute();
        for (var i = 0; i < PlannedRoute.MaximumStops; i++)
        {
            Assert.NotNull(route.Add(new MapPoint(i, 0)));
        }

        Assert.True(route.IsFull);
        Assert.Null(route.Add(new MapPoint(99, 0)));
        Assert.Equal(Enumerable.Range(1, 12), route.Stops.Select(stop => stop.Step));
    }

    [Fact]
    public void Undo_takes_the_last_stop_and_Clear_starts_a_new_route()
    {
        var route = new PlannedRoute();
        route.Add(new MapPoint(0, 0));
        route.Add(new MapPoint(10, 0));
        route.Add(new MapPoint(20, 0));

        Assert.Equal(3, route.Undo()!.Step);
        Assert.Equal(3, route.Add(new MapPoint(30, 0))!.Step);

        var first = route.RouteId;
        Assert.Equal(3, route.Clear().Count);
        Assert.Empty(route.Stops);
        Assert.NotEqual(first, route.RouteId);
        Assert.Null(route.Undo());
        Assert.Equal(1, route.Add(new MapPoint(0, 0))!.Step);
    }

    [Fact]
    public void A_stop_whose_mark_went_leaves_the_route_and_a_pending_one_stays()
    {
        var route = new PlannedRoute();
        route.Add(new MapPoint(0, 0));
        route.Add(new MapPoint(10, 0));
        route.Add(new MapPoint(20, 0));
        var kept = Guid.NewGuid();
        route.Bind(1, kept);
        route.Bind(2, Guid.NewGuid());

        Assert.True(route.Forget(id => id == kept));

        Assert.Equal([1, 3], route.Stops.Select(stop => stop.Step));
        Assert.False(route.Forget(id => id == kept));
    }

    [Fact]
    public void A_routes_minutes_are_the_suggested_routes_pace_over_its_legs()
    {
        var route = new PlannedRoute();
        (double X, double Z)? Identity(MapPoint point) => (point.X, point.Y);
        route.Add(new MapPoint(0, 0));
        Assert.Null(route.Minutes(Identity));

        route.Add(new MapPoint(300, 0));
        route.Add(new MapPoint(300, 400));

        Assert.Equal(700, route.Metres(Identity)!.Value, 6);
        Assert.Equal(TrafficRoute.MinutesFor(700), route.Minutes(Identity));
        // 700 m, a quarter more for walls, at 3.2 and 1.8 m/s.
        Assert.Equal((4, 9), route.Minutes(Identity));
        // No transform: no minutes rather than a partial sum.
        Assert.Null(route.Minutes(_ => null));
    }

    [Fact]
    public void Route_stops_are_numbered_along_their_route_not_among_the_lone_waypoints()
    {
        var routeId = Guid.NewGuid();
        var lone = Waypoint(1, 1, NowUtc);
        RaidMark[] marks =
        [
            lone,
            Waypoint(30, 30, NowUtc.AddSeconds(1)) with { Route = new RaidMarkRoute(routeId, 5) },
            Waypoint(10, 10, NowUtc.AddSeconds(2)) with { Route = new RaidMarkRoute(routeId, 2) },
            Waypoint(2, 2, NowUtc.AddSeconds(3)),
        ];

        var labels = RaidCockpitViewModel.LabelMarksForMap(marks, "customs").ToDictionary(item => item.Mark.Id, item => item.Label);

        Assert.Equal("1", labels[lone.Id]);
        Assert.Equal("2", labels[marks[1].Id]);
        Assert.Equal("1", labels[marks[2].Id]);
        Assert.Equal("2", labels[marks[3].Id]);
    }

    // ---- Inspect lookups ----

    [Fact]
    public void Inspect_names_the_nearest_extracts_with_minutes_and_what_is_close_on_the_layers()
    {
        MapSceneObject[] all =
        [
            Pin("x-far", MapSceneObjectKind.Extract, "Far exit", 900, 0),
            Pin("x-near", MapSceneObjectKind.Extract, "Crossroads", 100, 0),
            Pin("x-near-2", MapSceneObjectKind.Extract, "Crossroads", 105, 0),
            Pin("x-mid", MapSceneObjectKind.Extract, "RUAF", 400, 0),
            Pin("x-mid-2", MapSceneObjectKind.Extract, "Smugglers", 500, 0),
            Pin("q-close", MapSceneObjectKind.QuestObjective, "Mark the truck", 30, 0),
            Pin("q-far", MapSceneObjectKind.QuestObjective, "Far objective", 200, 0),
            Pin("l-close", MapSceneObjectKind.LootContainer, "Weapon box", 0, 40),
            Pin("l-hidden", MapSceneObjectKind.LootSpawn, "Hidden loot", 0, 10),
            new(new("s-area"), new("spawns"), MapSceneObjectKind.SpawnArea, MapSceneTruthKind.PotentialSpawn, "Trailer park",
                null, new(MapSceneGeometryKind.Area, [new(50, 50), new(150, 50), new(150, 150), new(50, 150)]), [],
                new DataProvenance("map-catalog", NowUtc)),
            Pin("label", MapSceneObjectKind.Label, "Dorms", 20, 20),
        ];
        var visible = all.Where(item => item.Id.Value != "l-hidden").ToArray();

        var inspection = MapPointInspector.Inspect(new(0, 0), all, visible, point => (point.X, point.Y), areaName: null);

        Assert.True(inspection.HasScale);
        Assert.Equal(["Crossroads", "RUAF", "Smugglers"], inspection.Extracts.Select(hit => hit.Label));
        Assert.Equal(100, inspection.Extracts[0].Metres!.Value, 6);
        Assert.Equal(TrafficRoute.MinutesFor(100), inspection.Extracts[0].Minutes);
        Assert.Equal(["Mark the truck"], inspection.Objectives.Select(hit => hit.Label));
        Assert.Equal(["Weapon box"], inspection.Loot.Select(hit => hit.Label));
        // Nearest edge of the area, not its middle: (50, 50) is 70.7 m away.
        var spawn = Assert.Single(inspection.SpawnAreas);
        Assert.Equal(Math.Sqrt(5000), spawn.Metres!.Value, 6);
        Assert.Equal("Dorms", inspection.NearbyLabel);
        Assert.Null(inspection.AreaName);
    }

    [Fact]
    public void Inspect_without_a_transform_orders_extracts_and_claims_nothing_nearby()
    {
        MapSceneObject[] all =
        [
            Pin("a", MapSceneObjectKind.Extract, "Far", 90, 0),
            Pin("b", MapSceneObjectKind.Extract, "Near", 10, 0),
            Pin("q", MapSceneObjectKind.QuestObjective, "Objective", 1, 0),
        ];

        var inspection = MapPointInspector.Inspect(new(0, 0), all, all, _ => null, "dorms");

        Assert.False(inspection.HasScale);
        Assert.Equal(["Near", "Far"], inspection.Extracts.Select(hit => hit.Label));
        Assert.All(inspection.Extracts, hit => Assert.Null(hit.Metres));
        Assert.Empty(inspection.Objectives);
        Assert.Equal("dorms", inspection.AreaName);

        var sections = RaidCockpitViewModel.SectionsFor(inspection);
        var extracts = Assert.Single(sections);
        Assert.Equal(["Near", "Far"], extracts.Lines);
        Assert.False(extracts.HasCaveat);
    }

    [Fact]
    public void The_popover_reads_extracts_first_with_the_straight_line_caveat()
    {
        MapSceneObject[] all =
        [
            Pin("x", MapSceneObjectKind.Extract, "Crossroads", 300, 0),
            Pin("q", MapSceneObjectKind.QuestObjective, "Mark the truck", 30, 0),
        ];

        var sections = RaidCockpitViewModel.SectionsFor(
            MapPointInspector.Inspect(new(0, 0), all, all, point => (point.X, point.Y), null));

        Assert.Equal(2, sections.Count);
        Assert.Equal("Crossroads · 300 m · ~1–4 min", Assert.Single(sections[0].Lines));
        Assert.True(sections[0].HasCaveat);
        Assert.Equal("Mark the truck · 30 m", Assert.Single(sections[1].Lines));

        var empty = RaidCockpitViewModel.SectionsFor(
            MapPointInspector.Inspect(new(0, 0), [all[0]], [all[0]], point => (point.X, point.Y), null));
        Assert.Equal(2, empty.Count);
        Assert.Empty(empty[1].Lines);
    }

    // ---- Mode transitions ----

    [Fact]
    public async Task Modes_switch_one_at_a_time_and_a_lit_mode_pressed_again_goes_back_to_Navigate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-modes-{Guid.NewGuid():N}");
        try
        {
            await using var services = AppComposition.Build(
                new AppCommandLine(false, true, false, false, null, null, null) { UiShell = V2ShellMode.VariantA },
                new(DataRoot: root, Offline: true));
            var cockpit = services.GetRequiredService<RaidCockpitViewModel>();

            Assert.True(cockpit.IsNavigateMode);
            Assert.False(cockpit.IsClickMode);
            Assert.False(cockpit.LeavesModeOnEscape);

            cockpit.InspectModeCommand.Execute(null);
            Assert.True(cockpit.IsInspectMode);
            Assert.True(cockpit.IsClickMode);
            Assert.True(cockpit.ShowsInspectHint);
            Assert.True(cockpit.LeavesModeOnEscape);

            cockpit.RouteModeCommand.Execute(null);
            Assert.True(cockpit.IsRouteMode);
            Assert.False(cockpit.IsInspectMode);
            Assert.False(cockpit.ShowsInspectHint);
            Assert.Equal("Click the map to add stops", cockpit.PlannedRouteSummary);
            // No map yet: a click adds nothing rather than a stop nobody can see.
            Assert.Null(cockpit.AddRouteStop(new(1, 1)));
            Assert.False(cockpit.HasPlannedRouteStops);

            cockpit.RouteModeCommand.Execute(null);
            Assert.True(cockpit.IsNavigateMode);

            if (cockpit.IsDrawAvailable)
            {
                cockpit.ToggleDrawCommand.Execute(null);
                Assert.True(cockpit.IsDrawMode);
                Assert.False(cockpit.IsClickMode);
                cockpit.InspectModeCommand.Execute(null);
                Assert.True(cockpit.IsInspectMode);
                Assert.False(cockpit.IsDrawMode);
            }

            // What Escape does (the map view raises ModeEscaped, the page calls this).
            cockpit.SetInteractionMode(MapInteractionMode.Navigate);
            Assert.True(cockpit.IsNavigateMode);
            Assert.False(cockpit.HasInspection);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static MapSceneObject Pin(string id, MapSceneObjectKind kind, string label, double x, double y) => new(
        new(id),
        new(kind.ToString().ToLowerInvariant()),
        kind,
        kind == MapSceneObjectKind.SpawnArea ? MapSceneTruthKind.PotentialSpawn : MapSceneTruthKind.StaticReference,
        label,
        null,
        MapSceneGeometry.At(new(x, y)),
        [],
        new DataProvenance("map-catalog", NowUtc));

    private static RaidMark Waypoint(double x, double y, DateTimeOffset createdUtc) =>
        new(Guid.NewGuid(), RaidMarkKind.Waypoint, new MapMarkState("customs", null, x, y, null, null), createdUtc);
}
