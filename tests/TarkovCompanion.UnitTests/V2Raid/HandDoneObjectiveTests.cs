using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Infrastructure.Maps;
using TarkovCompanion.UnitTests.V2MapRenderer;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// Issue 571: an objective the player marks done by hand leaves the map and its list at once;
/// "Show completed" brings it back dimmed with a check, exactly as one progress already calls
/// complete has always been drawn.
/// </summary>
public sealed class HandDoneObjectiveSceneTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly RealQuestZones Zones = RealQuestZones.Load();

    [Fact]
    public void A_hand_marked_objective_leaves_the_map_and_the_list()
    {
        var scene = PlacedScene(out var objectiveId);

        var hidden = HandDoneObjectiveScene.Apply(scene, [objectiveId], showCompleted: false);

        Assert.DoesNotContain(hidden.Entries, entry => entry.ObjectiveId == objectiveId);
        Assert.DoesNotContain(hidden.Objects, item => ObjectiveIdOf(item.Id) == objectiveId);
        Assert.True(hidden.Entries.Count < scene.Entries.Count);
        // Nothing else on the map moved or lost its letter because of this.
        foreach (var survivor in scene.Entries.Where(entry => entry.ObjectiveId != objectiveId))
        {
            Assert.Equal(survivor.Number, hidden.Entries.Single(entry => entry.ObjectiveId == survivor.ObjectiveId).Number);
        }
    }

    [Fact]
    public void Show_completed_brings_it_back_dimmed_with_a_check()
    {
        var scene = PlacedScene(out var objectiveId);

        var shown = HandDoneObjectiveScene.Apply(scene, [objectiveId], showCompleted: true);

        Assert.Equal(scene.Entries.Count, shown.Entries.Count);
        var entry = shown.Entries.Single(e => e.ObjectiveId == objectiveId);
        Assert.NotEmpty(entry.ObjectIds);
        Assert.All(entry.ObjectIds, id => Assert.True(shown.Objects.Single(item => item.Id == id).IsCompleted));
    }

    [Fact]
    public void Nothing_marked_leaves_the_scene_exactly_as_it_was()
    {
        var scene = PlacedScene(out _);

        var untouched = HandDoneObjectiveScene.Apply(scene, [], showCompleted: false);

        Assert.Same(scene, untouched);
    }

    [Fact]
    public void An_objective_progress_already_calls_complete_is_hidden_the_same_way_with_no_hand_mark_at_all()
    {
        var scene = PlacedScene(out var objectiveId, RecordedObjectiveState.Completed);

        var hidden = HandDoneObjectiveScene.Apply(scene, [], showCompleted: false);

        Assert.DoesNotContain(hidden.Entries, entry => entry.ObjectiveId == objectiveId);
    }

    [Fact]
    public async Task Marking_done_persists_and_a_later_load_still_hides_it_not_done_undoes_it()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-handdone-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(root, "hand-done-objectives.json");
            var profile = Guid.NewGuid();
            var first = new JsonFileHandDoneObjectiveStore(path);
            await first.MarkDoneAsync(profile, "task-1", "objective-1");
            await first.MarkDoneAsync(profile, "task-2", "objective-2");

            var second = new JsonFileHandDoneObjectiveStore(path);
            await second.LoadAsync();
            Assert.Equal(2, second.Entries.Count);
            Assert.Contains(second.Entries, mark => mark.ObjectiveId == "objective-1" && mark.TaskId == "task-1");

            await second.MarkNotDoneAsync(profile, "objective-1");
            var third = new JsonFileHandDoneObjectiveStore(path);
            await third.LoadAsync();
            Assert.Equal(["objective-2"], third.Entries.Select(mark => mark.ObjectiveId));
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
        }
    }

    [Fact]
    public async Task A_quest_reported_failed_or_restarted_clears_every_hand_mark_of_it_together()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-handdone-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(root, "hand-done-objectives.json");
            var profile = Guid.NewGuid();
            var store = new JsonFileHandDoneObjectiveStore(path);
            await store.MarkDoneAsync(profile, "task-1", "objective-1");
            await store.MarkDoneAsync(profile, "task-1", "objective-2");
            await store.MarkDoneAsync(profile, "task-2", "objective-3");

            await store.ClearForTaskAsync(profile, "task-1");

            Assert.Equal(["objective-3"], store.Entries.Select(mark => mark.ObjectiveId));
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
        }
    }

    [Fact]
    public async Task A_damaged_file_or_an_incomplete_mark_costs_that_mark_and_nothing_else()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-handdone-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "hand-done-objectives.json");
            await File.WriteAllTextAsync(path, "{ not json");
            var store = new JsonFileHandDoneObjectiveStore(path);
            await store.LoadAsync();
            Assert.Empty(store.Entries);

            await Assert.ThrowsAsync<ArgumentException>(() => store.MarkDoneAsync(Guid.Empty, "task-1", "objective-1"));
            await Assert.ThrowsAsync<ArgumentException>(() => store.MarkDoneAsync(Guid.NewGuid(), " ", "objective-1"));
            Assert.Empty(store.Entries);
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
        }
    }

    /// <summary>Issue 571: a letter this raid already assigned is never handed to a rebuild's next
    /// pass just because the objective that first got it left the map in between.</summary>
    [Fact]
    public void Rebuilding_after_an_objective_leaves_the_projection_keeps_the_survivors_letters()
    {
        var map = MapWithTwoPlaced(out var model, out var objectives);
        var letters = new QuestObjectiveLetterAssignment();
        var projection = new QuestMapProjectionReadModel(RealQuestZones.Scope, 1, model.Location.Id, model.Variant.Key, objectives, []);

        var before = RaidCockpitViewModel.BuildQuestScene(projection, model, NowUtc, letters);
        var kept = before.Entries.Where(entry => entry.IsPlaced).Skip(1).Select(entry => entry.ObjectiveId).ToArray();
        Assert.NotEmpty(kept);
        var keptLettersBefore = kept.ToDictionary(id => id, id => before.Entries.Single(entry => entry.ObjectiveId == id).Number);

        // The quest that owned the first placed objective is done and its objective no longer
        // projects at all — the same shape a game-log-driven task completion produces upstream.
        var removedId = before.Entries.First(entry => entry.IsPlaced).ObjectiveId;
        var shrunk = objectives.Where(item => item.ObjectiveId != removedId).ToArray();
        var afterProjection = projection with { Objectives = shrunk };

        var after = RaidCockpitViewModel.BuildQuestScene(afterProjection, model, NowUtc, letters);

        foreach (var id in kept)
        {
            Assert.Equal(keptLettersBefore[id], after.Entries.Single(entry => entry.ObjectiveId == id).Number);
        }

        _ = map;
    }

    private static string ObjectiveIdOf(MapSceneObjectId id)
    {
        var value = id.Value;
        var first = value.IndexOf(':');
        var second = value.IndexOf(':', first + 1);
        return second < 0 ? value[(first + 1)..] : value[(first + 1)..second];
    }

    private static QuestObjectiveScene PlacedScene(out string objectiveId, RecordedObjectiveState state = RecordedObjectiveState.InProgress)
    {
        const string map = "customs";
        var model = Zones.Model(map);
        var readModels = Zones.Objectives(map).Select(item => item.ReadModel).ToArray();
        var target = readModels.First(item => item.Zones.Count > 0);
        var targetId = target.ObjectiveId;
        var edited = readModels
            .Select(item => item.ObjectiveId == targetId ? item with { ObjectiveState = state } : item)
            .ToArray();
        var query = new QuestMapObjectivesReadModel(RealQuestZones.Scope, 1, RealQuestZones.Provenance, [Zones.GameMapId(map)], edited, []);
        var projected = new QuestMapProjectionService().Project(query, Zones.Location(map), model.Variant, null, Zones.MapProvenance).Objectives;
        var scene = new QuestObjectiveSceneBuilder().Build(projected, model.Floors, null, NowUtc);
        Assert.Contains(scene.Entries, entry => entry.ObjectiveId == targetId && entry.IsPlaced);
        objectiveId = targetId;
        return scene;
    }

    private static string MapWithTwoPlaced(
        out TarkovCompanion.Application.Services.Maps.MapRenderModel model,
        out IReadOnlyList<QuestMapObjectiveProjection> objectives)
    {
        foreach (var map in new[] { "customs", "lighthouse", "interchange", "reserve", "streets-of-tarkov", "shoreline", "woods" })
        {
            var candidateModel = Zones.Model(map);
            var projected = Zones.Project(map, candidateModel);
            var scene = new QuestObjectiveSceneBuilder().Build(projected, candidateModel.Floors, null, NowUtc);
            if (scene.Entries.Count(entry => entry.IsPlaced) >= 2)
            {
                model = candidateModel;
                objectives = projected;
                return map;
            }
        }

        throw new InvalidOperationException("The real quest-zone fixture has no map with two placed objectives.");
    }
}
