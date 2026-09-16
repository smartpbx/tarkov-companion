using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TarkovCompanion.Infrastructure.TarkovDevJson;

public sealed record TarkovDevCacheEntry(
    string CacheKey,
    string BodyJson,
    DateTimeOffset CachedUtc,
    string? ETag,
    DateTimeOffset? LastModified,
    string? ContentSha256 = null);

public sealed record TarkovDevCachePolicy
{
    public long MaximumCompressedBytes { get; init; } = 32L * 1024 * 1024;

    public long MaximumUncompressedBodyBytes { get; init; } = 256L * 1024 * 1024;

    public int MaximumEntries { get; init; } = 512;

    public TimeSpan MaximumAge { get; init; } = TimeSpan.FromDays(30);

    internal void Validate()
    {
        if (MaximumCompressedBytes is < 1024 or > 1024L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumCompressedBytes));
        }

        if (MaximumUncompressedBodyBytes is < 1024 or > 1024L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumUncompressedBodyBytes));
        }

        if (MaximumEntries is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries));
        }

        if (MaximumAge <= TimeSpan.Zero || MaximumAge > TimeSpan.FromDays(3650))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAge));
        }
    }
}

/// <summary>
/// Bounds metadata before either cache implementation stores it. Keeping these limits beside
/// the shared contract prevents one implementation from accepting rows that the durable cache's
/// inspection and cleanup paths must later refuse.
/// </summary>
internal static class TarkovDevCacheMetadata
{
    public const int MaximumCacheKeyUtf8Bytes = 4096;
    public const int MaximumEntityTagUtf8Bytes = 4096;
    public const int MaximumTimestampUtf8Bytes = 128;
    public const int Sha256HexLength = 64;
    public const int MaximumDiagnosticCodeUtf8Bytes = 128;

    public static void ValidateCacheKey(string cacheKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        if (Encoding.UTF8.GetByteCount(cacheKey) > MaximumCacheKeyUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cacheKey),
                $"Cache keys cannot exceed {MaximumCacheKeyUtf8Bytes} UTF-8 bytes.");
        }
    }

    public static void ValidateEntityTag(string? entityTag)
    {
        if (entityTag is not null && Encoding.UTF8.GetByteCount(entityTag) > MaximumEntityTagUtf8Bytes)
        {
            throw new InvalidDataException(
                $"Cache entity tags cannot exceed {MaximumEntityTagUtf8Bytes} UTF-8 bytes.");
        }
    }

    public static void ValidateOptionalContentHash(string? contentSha256)
    {
        if (contentSha256 is not null && !IsHexHash(contentSha256))
        {
            throw new InvalidDataException("The supplied cache content hash is not a SHA-256 hex value.");
        }
    }

    public static string NormalizeExpectedContentHash(string contentSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSha256);
        if (!IsHexHash(contentSha256))
        {
            throw new ArgumentOutOfRangeException(
                nameof(contentSha256),
                "The expected cache content hash must be a SHA-256 hex value.");
        }

        return contentSha256.ToLowerInvariant();
    }

    public static void ValidateDiagnosticCode(string diagnosticCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        if (Encoding.UTF8.GetByteCount(diagnosticCode) > MaximumDiagnosticCodeUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(diagnosticCode),
                $"Cache diagnostic codes cannot exceed {MaximumDiagnosticCodeUtf8Bytes} UTF-8 bytes.");
        }
    }

    public static bool IsLowerHexHash(string? value) =>
        value is { Length: Sha256HexLength } &&
        value.All(character =>
            (character >= '0' && character <= '9') ||
            (character >= 'a' && character <= 'f'));

    private static bool IsHexHash(string value) =>
        value.Length == Sha256HexLength &&
        value.All(character =>
            (character >= '0' && character <= '9') ||
            (character >= 'a' && character <= 'f') ||
            (character >= 'A' && character <= 'F'));
}

public sealed record TarkovDevCacheEntryInfo(
    string CacheKey,
    string ContentSha256,
    long CompressedBytes,
    long UncompressedBytes,
    DateTimeOffset CachedUtc,
    DateTimeOffset LastAccessedUtc);

public sealed record TarkovDevCacheInspection(
    int EntryCount,
    int UniqueBodyCount,
    long CompressedBytes,
    long UncompressedBytes,
    int QuarantinedDocumentCount,
    IReadOnlyList<TarkovDevCacheEntryInfo> Entries);

public sealed record TarkovDevCacheCleanupResult(
    bool WasDryRun,
    int RemovedEntries,
    int RemovedBodies,
    long ReclaimedCompressedBytes,
    IReadOnlyList<string> CacheKeys);

public interface ITarkovDevResponseCache
{
    Task<TarkovDevCacheEntry?> GetAsync(string cacheKey, CancellationToken cancellationToken);

    Task PutAsync(TarkovDevCacheEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Removes exactly the invalid body a reader observed and records why it was refused.
    /// </summary>
    /// <remarks>
    /// The expected hash is a compare-and-delete fence: validation can finish after another
    /// request has already published a good replacement, and that replacement must survive.
    /// </remarks>
    Task<bool> QuarantineAsync(
        string cacheKey,
        string expectedContentSha256,
        string diagnosticCode,
        CancellationToken cancellationToken);

    Task<TarkovDevCacheInspection> InspectAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task<TarkovDevCacheCleanupResult> CleanupAsync(
        DateTimeOffset nowUtc,
        bool dryRun,
        CancellationToken cancellationToken);
}

public sealed class InMemoryTarkovDevResponseCache : ITarkovDevResponseCache
{
    private sealed record Held(TarkovDevCacheEntry Entry, DateTimeOffset LastAccessedUtc, int Bytes);

    private readonly ConcurrentDictionary<string, Held> _entries = new(StringComparer.Ordinal);
    private readonly TarkovDevCachePolicy _policy;
    private readonly TimeProvider _timeProvider;

    public InMemoryTarkovDevResponseCache(TarkovDevCachePolicy? policy = null, TimeProvider? timeProvider = null)
    {
        _policy = policy ?? new();
        _policy.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<TarkovDevCacheEntry?> GetAsync(string cacheKey, CancellationToken cancellationToken)
    {
        TarkovDevCacheMetadata.ValidateCacheKey(cacheKey);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(cacheKey, out var held))
        {
            return Task.FromResult<TarkovDevCacheEntry?>(null);
        }

        _entries.TryUpdate(cacheKey, held with { LastAccessedUtc = _timeProvider.GetUtcNow().ToUniversalTime() }, held);
        return Task.FromResult<TarkovDevCacheEntry?>(held.Entry);
    }

    public async Task PutAsync(TarkovDevCacheEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        TarkovDevCacheMetadata.ValidateCacheKey(entry.CacheKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.BodyJson);
        TarkovDevCacheMetadata.ValidateEntityTag(entry.ETag);
        TarkovDevCacheMetadata.ValidateOptionalContentHash(entry.ContentSha256);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = Encoding.UTF8.GetByteCount(entry.BodyJson);
        if (bytes > _policy.MaximumUncompressedBodyBytes)
        {
            throw new InvalidDataException("One catalog response exceeds the decoded body budget.");
        }

        // This implementation holds decoded strings rather than compressed blobs. Treat the
        // cache's total-byte budget as the stricter resident-memory budget so a highly
        // compressible response cannot turn the test/fallback cache into an unbounded allocator.
        if (bytes > _policy.MaximumCompressedBytes)
        {
            throw new InvalidDataException("One catalog response exceeds the in-memory cache byte budget.");
        }

        var bodyBytes = Encoding.UTF8.GetBytes(entry.BodyJson);
        var hash = Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();
        if (entry.ContentSha256 is not null &&
            !string.Equals(entry.ContentSha256, hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The supplied cache content hash does not match the body.");
        }

        var frozen = entry with
        {
            ContentSha256 = hash,
            CachedUtc = entry.CachedUtc.ToUniversalTime(),
            LastModified = entry.LastModified?.ToUniversalTime(),
        };
        _entries[entry.CacheKey] = new(frozen, _timeProvider.GetUtcNow().ToUniversalTime(), bytes);
        await CleanupAsync(_timeProvider.GetUtcNow(), dryRun: false, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> QuarantineAsync(
        string cacheKey,
        string expectedContentSha256,
        string diagnosticCode,
        CancellationToken cancellationToken)
    {
        TarkovDevCacheMetadata.ValidateCacheKey(cacheKey);
        var normalizedExpectedHash = TarkovDevCacheMetadata.NormalizeExpectedContentHash(expectedContentSha256);
        TarkovDevCacheMetadata.ValidateDiagnosticCode(diagnosticCode);

        cancellationToken.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(cacheKey, out var held) ||
            !string.Equals(held.Entry.ContentSha256, normalizedExpectedHash, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(_entries.TryRemove(new KeyValuePair<string, Held>(cacheKey, held)));
    }

    public Task<TarkovDevCacheInspection> InspectAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = _entries.Values
            .OrderBy(value => value.LastAccessedUtc)
            .Select(value => new TarkovDevCacheEntryInfo(
                value.Entry.CacheKey,
                value.Entry.ContentSha256!,
                value.Bytes,
                value.Bytes,
                value.Entry.CachedUtc,
                value.LastAccessedUtc))
            .ToArray();
        var unique = entries.GroupBy(entry => entry.ContentSha256, StringComparer.Ordinal).ToArray();
        return Task.FromResult(new TarkovDevCacheInspection(
            entries.Length,
            unique.Length,
            unique.Sum(group => group.First().CompressedBytes),
            unique.Sum(group => group.First().UncompressedBytes),
            0,
            entries));
    }

    public Task<TarkovDevCacheCleanupResult> CleanupAsync(
        DateTimeOffset nowUtc,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        nowUtc = nowUtc.ToUniversalTime();
        var ordered = _entries.Values.OrderBy(value => value.LastAccessedUtc).ToList();
        var remove = ordered.Where(value => nowUtc - value.Entry.CachedUtc > _policy.MaximumAge).ToList();
        var retained = ordered.Except(remove).ToList();
        while (retained.Count > _policy.MaximumEntries || UniqueBytes(retained) > _policy.MaximumCompressedBytes)
        {
            remove.Add(retained[0]);
            retained.RemoveAt(0);
        }

        var keys = remove.Select(value => value.Entry.CacheKey).Distinct(StringComparer.Ordinal).ToArray();
        var retainedHashes = retained
            .Select(value => value.Entry.ContentSha256!)
            .ToHashSet(StringComparer.Ordinal);
        var removedBodies = remove
            .Where(value => !retainedHashes.Contains(value.Entry.ContentSha256!))
            .DistinctBy(value => value.Entry.ContentSha256!, StringComparer.Ordinal)
            .ToArray();
        if (!dryRun)
        {
            foreach (var held in remove.DistinctBy(value => value.Entry.CacheKey, StringComparer.Ordinal))
            {
                // Cleanup works from a snapshot. A concurrent publisher may have replaced this
                // key since then; remove only the exact observation selected by the snapshot.
                _entries.TryRemove(new KeyValuePair<string, Held>(held.Entry.CacheKey, held));
            }
        }

        return Task.FromResult(new TarkovDevCacheCleanupResult(
            dryRun,
            keys.Length,
            removedBodies.Length,
            removedBodies.Sum(value => (long)value.Bytes),
            keys));
    }

    private static long UniqueBytes(IEnumerable<Held> entries) =>
        entries
            .DistinctBy(value => value.Entry.ContentSha256!, StringComparer.Ordinal)
            .Sum(value => (long)value.Bytes);
}
