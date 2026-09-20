using System.Globalization;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.Application.Services.Quests;

/// <summary>
/// Where the player says a quest objective happens, for one the quest data gives no place for.
/// </summary>
/// <remarks>
/// Source is always the player. It is kept apart from the catalog on purpose: a sync replaces the
/// catalog wholesale, and a marker is a note the player wrote, so it must survive that and must
/// never be mistaken for quest data. Plan units, the same rectangle the map's own marks use.
/// </remarks>
public sealed record UserQuestMarker(
    string ObjectiveId,
    string MapId,
    string? FloorId,
    double X,
    double Y,
    DateTimeOffset PlacedUtc);

/// <summary>The player's own objective markers, kept between runs.</summary>
public interface IUserQuestMarkStore
{
    IReadOnlyList<UserQuestMarker> Markers { get; }

    /// <summary>Raised after a load, place or removal changes <see cref="Markers"/>.</summary>
    event Action? Changed;

    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Puts the objective's marker on this spot, replacing the one it had on that map.</summary>
    Task PlaceAsync(
        string objectiveId,
        string mapId,
        string? floorId,
        double x,
        double y,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(string objectiveId, string mapId, CancellationToken cancellationToken = default);
}

/// <summary>Lays the player's own markers over a map's quest scene.</summary>
public static class UserQuestMarkerScene
{
    /// <summary>
    /// Gives each objective the catalog could not place, and the player has, a marker.
    /// </summary>
    /// <remarks>
    /// Only an objective with <see cref="QuestObjectivePlacement.NoLocation"/> is touched: where the
    /// catalog authored a place, the catalog's place stands and a stale marker of the player's is
    /// simply not drawn, so official data is never replaced by a note. The marker is drawn as a
    /// numbered objective, the next number after the ones on the map, and its scene object says
    /// <see cref="MapSceneTruthKind.UserAuthored"/> and "placed by you" so no renderer or tablet
    /// can present it as the game's.
    /// </remarks>
    public static QuestObjectiveScene Apply(
        QuestObjectiveScene scene,
        IEnumerable<UserQuestMarker> markers,
        string mapId,
        IReadOnlyList<MapFloorDefinition> floors)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(floors);
        var mine = markers
            .Where(marker => string.Equals(marker.MapId, mapId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(marker => marker.ObjectiveId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(marker => marker.PlacedUtc).First(), StringComparer.Ordinal);
        if (mine.Count == 0 ||
            !scene.Entries.Any(entry => entry.Placement == QuestObjectivePlacement.NoLocation && mine.ContainsKey(entry.ObjectiveId)))
        {
            return scene;
        }

        var layer = MapSceneAssembler.IdFor(MapOverlayKind.QuestObjectives);
        var next = scene.Entries
            .Select(entry => int.TryParse(entry.Number, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0)
            .DefaultIfEmpty(0)
            .Max();
        var objects = scene.Objects.ToList();
        var entries = new List<QuestObjectiveEntry>(scene.Entries.Count);
        foreach (var entry in scene.Entries)
        {
            if (entry.Placement != QuestObjectivePlacement.NoLocation || !mine.TryGetValue(entry.ObjectiveId, out var marker))
            {
                entries.Add(entry);
                continue;
            }

            var number = (++next).ToString(CultureInfo.InvariantCulture);
            var floorIds = marker.FloorId is null ? [] : new[] { marker.FloorId };
            var floorNames = floors
                .Where(floor => floorIds.Contains(floor.Id, StringComparer.OrdinalIgnoreCase))
                .Select(floor => floor.Name)
                .ToArray();
            var where = floorNames.Length == 0 ? "Placed by you" : $"Placed by you · {string.Join(", ", floorNames)}";
            var spot = new MapSceneObject(
                new($"quest:{entry.ObjectiveId}:user"),
                layer,
                MapSceneObjectKind.QuestObjective,
                MapSceneTruthKind.UserAuthored,
                number,
                $"{entry.Objective.TaskName}\n{entry.Objective.Description}\n{where}\nPlaced by you, not from the quest data.",
                MapSceneGeometry.At(new(marker.X, marker.Y)),
                floorIds,
                new DataProvenance("user-placed", marker.PlacedUtc.ToUniversalTime()));
            objects.Add(spot);
            entries.Add(entry with
            {
                Number = number,
                Placement = QuestObjectivePlacement.UserPlaced,
                PlaceCount = 1,
                PlacementLabel = "Placed by you",
                NoLocationReason = null,
                FloorIds = floorIds,
                FloorNames = floorNames,
                ObjectIds = [spot.Id],
            });
        }

        return new(objects, entries);
    }
}
