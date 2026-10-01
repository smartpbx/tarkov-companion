namespace TarkovCompanion.Application.Services.Maps;

/// <summary>
/// Which catalog map a raid's map id is drawn on (#983, #985).
/// </summary>
/// <remarks>
/// The log names a raid's map by tarkov.dev's <c>normalizedName</c>, and for most maps that is
/// also the catalog location's id. Four are not: tarkov.dev's maps table has Night Factory, Ground
/// Zero 21+, the Ground Zero tutorial and The Lab (Dark) as maps of their own, while its map
/// catalog draws each on the plan of its day/base map and has no location for them. A raid there
/// never brought its map up, so neither its possible PMC spawns nor anything keyed to the raid's
/// map showed. Measured over every map of the 2026-09-14 catalog through the composed app.
/// </remarks>
public static class RaidMapCatalogIds
{
    private static readonly IReadOnlyDictionary<string, string> DrawnOn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["night-factory"] = "factory",
        ["ground-zero-21"] = "ground-zero",
        ["ground-zero-tutorial"] = "ground-zero",
        ["the-lab-dark"] = "the-lab",
    };

    /// <summary>The catalog location id a raid map id is drawn on; the id itself for every other map.</summary>
    public static string CatalogIdFor(string mapId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        return DrawnOn.TryGetValue(mapId, out var drawnOn) ? drawnOn : mapId;
    }

    /// <summary>Whether a raid on <paramref name="raidMapId"/> is drawn on <paramref name="location"/>'s plan.</summary>
    public static bool IsDrawnOn(string? raidMapId, MapLocation? location)
    {
        if (string.IsNullOrWhiteSpace(raidMapId) || location is null)
        {
            return false;
        }

        return string.Equals(location.Id, raidMapId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(location.Id, CatalogIdFor(raidMapId), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(location.SourceId, raidMapId, StringComparison.OrdinalIgnoreCase) ||
            location.Variants.Any(variant => variant.AlternateLocationIds.Contains(raidMapId, StringComparer.OrdinalIgnoreCase));
    }
}
