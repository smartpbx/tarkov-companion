using System.Text.Json;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// [#286] Draw mode's lines: simplified, bounded, placed in plan units, scoped like marks, and
/// shared with the squad inside the member's own state.
/// </summary>
public sealed class RaidDrawingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_straight_drag_of_hundreds_of_moves_keeps_only_its_ends()
    {
        var points = Enumerable.Range(0, 300).Select(index => new MapPoint(index * 0.5, index * 0.25)).ToArray();

        var simplified = StrokeSimplifier.Simplify(points, RaidDrawingLimits.SimplifyTolerance, RaidDrawingLimits.MaximumPoints);

        Assert.Equal(new[] { points[0], points[^1] }, simplified);
    }

    [Fact]
    public void A_corner_survives_simplifying()
    {
        var down = Enumerable.Range(0, 50).Select(index => new MapPoint(0, index));
        var across = Enumerable.Range(1, 50).Select(index => new MapPoint(index, 49));

        var simplified = StrokeSimplifier.Simplify([.. down, .. across], RaidDrawingLimits.SimplifyTolerance, RaidDrawingLimits.MaximumPoints);

        Assert.Equal(new[] { new MapPoint(0, 0), new MapPoint(0, 49), new MapPoint(50, 49) }, simplified);
    }

    [Fact]
    public void A_long_scribble_is_cut_to_the_point_bound_and_keeps_its_ends()
    {
        // A zigzag whose every corner is well past the tolerance: Douglas–Peucker alone keeps all 2,000.
        var points = Enumerable.Range(0, 2000).Select(index => new MapPoint(index, index % 2 == 0 ? 0 : 40)).ToArray();

        var simplified = StrokeSimplifier.Simplify(points, RaidDrawingLimits.SimplifyTolerance, RaidDrawingLimits.MaximumPoints);

        Assert.InRange(simplified.Count, 2, RaidDrawingLimits.MaximumPoints);
        Assert.Equal(points[0], simplified[0]);
        Assert.Equal(points[^1], simplified[^1]);
    }

    [Fact]
    public void A_click_or_points_that_are_not_numbers_are_not_a_line()
    {
        var store = new RaidDrawingStore(new Clock(Now));

        Assert.Null(store.Add("customs", null, [new(1, 1), new(1, 1)], RaidMarkScope.Squad, RaidMarkLifetime.ThisRaid));
        Assert.Null(store.Add("customs", null, [new(1, 1), new(double.NaN, 2)], RaidMarkScope.Squad, RaidMarkLifetime.ThisRaid));
        Assert.Empty(store.Drawings);
    }

    [Fact]
    public void The_store_keeps_the_newest_forty_lines()
    {
        var store = new RaidDrawingStore(new Clock(Now));
        var first = store.Add("customs", null, [new(0, 0), new(1, 1)], RaidMarkScope.Private, RaidMarkLifetime.ThisRaid);
        for (var index = 0; index < RaidDrawingLimits.MaximumStoredStrokes; index++)
        {
            store.Add("customs", null, [new(index, 0), new(index, 5)], RaidMarkScope.Private, RaidMarkLifetime.ThisRaid);
        }

        Assert.Equal(RaidDrawingLimits.MaximumStoredStrokes, store.Drawings.Count);
        Assert.DoesNotContain(store.Drawings, drawing => drawing.Id == first!.Id);
    }

    [Fact]
    public void Lifetimes_mean_what_they_mean_for_marks()
    {
        var clock = new Clock(Now);
        var store = new RaidDrawingStore(clock);
        var raid = store.Add("customs", null, [new(0, 0), new(5, 5)], RaidMarkScope.Squad, RaidMarkLifetime.ThisRaid)!;
        var five = store.Add("customs", null, [new(0, 0), new(6, 5)], RaidMarkScope.Squad, RaidMarkLifetime.FiveMinutes)!;
        var kept = store.Add("customs", null, [new(0, 0), new(7, 5)], RaidMarkScope.Squad, RaidMarkLifetime.UntilRemoved)!;

        Assert.Equal(Now + TimeSpan.FromMinutes(5), five.ExpiresUtc);
        Assert.Equal(five.ExpiresUtc, store.NextExpiryUtc);

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(store.Expire());
        Assert.Equal(new[] { raid.Id, kept.Id }, store.Drawings.Select(drawing => drawing.Id));

        Assert.True(store.EndRaid());
        Assert.Equal(new[] { kept.Id }, store.Drawings.Select(drawing => drawing.Id));
    }

    [Fact]
    public void Clearing_my_drawings_clears_one_map_only()
    {
        var store = new RaidDrawingStore(new Clock(Now));
        store.Add("customs", null, [new(0, 0), new(5, 5)], RaidMarkScope.Squad, RaidMarkLifetime.ThisRaid);
        store.Add("customs", "floor-2", [new(0, 0), new(5, 6)], RaidMarkScope.Private, RaidMarkLifetime.ThisRaid);
        var woods = store.Add("woods", null, [new(0, 0), new(5, 7)], RaidMarkScope.Squad, RaidMarkLifetime.ThisRaid)!;

        Assert.Equal(2, store.Clear("Customs"));
        Assert.Equal(new[] { woods.Id }, store.Drawings.Select(drawing => drawing.Id));
    }

    [Fact]
    public void Only_squad_lines_are_shared_newest_first_within_the_relay_bounds()
    {
        var clock = new Clock(Now);
        var store = new RaidDrawingStore(clock);
        var mine = store.Add("customs", null, [new(0, 0), new(5, 5)], RaidMarkScope.Private, RaidMarkLifetime.ThisRaid)!;
        var squad = new List<RaidDrawing>();
        for (var index = 0; index < 25; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            squad.Add(store.Add("customs", null, [new(index, 0), new(index, 5)], RaidMarkScope.Squad, RaidMarkLifetime.ThisRaid)!);
        }

        var shared = RaidDrawingStore.SelectShared(store.Drawings, drawing => drawing, drawing => drawing.Points.Count);

        Assert.Equal(RaidDrawingLimits.MaximumSharedStrokes, shared.Count);
        Assert.DoesNotContain(mine, shared);
        Assert.Equal(squad.Skip(5).Select(drawing => drawing.Id), shared.Select(drawing => drawing.Id));
    }

    [Fact]
    public void Shared_lines_stop_at_the_point_budget_so_a_publish_fits_the_relay()
    {
        var store = new RaidDrawingStore(new Clock(Now));
        var zigzag = Enumerable.Range(0, RaidDrawingLimits.MaximumPoints).Select(index => new MapPoint(index, index % 2 * 40)).ToArray();
        for (var index = 0; index < 8; index++)
        {
            store.Add("customs", null, zigzag, RaidMarkScope.Squad, RaidMarkLifetime.ThisRaid);
        }

        var shared = RaidDrawingStore.SelectShared(store.Drawings, drawing => drawing, drawing => drawing.Points.Count);

        Assert.Equal(RaidDrawingLimits.MaximumSharedPoints / RaidDrawingLimits.MaximumPoints, shared.Count);
        Assert.True(shared.Sum(drawing => drawing.Points.Count) <= RaidDrawingLimits.MaximumSharedPoints);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(213)]
    public void A_line_in_plan_units_goes_to_world_and_back_to_the_same_place(double rotation)
    {
        var transform = new MapCatalogTransform(1.7, 120, 2.3, -45, rotation);
        var drawing = new RaidDrawing(Guid.NewGuid(), "customs", "floor-1", [new(10, 20), new(35.5, -4), new(80, 12)], Now, RaidMarkScope.Squad, RaidMarkLifetime.ThisRaid, null);

        var world = RaidCockpitViewModel.ToWorld(transform, 2.5, drawing);

        Assert.NotNull(world);
        Assert.Equal("floor-1", world!.FloorId);
        Assert.Equal(12, world.Id.Length);
        var back = world.Points
            .Select(point => transform.TryProject(new WorldPosition(point.X, 2.5, point.Z), out var planPoint) ? planPoint : default)
            .ToArray();
        for (var index = 0; index < back.Length; index++)
        {
            Assert.Equal(drawing.Points[index].X, back[index].X, 6);
            Assert.Equal(drawing.Points[index].Y, back[index].Y, 6);
        }
    }

    [Fact]
    public void The_scene_draws_our_lines_on_their_floor_and_a_squadmates_in_their_colour()
    {
        var ours = new RaidDrawing(Guid.NewGuid(), "customs", "floor-2", [new(1, 1), new(4, 4)], Now, RaidMarkScope.Private, RaidMarkLifetime.ThisRaid, null);
        var elsewhere = ours with { Id = Guid.NewGuid(), MapId = "woods" };
        var theirs = new GroupDrawingView("abc", "customs", null, [(10, 20), (30, 40)]);
        var offMap = new GroupDrawingView("def", "woods", null, [(10, 20), (30, 40)]);

        var (layer, objects, styles) = RaidCockpitViewModel.BuildDrawingLayer(
            [ours, elsewhere],
            "customs",
            [("Geo", "#FFAA3300", theirs), ("Geo", "#FFAA3300", offMap)],
            mapId => mapId == "customs",
            position => new MapScenePoint(position.X / 10, position.Z / 10),
            Now);

        Assert.NotNull(layer);
        Assert.Equal(2, objects.Count);
        var own = objects.Single(item => item.Id.Value == $"drawing:{ours.Id}");
        Assert.Equal(MapSceneGeometryKind.Line, own.Geometry.Kind);
        Assert.Equal(new[] { "floor-2" }, own.FloorIds);
        var squad = objects.Single(item => item.Id.Value.StartsWith("squad-drawing:Geo:", StringComparison.Ordinal));
        Assert.Equal(new[] { new MapScenePoint(1, 2), new MapScenePoint(3, 4) }, squad.Geometry.Points);
        Assert.Equal("#FFAA3300", styles[squad.Id].Color);
    }

    [Fact]
    public void Lines_cross_the_wire_and_back_at_a_tenth_of_a_metre()
    {
        var sent = new GroupDrawingView("abc", "customs", "floor-1", [(12.345, -6.789), (100.04, 20.06)]);

        var json = JsonSerializer.Serialize(GroupDrawingWire.Describe([sent]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var read = GroupDrawingWire.Read(JsonSerializer.Deserialize<List<GroupDrawingDto>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.Contains("\"points\":[12.3,-6.8,100,20.1]", json, StringComparison.Ordinal);
        var line = Assert.Single(read);
        Assert.Equal("floor-1", line.FloorId);
        Assert.Equal(new[] { (12.3, -6.8), (100.0, 20.1) }, line.Points);
    }

    [Fact]
    public void Nothing_drawn_leaves_the_publish_as_it_was()
    {
        Assert.Null(GroupDrawingWire.Describe([]));
        Assert.Empty(GroupDrawingWire.Read(null));
    }

    [Fact]
    public void A_receiver_drops_lines_outside_the_bounds()
    {
        GroupDrawingDto Line(int numbers) => new("x", "customs", [.. Enumerable.Range(0, numbers).Select(index => (double)index)]);

        var read = GroupDrawingWire.Read([Line(4), Line(5), Line(2), Line(RaidDrawingLimits.MaximumPoints * 2 + 2), Line(400)]);

        Assert.Equal(new[] { 2, 200 }, read.Select(line => line.Points.Count));
    }

    [Fact]
    public void A_full_share_fits_well_inside_the_relays_body_bound()
    {
        var random = new Random(286);
        var lines = Enumerable.Range(0, 5)
            .Select(index => new GroupDrawingView(
                $"line{index:D8}",
                "streets-of-tarkov",
                "floor-basement",
                [.. Enumerable.Range(0, RaidDrawingLimits.MaximumSharedPoints / 5).Select(_ => (random.NextDouble() * -400.123, random.NextDouble() * 400.123))]))
            .ToArray();

        var json = JsonSerializer.Serialize(GroupDrawingWire.Describe(lines), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.True(json.Length < 16 * 1024, $"{json.Length} bytes");
    }

    [Fact]
    public void The_relay_holds_a_member_to_twenty_lines_of_two_hundred_points()
    {
        GroupDrawingState Line(int numbers) => new("x", "customs", [.. Enumerable.Range(0, numbers).Select(index => (double)index)]);
        GroupMemberState With(params GroupDrawingState[] drawings) =>
            new GroupMemberState("Geo", "customs", "InRaid", null, null, null, null, null, [], []) { Drawings = drawings };

        Assert.Null(With(Line(4), Line(400)).Validate());
        Assert.Null((With() with { Drawings = null }).Validate());
        Assert.NotNull(With(Line(402)).Validate());
        Assert.NotNull(With(Line(5)).Validate());
        Assert.NotNull(With(Line(2)).Validate());
        Assert.NotNull(With([.. Enumerable.Range(0, 21).Select(_ => Line(4))]).Validate());
        Assert.NotNull(With(new GroupDrawingState("x", new string('m', 65), [1, 2, 3, 4])).Validate());
    }

    [Fact]
    public void A_new_line_wakes_the_room_and_the_same_lines_do_not()
    {
        var rooms = new GroupRooms(TimeProvider.System);
        var member = new GroupMemberState("Geo", "customs", "InRaid", null, null, null, null, null, [], []);
        rooms.Publish("room", "Geo", member);

        var drawn = member with { Drawings = [new("a", "customs", [1, 2, 3, 4])] };
        Assert.True(rooms.Publish("room", "Geo", drawn));
        Assert.False(rooms.Publish("room", "Geo", drawn with { Drawings = [new("a", "customs", [1, 2, 3, 4])] }));
        Assert.Single(Assert.Single(rooms.Read("room", "Max").Members).Drawings!);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
