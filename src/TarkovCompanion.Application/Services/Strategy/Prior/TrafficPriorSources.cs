using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Strategy.Prior;

/// <summary>Reads the catalog's own map features as the sources of a structural traffic prior.</summary>
/// <remarks>
/// Only what says where a PMC raid starts, what it goes to and where it ends. Bot and sniper spawn
/// points are left out: they say where AI stands, and this models players. A scav-only spawn or
/// extract counts half, because a scav raid is a shorter walk by fewer people than a PMC one.
/// </remarks>
public static class TrafficPriorSources
{
    private static readonly string[] CrossingWords =
        ["bridge", "checkpoint", "crossroads", "tunnel", "underpass", "overpass", "gate"];

    public static IReadOnlyList<TrafficPriorSource> FromOverlay(IEnumerable<MapOverlayElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var sources = new List<TrafficPriorSource>();
        var namedWaysOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in elements)
        {
            var side = element.Faction == MapFeatureFaction.Scav ? 0.5 : 1;
            switch (element.Layer)
            {
                case MapOverlayKind.Spawns when element.Label.StartsWith("Boss spawn", StringComparison.Ordinal):
                    sources.Add(new(TrafficPriorSourceKind.BossArea, element.Position, 1, AfterDot(element.Label) ?? "Boss area"));
                    break;
                case MapOverlayKind.Spawns when element.Label.StartsWith("Spawn", StringComparison.Ordinal):
                    sources.Add(new(TrafficPriorSourceKind.PlayerSpawn, element.Position, side, "Player spawn"));
                    break;
                case MapOverlayKind.Extracts:
                    // The Raid card offers one row per name, includes transits, and hides co-op
                    // exits by default. Count and model that same set so duplicate catalog rows
                    // cannot make the traffic coverage chip disagree with the card.
                    if (CoOpExtracts.IsCoOp(element.Label) || !namedWaysOut.Add(element.Label))
                    {
                        break;
                    }

                    // A transit ("Factory →") is a way out that fewer raids take than an extract.
                    var transit = element.Label.EndsWith('→');
                    sources.Add(new(TrafficPriorSourceKind.Extract, element.Position, transit ? 0.5 : side, element.Label));
                    break;
                case MapOverlayKind.Labels:
                    var crossing = CrossingWords.Any(word => element.Label.Contains(word, StringComparison.OrdinalIgnoreCase));
                    sources.Add(new(
                        crossing ? TrafficPriorSourceKind.Crossing : TrafficPriorSourceKind.NamedPlace,
                        element.Position,
                        1,
                        element.Label));
                    break;
            }
        }

        return sources;
    }

    /// <summary>How much a loot spawn draws players, from the value tier #318 already gave it.</summary>
    public static double LootWeight(LootSpawnValueTier tier) => tier switch
    {
        LootSpawnValueTier.Exceptional => 1,
        LootSpawnValueTier.High => 0.7,
        LootSpawnValueTier.Moderate => 0.4,
        LootSpawnValueTier.Qualifying => 0.25,
        _ => 0,
    };

    private static string? AfterDot(string label)
    {
        // "Boss spawn · Depo · 4 points": the place is the middle part, and a count is not a place.
        var parts = label.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && !parts[1].EndsWith("points", StringComparison.Ordinal) ? parts[1] : null;
    }
}
