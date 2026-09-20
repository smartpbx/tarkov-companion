using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Quests;

/// <summary>How many of one map's quest objectives the catalog can place, and how many it cannot.</summary>
/// <param name="MapId">The catalog's map id.</param>
/// <param name="Objectives">Objectives that name this map.</param>
/// <param name="Placed">Of those, the ones with an authored spot or region.</param>
/// <param name="CandidatesOnly">Of those, the ones with only "possible location" spots.</param>
/// <param name="NoLocation">The rest: the catalog gives them no place.</param>
/// <param name="PlacedByPlayer">Of the ones with none, how many the player has put a marker on.</param>
public sealed record QuestMapCoverage(
    string MapId,
    int Objectives,
    int Placed,
    int CandidatesOnly,
    int NoLocation,
    int PlacedByPlayer)
{
    /// <summary>Objectives the map can show something for, from the catalog or from the player.</summary>
    public int Drawable => Placed + CandidatesOnly + PlacedByPlayer;
}

/// <summary>
/// Measures quest objective coordinate coverage per map, so a gap is a number a player can read
/// rather than a marker that is silently missing.
/// </summary>
/// <remarks>
/// It counts objectives that name a map, because those are the ones a map could be asked to show;
/// an objective that names no map (hand in an item, reach a level) is not a coverage gap. Failure
/// conditions are not counted: they are not something to go and do. A zone with neither a position
/// nor an outline is no place at all, the same rule the map projection applies.
/// </remarks>
public static class QuestObjectiveCoverage
{
    /// <param name="catalog">The synced quest catalog.</param>
    /// <param name="playerMarkers">What the player has placed, keyed by map slug.</param>
    /// <param name="mapKeyFor">
    /// The map's slug for one of the catalog's own map ids (a location's slug, its game id and its
    /// variants' alternates all name one map), or null when it is not a map the app knows, which is
    /// then counted under the catalog's id rather than dropped.
    /// </param>
    public static IReadOnlyList<QuestMapCoverage> Measure(
        QuestCatalogSnapshot catalog,
        IReadOnlyCollection<UserQuestMarker> playerMarkers,
        Func<string, string?> mapKeyFor)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(playerMarkers);
        ArgumentNullException.ThrowIfNull(mapKeyFor);
        var byMap = new Dictionary<string, (int Objectives, int Placed, int Candidates, int None, int Player)>(StringComparer.OrdinalIgnoreCase);
        foreach (var objective in catalog.Tasks.SelectMany(task => task.Objectives))
        {
            var named = objective.MapAssociations
                .Select(association => association.MapId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => (Catalog: id, Key: mapKeyFor(id) ?? id))
                .DistinctBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var (catalogId, mapId) in named)
            {
                var zones = objective.Zones
                    .Where(zone => (zone.MapId is null || mapKeyFor(zone.MapId) is { } zoneKey && string.Equals(zoneKey, mapId, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(zone.MapId, catalogId, StringComparison.OrdinalIgnoreCase)) &&
                                   (zone.Position is not null || zone.Outline.Count > 0))
                    .ToArray();
                byMap.TryGetValue(mapId, out var counts);
                counts.Objectives++;
                if (zones.Any(zone => !zone.IsPossibleLocation))
                {
                    counts.Placed++;
                }
                else if (zones.Length > 0)
                {
                    counts.Candidates++;
                }
                else if (playerMarkers.Any(marker =>
                             string.Equals(marker.ObjectiveId, objective.Id, StringComparison.Ordinal) &&
                             string.Equals(marker.MapId, mapId, StringComparison.OrdinalIgnoreCase)))
                {
                    counts.Player++;
                }
                else
                {
                    counts.None++;
                }

                byMap[mapId] = counts;
            }
        }

        return [.. byMap
            .Select(pair => new QuestMapCoverage(
                pair.Key,
                pair.Value.Objectives,
                pair.Value.Placed,
                pair.Value.Candidates,
                pair.Value.None,
                pair.Value.Player))
            .OrderByDescending(row => row.NoLocation)
            .ThenBy(row => row.MapId, StringComparer.OrdinalIgnoreCase)];
    }
}
