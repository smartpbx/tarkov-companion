using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Situations;

/// <summary>A place name for a position, and a floor where the catalog draws one.</summary>
public sealed record SituationPlace(string? AreaName, string? FloorName);

/// <summary>What the situation needs to know about maps, answered synchronously from what is loaded.</summary>
/// <remarks>
/// The fold runs under a lock on every input and must not wait on a download. An answer that is
/// not loaded yet is null ("area unknown"), and <see cref="Changed"/> fires once it arrives so the
/// situation is worked out again rather than staying unknown.
/// </remarks>
public interface ISituationPlaces
{
    event EventHandler? Changed;

    SituationPlace Describe(string mapId, WorldPosition position);

    /// <summary>The map's raid length for this side ("PMC"/"scav"), where the catalog states it.</summary>
    TimeSpan? RaidLength(string mapId, string? side);

    string? MapName(string mapId);
}

/// <summary>Names a position from the catalog's named rectangles (#868's MapAreaName).</summary>
public static class SituationPlaceLookup
{
    public static SituationPlace Describe(MapLocation? location, WorldPosition position)
    {
        if (location is null)
        {
            return new(null, null);
        }

        // The interactive variant carries the floors and named rectangles; the others are artwork.
        var variant = location.Variants.FirstOrDefault(candidate => candidate.IsInteractive && candidate.Floors.Count > 0)
            ?? location.Variants.FirstOrDefault(candidate => candidate.Floors.Count > 0);
        if (variant is null)
        {
            return new(null, null);
        }

        // The floor being stood on is the one whose height band holds the position inside one of
        // its named rectangles; a building repeats its footprint on every floor it has.
        var standing = variant.Floors.FirstOrDefault(floor =>
            floor.Extents.Any(extent => extent.Bounds.Count > 0 && extent.Contains(position)));
        var area = MapAreaName.Describe(standing, position)
            ?? variant.Floors.Select(floor => MapAreaName.Describe(floor, position)).FirstOrDefault(name => name is not null);
        return new(area, standing?.Name);
    }
}

/// <summary>The app's places: the tarkov.dev map catalog for areas, the map data for raid lengths.</summary>
public sealed class CatalogSituationPlaces : ISituationPlaces, IDisposable
{
    private readonly Func<CancellationToken, Task<TarkovDevMapCatalog?>> _catalog;
    private readonly IMapDataService? _maps;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MapDefinition?> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadingDefinitions = new(StringComparer.OrdinalIgnoreCase);
    private TarkovDevMapCatalog? _loadedCatalog;
    private bool _catalogRequested;

    public CatalogSituationPlaces(Func<CancellationToken, Task<TarkovDevMapCatalog?>> catalog, IMapDataService? maps = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _maps = maps;
    }

    public event EventHandler? Changed;

    public SituationPlace Describe(string mapId, WorldPosition position)
    {
        var catalog = Catalog();
        return catalog is null ? new(null, null) : SituationPlaceLookup.Describe(catalog.FindLocation(mapId), position);
    }

    public TimeSpan? RaidLength(string mapId, string? side)
    {
        var definition = Definition(mapId);
        return definition is null ? null : Raids.RaidTimer.LengthFor(side, definition.PmcRaidDuration, definition.ScavRaidDuration);
    }

    public string? MapName(string mapId) => Definition(mapId)?.Name ?? Catalog()?.FindLocation(mapId)?.Name;

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    private TarkovDevMapCatalog? Catalog()
    {
        lock (_gate)
        {
            if (_loadedCatalog is not null || _catalogRequested || _stopping.IsCancellationRequested)
            {
                return _loadedCatalog;
            }

            _catalogRequested = true;
        }

        _ = LoadCatalogAsync(_stopping.Token);
        return null;
    }

    private MapDefinition? Definition(string mapId)
    {
        if (_maps is null)
        {
            return null;
        }

        lock (_gate)
        {
            if (_definitions.TryGetValue(mapId, out var known))
            {
                return known;
            }

            if (!_loadingDefinitions.Add(mapId) || _stopping.IsCancellationRequested)
            {
                return null;
            }
        }

        _ = LoadDefinitionAsync(mapId, _stopping.Token);
        return null;
    }

    private async Task LoadCatalogAsync(CancellationToken cancellationToken)
    {
        // Never answer on the asking thread: the situation asks from inside its fold, and a cached
        // catalog completing synchronously re-entered that fold and logged one transition twice.
        await Task.Yield();
        try
        {
            var catalog = await _catalog(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _loadedCatalog = catalog;
                // A failed load may be retried on the next question rather than never.
                _catalogRequested = catalog is not null;
            }

            if (catalog is not null)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or HttpRequestException or InvalidOperationException)
        {
            lock (_gate)
            {
                _catalogRequested = false;
            }
        }
    }

    private async Task LoadDefinitionAsync(string mapId, CancellationToken cancellationToken)
    {
        await Task.Yield();
        MapDefinition? definition = null;
        try
        {
            definition = await _maps!.GetAsync(mapId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidOperationException)
        {
            // Unknown stays unknown; the next question asks again.
        }

        lock (_gate)
        {
            _loadingDefinitions.Remove(mapId);
            if (definition is not null)
            {
                _definitions[mapId] = definition;
            }
        }

        if (definition is not null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
