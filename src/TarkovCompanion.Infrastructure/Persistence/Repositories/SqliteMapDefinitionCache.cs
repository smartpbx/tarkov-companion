using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>
/// Builds a map's definition, extracts included, out of the synced catalog.
/// </summary>
/// <remarks>
/// The definition this replaces was held in memory and filled from a list of maps that nothing
/// ever supplied, so every lookup returned nothing. Reading the extract list off the screen
/// then had nothing on earth to compare the lines against: the scan recognised the panel,
/// matched none of it, and reported a partial result with no extracts in it.
///
/// The rows have been written on every sync since the first one. Names, positions and the
/// faction that may use each exit are all already on disk, so this needs no request and no
/// schema change.
///
/// Cached per map after the first read, and dropped on a sync, for the same reason the feature
/// catalog is: a map is re-selected often and the catalog only changes between raids.
/// </remarks>
public sealed class SqliteMapDefinitionCache(
    SqliteConnectionFactory connectionFactory,
    TimeProvider? timeProvider = null) : IMapDefinitionCache
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, MapDefinition?> _byMap = new(StringComparer.OrdinalIgnoreCase);

    public async Task<MapDefinition?> GetAsync(string mapId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_byMap.TryGetValue(mapId, out var cached))
            {
                return cached;
            }

            var definition = await LoadAsync(mapId, cancellationToken).ConfigureAwait(false);
            _byMap[mapId] = definition;
            return definition;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
        _gate.Wait();
        try
        {
            _byMap.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Finds the stored map by the slug the rest of the application uses.
    /// </summary>
    /// <remarks>
    /// The table's own columns hold a display name and a normalized display name, neither of
    /// which is the upstream slug, so the slug is read out of the payload exactly as the alias
    /// and feature catalogs do. Any other pairing would hand one map's extract names to
    /// another map's screen, which reads as confident and is wrong.
    /// </remarks>
    private async Task<MapDefinition?> LoadAsync(string mapId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        string? storedId = null;
        var name = mapId;
        int? pmcSeconds = null;
        int? scavSeconds = null;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT id, name, pmc_raid_duration_seconds, scav_raid_duration_seconds, source_json FROM maps;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Either name for the same map. The slug is what a raid reports and what every
                // caller has asked with until now; the id column is the game's own, and it is
                // what the item facts carry — a key's map is "56f40101d2720b2a4d8b45d6" and
                // nothing in the application could turn that into "Customs", so the Keys page
                // printed the identifier under a heading that said "Map id".
                var id = reader.GetString(0);
                if (!string.Equals(id, mapId, StringComparison.OrdinalIgnoreCase) &&
                    (reader.IsDBNull(4) || !Matches(reader.GetString(4), mapId)))
                {
                    continue;
                }

                storedId = id;
                name = reader.IsDBNull(1) ? mapId : reader.GetString(1);
                pmcSeconds = reader.IsDBNull(2) ? null : reader.GetInt32(2);
                scavSeconds = reader.IsDBNull(3) ? null : reader.GetInt32(3);
                break;
            }
        }

        if (storedId is null)
        {
            return null;
        }

        var provenance = new DataProvenance("tarkov.dev", _timeProvider.GetUtcNow(), Reference: storedId);
        var extracts = await LoadExtractsAsync(connection, storedId, mapId, provenance, cancellationToken)
            .ConfigureAwait(false);
        return new(
            mapId,
            name,
            pmcSeconds is null ? null : TimeSpan.FromSeconds(pmcSeconds.Value),
            scavSeconds is null ? null : TimeSpan.FromSeconds(scavSeconds.Value),
            [],
            extracts,
            null,
            provenance);
    }

    private static async Task<IReadOnlyList<MapExtract>> LoadExtractsAsync(
        SqliteConnection connection,
        string storedId,
        string mapId,
        DataProvenance provenance,
        CancellationToken cancellationToken)
    {
        var rows = new List<(string Id, string Name, MapPoint? Position, string? Payload)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name, x, z, source_json FROM map_extracts WHERE map_id = $mapId;";
            command.Parameters.AddWithValue("$mapId", storedId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // The map projects world X and world Z; the Y column is height and is not a
                // coordinate on the picture. Both halves have to be present or the extract is
                // listed without a marker rather than drawn at the origin.
                MapPoint? position = reader.IsDBNull(2) || reader.IsDBNull(3)
                    ? null
                    : new(reader.GetDouble(2), reader.GetDouble(3));
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    position,
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        var itemNames = await ReadItemNamesAsync(
            connection,
            rows.Select(row => row.Payload).OfType<string>().ToArray(),
            cancellationToken).ConfigureAwait(false);
        return rows
            .Select(row => new MapExtract(
                row.Id,
                mapId,
                row.Name,
                row.Position,
                row.Payload is null ? null : Conditions(row.Payload, itemNames),
                provenance))
            .ToArray();
    }

    private static bool Matches(string sourceJson, string mapId)
    {
        try
        {
            using var document = JsonDocument.Parse(sourceJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("normalizedName", out var slug) &&
                slug.ValueKind == JsonValueKind.String &&
                string.Equals(slug.GetString(), mapId, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // A reshaped payload costs one map rather than the whole catalog.
            return false;
        }
    }

    /// <summary>
    /// What this exit asks of you: who may take it, whether it needs a switch, what it costs.
    /// </summary>
    private static string? Conditions(string sourceJson, IReadOnlyDictionary<string, string> itemNames)
    {
        try
        {
            using var document = JsonDocument.Parse(sourceJson);
            return ExtractConditions.Describe(document.RootElement, id => itemNames.GetValueOrDefault(id));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Names for whatever these exits ask to be handed over.</summary>
    /// <remarks>
    /// A handful of ids per map and one lookup each, done once because the whole definition is
    /// cached after the first read.
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, string>> ReadItemNamesAsync(
        SqliteConnection connection,
        IReadOnlyList<string> payloads,
        CancellationToken cancellationToken)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var payload in payloads)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                foreach (var itemId in ExtractConditions.TransferItemIds(document.RootElement))
                {
                    wanted.Add(itemId);
                }
            }
            catch (JsonException)
            {
                // One unreadable extract costs one cost line, not the map.
            }
        }

        var names = new Dictionary<string, string>(wanted.Count, StringComparer.Ordinal);
        foreach (var itemId in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM items WHERE id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", itemId);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string name)
            {
                names[itemId] = name;
            }
        }

        return names;
    }
}
