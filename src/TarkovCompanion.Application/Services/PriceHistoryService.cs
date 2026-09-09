using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Application.Services;

public interface IPriceHistoryStore
{
    Task<IReadOnlyList<PriceHistoryPoint>> GetAsync(
        string itemId,
        DateTimeOffset sinceUtc,
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
