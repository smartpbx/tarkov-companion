namespace TarkovCompanion.Core.Domain.Maps;

/// <summary>What a marker on the map is.</summary>
/// <remarks>
/// Deliberately few. These are the things a player looks for mid-raid; loot containers and
/// hazards are in the same data and are left out because a map covered in markers answers no
/// question quickly, which is the only way this panel is read.
/// </remarks>
public enum MapFeatureKind
{
    Extract,
    Transit,
    Spawn,
    Lock,
}

/// <summary>
/// One fixed thing on a map, from the data tarkov.dev already publishes.
/// </summary>
/// <remarks>
/// The whole map record, extracts and spawns included, has been downloaded, parsed and stored
/// in the local database since the first sync, and nothing ever read it back. So this needs no
/// new request and no new table: it is the data already on disk, finally used.
/// </remarks>
/// <param name="Kind">What sort of thing it is.</param>
/// <param name="Name">What the game calls it.</param>
/// <param name="Position">Where it is, in world coordinates, before any map transform.</param>
/// <param name="Faction">
/// Who may use it, for an extract: pmc, scav or shared. Null where the distinction does not
/// apply. A scav extract a PMC cannot take is worse than no marker, so this is not dropped.
/// </param>
/// <param name="Detail">A sentence for the tooltip, where there is more worth saying.</param>
public sealed record MapFeature(
    MapFeatureKind Kind,
    string Name,
    WorldPosition Position,
    string? Faction = null,
    string? Detail = null);

/// <summary>Reads the fixed features of a map out of the local catalog.</summary>
public interface IMapFeatureCatalog
{
    Task<IReadOnlyList<MapFeature>> GetAsync(string mapId, CancellationToken cancellationToken);
}
