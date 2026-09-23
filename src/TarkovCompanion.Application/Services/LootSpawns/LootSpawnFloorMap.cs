using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.LootSpawns;

namespace TarkovCompanion.Application.Services.LootSpawns;

/// <summary>Maps source floor facts onto the floor choices the selected catalog artwork exposes.</summary>
/// <remarks>
/// Interchange's catalog has an unbounded <c>base</c> plan plus bounded upper-floor layers. The
/// base plan is an overview, not a physical floor: markers with an upper-floor fact belong on both
/// that overview and their own layer, while a positioned marker with no resolved floor belongs on
/// the overview only. Treating the overview as a physical floor left its default view empty.
/// </remarks>
public static class LootSpawnFloorMap
{
    public static IReadOnlyList<string> OverviewFloorIds(IReadOnlyList<MapFloorDefinition> floors)
    {
        ArgumentNullException.ThrowIfNull(floors);
        return floors
            .Where(IsOverview)
            .Select(floor => floor.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> RenderFloorIds(
        IReadOnlyList<string> sourceFloorIds,
        IReadOnlyList<string> overviewFloorIds)
    {
        ArgumentNullException.ThrowIfNull(sourceFloorIds);
        ArgumentNullException.ThrowIfNull(overviewFloorIds);
        return sourceFloorIds
            .Concat(overviewFloorIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(LootSpawnLocation.MaximumFloors)
            .ToArray();
    }

    private static bool IsOverview(MapFloorDefinition floor) =>
        string.Equals(floor.Id, "base", StringComparison.OrdinalIgnoreCase) &&
        floor.Extents.Count == 1 &&
        floor.Extents[0] is { MinimumHeight: null, MaximumHeight: null, Bounds.Count: 0 };
}
