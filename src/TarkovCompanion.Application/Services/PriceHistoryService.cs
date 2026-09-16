using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Application.Services;

public sealed record UnresolvedPriceHistoryPoint(
    int SourceOrdinal,
    long? FleaPriceRoubles,
    long? TraderValueRoubles,
    string Source,
    string RawJson);

public interface IPriceHistoryStore
{
    Task<IReadOnlyList<PriceHistoryPoint>> GetAsync(
        string itemId,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns source points whose timestamp was absent, preserving their ordinal and raw shape
    /// instead of inventing a time or silently dropping them.
    /// </summary>
    Task<IReadOnlyList<UnresolvedPriceHistoryPoint>> GetUnresolvedAsync(
        string itemId,
        CancellationToken cancellationToken);
}

public sealed class PriceHistoryService(IPriceHistoryStore store, TimeProvider? timeProvider = null) : IPriceHistoryService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<IReadOnlyList<PriceHistoryPoint>> GetAsync(
        string itemId,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        return store.GetAsync(itemId, _timeProvider.GetUtcNow() - window, cancellationToken);
    }
}
