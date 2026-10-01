using System.Text.RegularExpressions;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.UnitTests.V2MapRenderer;

namespace TarkovCompanion.UnitTests.Raids;

/// <summary>
/// [#985] Every location id the game writes resolves to a map the catalog can draw.
/// </summary>
/// <remarks>
/// On build 91 a level 21+ raid wrote <c>Sandbox_high</c>, the parser made it
/// <c>ground-zero-21</c>, the catalog has no such location, and the map stayed on the last one:
/// no spawns, no marks, nothing said why. A ratchet: a new id in a format pack or in the owner's
/// logs has to resolve before the build passes.
/// </remarks>
public sealed partial class EveryLoggedLocationHasACatalogMapTests
{
    /// <summary>
    /// The ids EFT_LOG_FACTS.md and the owner's logs of 2026-09-27 to 30 record on notifications and
    /// <c>profileStatus</c> lines, and the rest of tarkov.dev's <c>nameId</c>s for playable maps.
    /// </summary>
    private static readonly string[] Documented =
    [
        "bigmap", "factory4_day", "factory4_night", "Woods", "Lighthouse", "Shoreline", "RezervBase",
        "Interchange", "TarkovStreets", "laboratory", "Sandbox", "Sandbox_high", "Terminal", "Labyrinth",
    ];

    [Fact]
    public void Every_logged_location_id_resolves_to_a_catalog_map()
    {
        var catalog = RealMapCatalog.Load().Locations;
        var ids = Documented.Concat(PackLocationIds()).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Contains("Sandbox_high", ids);

        var unresolved = new List<string>();
        foreach (var id in ids)
        {
            var mapId = new EftLogParser().ParseLine(ProfileStatus(id), DateTimeOffset.UtcNow)?.MapId;
            if (mapId is null || !catalog.Any(location => RaidMapCatalogIds.IsDrawnOn(mapId, location)))
            {
                unresolved.Add($"{id} -> {mapId ?? "(no map)"}");
            }
        }

        Assert.True(unresolved.Count == 0, "No catalog map for: " + string.Join(", ", unresolved));
    }

    [Theory]
    [InlineData("ground-zero-21", "ground-zero")]
    [InlineData("night-factory", "factory")]
    [InlineData("the-lab-dark", "the-lab")]
    [InlineData("woods", "woods")]
    [InlineData("streets-of-tarkov", "streets-of-tarkov")]
    public void A_raid_map_is_drawn_on_its_catalog_plan(string raidMapId, string catalogId)
    {
        Assert.Equal(catalogId, RaidMapCatalogIds.CatalogIdFor(raidMapId));
        var location = RealMapCatalog.Load().Locations.Single(item => item.Id == catalogId);
        Assert.True(RaidMapCatalogIds.IsDrawnOn(raidMapId, location));
    }

    private static string ProfileStatus(string location) =>
        "2026-01-01 20:00:00.000|1.1.5.1.47510|Debug|application|TRACE-NetworkGameCreate profileStatus: " +
        $"'Profileid: f00000000000000000000001, Status: Busy, RaidMode: Online, Ip: 203.0.113.10, Port: 17000, Location: {location}, " +
        "Sid: FAKE-SID, GameMode: deathmatch, shortId: FAKE01'";

    private static IEnumerable<string> PackLocationIds()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "FormatGuards", "Packs");
        foreach (var path in Directory.EnumerateFiles(root, "pack.json", SearchOption.AllDirectories))
        {
            foreach (Match match in LocationInPack().Matches(File.ReadAllText(path)))
            {
                yield return match.Groups["id"].Value;
            }
        }
    }

    [GeneratedRegex(@"(?:Location: |\\""location\\"":\\"")(?<id>[A-Za-z0-9_]+)")]
    private static partial Regex LocationInPack();
}
