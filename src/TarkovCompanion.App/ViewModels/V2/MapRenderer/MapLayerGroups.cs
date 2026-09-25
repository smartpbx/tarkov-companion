using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.ViewModels.V2.MapRenderer;

/// <summary>[#902] Where each Layers-menu row sits: under a header saying what it is about.</summary>
/// <remarks>
/// The menu was one list of fourteen or more switches in drawing order, so "Routes" (a dead V1
/// layer) sat beside "Suggested routes" and the player's own marker sat between traffic and
/// loot. Grouped by what the player reads on the map, as the #902 proposal lays out. A layer
/// this table does not know goes under Map rather than being dropped: a host's new layer still
/// gets a switch.
/// </remarks>
public static class MapLayerGroups
{
    private static readonly (string Key, string[] LayerIds)[] Groups =
    [
        ("You", ["you", "my-trail"]),
        ("Squad", ["squad"]),
        ("Routes", [ObjectiveRouteSceneBuilder.LayerId.Value, "traffic-routes", "traffic-route-direct"]),
        ("Quests", ["quest-objectives"]),
        ("Map", ["labels", "extracts", "keys", "switches", "hazards", "nearby-spawns", "spawns"]),
        ("Marks", ["my-marks", "group-marks", "drawings"]),
        ("Loot", [HighValueLootLayerService.LayerId.Value]),
        ("Traffic", [MapSceneRendererViewModel.TrafficHeatLayerId.Value]),
    ];

    private const string FallbackKey = "Map";

    /// <summary>The group key for one layer id, such as "Routes"; Map for an id it does not know.</summary>
    public static string KeyOf(MapSceneLayerId layerId)
    {
        foreach (var (key, ids) in Groups)
        {
            if (ids.Contains(layerId.Value, StringComparer.Ordinal))
            {
                return key;
            }
        }

        return FallbackKey;
    }

    /// <summary>The rows under their headers, in the fixed group order; an empty group is left out.</summary>
    /// <param name="loot">The loot filter panel, shown under the Loot header, where its chips belong.</param>
    public static IReadOnlyList<MapSceneRendererLayerGroupViewModel> Group(
        IReadOnlyList<MapSceneRendererLayerViewModel> layers,
        MapSceneRendererPresentation presentation,
        HighValueLootLayerViewModel? loot = null)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(presentation);
        var byKey = layers.GroupBy(layer => KeyOf(layer.Layer.Id)).ToDictionary(group => group.Key, group => group.ToArray());
        return Groups
            .Where(group => byKey.ContainsKey(group.Key))
            .Select(group => new MapSceneRendererLayerGroupViewModel(
                group.Key,
                presentation.Get($"Map.LayerGroup.{group.Key}"),
                // In the table's order within a group, so the three routes read as the proposal names them.
                [.. byKey[group.Key].OrderBy(layer => Array.IndexOf(group.LayerIds, layer.Layer.Id.Value) is var at and >= 0 ? at : int.MaxValue)],
                group.Key == "Loot" ? loot : null))
            .ToArray();
    }
}

public sealed class MapSceneRendererLayerGroupViewModel(
    string key,
    string header,
    IReadOnlyList<MapSceneRendererLayerViewModel> layers,
    HighValueLootLayerViewModel? loot = null)
{
    public string Key { get; } = key;
    public string Header { get; } = header;
    public IReadOnlyList<MapSceneRendererLayerViewModel> Layers { get; } = layers;
    /// <summary>The loot filter chips, on the Loot group only.</summary>
    public HighValueLootLayerViewModel? Loot { get; } = loot;
    public bool HasLoot => Loot is not null;
    public string AutomationId => $"v2-map-layer-group-{Key.ToLowerInvariant()}";
}
