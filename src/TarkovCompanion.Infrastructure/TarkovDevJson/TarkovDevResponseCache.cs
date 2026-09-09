using System.Collections.Concurrent;

namespace TarkovCompanion.Infrastructure.TarkovDevJson;

public sealed record TarkovDevCacheEntry(
    string CacheKey,
    string BodyJson,
    DateTimeOffset CachedUtc,
    string? ETag,
    DateTimeOffset? LastModified);

public interface ITarkovDevResponseCache
{
    Task<TarkovDevCacheEntry?> GetAsync(string cacheKey, CancellationToken cancellationToken);

    Task PutAsync(TarkovDevCacheEntry entry, CancellationToken cancellationToken);
}

public sealed class InMemoryTarkovDevResponseCache : ITarkovDevResponseCache
{
    private readonly ConcurrentDictionary<string, TarkovDevCacheEntry> _entries = new(StringComparer.Ordinal);

    public Task<TarkovDevCacheEntry?> GetAsync(string cacheKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _entries.TryGetValue(cacheKey, out var entry);
        return Task.FromResult(entry);
    }

    public Task PutAsync(TarkovDevCacheEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _entries[entry.CacheKey] = entry;
        return Task.CompletedTask;
    }
}
