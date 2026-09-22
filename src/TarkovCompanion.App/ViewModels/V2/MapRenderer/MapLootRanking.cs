using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.ViewModels.V2.MapRenderer;

/// <summary>
/// [#573] Which potential loot spawns the map draws at the current zoom: the most valuable ones
/// first, about <see cref="TopAtFit"/> of them with the whole plan in view, more as the player
/// zooms in, the rest counted on a badge per area.
/// </summary>
/// <remarks>
/// With #563 fixed the "High-value loot only" view drew every one of Customs' 232 spawns as the
/// same diamond, on every building, over the place names. A spawn's rank is its value tier, then
/// its highest candidate value. The zoom at which rank r appears keeps roughly the same number on
/// screen at any zoom: the plan's visible area shrinks with the square of the zoom, so rank r
/// shows from zoom √((r + 1) / TopAtFit).
/// </remarks>
public static class MapLootRanking
{
    /// <summary>How many loot spawns the fitted plan draws.</summary>
    public const int TopAtFit = 20;

    /// <summary>Canvas DIPs per side of the square each count badge collects the not-yet-drawn spawns of.</summary>
    public const double BadgeCell = 160;

    /// <summary>The zoom from which the spawn of this rank (0 = most valuable) is drawn.</summary>
    public static double RevealZoom(int rank) =>
        rank < TopAtFit ? 0 : Math.Sqrt((rank + 1d) / TopAtFit);

    /// <summary>A spawn's tier and top value, by the scene object it is drawn as.</summary>
    public static IReadOnlyDictionary<MapSceneObjectId, (LootSpawnValueTier Tier, long Value)> ValuesOf(
        HighValueLootLayerResult? result)
    {
        var values = new Dictionary<MapSceneObjectId, (LootSpawnValueTier, long)>();
        foreach (var entry in result?.Entries ?? [])
        {
            if (entry.SceneObjectId is { } id)
            {
                values[id] = (entry.Tier, entry.MaximumValue ?? 0);
            }
        }

        return values;
    }

    /// <summary>The spawns in drawing order: most valuable first, ties by id so the order is stable.</summary>
    public static IReadOnlyList<MapSceneObject> Ranked(
        IReadOnlyList<MapSceneObject> loot,
        IReadOnlyDictionary<MapSceneObjectId, (LootSpawnValueTier Tier, long Value)> values) =>
        loot
            .OrderByDescending(item => values.TryGetValue(item.Id, out var value) ? (int)value.Tier : -1)
            .ThenByDescending(item => values.TryGetValue(item.Id, out var value) ? value.Value : 0)
            .ThenBy(item => item.Id.Value, StringComparer.Ordinal)
            .ToArray();
}
