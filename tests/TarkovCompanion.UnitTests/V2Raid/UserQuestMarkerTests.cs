using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Infrastructure.Maps;
using TarkovCompanion.UnitTests.V2MapRenderer;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// Issue 379: an objective the quest data gives no place for can be put on the map by the player,
/// and what they put there is theirs, never official.
/// </summary>
public sealed class UserQuestMarkerTests : IDisposable
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly RealQuestZones Zones = RealQuestZones.Load();

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tarkov-questmarkers-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void AnObjectiveWithNoPlaceGetsTheNextNumberAndAMarkerThatSaysItIsTheirs()
    {
        var (map, model, scene) = MapWithBothKinds();
        var unplaced = scene.Entries.First(entry => entry.Placement == QuestObjectivePlacement.NoLocation);
        var highest = scene.Entries.Where(entry => entry.IsPlaced).Max(entry => QuestObjectiveLetters.IndexFor(entry.Number));
        var marker = new UserQuestMarker(unplaced.ObjectiveId, model.Location.Id, null, 12.5, 34.5, NowUtc);

        var placed = UserQuestMarkerScene.Apply(scene, [marker], model.Location.Id, model.Floors);

        var entry = placed.Entries.Single(candidate => candidate.ObjectiveId == unplaced.ObjectiveId);
        Assert.Equal(QuestObjectivePlacement.UserPlaced, entry.Placement);
        Assert.True(entry.IsPlaced);
        Assert.Equal("Placed by you", entry.PlacementLabel);
        Assert.Null(entry.NoLocationReason);
        // Issue 508: a player's own marker keeps the same letter sequence as the catalog's.
        Assert.Equal(QuestObjectiveLetters.LetterFor(highest + 1), entry.Number);
        var spot = Assert.Single(placed.Objects, item => item.Id == entry.ObjectIds.Single());
        Assert.Equal(MapSceneTruthKind.UserAuthored, spot.Truth);
        Assert.Equal(MapSceneObjectKind.QuestObjective, spot.Kind);
        Assert.Contains("Placed by you, not from the quest data.", spot.Detail, StringComparison.Ordinal);
        Assert.Equal([new MapScenePoint(12.5, 34.5)], spot.Geometry.Points);
        Assert.Equal(scene.Objects.Count + 1, placed.Objects.Count);
        _ = map;
    }

    [Fact]
    public void AnObjectiveTheCatalogPlacedKeepsTheCatalogsPlaceAndAMarkerOnAnotherMapIsIgnored()
    {
        var (_, model, scene) = MapWithBothKinds();
        var official = scene.Entries.First(entry => entry.IsPlaced);
        var unplaced = scene.Entries.First(entry => entry.Placement == QuestObjectivePlacement.NoLocation);

        var placed = UserQuestMarkerScene.Apply(
            scene,
            [
                new UserQuestMarker(official.ObjectiveId, model.Location.Id, null, 1, 1, NowUtc),
                new UserQuestMarker(unplaced.ObjectiveId, "some-other-map", null, 2, 2, NowUtc),
            ],
            model.Location.Id,
            model.Floors);

        Assert.Same(scene, placed);
        Assert.Equal(official.Placement, placed.Entries.Single(entry => entry.ObjectiveId == official.ObjectiveId).Placement);
    }

    [Fact]
    public async Task MarkersPersistAcrossRunsReplaceEachOtherAndCanBeRemoved()
    {
        var path = Path.Combine(_root, "user-quest-markers.json");
        var first = new JsonFileUserQuestMarkStore(path);
        await first.PlaceAsync("obj-1", "customs", "ground", 1, 2);
        await first.PlaceAsync("obj-1", "customs", "upper", 3, 4);
        await first.PlaceAsync("obj-1", "woods", null, 5, 6);

        var second = new JsonFileUserQuestMarkStore(path);
        await second.LoadAsync();

        Assert.Equal(2, second.Markers.Count);
        var customs = Assert.Single(second.Markers, marker => marker.MapId == "customs");
        Assert.Equal((3d, 4d, "upper"), (customs.X, customs.Y, customs.FloorId));

        await second.RemoveAsync("obj-1", "customs");
        var third = new JsonFileUserQuestMarkStore(path);
        await third.LoadAsync();
        Assert.Equal(["woods"], third.Markers.Select(marker => marker.MapId));
    }

    [Fact]
    public async Task ADamagedFileOrAHostilePositionCostsTheMarkerAndNothingElse()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "user-quest-markers.json");
        await File.WriteAllTextAsync(path, "{ not json");
        var store = new JsonFileUserQuestMarkStore(path);
        await store.LoadAsync();
        Assert.Empty(store.Markers);

        await Assert.ThrowsAsync<ArgumentException>(() => store.PlaceAsync("obj-1", "customs", null, double.NaN, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => store.PlaceAsync(" ", "customs", null, 1, 1));
        Assert.Empty(store.Markers);

        await File.WriteAllTextAsync(
            path,
            """{ "markers": [ { "objectiveId": "", "mapId": "customs", "x": 1, "y": 2 }, { "objectiveId": "ok", "mapId": "customs", "x": 3, "y": 4 } ] }""");
        var reloaded = new JsonFileUserQuestMarkStore(path);
        await reloaded.LoadAsync();
        Assert.Equal("ok", Assert.Single(reloaded.Markers).ObjectiveId);
    }

    private static (string Map, TarkovCompanion.Application.Services.Maps.MapRenderModel Model, QuestObjectiveScene Scene) MapWithBothKinds()
    {
        foreach (var map in new[] { "lighthouse", "customs", "woods", "shoreline", "interchange", "reserve", "streets-of-tarkov" })
        {
            var model = Zones.Model(map);
            var projection = new QuestMapProjectionReadModel(RealQuestZones.Scope, 1, model.Location.Id, model.Variant.Key, Zones.Project(map, model), []);
            var scene = RaidCockpitViewModel.BuildQuestScene(projection, model, NowUtc);
            if (scene.Entries.Any(entry => entry.IsPlaced) &&
                scene.Entries.Any(entry => entry.Placement == QuestObjectivePlacement.NoLocation))
            {
                return (map, model, scene);
            }
        }

        throw new InvalidOperationException("The real quest-zone fixture has no map with both placed and unplaced objectives.");
    }
}
