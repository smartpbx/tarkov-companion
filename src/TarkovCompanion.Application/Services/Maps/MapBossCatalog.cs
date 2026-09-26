namespace TarkovCompanion.Application.Services.Maps;

/// <summary>[#712 0-9] One boss the catalog says can spawn on a map, and how often.</summary>
/// <param name="MobId">The catalog's mob id ("bossBully").</param>
/// <param name="Name">What the catalog's language table calls it ("Reshala").</param>
/// <param name="Chance">The catalog's spawn chance, 0 to 1: a published rate, never a sighting.</param>
/// <param name="IsTriggered">The likeliest entry waits for something in raid (a switch, a lever).</param>
public sealed record MapBossChance(string MobId, string Name, double Chance, bool IsTriggered);

/// <summary>The catalog's boss spawn chances for a map (tarkov.dev <c>maps[].bosses[].spawnChance</c>).</summary>
/// <remarks>
/// Static catalog facts, read out of the synced maps rows. Nothing here knows whether a boss is in
/// a raid; the pre-raid brief says "possible" and the percentage, and never more.
/// </remarks>
public interface IMapBossCatalog
{
    /// <summary>Likeliest first; empty where the catalog has no row for the map or no bosses on it.</summary>
    Task<IReadOnlyList<MapBossChance>> GetAsync(string mapId, CancellationToken cancellationToken);
}
