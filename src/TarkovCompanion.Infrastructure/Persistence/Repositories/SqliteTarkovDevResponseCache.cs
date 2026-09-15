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
    private const int MaximumCompressionNameBytes = 16;
    private const int MaximumInspectableEntries = 100_001;
    private const long MaximumStoredBodyBytes = 1024L * 1024 * 1024;

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
        TarkovDevCacheMetadata.ValidateCacheKey(cacheKey);
        string? hash = null;
        string? compression = null;
        byte[] compressed = [];
        long? compressedBytes = null;
        long? actualCompressedBytes = null;
        long? expectedBytes = null;
        string? createdUtcText = null;
        string? cachedUtcText = null;
        string? lastAccessedUtcText = null;
        string? etag = null;
        string? lastModifiedText = null;
        var etagIsValid = false;
        var lastModifiedIsValid = false;
        var metadataReadFailed = false;

        await using (var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    CASE WHEN typeof(cache.content_sha256) = 'text'
                               AND length(CAST(cache.content_sha256 AS BLOB)) = 64
                         THEN cache.content_sha256 END,
                    CASE WHEN typeof(body.compression) = 'text'
                               AND length(CAST(body.compression AS BLOB)) BETWEEN 1 AND $maxCompressionNameBytes
                         THEN body.compression END,
                    CASE WHEN typeof(body.compressed_bytes) = 'integer' THEN body.compressed_bytes END,
                    CASE WHEN typeof(body.uncompressed_bytes) = 'integer' THEN body.uncompressed_bytes END,
                    CASE WHEN typeof(body.compressed_body) = 'blob' THEN length(body.compressed_body) END,
                    CASE WHEN typeof(body.created_utc) = 'text'
                               AND length(CAST(body.created_utc AS BLOB)) BETWEEN 1 AND $maxTimestampBytes
                         THEN body.created_utc END,
                    CASE WHEN typeof(cache.cached_utc) = 'text'
                               AND length(CAST(cache.cached_utc AS BLOB)) BETWEEN 1 AND $maxTimestampBytes
                         THEN cache.cached_utc END,
                    CASE WHEN typeof(cache.last_accessed_utc) = 'text'
                               AND length(CAST(cache.last_accessed_utc AS BLOB)) BETWEEN 1 AND $maxTimestampBytes
                         THEN cache.last_accessed_utc END,
                    CASE WHEN typeof(cache.etag) = 'text'
                               AND length(CAST(cache.etag AS BLOB)) <= $maxEntityTagBytes
                         THEN cache.etag END,
                    CASE WHEN cache.etag IS NULL OR (
                               typeof(cache.etag) = 'text'
                               AND length(CAST(cache.etag AS BLOB)) <= $maxEntityTagBytes)
                         THEN 1 ELSE 0 END,
                    CASE WHEN typeof(cache.last_modified) = 'text'
                               AND length(CAST(cache.last_modified AS BLOB)) BETWEEN 1 AND $maxTimestampBytes
                         THEN cache.last_modified END,
                    CASE WHEN cache.last_modified IS NULL OR (
                               typeof(cache.last_modified) = 'text'
                               AND length(CAST(cache.last_modified AS BLOB)) BETWEEN 1 AND $maxTimestampBytes)
                         THEN 1 ELSE 0 END,
                       body.compressed_body
                FROM http_response_cache AS cache
                LEFT JOIN raw_endpoint_bodies AS body ON body.content_sha256 = cache.content_sha256
                WHERE cache.cache_key = $cacheKey;
                """;
            command.Parameters.AddWithValue("$cacheKey", cacheKey);
            command.Parameters.AddWithValue("$maxCompressionNameBytes", MaximumCompressionNameBytes);
            command.Parameters.AddWithValue("$maxTimestampBytes", TarkovDevCacheMetadata.MaximumTimestampUtf8Bytes);
            command.Parameters.AddWithValue("$maxEntityTagBytes", TarkovDevCacheMetadata.MaximumEntityTagUtf8Bytes);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            try
            {
                hash = ReadNullableText(reader, 0);
                if (hash is not null && !TarkovDevCacheMetadata.IsLowerHexHash(hash))
                {
                    hash = null;
                }
                compression = ReadNullableText(reader, 1);
                compressedBytes = ReadNullableInt64(reader, 2);
                expectedBytes = ReadNullableInt64(reader, 3);
                actualCompressedBytes = ReadNullableInt64(reader, 4);
                createdUtcText = ReadNullableText(reader, 5);
                cachedUtcText = ReadNullableText(reader, 6);
                lastAccessedUtcText = ReadNullableText(reader, 7);
                etag = ReadNullableText(reader, 8);
                etagIsValid = reader.GetInt64(9) == 1;
                lastModifiedText = ReadNullableText(reader, 10);
                lastModifiedIsValid = reader.GetInt64(11) == 1;

                if (hash is not null &&
                    string.Equals(compression, "gzip", StringComparison.Ordinal) &&
                    compressedBytes is { } declaredCompressedBytes &&
                    declaredCompressedBytes is >= 1 && declaredCompressedBytes <= _policy.MaximumCompressedBytes &&
                    expectedBytes is { } declaredUncompressedBytes &&
                    declaredUncompressedBytes is >= 1 &&
                    declaredUncompressedBytes <= _policy.MaximumUncompressedBodyBytes &&
                    actualCompressedBytes is { } actualBodyBytes &&
                    actualBodyBytes == declaredCompressedBytes &&
                    actualBodyBytes <= int.MaxValue &&
                    createdUtcText is not null &&
                    cachedUtcText is not null &&
                    lastAccessedUtcText is not null &&
                    etagIsValid &&
                    lastModifiedIsValid)
                {
                    compressed = new byte[checked((int)actualBodyBytes)];
                    var offset = 0;
                    while (offset < compressed.Length)
                    {
                        var copied = checked((int)reader.GetBytes(
                            12,
                            offset,
                            compressed,
                            offset,
                            compressed.Length - offset));
                        if (copied == 0)
                        {
                            break;
                        }

                        offset += copied;
                    }

                    if (offset != compressed.Length)
                    {
                        compressed = [];
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or InvalidCastException or FormatException or OverflowException or
                ArgumentException or SqliteException)
            {
                // CASE projections keep hostile dynamic values out of GetString/GetInt64, and
                // this final fence converts provider-level decoding failures into quarantine too.
                metadataReadFailed = true;
                compressed = [];
            }
        }

        var metadataIsValid = !metadataReadFailed && hash is not null &&
            string.Equals(compression, "gzip", StringComparison.Ordinal) &&
            compressedBytes is { } declaredCompressedBytes &&
            declaredCompressedBytes is >= 1 && declaredCompressedBytes <= _policy.MaximumCompressedBytes &&
            expectedBytes is { } declaredUncompressedBytes &&
            declaredUncompressedBytes is >= 1 &&
            declaredUncompressedBytes <= _policy.MaximumUncompressedBodyBytes &&
            actualCompressedBytes is { } actualBodyBytes &&
            actualBodyBytes == declaredCompressedBytes &&
            actualBodyBytes <= int.MaxValue &&
            compressed.LongLength == declaredCompressedBytes &&
            createdUtcText is not null &&
            cachedUtcText is not null &&
            lastAccessedUtcText is not null &&
            etagIsValid &&
            lastModifiedIsValid;
        if (!metadataIsValid)
        {
            await QuarantineMalformedMetadataAsync(cacheKey, hash, cancellationToken).ConfigureAwait(false);
            return null;
        }

        string body;
        DateTimeOffset cachedUtc;
        DateTimeOffset? lastModified;
        try
        {
            _ = ParseTime(createdUtcText!);
            cachedUtc = ParseTime(cachedUtcText!);
            _ = ParseTime(lastAccessedUtcText!);
            lastModified = lastModifiedText is null ? null : ParseTime(lastModifiedText);
            body = Decompress(compressed, expectedBytes!.Value);
            var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            if (!string.Equals(hash, actualHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The cached catalog body does not match its content address.");
            }

            using var _ = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or JsonException or FormatException or ArgumentException or OverflowException)
        {
            await QuarantineAsync(cacheKey, hash!, "cache-json-invalid", cancellationToken).ConfigureAwait(false);
            return null;
        }

        await TouchAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        return new(cacheKey, body, cachedUtc, etag, lastModified, hash);
    }

    public async Task PutAsync(TarkovDevCacheEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        TarkovDevCacheMetadata.ValidateCacheKey(entry.CacheKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.BodyJson);
        TarkovDevCacheMetadata.ValidateEntityTag(entry.ETag);
        TarkovDevCacheMetadata.ValidateOptionalContentHash(entry.ContentSha256);
        var byteCount = Encoding.UTF8.GetByteCount(entry.BodyJson);
        if (byteCount > _policy.MaximumUncompressedBodyBytes)
        {
            throw new InvalidDataException("One catalog response exceeds the decoded body budget.");
        }

        // Count before allocating. A direct caller should not be able to force a body-sized
        // temporary allocation merely to discover that the cache policy rejects the body.
        var bytes = Encoding.UTF8.GetBytes(entry.BodyJson);
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
        var createdUtc = FormatTime(now);
        var cachedUtc = FormatTime(entry.CachedUtc);
        var accessedUtc = FormatTime(now);
        var lastModified = entry.LastModified is null ? null : FormatTime(entry.LastModified.Value);
        ValidateTimestamp(createdUtc);
        ValidateTimestamp(cachedUtc);
        ValidateTimestamp(accessedUtc);
        if (lastModified is not null)
        {
            ValidateTimestamp(lastModified);
        }

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
            ("$createdUtc", createdUtc)).ConfigureAwait(false);

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
            ("$cachedUtc", cachedUtc),
            ("$accessedUtc", accessedUtc),
            ("$etag", entry.ETag),
            ("$lastModified", lastModified))
            .ConfigureAwait(false);

        await RemoveUnreferencedBodiesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await EnforceBudgetsAsync(connection, transaction, entry.CacheKey, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TarkovDevCacheInspection> InspectAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var entries = await ReadEntriesAsync(connection, null, cancellationToken).ConfigureAwait(false);

        var unique = entries.GroupBy(entry => entry.ContentSha256, StringComparer.Ordinal).ToArray();
        await using var quarantine = connection.CreateCommand();
        quarantine.CommandText = """
            SELECT COUNT(*)
            FROM (
                SELECT 1
                FROM local_json_recovery
                WHERE state IN ('malformed', 'quarantined')
                LIMIT $maximumRows);
            """;
        quarantine.Parameters.AddWithValue("$maximumRows", MaximumInspectableEntries);
        var quarantinedValue = await quarantine.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var quarantinedLong = Convert.ToInt64(quarantinedValue, CultureInfo.InvariantCulture);
        if (quarantinedLong is < 0 or >= MaximumInspectableEntries)
        {
            throw new InvalidDataException("The cache recovery count is outside the supported range.");
        }

        var quarantined = (int)quarantinedLong;
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
            var candidateKeys = candidates.Select(candidate => candidate.CacheKey).ToHashSet(StringComparer.Ordinal);
            var stillReferenced = (await ReadEntriesAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
                .Where(entry => !candidateKeys.Contains(entry.CacheKey))
                .Select(entry => entry.ContentSha256)
                .ToHashSet(StringComparer.Ordinal);
            var removedHashes = hashes
                .Where(hash => !stillReferenced.Contains(hash))
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
        var remove = new List<TarkovDevCacheEntryInfo>();
        var retained = new List<TarkovDevCacheEntryInfo>(entries.Count);
        foreach (var entry in entries)
        {
            if (nowUtc - entry.CachedUtc > _policy.MaximumAge)
            {
                remove.Add(entry);
            }
            else
            {
                retained.Add(entry);
            }
        }

        // Entries are already ordered oldest-first. Maintain reference counts and a running
        // unique-byte total so evicting up to 100k rows remains O(n), even when many keys share
        // one content-addressed body.
        var referencesByHash = new Dictionary<string, int>(StringComparer.Ordinal);
        long uniqueCompressedBytes = 0;
        foreach (var entry in retained)
        {
            if (referencesByHash.TryGetValue(entry.ContentSha256, out var references))
            {
                referencesByHash[entry.ContentSha256] = references + 1;
            }
            else
            {
                referencesByHash.Add(entry.ContentSha256, 1);
                uniqueCompressedBytes = checked(uniqueCompressedBytes + entry.CompressedBytes);
            }
        }

        var retainedCount = retained.Count;
        var cursor = 0;
        while (retainedCount > _policy.MaximumEntries || uniqueCompressedBytes > _policy.MaximumCompressedBytes)
        {
            var entry = retained[cursor++];
            remove.Add(entry);
            retainedCount--;
            var remainingReferences = referencesByHash[entry.ContentSha256] - 1;
            if (remainingReferences == 0)
            {
                referencesByHash.Remove(entry.ContentSha256);
                uniqueCompressedBytes -= entry.CompressedBytes;
            }
            else
            {
                referencesByHash[entry.ContentSha256] = remainingReferences;
            }
        }

        return remove.DistinctBy(entry => entry.CacheKey).ToList();
    }

    private async Task<List<TarkovDevCacheEntryInfo>> ReadEntriesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var result = new List<TarkovDevCacheEntryInfo>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                CASE WHEN typeof(cache.cache_key) = 'text'
                           AND length(CAST(cache.cache_key AS BLOB)) BETWEEN 1 AND $maxCacheKeyBytes
                     THEN cache.cache_key END,
                CASE WHEN typeof(cache.content_sha256) = 'text'
                           AND length(CAST(cache.content_sha256 AS BLOB)) = 64
                     THEN cache.content_sha256 END,
                CASE WHEN typeof(body.compressed_bytes) = 'integer'
                           AND body.compressed_bytes BETWEEN 1 AND $maxStoredBodyBytes
                     THEN body.compressed_bytes END,
                CASE WHEN typeof(body.uncompressed_bytes) = 'integer'
                           AND body.uncompressed_bytes BETWEEN 1 AND $maxStoredBodyBytes
                     THEN body.uncompressed_bytes END,
                CASE WHEN typeof(cache.cached_utc) = 'text'
                           AND length(CAST(cache.cached_utc AS BLOB)) BETWEEN 1 AND $maxTimestampBytes
                     THEN cache.cached_utc END,
                CASE WHEN typeof(cache.last_accessed_utc) = 'text'
                           AND length(CAST(cache.last_accessed_utc AS BLOB)) BETWEEN 1 AND $maxTimestampBytes
                     THEN cache.last_accessed_utc END,
                CASE WHEN typeof(body.created_utc) = 'text'
                           AND length(CAST(body.created_utc AS BLOB)) BETWEEN 1 AND $maxTimestampBytes
                     THEN body.created_utc END,
                CASE WHEN typeof(cache.last_modified) = 'text'
                           AND length(CAST(cache.last_modified AS BLOB)) BETWEEN 1 AND $maxTimestampBytes
                     THEN cache.last_modified END,
                CASE WHEN
                    body.content_sha256 IS NOT NULL AND
                    typeof(body.compression) = 'text' AND
                    body.compression = 'gzip' AND
                    length(CAST(body.compression AS BLOB)) BETWEEN 1 AND $maxCompressionNameBytes AND
                    typeof(body.compressed_bytes) = 'integer' AND
                    body.compressed_bytes BETWEEN 1 AND $maxStoredBodyBytes AND
                    typeof(body.uncompressed_bytes) = 'integer' AND
                    body.uncompressed_bytes BETWEEN 1 AND $maxStoredBodyBytes AND
                    typeof(body.compressed_body) = 'blob' AND
                    length(body.compressed_body) = body.compressed_bytes AND
                    typeof(body.created_utc) = 'text' AND
                    length(CAST(body.created_utc AS BLOB)) BETWEEN 1 AND $maxTimestampBytes AND
                    (cache.etag IS NULL OR (
                        typeof(cache.etag) = 'text' AND
                        length(CAST(cache.etag AS BLOB)) <= $maxEntityTagBytes)) AND
                    (cache.last_modified IS NULL OR (
                        typeof(cache.last_modified) = 'text' AND
                        length(CAST(cache.last_modified AS BLOB)) BETWEEN 1 AND $maxTimestampBytes))
                    THEN 1 ELSE 0 END
            FROM http_response_cache AS cache
            LEFT JOIN raw_endpoint_bodies AS body ON body.content_sha256 = cache.content_sha256
            ORDER BY cache.rowid
            LIMIT $maximumRows;
            """;
        command.Parameters.AddWithValue("$maxCacheKeyBytes", TarkovDevCacheMetadata.MaximumCacheKeyUtf8Bytes);
        command.Parameters.AddWithValue("$maxStoredBodyBytes", MaximumStoredBodyBytes);
        command.Parameters.AddWithValue("$maxTimestampBytes", TarkovDevCacheMetadata.MaximumTimestampUtf8Bytes);
        command.Parameters.AddWithValue("$maxCompressionNameBytes", MaximumCompressionNameBytes);
        command.Parameters.AddWithValue("$maxEntityTagBytes", TarkovDevCacheMetadata.MaximumEntityTagUtf8Bytes);
        command.Parameters.AddWithValue("$maximumRows", MaximumInspectableEntries);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var cacheKey = ReadNullableText(reader, 0);
                var hash = ReadNullableText(reader, 1);
                var compressedBytes = ReadNullableInt64(reader, 2);
                var uncompressedBytes = ReadNullableInt64(reader, 3);
                var cachedUtc = ReadNullableText(reader, 4);
                var lastAccessedUtc = ReadNullableText(reader, 5);
                var createdUtc = ReadNullableText(reader, 6);
                var lastModifiedUtc = ReadNullableText(reader, 7);
                var metadataIsValid = reader.GetInt64(8) == 1;
                if (string.IsNullOrWhiteSpace(cacheKey) || !TarkovDevCacheMetadata.IsLowerHexHash(hash) || compressedBytes is null ||
                    uncompressedBytes is null || cachedUtc is null || lastAccessedUtc is null ||
                    createdUtc is null || !metadataIsValid)
                {
                    throw new InvalidDataException("The cache contains malformed or oversized metadata.");
                }

                _ = ParseTime(createdUtc);
                if (lastModifiedUtc is not null)
                {
                    _ = ParseTime(lastModifiedUtc);
                }

                result.Add(new(
                    cacheKey,
                    hash!,
                    compressedBytes.Value,
                    uncompressedBytes.Value,
                    ParseTime(cachedUtc),
                    ParseTime(lastAccessedUtc)));
            }
        }
        catch (Exception exception) when (
            exception is FormatException or ArgumentException or InvalidCastException or OverflowException or SqliteException)
        {
            throw new InvalidDataException("The cache contains malformed metadata.", exception);
        }

        if (result.Count == MaximumInspectableEntries)
        {
            throw new InvalidDataException("The cache contains more entries than maintenance can inspect safely.");
        }

        return result
            .OrderBy(entry => entry.LastAccessedUtc)
            .ThenBy(entry => entry.CacheKey, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<bool> QuarantineAsync(
        string cacheKey,
        string expectedContentSha256,
        string diagnosticCode,
        CancellationToken cancellationToken)
    {
        TarkovDevCacheMetadata.ValidateCacheKey(cacheKey);
        var normalizedExpectedHash = TarkovDevCacheMetadata.NormalizeExpectedContentHash(expectedContentSha256);
        TarkovDevCacheMetadata.ValidateDiagnosticCode(diagnosticCode);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var removed = await ExecuteAsync(
            connection,
            transaction,
            """
            DELETE FROM http_response_cache
            WHERE cache_key = $key
              AND content_sha256 = $hash;
            """,
            cancellationToken,
            ("$key", cacheKey),
            ("$hash", normalizedExpectedHash)).ConfigureAwait(false);
        if (removed == 0)
        {
            // A concurrent refresh replaced the corrupt observation after this reader loaded it.
            // Never let a stale quarantine decision remove or mislabel that newer valid body.
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await RemoveUnreferencedBodiesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO local_json_recovery(document_key, state, detected_utc, content_sha256, diagnostic_code)
            VALUES ($key, 'quarantined', $detected, $hash, $diagnostic)
            ON CONFLICT(document_key) DO UPDATE SET
                state = excluded.state,
                detected_utc = excluded.detected_utc,
                content_sha256 = excluded.content_sha256,
                diagnostic_code = excluded.diagnostic_code;
            """,
            cancellationToken,
            ("$key", $"cache:{cacheKey}"),
            ("$detected", FormatTime(_timeProvider.GetUtcNow())),
            ("$hash", normalizedExpectedHash),
            ("$diagnostic", diagnosticCode)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> QuarantineMalformedMetadataAsync(
        string cacheKey,
        string? expectedContentSha256,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var removed = await ExecuteAsync(
            connection,
            transaction,
            """
            DELETE FROM http_response_cache
            WHERE cache_key = $key
              AND ($hash IS NULL OR content_sha256 = $hash)
              AND (
                  typeof(content_sha256) <> 'text' OR
                  length(CAST(content_sha256 AS BLOB)) <> 64 OR
                  CASE WHEN typeof(content_sha256) = 'text'
                              AND length(CAST(content_sha256 AS BLOB)) = 64
                       THEN content_sha256 GLOB '*[^0-9a-f]*' ELSE 0 END OR
                  typeof(cached_utc) <> 'text' OR
                  length(CAST(cached_utc AS BLOB)) NOT BETWEEN 1 AND $maxTimestampBytes OR
                  typeof(last_accessed_utc) <> 'text' OR
                  length(CAST(last_accessed_utc AS BLOB)) NOT BETWEEN 1 AND $maxTimestampBytes OR
                  (etag IS NOT NULL AND (
                      typeof(etag) <> 'text' OR
                      length(CAST(etag AS BLOB)) > $maxEntityTagBytes)) OR
                  (last_modified IS NOT NULL AND (
                      typeof(last_modified) <> 'text' OR
                      length(CAST(last_modified AS BLOB)) NOT BETWEEN 1 AND $maxTimestampBytes)) OR
                  NOT EXISTS (
                      SELECT 1
                      FROM raw_endpoint_bodies AS body
                      WHERE body.content_sha256 = http_response_cache.content_sha256
                        AND typeof(body.compression) = 'text'
                        AND length(CAST(body.compression AS BLOB)) BETWEEN 1 AND $maxCompressionNameBytes
                        AND body.compression = 'gzip'
                        AND typeof(body.compressed_bytes) = 'integer'
                        AND body.compressed_bytes BETWEEN 1 AND $maxCompressedBytes
                        AND typeof(body.uncompressed_bytes) = 'integer'
                        AND body.uncompressed_bytes BETWEEN 1 AND $maxUncompressedBytes
                        AND typeof(body.compressed_body) = 'blob'
                        AND length(body.compressed_body) = body.compressed_bytes
                        AND typeof(body.created_utc) = 'text'
                        AND length(CAST(body.created_utc AS BLOB)) BETWEEN 1 AND $maxTimestampBytes));
            """,
            cancellationToken,
            ("$key", cacheKey),
            ("$hash", expectedContentSha256),
            ("$maxTimestampBytes", TarkovDevCacheMetadata.MaximumTimestampUtf8Bytes),
            ("$maxEntityTagBytes", TarkovDevCacheMetadata.MaximumEntityTagUtf8Bytes),
            ("$maxCompressionNameBytes", MaximumCompressionNameBytes),
            ("$maxCompressedBytes", _policy.MaximumCompressedBytes),
            ("$maxUncompressedBytes", _policy.MaximumUncompressedBodyBytes)).ConfigureAwait(false);
        if (removed == 0)
        {
            // The key was repaired or replaced after this reader observed it. The predicate is
            // intentionally re-evaluated by the deleting writer, so a good replacement survives
            // even when the corrupt hash was too large or the wrong SQLite type to materialize.
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await RemoveUnreferencedBodiesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO local_json_recovery(document_key, state, detected_utc, content_sha256, diagnostic_code)
            VALUES ($key, 'quarantined', $detected, $hash, 'cache-metadata-invalid')
            ON CONFLICT(document_key) DO UPDATE SET
                state = excluded.state,
                detected_utc = excluded.detected_utc,
                content_sha256 = excluded.content_sha256,
                diagnostic_code = excluded.diagnostic_code;
            """,
            cancellationToken,
            ("$key", $"cache:{cacheKey}"),
            ("$detected", FormatTime(_timeProvider.GetUtcNow())),
            ("$hash", expectedContentSha256)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
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
                WHERE http_response_cache.content_sha256 = raw_endpoint_bodies.content_sha256);
            """,
            cancellationToken).ConfigureAwait(false);

    private static async Task<(int Count, long Bytes)> BodyTotalsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN
                       typeof(compressed_bytes) = 'integer' AND compressed_bytes BETWEEN 1 AND $maximumBytes AND
                       typeof(uncompressed_bytes) = 'integer' AND uncompressed_bytes BETWEEN 1 AND $maximumBytes AND
                       typeof(compressed_body) = 'blob' AND length(compressed_body) = compressed_bytes
                       THEN compressed_bytes ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN
                       typeof(compressed_bytes) = 'integer' AND compressed_bytes BETWEEN 1 AND $maximumBytes AND
                       typeof(uncompressed_bytes) = 'integer' AND uncompressed_bytes BETWEEN 1 AND $maximumBytes AND
                       typeof(compressed_body) = 'blob' AND length(compressed_body) = compressed_bytes
                       THEN 0 ELSE 1 END), 0)
            FROM (
                SELECT compressed_bytes, uncompressed_bytes, compressed_body
                FROM raw_endpoint_bodies
                LIMIT $maximumRows) AS body;
            """;
        command.Parameters.AddWithValue("$maximumBytes", MaximumStoredBodyBytes);
        command.Parameters.AddWithValue("$maximumRows", MaximumInspectableEntries);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return ReadBodyTotals(reader);
    }

    private static async Task<(int Count, long Bytes)> CacheBodyTotalsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN
                       typeof(compressed_bytes) = 'integer' AND compressed_bytes BETWEEN 1 AND $maximumBytes AND
                       typeof(uncompressed_bytes) = 'integer' AND uncompressed_bytes BETWEEN 1 AND $maximumBytes AND
                       typeof(compressed_body) = 'blob' AND length(compressed_body) = compressed_bytes
                       THEN compressed_bytes ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN
                       typeof(compressed_bytes) = 'integer' AND compressed_bytes BETWEEN 1 AND $maximumBytes AND
                       typeof(uncompressed_bytes) = 'integer' AND uncompressed_bytes BETWEEN 1 AND $maximumBytes AND
                       typeof(compressed_body) = 'blob' AND length(compressed_body) = compressed_bytes
                       THEN 0 ELSE 1 END), 0)
            FROM (
                SELECT compressed_bytes, uncompressed_bytes, compressed_body
                FROM raw_endpoint_bodies AS candidate
                WHERE EXISTS (
                    SELECT 1 FROM http_response_cache AS cache
                    WHERE cache.content_sha256 = candidate.content_sha256)
                LIMIT $maximumRows) AS body;
            """;
        command.Parameters.AddWithValue("$maximumBytes", MaximumStoredBodyBytes);
        command.Parameters.AddWithValue("$maximumRows", MaximumInspectableEntries);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return ReadBodyTotals(reader);
    }

    private static (int Count, long Bytes) ReadBodyTotals(SqliteDataReader reader)
    {
        var count = reader.GetInt64(0);
        var bytes = reader.GetInt64(1);
        var invalid = reader.GetInt64(2);
        if (count is < 0 or >= MaximumInspectableEntries || bytes < 0 || invalid != 0)
        {
            throw new InvalidDataException("The cache body metadata is malformed or outside maintenance bounds.");
        }

        return ((int)count, bytes);
    }

    private static async Task<int> EntryCountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM (SELECT 1 FROM http_response_cache LIMIT $maximumRows);
            """;
        command.Parameters.AddWithValue("$maximumRows", MaximumInspectableEntries);
        var count = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (count is < 0 or >= MaximumInspectableEntries)
        {
            throw new InvalidDataException("The cache entry count is outside maintenance bounds.");
        }

        return (int)count;
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
        // Cache publication is on the refresh path. SmallestSize made a synthetic 50 MiB
        // fixture consume most of a Linux CI timeout for no meaningful budget improvement;
        // Optimal preserves compact JSON while bounding first-sync CPU cost.
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
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

    private static string? ReadNullableText(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);


    private static void ValidateTimestamp(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) is < 1 or > TarkovDevCacheMetadata.MaximumTimestampUtf8Bytes)
        {
            throw new InvalidDataException("A cache timestamp is outside the supported UTF-8 length.");
        }
    }

    private static string FormatTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
