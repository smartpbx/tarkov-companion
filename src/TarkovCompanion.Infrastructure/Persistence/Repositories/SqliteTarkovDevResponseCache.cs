using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>
/// A content-addressed cache. Cache keys point at gzip bodies, so two endpoints or validators
/// carrying identical UTF-8 JSON occupy one body row and cleanup cannot remove a still-referenced
/// body.
/// </summary>
public sealed class SqliteTarkovDevResponseCache : ITarkovDevResponseCache
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TarkovDevCachePolicy _policy;
    private readonly TimeProvider _timeProvider;

    public SqliteTarkovDevResponseCache(
        SqliteConnectionFactory connectionFactory,
        TarkovDevCachePolicy? policy = null,
        TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory;
        _policy = policy ?? new();
        _policy.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<TarkovDevCacheEntry?> GetAsync(string cacheKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        string hash;
        byte[] compressed;
        long expectedBytes;
        DateTimeOffset cachedUtc;
        string? etag;
        DateTimeOffset? lastModified;

        await using (var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT cache.content_sha256, body.compressed_body, body.uncompressed_bytes,
                       cache.cached_utc, cache.etag, cache.last_modified
                FROM http_response_cache AS cache
                JOIN raw_endpoint_bodies AS body ON body.content_sha256 = cache.content_sha256
                WHERE cache.cache_key = $cacheKey;
                """;
            command.Parameters.AddWithValue("$cacheKey", cacheKey);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            hash = reader.GetString(0);
            compressed = (byte[])reader[1];
            expectedBytes = reader.GetInt64(2);
            cachedUtc = ParseTime(reader.GetString(3));
            etag = reader.IsDBNull(4) ? null : reader.GetString(4);
            lastModified = reader.IsDBNull(5) ? null : ParseTime(reader.GetString(5));
        }

        string body;
        try
        {
            if (expectedBytes > _policy.MaximumUncompressedBodyBytes)
            {
                throw new InvalidDataException("The cached catalog body exceeds the decoded body budget.");
            }

            body = Decompress(compressed, expectedBytes);
            var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            if (!string.Equals(hash, actualHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The cached catalog body does not match its content address.");
            }

            using var _ = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException)
        {
            await QuarantineAsync(cacheKey, hash, cancellationToken).ConfigureAwait(false);
            return null;
        }

        await TouchAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        return new(cacheKey, body, cachedUtc, etag, lastModified, hash);
    }

    public async Task PutAsync(TarkovDevCacheEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.CacheKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.BodyJson);
        var bytes = Encoding.UTF8.GetBytes(entry.BodyJson);
        if (bytes.LongLength > _policy.MaximumUncompressedBodyBytes)
        {
            throw new InvalidDataException("One catalog response exceeds the decoded body budget.");
        }

        var compressed = Compress(bytes);
        if (compressed.LongLength > _policy.MaximumCompressedBytes)
        {
            throw new InvalidDataException("One catalog response exceeds the entire cache byte budget.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (entry.ContentSha256 is not null && !string.Equals(entry.ContentSha256, hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The supplied cache content hash does not match the body.");
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO raw_endpoint_bodies(
                content_sha256, compression, compressed_body, uncompressed_bytes, compressed_bytes, created_utc)
            VALUES ($hash, 'gzip', $body, $plainBytes, $compressedBytes, $createdUtc)
            ON CONFLICT(content_sha256) DO UPDATE SET
                compression = excluded.compression,
                compressed_body = excluded.compressed_body,
                uncompressed_bytes = excluded.uncompressed_bytes,
                compressed_bytes = excluded.compressed_bytes;
            """,
            cancellationToken,
            ("$hash", hash),
            ("$body", compressed),
            ("$plainBytes", bytes.LongLength),
            ("$compressedBytes", compressed.LongLength),
            ("$createdUtc", FormatTime(now))).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO http_response_cache(
                cache_key, content_sha256, cached_utc, last_accessed_utc, etag, last_modified)
            VALUES ($key, $hash, $cachedUtc, $accessedUtc, $etag, $lastModified)
            ON CONFLICT(cache_key) DO UPDATE SET
                content_sha256 = excluded.content_sha256,
                cached_utc = excluded.cached_utc,
                last_accessed_utc = excluded.last_accessed_utc,
                etag = excluded.etag,
                last_modified = excluded.last_modified;
            """,
            cancellationToken,
            ("$key", entry.CacheKey),
            ("$hash", hash),
            ("$cachedUtc", FormatTime(entry.CachedUtc)),
            ("$accessedUtc", FormatTime(now)),
            ("$etag", entry.ETag),
            ("$lastModified", entry.LastModified is null ? null : FormatTime(entry.LastModified.Value)))
            .ConfigureAwait(false);

        await RemoveUnreferencedBodiesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await EnforceBudgetsAsync(connection, transaction, entry.CacheKey, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TarkovDevCacheInspection> InspectAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var entries = new List<TarkovDevCacheEntryInfo>();
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT cache.cache_key, cache.content_sha256, body.compressed_bytes,
                       body.uncompressed_bytes, cache.cached_utc, cache.last_accessed_utc
                FROM http_response_cache AS cache
                JOIN raw_endpoint_bodies AS body ON body.content_sha256 = cache.content_sha256
                ORDER BY cache.last_accessed_utc, cache.cache_key;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    ParseTime(reader.GetString(4)),
                    ParseTime(reader.GetString(5))));
            }
        }

        var unique = entries.GroupBy(entry => entry.ContentSha256, StringComparer.Ordinal).ToArray();
        await using var quarantine = connection.CreateCommand();
        quarantine.CommandText = "SELECT COUNT(*) FROM local_json_recovery WHERE state IN ('malformed', 'quarantined');";
        var quarantined = Convert.ToInt32(
            await quarantine.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        return new(
            entries.Count,
            unique.Length,
            unique.Sum(group => group.First().CompressedBytes),
            unique.Sum(group => group.First().UncompressedBytes),
            quarantined,
            entries);
    }

    public async Task<TarkovDevCacheCleanupResult> CleanupAsync(
        DateTimeOffset nowUtc,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        nowUtc = nowUtc.ToUniversalTime();
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await FindCleanupCandidatesAsync(connection, transaction, nowUtc, cancellationToken).ConfigureAwait(false);
        long reclaimed = 0;
        var removedBodies = 0;
        if (!dryRun && candidates.Count > 0)
        {
            foreach (var candidate in candidates)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM http_response_cache WHERE cache_key = $key;",
                    cancellationToken,
                    ("$key", candidate.CacheKey)).ConfigureAwait(false);
            }

            var before = await BodyTotalsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await RemoveUnreferencedBodiesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var after = await BodyTotalsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            reclaimed = before.Bytes - after.Bytes;
            removedBodies = before.Count - after.Count;
        }
        else
        {
            var hashes = candidates.Select(candidate => candidate.ContentSha256).ToHashSet(StringComparer.Ordinal);
            var stillReferenced = (await ReadEntriesAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
                .Where(entry => !candidates.Any(candidate => candidate.CacheKey == entry.CacheKey))
                .Select(entry => entry.ContentSha256)
                .ToHashSet(StringComparer.Ordinal);
            var published = await ReadPublicationBodyHashesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var removedHashes = hashes
                .Where(hash => !stillReferenced.Contains(hash) && !published.Contains(hash))
                .ToHashSet(StringComparer.Ordinal);
            reclaimed = candidates.Where(candidate => removedHashes.Contains(candidate.ContentSha256))
                .DistinctBy(candidate => candidate.ContentSha256)
                .Sum(candidate => candidate.CompressedBytes);
            removedBodies = removedHashes.Count;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(dryRun, candidates.Count, removedBodies, reclaimed, candidates.Select(entry => entry.CacheKey).ToArray());
    }

    private async Task EnforceBudgetsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string protectedKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in await FindCleanupCandidatesAsync(connection, transaction, nowUtc, cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(candidate.CacheKey, protectedKey, StringComparison.Ordinal))
            {
                continue;
            }

            await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM http_response_cache WHERE cache_key = $key;",
                cancellationToken,
                ("$key", candidate.CacheKey)).ConfigureAwait(false);
        }

        await RemoveUnreferencedBodiesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var totals = await CacheBodyTotalsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var entryCount = await EntryCountAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (totals.Bytes > _policy.MaximumCompressedBytes || entryCount > _policy.MaximumEntries)
        {
            throw new InvalidDataException("The protected cache response cannot fit within the configured cache budgets.");
        }
    }

    private async Task<List<TarkovDevCacheEntryInfo>> FindCleanupCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var entries = await ReadEntriesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var remove = entries.Where(entry => nowUtc - entry.CachedUtc > _policy.MaximumAge).ToList();
        var retained = entries.Except(remove).ToList();
        while (retained.Count > _policy.MaximumEntries || UniqueCompressedBytes(retained) > _policy.MaximumCompressedBytes)
        {
            remove.Add(retained[0]);
            retained.RemoveAt(0);
        }

        return remove.DistinctBy(entry => entry.CacheKey).ToList();
    }

    private static long UniqueCompressedBytes(IEnumerable<TarkovDevCacheEntryInfo> entries) =>
        entries.GroupBy(entry => entry.ContentSha256, StringComparer.Ordinal).Sum(group => group.First().CompressedBytes);

    private static async Task<List<TarkovDevCacheEntryInfo>> ReadEntriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var result = new List<TarkovDevCacheEntryInfo>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT cache.cache_key, cache.content_sha256, body.compressed_bytes,
                   body.uncompressed_bytes, cache.cached_utc, cache.last_accessed_utc
            FROM http_response_cache AS cache
            JOIN raw_endpoint_bodies AS body ON body.content_sha256 = cache.content_sha256
            ORDER BY cache.last_accessed_utc, cache.cache_key;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                ParseTime(reader.GetString(4)),
                ParseTime(reader.GetString(5))));
        }

        return result;
    }

    private async Task QuarantineAsync(string cacheKey, string hash, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var removed = await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM http_response_cache WHERE cache_key = $key AND content_sha256 = $hash;",
            cancellationToken,
            ("$key", cacheKey),
            ("$hash", hash)).ConfigureAwait(false);
        if (removed == 0)
        {
            // A concurrent refresh replaced the corrupt observation after this reader loaded it.
            // Never let a stale quarantine decision remove or mislabel that newer valid body.
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await RemoveUnreferencedBodiesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO local_json_recovery(document_key, state, detected_utc, content_sha256, diagnostic_code)
            VALUES ($key, 'quarantined', $detected, $hash, 'cache-json-invalid')
            ON CONFLICT(document_key) DO UPDATE SET
                state = excluded.state,
                detected_utc = excluded.detected_utc,
                content_sha256 = excluded.content_sha256,
                diagnostic_code = excluded.diagnostic_code;
            """,
            cancellationToken,
            ("$key", $"cache:{cacheKey}"),
            ("$detected", FormatTime(_timeProvider.GetUtcNow())),
            ("$hash", hash)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TouchAsync(string cacheKey, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE http_response_cache SET last_accessed_utc = $now WHERE cache_key = $key;";
        command.Parameters.AddWithValue("$now", FormatTime(_timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue("$key", cacheKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RemoveUnreferencedBodiesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        _ = await ExecuteAsync(
            connection,
            transaction,
            """
            DELETE FROM raw_endpoint_bodies
            WHERE NOT EXISTS (
                SELECT 1 FROM http_response_cache
                WHERE http_response_cache.content_sha256 = raw_endpoint_bodies.content_sha256)
              AND NOT EXISTS (
                SELECT 1 FROM dataset_publications
                WHERE dataset_publications.content_sha256 = raw_endpoint_bodies.content_sha256);
            """,
            cancellationToken).ConfigureAwait(false);

    private static async Task<HashSet<string>> ReadPublicationBodyHashesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT DISTINCT content_sha256 FROM dataset_publications WHERE content_sha256 IS NOT NULL;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            hashes.Add(reader.GetString(0));
        }

        return hashes;
    }

    private static async Task<(int Count, long Bytes)> BodyTotalsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(compressed_bytes), 0) FROM raw_endpoint_bodies;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetInt32(0), reader.GetInt64(1));
    }

    private static async Task<(int Count, long Bytes)> CacheBodyTotalsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(compressed_bytes), 0)
            FROM raw_endpoint_bodies AS body
            WHERE EXISTS (
                SELECT 1 FROM http_response_cache AS cache
                WHERE cache.content_sha256 = body.content_sha256);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetInt32(0), reader.GetInt64(1));
    }

    private static async Task<int> EntryCountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM http_response_cache;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(bytes);
        }

        return output.ToArray();
    }

    private static string Decompress(byte[] compressed, long expectedBytes)
    {
        if (expectedBytes is < 1 or > int.MaxValue)
        {
            throw new InvalidDataException("The cached body declares an invalid decoded size.");
        }

        using var input = new MemoryStream(compressed, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream((int)expectedBytes);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = gzip.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > expectedBytes)
                throw new InvalidDataException("The cached body decoded past its declared size.");
            output.Write(buffer, 0, read);
        }
        if (output.Length != expectedBytes)
        {
            throw new InvalidDataException("The cached body decoded to a different size than recorded.");
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static string FormatTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
