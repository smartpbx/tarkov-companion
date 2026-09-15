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
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = Encoding.UTF8.GetByteCount(entry.BodyJson);
        if (bytes > _policy.MaximumUncompressedBodyBytes)
        {
            throw new InvalidDataException("One catalog response exceeds the decoded body budget.");
        }

        var hash = entry.ContentSha256 ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.BodyJson))).ToLowerInvariant();
        var frozen = entry with { ContentSha256 = hash, CachedUtc = entry.CachedUtc.ToUniversalTime() };
        _entries[entry.CacheKey] = new(frozen, _timeProvider.GetUtcNow().ToUniversalTime(), bytes);
        await CleanupAsync(_timeProvider.GetUtcNow(), dryRun: false, cancellationToken).ConfigureAwait(false);
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
        while (retained.Count > _policy.MaximumEntries || retained.Sum(value => (long)value.Bytes) > _policy.MaximumCompressedBytes)
        {
            remove.Add(retained[0]);
            retained.RemoveAt(0);
        }

        var keys = remove.Select(value => value.Entry.CacheKey).Distinct(StringComparer.Ordinal).ToArray();
        if (!dryRun)
        {
            foreach (var key in keys)
            {
                _entries.TryRemove(key, out _);
            }
        }

        return Task.FromResult(new TarkovDevCacheCleanupResult(
            dryRun,
            keys.Length,
            remove.Select(value => value.Entry.ContentSha256).Distinct(StringComparer.Ordinal).Count(),
            remove.Sum(value => (long)value.Bytes),
            keys));
    }
}
