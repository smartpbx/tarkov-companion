using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>[#712 0-9] Boss spawn chances out of the synced maps rows, for the pre-raid brief.</summary>
/// <remarks>
/// Every sync has stored each map's whole payload in <c>maps.source_json</c>, bosses included, and
/// nothing read them. The payload names a boss by its mob id ("bossBully"); the display name comes
/// from the maps language table the same sync cached (<c>{mode}/maps_{language}</c>), where the
/// mob id is the key. No request, no schema change.
/// </remarks>
public sealed class SqliteMapBossCatalog(SqliteConnectionFactory connectionFactory) : IMapBossCatalog
{
    private const string MapsSql = "SELECT id, source_json FROM maps;";

    // Bodies are stored once, gzip-compressed and addressed by hash (migration 0011).
    private const string NamesSql = """
        SELECT body.compressed_body
        FROM http_response_cache AS cache
        JOIN raw_endpoint_bodies AS body ON body.content_sha256 = cache.content_sha256
        WHERE cache.cache_key LIKE '%/maps\_%' ESCAPE '\' AND body.compression = 'gzip'
        ORDER BY cache.cached_utc DESC
        LIMIT 1;
        """;

    /// <summary>The language table is about 22 KB; anything past this is not one.</summary>
    private const int MaximumNamesBytes = 4 * 1024 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IReadOnlyList<MapBossChance>> _byMap = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<MapBossChance>> GetAsync(string mapId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_byMap.TryGetValue(mapId, out var cached))
            {
                return cached;
            }

            var read = await LoadAsync(mapId, cancellationToken).ConfigureAwait(false);
            // An empty answer is not kept: before the first sync there is nothing, and after it there is.
            if (read.Count > 0)
            {
                _byMap[mapId] = read;
            }

            return read;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<MapBossChance>> LoadAsync(string mapId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        string? payload = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = MapsSql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(1))
                {
                    continue;
                }

                var json = reader.GetString(1);
                if (string.Equals(reader.GetString(0), mapId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(MapBossChances.Slug(json), mapId, StringComparison.OrdinalIgnoreCase))
                {
                    payload = json;
                    break;
                }
            }
        }

        if (payload is null)
        {
            return [];
        }

        string? names = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = NamesSql;
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is byte[] compressed)
            {
                names = Decompress(compressed);
            }
        }

        return MapBossChances.Read(payload, MapBossChances.Names(names));
    }

    private static string? Decompress(byte[] compressed)
    {
        try
        {
            using var input = new MemoryStream(compressed, writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaximumNamesBytes)
                {
                    return null;
                }

                output.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
        }
        catch (InvalidDataException)
        {
            // Names fall back to readable ids; the chances are still the catalog's.
            return null;
        }
    }
}

/// <summary>Reads one map payload's bosses: the pure half of <see cref="SqliteMapBossCatalog"/>.</summary>
public static class MapBossChances
{
    /// <summary>
    /// One row per boss, at its highest chance, likeliest first. The catalog lists some bosses more
    /// than once (Reserve's Raiders: on a timer, on a switch, on a lever), and the brief is one line.
    /// </summary>
    public static IReadOnlyList<MapBossChance> Read(string mapPayload, IReadOnlyDictionary<string, string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var best = new Dictionary<string, MapBossChance>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(mapPayload);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("bosses", out var bosses) ||
                bosses.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var boss in bosses.EnumerateArray())
            {
                if (boss.ValueKind != JsonValueKind.Object ||
                    !boss.TryGetProperty("mob", out var mob) || mob.ValueKind != JsonValueKind.String ||
                    mob.GetString() is not { Length: > 0 } mobId ||
                    !boss.TryGetProperty("spawnChance", out var chanceElement) ||
                    !chanceElement.TryGetDouble(out var chance) || double.IsNaN(chance) || chance <= 0)
                {
                    continue;
                }

                chance = Math.Min(chance, 1);
                var triggered = boss.TryGetProperty("spawnTrigger", out var trigger) &&
                    trigger.ValueKind == JsonValueKind.String && trigger.GetString() is { Length: > 0 };
                if (!best.TryGetValue(mobId, out var held) || chance > held.Chance)
                {
                    best[mobId] = new(mobId, NameOf(mobId, names), chance, triggered);
                }
            }
        }
        catch (JsonException)
        {
            // A reshaped payload costs the brief its boss line, not the brief.
            return [];
        }

        return [.. best.Values
            .OrderByDescending(boss => boss.Chance)
            .ThenBy(boss => boss.Name, StringComparer.CurrentCulture)];
    }

    /// <summary>The maps language table's <c>data</c> object, as mob id to name; empty when absent or unreadable.</summary>
    public static IReadOnlyDictionary<string, string> Names(string? languageTable)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(languageTable))
        {
            return names;
        }

        try
        {
            using var document = JsonDocument.Parse(languageTable);
            var data = document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("data", out var inner) ? inner : document.RootElement;
            if (data.ValueKind != JsonValueKind.Object)
            {
                return names;
            }

            foreach (var entry in data.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { Length: > 0 } text)
                {
                    names[entry.Name] = text;
                }
            }
        }
        catch (JsonException)
        {
        }

        return names;
    }

    internal static string? Slug(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("normalizedName", out var slug) &&
                slug.ValueKind == JsonValueKind.String
                    ? slug.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// English names for when the language table is not cached yet: migration 0011 emptied the
    /// response cache, and the maps rows outlive it until the next sync fills it again.
    /// </summary>
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.Ordinal)
    {
        ["bossBully"] = "Reshala",
        ["bossKilla"] = "Killa",
        ["bossKojaniy"] = "Shturman",
        ["bossGluhar"] = "Glukhar",
        ["bossSanitar"] = "Sanitar",
        ["bossTagilla"] = "Tagilla",
        ["bossKnight"] = "Knight",
        ["bossPartisan"] = "Partisan",
        ["bossZryachiy"] = "Zryachiy",
        ["bossBoar"] = "Kaban",
        ["bossKolontay"] = "Kollontay",
        ["sectantPriest"] = "Cultist Priest",
        ["PmcBot"] = "Raider",
        ["ExUsec"] = "Rogue",
        ["exUsecFree"] = "Rogue",
    };

    /// <summary>
    /// The table's name, or a readable form of the id where the table has none or repeats the id
    /// ("exUsecFree" is its own name there; the table calls the same fighters "ExUsec": Rogue).
    /// </summary>
    private static string NameOf(string mobId, IReadOnlyDictionary<string, string> names)
    {
        if (names.TryGetValue(mobId, out var name) && !string.Equals(name, mobId, StringComparison.Ordinal))
        {
            return name;
        }

        if (mobId.StartsWith("exUsec", StringComparison.OrdinalIgnoreCase) && names.TryGetValue("ExUsec", out var rogue))
        {
            return rogue;
        }

        if (KnownNames.TryGetValue(mobId, out var known))
        {
            return known;
        }

        var bare = mobId.StartsWith("boss", StringComparison.Ordinal) && mobId.Length > 4 ? mobId[4..] : mobId;
        return char.ToUpperInvariant(bare[0]) + bare[1..];
    }
}
