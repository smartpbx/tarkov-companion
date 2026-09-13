using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

public interface IMapDefinitionCache
{
    Task<MapDefinition?> GetAsync(string mapId, CancellationToken cancellationToken);

    /// <summary>Drops whatever was read before a sync landed.</summary>
    /// <remarks>
    /// A lookup that found nothing is remembered like any other answer, so without this the
    /// first map selected on a fresh install would keep reporting no extracts for the rest of
    /// the session even after the catalog arrived.
    /// </remarks>
    void Invalidate();
}

public sealed class MapDataService(IMapDefinitionCache cache) : IMapDataService
{
    public Task<MapDefinition?> GetAsync(string mapId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        return cache.GetAsync(mapId, cancellationToken);
    }
}

public sealed class InMemoryMapDefinitionCache(IEnumerable<MapDefinition> maps) : IMapDefinitionCache
{
    private readonly IReadOnlyDictionary<string, MapDefinition> _maps = maps.ToDictionary(map => map.Id, StringComparer.OrdinalIgnoreCase);

    public Task<MapDefinition?> GetAsync(string mapId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _maps.TryGetValue(mapId, out var map);
        return Task.FromResult(map);
    }

    public void Invalidate()
    {
        // The list was fixed when this was built; there is nothing to re-read.
    }
}

public sealed record ActiveExtractView(ActiveExtract Observation, MapExtract? Definition)
{
    public bool HasKnownPosition => Definition?.Position is not null;

    public string PositionGuidance => HasKnownPosition
        ? "Static extract position is available."
        : "No verified static position is available; the extract is listed without a marker.";
}

public static class ActiveExtractState
{
    public static IReadOnlyList<ActiveExtractView> Resolve(MapDefinition map, IReadOnlyList<ActiveExtract> observedExtracts)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(observedExtracts);
        var definitions = map.Extracts.ToDictionary(extract => extract.Id, StringComparer.OrdinalIgnoreCase);
        return observedExtracts
            .Select(observation => new ActiveExtractView(
                observation,
                definitions.GetValueOrDefault(observation.ExtractId)))
            .ToArray();
    }
}
