using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>
/// Gives a map catalog location the game's own id for the map, from the synced maps table.
/// </summary>
/// <remarks>
/// Quests and keys name a map by the game's id ("56f40101d2720b2a4d8b45d6") and tarkov.dev's
/// maps.json names it only by its slug ("customs"): of its fifteen entries, only two carry an
/// <c>id</c>, and Customs, Lighthouse and Interchange are not among them. A location as parsed
/// therefore matches none of the objectives a quest reads for it, and the map answers "nothing to do
/// here" for every map that matters. The maps table has both names for each map, so this asks it.
/// </remarks>
public sealed class MapGameIdResolver(IMapDataService mapData)
{
    private readonly Dictionary<string, string> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The location with its game id, or as it was where the maps table has no answer (yet).</summary>
    public async Task<MapLocation> ResolveAsync(MapLocation location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!string.IsNullOrWhiteSpace(location.SourceId))
        {
            return location;
        }

        if (!_known.TryGetValue(location.Id, out var gameId))
        {
            try
            {
                gameId = (await mapData.GetAsync(location.Id, cancellationToken).ConfigureAwait(false))?.GameId ?? string.Empty;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Without the id the layer says it has nothing for this map, which is the state
                // this lookup improves on rather than one it has to guarantee.
                gameId = string.Empty;
            }

            // Only an answer is kept: a sync that has not finished yet is worth asking again.
            if (gameId.Length > 0)
            {
                _known[location.Id] = gameId;
            }
        }

        return gameId.Length == 0 ? location : location with { SourceId = gameId };
    }
}
