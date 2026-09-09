using System.Globalization;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

public sealed class SqliteTarkovDevResponseCache(SqliteConnectionFactory connectionFactory) : ITarkovDevResponseCache
{
    public async Task<TarkovDevCacheEntry?> GetAsync(string cacheKey, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT body_json, cached_utc, etag, last_modified
            FROM http_response_cache
            WHERE cache_key = $cacheKey;
            """;
        command.Parameters.AddWithValue("$cacheKey", cacheKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new(
            cacheKey,
            reader.GetString(0),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3)
                ? null
                : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public async Task PutAsync(TarkovDevCacheEntry entry, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO http_response_cache(cache_key, body_json, cached_utc, etag, last_modified)
            VALUES ($cacheKey, $bodyJson, $cachedUtc, $etag, $lastModified)
            ON CONFLICT(cache_key) DO UPDATE SET
                body_json = excluded.body_json,
                cached_utc = excluded.cached_utc,
                etag = excluded.etag,
                last_modified = excluded.last_modified;
            """;
        command.Parameters.AddWithValue("$cacheKey", entry.CacheKey);
        command.Parameters.AddWithValue("$bodyJson", entry.BodyJson);
        command.Parameters.AddWithValue("$cachedUtc", entry.CachedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$etag", (object?)entry.ETag ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$lastModified",
            entry.LastModified?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
