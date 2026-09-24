using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [Package 35] What the plan draws for a quest objective, and what it says about the ones it
/// cannot draw. The objectives are the real ones from fixtures/quest-zones; where the feed has no
/// example (an unsupported kind), the real objective is changed in the one way that makes it one.
/// </summary>
public sealed class QuestObjectiveSceneBuilderTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly RealQuestZones Zones = RealQuestZones.Load();

    [Fact]
    public void An_objective_with_an_outline_is_one_area_with_its_number_inside_it_however_often_the_feed_lists_it()
    {
        // Dorm room 214 is listed twice by json.tarkov.dev, identically.
        var scene = Build("customs");
        var entry = Entry(scene, "5a3fc032");

        Assert.Equal(QuestObjectivePlacement.Area, entry.Placement);
        Assert.Equal(1, entry.PlaceCount);
        Assert.Equal("Somewhere in this area", entry.PlacementLabel);
        var area = Assert.Single(scene.Objects, item => item.Id.Value.StartsWith("quest:5a3fc032", StringComparison.Ordinal) &&
            item.Geometry.Kind == MapSceneGeometryKind.Area);
        Assert.Equal(4, area.Geometry.Points.Count);
        var number = Assert.Single(scene.Objects, item => item.Id.Value.StartsWith("quest:5a3fc032", StringComparison.Ordinal) &&
            item.Geometry.Kind == MapSceneGeometryKind.Point);
        Assert.Equal(entry.Number, number.Label);
        Assert.True(Inside(area.Geometry.Points, number.Geometry.Points[0]));
        Assert.Equal(2, entry.ObjectIds.Count);
    }

    [Fact]
    public void A_number_lands_inside_a_region_that_wraps_around_its_own_middle()
    {
        // An L: the mean of its vertices is in the notch, outside the shape.
        MapScenePoint[] outline = [new(0, 0), new(10, 0), new(10, 2), new(2, 2), new(2, 10), new(0, 10)];

        var point = QuestObjectiveSceneBuilder.LabelPoint(outline);

        Assert.True(Inside(outline, point), $"({point.X}, {point.Y}) is outside the L.");
        Assert.False(Inside(outline, new(outline.Average(p => p.X), outline.Average(p => p.Y))));
    }

    [Fact]
    public void Several_possible_locations_are_several_spots_that_say_they_are_one_of_them()
    {
        // The console on Interchange: nine candidate spots in the mall, on its 3rd floor.
        var scene = Build("interchange");
        var entry = Entry(scene, "667a958e");

        Assert.Equal(QuestObjectivePlacement.Candidates, entry.Placement);
        Assert.Equal(9, entry.PlaceCount);
        Assert.Equal("One of 9 places", entry.PlacementLabel);
        Assert.Equal(9, scene.Objects.Count(item => item.Id.Value.StartsWith("quest:667a958e", StringComparison.Ordinal)));
        Assert.All(
            scene.Objects.Where(item => item.Id.Value.StartsWith("quest:667a958e", StringComparison.Ordinal)),
            item =>
            {
                Assert.Equal(MapSceneGeometryKind.Point, item.Geometry.Kind);
                Assert.Equal(entry.Number, item.Label);
                Assert.Contains("One of 9 places", item.Detail);
            });
    }

    [Fact]
    public void A_single_possible_location_is_a_marked_spot_and_not_one_of_one_places()
    {
        var model = Zones.Model("customs");
        var projected = Zones.Project("customs", model);
        var scene = new QuestObjectiveSceneBuilder().Build(projected, model.Floors, null, NowUtc);
        var entry = Entry(scene, "5968ec99");

        Assert.Equal(QuestObjectivePlacement.Point, entry.Placement);
        Assert.Equal("Marked spot", entry.PlacementLabel);

        // [#797] The catalog's own spawn position is the pin, exactly, and no area is drawn
        // around it: an exact spot never reads as "somewhere in this area".
        var exact = projected.Single(item => item.ObjectiveId.StartsWith("5968ec99", StringComparison.Ordinal) && item.HasExactGeometry);
        var pin = Assert.Single(scene.Objects, item => item.Id.Value.StartsWith("quest:5968ec99", StringComparison.Ordinal));
        Assert.Equal(MapSceneGeometryKind.Point, pin.Geometry.Kind);
        Assert.Equal(exact.Points[0].X, pin.Geometry.Points[0].X);
        Assert.Equal(exact.Points[0].Y, pin.Geometry.Points[0].Y);
        Assert.DoesNotContain("Somewhere in", pin.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_objective_with_no_zones_is_listed_as_having_no_location_and_takes_no_number()
    {
        var scene = Build("customs");
        var entry = Entry(scene, "5968eb9b");

        Assert.Equal(QuestObjectivePlacement.NoLocation, entry.Placement);
        Assert.False(entry.IsPlaced);
        Assert.Equal("No location", entry.PlacementLabel);
        Assert.Equal(string.Empty, entry.Number);
        Assert.Equal("The catalog gives no position for it.", entry.NoLocationReason);
        Assert.Empty(entry.ObjectIds);
        Assert.DoesNotContain(scene.Objects, item => item.Id.Value.StartsWith("quest:5968eb9b", StringComparison.Ordinal));

        // The letters on the map run A, B, C with no gap where this one would have been. Letters,
        // not numbers (issue 508): a quest objective's marker must never read like a waypoint's.
        var numbers = scene.Entries.Where(item => item.IsPlaced).Select(item => item.Number).ToArray();
        Assert.Equal(Enumerable.Range(1, numbers.Length).Select(QuestObjectiveLetters.LetterFor), numbers);
    }

    [Fact]
    public void An_unsupported_kind_is_never_guessed_onto_the_map_even_where_it_names_a_zone()
    {
        var real = Zones.Objectives("customs").Single(item => item.Id.StartsWith("5a3fc032", StringComparison.Ordinal)).ReadModel;
        var skill = real with { IsUnsupported = true, Kind = QuestObjectiveKind.Skill };
        var query = new QuestMapObjectivesReadModel(RealQuestZones.Scope, 1, RealQuestZones.Provenance, [Zones.GameMapId("customs")], [skill], []);
        var model = Zones.Model("customs");
        var projected = new QuestMapProjectionService()
            .Project(query, Zones.Location("customs"), model.Variant, null, Zones.MapProvenance)
            .Objectives;

        var scene = new QuestObjectiveSceneBuilder().Build(projected, model.Floors, null, NowUtc);

        var entry = Assert.Single(scene.Entries);
        Assert.Equal(QuestObjectivePlacement.NoLocation, entry.Placement);
        Assert.Equal("This kind of objective has no place on the map.", entry.NoLocationReason);
        Assert.Empty(scene.Objects);
    }

    [Fact]
    public void A_caller_that_numbers_its_own_rows_is_believed()
    {
        var model = Zones.Model("customs");
        var projected = Zones.Project("customs", model);

        var scene = new QuestObjectiveSceneBuilder().Build(projected, model.Floors, id => id.StartsWith("5a3fc032", StringComparison.Ordinal) ? "7" : null, NowUtc);

        Assert.Equal("7", Entry(scene, "5a3fc032").Number);
        var drawn = scene.Objects.Where(item => item.Id.Value.StartsWith("quest:5a3fc032", StringComparison.Ordinal)).ToArray();
        Assert.Contains(drawn, item => item.Geometry.Kind == MapSceneGeometryKind.Point && item.Label == "7");
        Assert.Contains(drawn, item => item.Geometry.Kind == MapSceneGeometryKind.Area && item.Label == "Area 7");
    }

    private static QuestObjectiveScene Build(string map)
    {
        var model = Zones.Model(map);
        return new QuestObjectiveSceneBuilder().Build(Zones.Project(map, model), model.Floors, null, NowUtc);
    }

    private static QuestObjectiveEntry Entry(QuestObjectiveScene scene, string objectiveStart) =>
        scene.Entries.Single(entry => entry.ObjectiveId.StartsWith(objectiveStart, StringComparison.Ordinal));

    private static bool Inside(IReadOnlyList<MapScenePoint> polygon, MapScenePoint point)
    {
        var inside = false;
        for (int index = 0, previous = polygon.Count - 1; index < polygon.Count; previous = index++)
        {
            var a = polygon[index];
            var b = polygon[previous];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < ((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
