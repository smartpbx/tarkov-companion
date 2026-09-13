using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>
/// Reads a map's extracts, transits, spawns and locked doors out of the synced catalog.
/// </summary>
/// <remarks>
/// Every one of these has been downloaded, parsed and written into the local database since
/// the first sync, and nothing has ever read them back. So the map showed tiles and street
/// names while the data for everything a player actually looks for sat in a column beside it.
/// This needs no request and no schema change; it is the payload already stored.
///
/// Cached per map after the first read. The catalog changes on a sync, not while a raid is
/// running, and a map is re-selected often enough that re-parsing a large JSON payload every
/// time would be felt.
/// </remarks>
public sealed class SqliteMapFeatureCatalog(SqliteConnectionFactory connectionFactory) : IMapFeatureCatalog
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IReadOnlyList<MapFeature>> _byMap = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<MapFeature>> GetAsync(string mapId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_byMap.TryGetValue(mapId, out var cached))
            {
                return cached;
            }

            var features = await LoadAsync(mapId, cancellationToken).ConfigureAwait(false);
            _byMap[mapId] = features;
            return features;
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
    /// catalog does. Doing it any other way would pair the wrong map with the wrong markers,
    /// which is worse than showing none.
    /// </remarks>
    private async Task<IReadOnlyList<MapFeature>> LoadAsync(string mapId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        string? payload = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT source_json FROM maps;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0) && Matches(reader.GetString(0), mapId))
                {
                    payload = reader.GetString(0);
                    break;
                }
            }
        }

        if (payload is null)
        {
            return [];
        }

        // Looked up before the features are built, because an exit that asks for twenty
        // thousand roubles has to say so in money and one that asks for a key has to name
        // the key. A handful of ids per map, once per map.
        var itemNames = await ReadItemNamesAsync(connection, payload, cancellationToken).ConfigureAwait(false);
        return Read(payload, itemNames) ?? [];
    }

    /// <summary>Whether this stored map is the one being asked for.</summary>
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
            return false;
        }
    }

    /// <summary>Names for whatever this map's exits ask to be handed over.</summary>
    private static async Task<IReadOnlyDictionary<string, string>> ReadItemNamesAsync(
        SqliteConnection connection,
        string sourceJson,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] wanted;
        try
        {
            using var document = JsonDocument.Parse(sourceJson);
            wanted = document.RootElement.TryGetProperty("extracts", out var extracts) &&
                extracts.ValueKind == JsonValueKind.Array
                    ? extracts.EnumerateArray()
                        .SelectMany(ExtractConditions.TransferItemIds)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                    : [];
        }
        catch (JsonException)
        {
            return names;
        }

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

    private static IReadOnlyList<MapFeature>? Read(string sourceJson, IReadOnlyDictionary<string, string> itemNames)
    {
        try
        {
            using var document = JsonDocument.Parse(sourceJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var itemName = (string id) => itemNames.GetValueOrDefault(id);
            var features = new List<MapFeature>();
            AddExtracts(root, "extracts", MapFeatureKind.Extract, features, itemName);
            AddExtracts(root, "transits", MapFeatureKind.Transit, features, itemName);
            AddSpawns(root, features);
            AddLocks(root, features);
            return features;
        }
        catch (JsonException)
        {
            // A reshaped payload costs the markers for one map rather than the whole catalog.
            return null;
        }
    }

    private static void AddExtracts(
        JsonElement root,
        string property,
        MapFeatureKind kind,
        List<MapFeature> features,
        Func<string, string?> itemName)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in array.EnumerateArray())
        {
            if (ReadPosition(entry, "position") is not { } position)
            {
                continue;
            }

            var name = ReadText(entry, "name") ?? ReadText(entry, "description") ?? "Unnamed";
            var faction = ReadText(entry, "faction");
            features.Add(new(
                kind,
                name,
                position,
                faction,
                kind == MapFeatureKind.Transit
                    ? "Transit to another map"
                    : ExtractConditions.Describe(entry, itemName)));
        }
    }

    private static void AddSpawns(JsonElement root, List<MapFeature> features)
    {
        if (!root.TryGetProperty("spawns", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in array.EnumerateArray())
        {
            if (ReadPosition(entry, "position") is not { } position)
            {
                continue;
            }

            var sides = ReadStrings(entry, "sides");
            var categories = ReadStrings(entry, "categories");
            // The zone name is frequently blank upstream, and "Spawn" is more useful on a map
            // than an empty label.
            var name = ReadText(entry, "zoneName") is { Length: > 0 } zone ? zone : "Spawn";
            features.Add(new(
                MapFeatureKind.Spawn,
                name,
                position,
                sides.Count > 0 ? string.Join(", ", sides) : null,
                categories.Count > 0 ? string.Join(", ", categories) : null));
        }
    }

    private static void AddLocks(JsonElement root, List<MapFeature> features)
    {
        if (!root.TryGetProperty("locks", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in array.EnumerateArray())
        {
            if (ReadPosition(entry, "position") is not { } position)
            {
                continue;
            }

            var key = entry.TryGetProperty("key", out var keyElement) && keyElement.ValueKind == JsonValueKind.Object
                ? ReadText(keyElement, "shortName") ?? ReadText(keyElement, "name")
                : null;
            features.Add(new(
                MapFeatureKind.Lock,
                key is null ? "Locked" : $"Needs {key}",
                position,
                null,
                ReadText(entry, "lockType")));
        }
    }

    private static WorldPosition? ReadPosition(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var position) || position.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return position.TryGetProperty("x", out var x) && x.TryGetDouble(out var xValue) &&
            position.TryGetProperty("y", out var y) && y.TryGetDouble(out var yValue) &&
            position.TryGetProperty("z", out var z) && z.TryGetDouble(out var zValue)
                ? new WorldPosition(xValue, yValue, zValue)
                : null;
    }

    private static string? ReadText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> ReadStrings(JsonElement element, string property) =>
        element.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.String)
                .Select(entry => entry.GetString()!)
                .ToArray()
            : [];
}
