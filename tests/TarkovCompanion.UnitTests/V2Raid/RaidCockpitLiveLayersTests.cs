using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.UnitTests.PlayerTime;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// [V2 rough package 22] The V1 map's live evidence — you, your trail, the squad, past raids —
/// turned into scene layers the canonical renderer draws.
/// </summary>
public sealed class RaidCockpitLiveLayersTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_observed_produces_no_layers_at_all()
    {
        var built = RaidCockpitViewModel.BuildLiveLayers(Inputs(), Model(), NowUtc);

        Assert.Empty(built.Layers);
        Assert.Empty(built.Objects);
        Assert.Empty(built.Styles);
    }

    [Fact]
    public void A_screenshot_position_becomes_a_local_last_known_marker_with_its_facing()
    {
        var built = RaidCockpitViewModel.BuildLiveLayers(
            Inputs(player: Position(30, 40, heading: 120, secondsAgo: 5)),
            Model(),
            NowUtc);

        Assert.Equal(["you"], built.Layers.Select(layer => layer.Id.Value));
        var marker = Assert.Single(built.Objects, item => item.Kind == MapSceneObjectKind.LastKnownPosition);
        Assert.Equal(MapSceneTruthKind.LocalLastKnown, marker.Truth);
        Assert.Equal(120, marker.HeadingDegrees);
        Assert.Equal(30, marker.Geometry.Points[0].X);
        Assert.Equal(40, marker.Geometry.Points[0].Y);
        Assert.StartsWith("You · ", marker.Label, StringComparison.Ordinal);
        Assert.Contains("facing 120°", marker.Label, StringComparison.Ordinal);
        // Fresh: nothing is faded out.
        Assert.Equal(1, built.Styles[marker.Id].Opacity);
    }

    [Fact]
    public void A_stale_position_is_faded_and_says_why()
    {
        var built = RaidCockpitViewModel.BuildLiveLayers(
            Inputs(player: Position(30, 40, heading: 0, secondsAgo: 600)),
            Model(),
            NowUtc);

        var marker = Assert.Single(built.Objects, item => item.Kind == MapSceneObjectKind.LastKnownPosition);
        Assert.NotNull(marker.Detail);
        Assert.Equal(0.65, built.Styles[marker.Id].Opacity);
    }

    [Fact]
    public void The_heading_is_turned_by_the_artwork_rotation_so_the_cone_points_where_they_looked()
    {
        // The screenshot records a world bearing; the artwork is drawn with the world turned by
        // its transform's own rotation, so the two differ by exactly that.
        Assert.Equal(30, RaidCockpitViewModel.Bearing(120, 90));
        Assert.Equal(350, RaidCockpitViewModel.Bearing(80, 90));
        Assert.Equal(0, RaidCockpitViewModel.Bearing(double.NaN, 90));
    }

    [Fact]
    public void The_trail_becomes_one_route_line_and_a_single_step_becomes_nothing()
    {
        var single = RaidCockpitViewModel.BuildLiveLayers(
            Inputs(trail: [Position(10, 10, 0, 30)]),
            Model(),
            NowUtc);
        Assert.Empty(single.Objects);

        var built = RaidCockpitViewModel.BuildLiveLayers(
            Inputs(trail: [Position(10, 10, 0, 30), Position(20, 25, 0, 20), Position(30, 40, 0, 10)]),
            Model(),
            NowUtc);

        var route = Assert.Single(built.Objects, item => item.Kind == MapSceneObjectKind.Route);
        Assert.Equal(MapSceneGeometryKind.Line, route.Geometry.Kind);
        Assert.Equal(3, route.Geometry.Points.Count);
        Assert.Equal(MapSceneTruthKind.LocalLastKnown, route.Truth);
    }

    [Fact]
    public void A_step_this_transform_cannot_place_on_the_plan_is_dropped_rather_than_taking_the_trail_with_it()
    {
        // The renderer refuses to draw a line that leaves the plan at all, so one stray position
        // must not cost the whole path.
        var built = RaidCockpitViewModel.BuildLiveLayers(
            Inputs(trail: [Position(10, 10, 0, 30), Position(4000, 4000, 0, 20), Position(30, 40, 0, 10)]),
            Model(),
            NowUtc);

        var route = Assert.Single(built.Objects, item => item.Kind == MapSceneObjectKind.Route);
        Assert.Equal(2, route.Geometry.Points.Count);
    }

    [Fact]
    public void Squadmates_on_this_map_become_team_shared_markers_in_their_own_colour()
    {
        var built = RaidCockpitViewModel.BuildLiveLayers(
            Inputs(squad: [Mate("Geo", 60, 20, heading: 200), Mate("Riley", 20, 70, heading: null)]),
            Model(),
            NowUtc);

        Assert.Equal(["squad"], built.Layers.Select(layer => layer.Id.Value));
        var geo = Assert.Single(built.Objects, item => item.Id.Value == "squad:Geo");
        Assert.Equal(MapSceneObjectKind.TeammateLastKnown, geo.Kind);
        Assert.Equal(MapSceneTruthKind.TeamSharedLastKnown, geo.Truth);
        Assert.Equal(200, geo.HeadingDegrees);
        Assert.Equal("#FF00FF00", built.Styles[geo.Id].Color);

        var riley = Assert.Single(built.Objects, item => item.Id.Value == "squad:Riley");
        Assert.Null(riley.HeadingDegrees);
    }

    /// <summary>
    /// [Issue 581] The test above stubs every member to the one fake colour, so it never actually
    /// proved two squadmates read apart on the map — only that whatever ColorFor returns reaches
    /// the marker. This wires the real assignment through, the same way MapViewModel.GroupColorFor
    /// now does, so it also catches the un-prefixed-hex bug that made every teammate fall back to
    /// one shared colour in the running app (Avalonia's colour parser silently rejected "E0B45C").
    /// </summary>
    [Fact]
    public void Two_real_squadmates_get_their_own_distinct_well_formed_colour_on_marker_and_name()
    {
        var assigned = GroupMemberColors.Assign(["Geo", "Riley"]);
        string ColorFor(string name) => GroupMemberColors.WithAlpha(
            assigned.GetValueOrDefault(name, GroupMemberColors.Fallback), "FF");

        var built = RaidCockpitViewModel.BuildLiveLayers(
            Inputs(
                squad: [Mate("Geo", 60, 20, heading: 0), Mate("Riley", 20, 70, heading: 0)],
                showsGroupNames: true,
                colorFor: ColorFor),
            Model(),
            NowUtc);

        var geo = Assert.Single(built.Objects, item => item.Id.Value == "squad:Geo");
        var riley = Assert.Single(built.Objects, item => item.Id.Value == "squad:Riley");
        var geoColor = built.Styles[geo.Id].Color;
        var rileyColor = built.Styles[riley.Id].Color;
        Assert.NotNull(geoColor);
        Assert.NotNull(rileyColor);
        Assert.NotEqual(geoColor, rileyColor);
        // Well-formed: a leading '#' and eight hex digits, exactly what GroupMemberColors.WithAlpha
        // builds and what Avalonia's own Color.TryParse needs — a bare "E0B45C" (no '#') was the bug.
        Assert.Matches("^#[0-9A-Fa-f]{8}$", geoColor!);
        Assert.Matches("^#[0-9A-Fa-f]{8}$", rileyColor!);

        // The name label is the same colour as its marker — one source of truth.
        var geoLabel = Assert.Single(built.Objects, item => item.Id.Value == "squad-name:Geo");
        Assert.Equal(geoColor, built.Styles[geoLabel.Id].Color);
    }

    [Fact]
    public void A_squadmate_on_another_map_is_not_projected_onto_this_one()
    {
        var built = RaidCockpitViewModel.BuildLiveLayers(
            Inputs(squad: [Mate("Geo", 60, 20, heading: 0, mapId: "streets")], isOnThisMap: id => id == "factory"),
            Model(),
            NowUtc);

        Assert.Empty(built.Objects);
    }

    [Fact]
    public void Squad_names_are_written_beside_their_markers_only_when_names_are_on()
    {
        var off = RaidCockpitViewModel.BuildLiveLayers(
            Inputs(squad: [Mate("Geo", 60, 20, heading: 0)]),
            Model(),
            NowUtc);
        Assert.DoesNotContain(off.Objects, item => item.Kind == MapSceneObjectKind.Label);

        var on = RaidCockpitViewModel.BuildLiveLayers(
            Inputs(squad: [Mate("Geo", 60, 20, heading: 0)], showsGroupNames: true),
            Model(),
            NowUtc);

        var label = Assert.Single(on.Objects, item => item.Kind == MapSceneObjectKind.Label);
        Assert.Equal("Geo", label.Label);
        Assert.Equal("#FF00FF00", on.Styles[label.Id].Color);
    }

    [Fact]
    public void Past_raids_become_their_own_layer_which_is_off_until_it_is_asked_for()
    {
        var visited = new[]
        {
            new RaidTrail(Guid.NewGuid(), NowUtc.AddDays(-1), [Position(10, 10, 0, 0), Position(20, 20, 0, 0)]),
            new RaidTrail(Guid.NewGuid(), NowUtc.AddDays(-2), [Position(30, 30, 0, 0), Position(40, 40, 0, 0)]),
        };

        var off = RaidCockpitViewModel.BuildLiveLayers(Inputs(visited: visited), Model(), NowUtc);
        var layer = Assert.Single(off.Layers);
        Assert.Equal("visited", layer.Id.Value);
        Assert.False(layer.IsVisibleByDefault);

        var on = RaidCockpitViewModel.BuildLiveLayers(Inputs(visited: visited, showsVisited: true), Model(), NowUtc);
        Assert.True(Assert.Single(on.Layers).IsVisibleByDefault);
        Assert.Equal(2, on.Objects.Count);
        // Oldest faintest, as V1 draws them: the history arrives newest first.
        var opacities = on.Objects.Select(item => on.Styles[item.Id].Opacity).ToArray();
        Assert.True(opacities[0] > opacities[1]);
    }

    /// <summary>
    /// The map's own labels read the player's clock. The position is 11:59:55 UTC, which a UTC label
    /// would print as-is and a wall clock at UTC-4 reads as 07:59:55; the trail's raid began at
    /// 02:00 UTC on the 18th, which is still the evening of the 17th there.
    /// </summary>
    [Fact]
    public void The_map_labels_read_the_players_clock_not_utc()
    {
        using var pin = PlayerClock.Pin();
        var visited = new[]
        {
            new RaidTrail(
                Guid.NewGuid(),
                new DateTimeOffset(2026, 9, 18, 2, 0, 0, TimeSpan.Zero),
                [Position(10, 10, 0, 0), Position(20, 20, 0, 0)]),
        };

        var built = RaidCockpitViewModel.BuildLiveLayers(
            Inputs(player: Position(30, 40, heading: 120, secondsAgo: 5), visited: visited, showsVisited: true),
            Model(),
            NowUtc);

        var marker = Assert.Single(built.Objects, item => item.Kind == MapSceneObjectKind.LastKnownPosition);
        Assert.Equal("You · 07:59:55 · facing 120°", marker.Label);
        Assert.Contains(built.Objects, item => item.Label == "Raid on 09/17/2026");
    }

    [Fact]
    public void Every_live_object_carries_a_stable_identity_so_a_rebuild_is_not_a_duplicate()
    {
        var inputs = Inputs(
            player: Position(30, 40, 0, 5),
            trail: [Position(10, 10, 0, 30), Position(30, 40, 0, 5)],
            squad: [Mate("Geo", 60, 20, heading: 0)],
            showsGroupNames: true);

        var first = RaidCockpitViewModel.BuildLiveLayers(inputs, Model(), NowUtc);
        var second = RaidCockpitViewModel.BuildLiveLayers(inputs, Model(), NowUtc.AddSeconds(30));

        Assert.Equal(
            first.Objects.Select(item => item.Id.Value).Order(),
            second.Objects.Select(item => item.Id.Value).Order());
        Assert.Equal(first.Objects.Count, first.Objects.Select(item => item.Id).Distinct().Count());
    }

    private static RaidCockpitViewModel.LiveSceneInputs Inputs(
        ScreenshotPosition? player = null,
        IReadOnlyList<ScreenshotPosition>? trail = null,
        IReadOnlyList<GroupMemberView>? squad = null,
        Func<string?, bool>? isOnThisMap = null,
        bool showsGroupNames = false,
        IReadOnlyList<RaidTrail>? visited = null,
        bool showsVisited = false,
        Func<string, string>? colorFor = null) => new(
            player,
            trail ?? [],
            squad ?? [],
            isOnThisMap ?? (_ => true),
            colorFor ?? (_ => "#FF00FF00"),
            showsGroupNames,
            visited ?? [],
            showsVisited);

    /// <summary>A screenshot whose world position lands exactly on the plan point asked for.</summary>
    private static ScreenshotPosition Position(double planX, double planY, double heading, int secondsAgo) =>
        new(NowUtc.AddSeconds(-secondsAgo), new(planX, 0, -planY), default, heading, null, null, $"{planX}-{planY}.png");

    private static GroupMemberView Mate(string name, double planX, double planY, double? heading, string mapId = "factory") =>
        new(
            name,
            mapId,
            RaidLifecycleState.InRaid,
            "PMC",
            new(planX, 0, -planY),
            heading,
            TimeSpan.FromSeconds(5),
            [],
            []);

    /// <summary>
    /// A map whose transform is the identity on X and negates Z, so a world position written as
    /// (planX, 0, -planY) lands on the plan at (planX, planY) and a test can say where something
    /// should appear without solving a projection first.
    /// </summary>
    private static MapRenderModel Model()
    {
        var location = new MapLocation("factory", null, "Factory", null, null, []);
        var floors = new[]
        {
            new MapFloorDefinition("lower", "Lower", null, null, true, [new(-10, 10, [])]),
        };
        var variant = new MapVariant(
            location.Id,
            "factory-plan",
            MapProjectionKind.TwoDimensional,
            "2D",
            null,
            null,
            new("https://example.test/factory.svg"),
            null,
            256,
            null,
            null,
            // World Z runs the other way from plan Y (the transform negates it), so the reviewed
            // bounds have to run the other way too for the projected plan rectangle to be the
            // 0-100 box this fixture's positions are written against.
            new(new(0, 0), new(100, -100)),
            new(new(0, 0), new(100, -100)),
            new(1, 0, 1, 0, 0),
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
            new(MapBackgroundKind.Svg, variant.SvgPath!, "/cache/factory.svg", MapAssetAvailability.Available, null),
            MapTransformAvailability.Valid,
            "Validated transform.",
            overlays,
            [],
            floors,
            floors[0],
            "Example attribution",
            new("https://example.test/licence"));
    }
}
