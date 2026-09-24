using System.Globalization;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>[#573] Marks are smallest at fit zoom, and a dense spot is one count badge until zoomed into.</summary>
public sealed class MapMarkerZoomTests
{
    [Fact]
    public void A_mark_is_smallest_fitted_and_full_size_zoomed_in_and_grows_in_between()
    {
        Assert.Equal(MapMarkerScale.AtFit, MapMarkerScale.For(1));
        Assert.Equal(MapMarkerScale.AtFit, MapMarkerScale.For(0.8));
        Assert.Equal(1, MapMarkerScale.For(MapMarkerScale.FullSizeZoom));
        Assert.Equal(1, MapMarkerScale.For(20));
        var previous = 0d;
        for (var zoom = 1d; zoom <= 4; zoom *= 1.25)
        {
            var scale = MapMarkerScale.For(zoom);
            Assert.True(scale > previous, $"zoom {zoom}");
            previous = scale;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(4)]
    public void The_hit_box_never_falls_under_32_screen_pixels(double zoom)
    {
        var scale = MapMarkerScale.For(zoom);
        Assert.True(MapMarkerScale.HitExtent(scale) * scale >= MapMarkerScale.MinimumHitPixels - 1e-9);
        Assert.True(MapMarkerScale.HitExtent(scale) >= MapMarkerScale.HitBoxAtFullSize);
    }

    [Fact]
    public void Hand_waypoints_and_pings_grow_with_zoom()
    {
        var renderer = Renderer(
            At("waypoint", MapSceneObjectKind.Waypoint, 100, 100),
            At("ping", MapSceneObjectKind.Ping, 300, 250));

        Assert.All(renderer.PointMarkers, marker => Assert.Equal(MapMarkerScale.AtFit, marker.MarkerScale));

        renderer.RequestZoom(1);

        Assert.All(renderer.PointMarkers, marker => Assert.True(marker.MarkerScale > MapMarkerScale.AtFit));
    }

    [Fact]
    public void Stacks_chain_marks_along_one_spot_and_leave_pairs_and_loners_alone()
    {
        var anchors = new (double, double)[] { (0, 0), (20, 0), (40, 0), (200, 0), (215, 0), (400, 400) };

        var stacks = MapMarkerOverlapLayout.Stacks(anchors, 30, minimumCount: 3);

        var only = Assert.Single(stacks);
        Assert.Equal([0, 1, 2], only);
    }

    [Fact]
    public void Three_extracts_on_one_building_are_one_badge_fitted_and_three_marks_zoomed_in()
    {
        var renderer = Renderer(
            Extract("a", 100, 100), Extract("b", 104, 102), Extract("c", 98, 105), Extract("far", 300, 250));

        var badge = Assert.Single(renderer.StackMarkers);
        Assert.True(badge.IsShownOnPlan);
        Assert.Equal("3", badge.MarkerGlyph);
        var stacked = renderer.PointMarkers.Where(marker => marker.Label != "far").ToArray();
        Assert.All(stacked, marker => Assert.False(marker.IsShownOnPlan));
        Assert.True(renderer.PointMarkers.Single(marker => marker.Label == "far").IsShownOnPlan);
        Assert.All(renderer.PointMarkers, marker => Assert.Equal(MapMarkerScale.AtFit, marker.MarkerScale));

        // Pressing the badge zooms in on the spot, past the zoom where the marks separate.
        badge.SelectCommand.Execute(null);
        Assert.True(renderer.CameraZoom > MapSceneRendererViewModel.StackZoom);

        Assert.False(badge.IsShownOnPlan);
        Assert.All(stacked, marker => Assert.True(marker.IsShownOnPlan));
        Assert.All(renderer.PointMarkers, marker => Assert.True(marker.MarkerScale > MapMarkerScale.AtFit));
    }

    [Fact]
    public void Hand_waypoints_and_a_ping_share_one_badge_until_zoomed_in()
    {
        var renderer = Renderer(
            At("waypoint-a", MapSceneObjectKind.Waypoint, 100, 100),
            At("waypoint-b", MapSceneObjectKind.Waypoint, 104, 102),
            At("ping", MapSceneObjectKind.Ping, 98, 105));

        var badge = Assert.Single(renderer.StackMarkers);
        Assert.Equal("3", badge.MarkerGlyph);
        Assert.All(renderer.PointMarkers, marker => Assert.False(marker.IsShownOnPlan));

        badge.SelectCommand.Execute(null);

        Assert.False(badge.IsShownOnPlan);
        Assert.All(renderer.PointMarkers, marker => Assert.True(marker.IsShownOnPlan));
    }

    [Fact]
    public void The_selected_mark_of_a_dense_spot_stays_on_the_plan()
    {
        var renderer = Renderer(Extract("a", 100, 100), Extract("b", 104, 102), Extract("c", 98, 105));

        renderer.SelectObject(new("object:b"));

        var selected = renderer.PointMarkers.Single(marker => marker.Label == "b");
        Assert.True(selected.IsShownOnPlan);
        Assert.False(renderer.PointMarkers.Single(marker => marker.Label == "a").IsShownOnPlan);
    }

    [Fact]
    public void A_dense_spot_holding_a_route_step_is_never_one_count_badge()
    {
        // [#307] The route's own step on Customs' Warehouse 17 hid under a "7" count badge.
        var renderer = Renderer(
            styleResolver: item => item.Label == "step" ? new MapSceneObjectStyle("#FFF1C75B", Badge: "7") : null,
            At("waypoint-a", MapSceneObjectKind.Waypoint, 100, 100),
            At("step", MapSceneObjectKind.Waypoint, 104, 102),
            At("ping", MapSceneObjectKind.Ping, 98, 105));

        Assert.Empty(renderer.StackMarkers);
        Assert.All(renderer.PointMarkers, marker => Assert.True(marker.IsShownOnPlan));
        var step = renderer.PointMarkers.Single(marker => marker.Label == "step");
        Assert.Equal("7", step.PinBadge);
        Assert.False(step.ShowsPinLetter);
    }

    [Fact]
    public void Quest_objectives_on_one_spot_are_never_one_count_badge_and_are_fanned_apart()
    {
        // [#797] On Reserve three objectives on one bunker read as an anonymous "3".
        var renderer = Renderer(
            At("A", MapSceneObjectKind.QuestObjective, 100, 100),
            At("B", MapSceneObjectKind.QuestObjective, 104, 102),
            At("C", MapSceneObjectKind.QuestObjective, 98, 105),
            At("D", MapSceneObjectKind.QuestObjective, 101, 99),
            At("E", MapSceneObjectKind.QuestObjective, 103, 104));

        Assert.Empty(renderer.StackMarkers);
        Assert.Empty(renderer.ClusterMarkers);
        Assert.All(renderer.PointMarkers, marker => Assert.True(marker.IsShownOnPlan));
        Assert.Equal(["A", "B", "C", "D", "E"], renderer.PointMarkers.Select(marker => marker.Label).Order());
        var drawn = renderer.PointMarkers
            .Select(marker => (X: marker.AnchorLeft + (marker.PinOffsetX * marker.MarkerScale), Y: marker.AnchorTop + (marker.PinOffsetY * marker.MarkerScale)))
            .ToArray();
        for (var i = 0; i < drawn.Length; i++)
        {
            for (var j = i + 1; j < drawn.Length; j++)
            {
                var distance = Math.Sqrt(Math.Pow(drawn[i].X - drawn[j].X, 2) + Math.Pow(drawn[i].Y - drawn[j].Y, 2));
                Assert.True(distance >= MapMarkerOverlapLayout.MinimumRingSpacing * MapMarkerScale.AtFit * 0.8, $"{i},{j}: {distance}");
            }
        }
    }

    [Fact]
    public void Extracts_beside_an_objective_still_share_a_badge_but_the_objective_is_never_in_it()
    {
        var renderer = Renderer(
            Extract("a", 100, 100), Extract("b", 104, 102), Extract("c", 98, 105),
            At("objective", MapSceneObjectKind.QuestObjective, 102, 103));

        Assert.Empty(renderer.StackMarkers);
        Assert.True(renderer.PointMarkers.Single(marker => marker.Label == "objective").IsShownOnPlan);
    }

    [Fact]
    public void A_crowded_scene_counts_everything_but_the_objectives()
    {
        var hazards = MapSceneRendererViewModel.MaximumPointMarkers + 20;
        var objects = Enumerable.Range(0, hazards)
            .Select(index => At($"x{index}", MapSceneObjectKind.Hazard, 5 + ((index % 20) * 20), 5 + ((index / 20) * 19)))
            .Append(At("A", MapSceneObjectKind.QuestObjective, 5, 5))
            .Append(At("B", MapSceneObjectKind.QuestObjective, 6, 6))
            .ToArray();

        var renderer = Renderer(objects);

        Assert.NotEmpty(renderer.ClusterMarkers);
        Assert.All(["A", "B"], letter => Assert.True(renderer.PointMarkers.Single(marker => marker.Label == letter).IsShownOnPlan));
        // Every count is hazards alone: the objectives are drawn and never counted.
        var counted = renderer.ClusterMarkers.Sum(cluster => int.Parse(cluster.MarkerGlyph, CultureInfo.InvariantCulture)) +
            renderer.PointMarkers.Count(marker => marker.Label.StartsWith('x'));
        Assert.Equal(hazards, counted);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void A_route_step_badge_never_covers_the_objective_letter(double zoom)
    {
        // [#797] On Windows the gold "5" hid the objective's letter under it at fit zoom.
        var renderer = Renderer(
            styleResolver: _ => new MapSceneObjectStyle(Badge: "5"),
            At("A", MapSceneObjectKind.QuestObjective, 100, 100));
        if (zoom > 1)
        {
            renderer.RequestZoom(zoom);
        }

        var pin = Assert.Single(renderer.PointMarkers);
        Assert.True(pin.ShowsPinLetter);
        var radius = MapSceneRendererObjectViewModel.PinBadgeExtent / 2 / pin.MarkerScale;
        var badgeX = pin.PinBadgeMargin.Left + (MapSceneRendererObjectViewModel.PinBadgeExtent / 2);
        var badgeY = pin.PinBadgeMargin.Top + (MapSceneRendererObjectViewModel.PinBadgeExtent / 2);
        var letterX = pin.PinWidth / 2;
        var letterY = pin.PinLabelMargin.Top + (pin.PinLabelFontSize / 2);
        var gap = Math.Sqrt(Math.Pow(badgeX - letterX, 2) + Math.Pow(badgeY - letterY, 2)) - radius;
        Assert.True(gap >= pin.PinLabelFontSize / 2, $"zoom {zoom}: the badge reaches {gap:F1} from the letter's middle");
    }

    [Fact]
    public void A_route_step_badge_keeps_its_screen_size_at_every_zoom()
    {
        var renderer = Renderer(
            styleResolver: _ => new MapSceneObjectStyle(Badge: "3"),
            At("step", MapSceneObjectKind.Waypoint, 100, 100));
        var step = Assert.Single(renderer.PointMarkers);

        Assert.Equal(1, step.PinBadgeScale * step.MarkerScale, 9);
        renderer.RequestZoom(1);
        step = Assert.Single(renderer.PointMarkers);
        Assert.True(step.MarkerScale > MapMarkerScale.AtFit);
        Assert.Equal(1, step.PinBadgeScale * step.MarkerScale, 9);
    }

    private static readonly MapSceneLayer Layer = new(new("extracts"), "Extracts", 10, true);

    private static MapSceneObject Extract(string id, double x, double y) => At(id, MapSceneObjectKind.Extract, x, y);

    private static MapSceneObject At(string id, MapSceneObjectKind kind, double x, double y) => new(
        new($"object:{id}"),
        Layer.Id,
        kind,
        kind is MapSceneObjectKind.Waypoint or MapSceneObjectKind.Ping
            ? MapSceneTruthKind.UserAuthored
            : MapSceneTruthKind.StaticReference,
        id,
        null,
        MapSceneGeometry.At(new(x, y)),
        [],
        new DataProvenance("fixture", new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero), Confidence: new Confidence(1)),
        faction: kind == MapSceneObjectKind.Extract ? MapFeatureFaction.Pmc : MapFeatureFaction.Unknown);

    private static MapSceneRendererViewModel Renderer(params MapSceneObject[] objects) => Renderer(null, objects);

    private static MapSceneRendererViewModel Renderer(
        Func<MapSceneObject, MapSceneObjectStyle?>? styleResolver,
        params MapSceneObject[] objects)
    {
        var scene = new MapSceneSnapshot(
            1,
            "shoreline",
            "shoreline",
            "shoreline",
            new MapSceneBounds(0, 0, 400, 300),
            [],
            new(MapSceneCapability.Available, MapSceneCapability.Unavailable("no stack"), MapSceneCapability.Unavailable("no interior")),
            new(MapSceneMode.Flat2D, null, new(200, 150, 1, 0, 0), [new(Layer.Id, true)]),
            [Layer],
            objects,
            []);
        var renderer = new MapSceneRendererViewModel(
            scene,
            MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc),
            showsDetailsPanel: false,
            styleResolver: styleResolver);
        renderer.ViewChangeRequested += change =>
        {
            var result = MapSceneViewReducer.Apply(renderer.Scene, change);
            if (result.Status is MapSceneViewChangeStatus.Applied or MapSceneViewChangeStatus.Unchanged)
            {
                renderer.Present(result.Scene);
            }
        };
        return renderer;
    }
}

/// <summary>[#573] Loot spawns: the most valuable few with the plan fitted, more zoomed in, the rest counted.</summary>
public sealed class MapLootRankingTests
{
    [Fact]
    public void The_top_ranks_show_fitted_and_the_rest_appear_as_the_plan_zooms_in()
    {
        Assert.Equal(0, MapLootRanking.RevealZoom(0));
        Assert.Equal(0, MapLootRanking.RevealZoom(MapLootRanking.TopAtFit - 1));
        Assert.True(MapLootRanking.RevealZoom(MapLootRanking.TopAtFit) > 1);
        // Four times the spawns on screen at twice the zoom: the same density per visible area.
        Assert.Equal(2, MapLootRanking.RevealZoom((4 * MapLootRanking.TopAtFit) - 1), 6);
    }

    [Fact]
    public void A_crowded_loot_layer_draws_twenty_fitted_and_counts_the_rest_on_badges()
    {
        var layer = new MapSceneLayer(new("loot"), "Loot", 10, true);
        var loot = Enumerable.Range(0, 60)
            .Select(index => new MapSceneObject(
                new($"loot:{index:D2}"),
                layer.Id,
                MapSceneObjectKind.LootSpawn,
                MapSceneTruthKind.PotentialSpawn,
                $"Loot {index}",
                null,
                MapSceneGeometry.At(new(20 + (index % 10 * 35), 30 + (index / 10 * 40))),
                [],
                new DataProvenance("fixture", new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero), Confidence: new Confidence(1))))
            .ToArray();
        var scene = new MapSceneSnapshot(
            1, "customs", "customs", "customs", new MapSceneBounds(0, 0, 400, 300), [],
            new(MapSceneCapability.Available, MapSceneCapability.Unavailable("no stack"), MapSceneCapability.Unavailable("no interior")),
            new(MapSceneMode.Flat2D, null, new(200, 150, 1, 0, 0), [new(layer.Id, true)]),
            [layer], loot, []);
        var renderer = new MapSceneRendererViewModel(
            scene,
            MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc),
            showsDetailsPanel: false,
            ranksLootByValue: true);
        renderer.ViewChangeRequested += change => renderer.Present(MapSceneViewReducer.Apply(renderer.Scene, change).Scene);

        Assert.Empty(renderer.PointMarkers);
        // Only what is drawn is in the list the view builds controls for.
        Assert.Equal(MapLootRanking.TopAtFit, renderer.LootMarkers.Count);
        Assert.All(renderer.LootMarkers, marker => Assert.True(marker.IsShownOnPlan));
        Assert.Equal(40, renderer.LootBadges.Where(badge => badge.IsShownOnPlan).Sum(badge => badge.HiddenLootCount));
        Assert.All(renderer.LootBadges, badge => Assert.StartsWith("+", badge.MarkerGlyph, StringComparison.Ordinal));

        var badge = renderer.LootBadges.First(item => item.IsShownOnPlan);
        badge.SelectCommand.Execute(null);

        Assert.True(renderer.CameraZoom >= MapSceneRendererViewModel.StackZoom);
        var shown = renderer.LootMarkers.Count(marker => marker.IsShownOnPlan);
        Assert.True(shown > MapLootRanking.TopAtFit, $"{shown} drawn at zoom {renderer.CameraZoom}");
        Assert.Equal(60 - shown, renderer.LootBadges.Sum(item => item.HiddenLootCount));
    }
}
