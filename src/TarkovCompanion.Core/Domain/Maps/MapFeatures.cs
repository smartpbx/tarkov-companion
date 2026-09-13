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
/// <summary>Who a map feature belongs to, where the upstream data says.</summary>
/// <remarks>
/// Taking the wrong extract is not possible, so which side an exit is for is the single most
/// useful thing a map can say about it. The feed already carries this and it was being folded
/// into a sentence nobody read, which is why every marker looked the same.
/// </remarks>
public enum MapFeatureFaction
{
    Unknown,
    Pmc,
    Scav,
    Shared,
}

public sealed record MapFeature(
    MapFeatureKind Kind,
    string Name,
    WorldPosition Position,
    string? Faction = null,
    string? Detail = null)
{
    /// <summary>
    /// <see cref="Faction"/> reduced to something that can be drawn.
    /// </summary>
    /// <remarks>
    /// The upstream feed says it two different ways and both arrive here. An extract carries a
    /// single word, lower case: "pmc", "scav" or "shared". A spawn carries its list of sides
    /// joined together, so "Pmc, Scav" means either may start there and is the same claim that
    /// "shared" makes for an exit. Reading both here means nothing downstream has to know that.
    /// </remarks>
    public MapFeatureFaction Side
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Faction))
            {
                return MapFeatureFaction.Unknown;
            }

            var pmc = Faction.Contains("pmc", StringComparison.OrdinalIgnoreCase);
            var scav = Faction.Contains("scav", StringComparison.OrdinalIgnoreCase);
            return (pmc, scav) switch
            {
                (true, true) => MapFeatureFaction.Shared,
                (true, false) => MapFeatureFaction.Pmc,
                (false, true) => MapFeatureFaction.Scav,
                // "all" is the third word the feed uses and it means exactly what "shared"
                // means. It is also the commonest: of 3018 spawn points across every map, 860
                // say "all" and 528 say "pmc", so treating it as unknown left the largest
                // group of player spawns with no side at all.
                _ => Faction.Contains("shared", StringComparison.OrdinalIgnoreCase) ||
                    Faction.Contains("all", StringComparison.OrdinalIgnoreCase)
                    ? MapFeatureFaction.Shared
                    : MapFeatureFaction.Unknown,
            };
        }
    }
}

/// <summary>Reads the fixed features of a map out of the local catalog.</summary>
public interface IMapFeatureCatalog
{
    Task<IReadOnlyList<MapFeature>> GetAsync(string mapId, CancellationToken cancellationToken);

    /// <summary>Drops whatever was read before a sync landed.</summary>
    /// <remarks>
    /// An empty read is cached like any other, so a map opened before the first sync finished
    /// kept showing no markers until the application was restarted.
    /// </remarks>
    void Invalidate();
}
