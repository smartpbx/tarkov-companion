using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Application.Services;

public sealed record UnresolvedPriceHistoryPoint(
    int SourceOrdinal,
    long? FleaPriceRoubles,
    long? TraderValueRoubles,
    string Source,
    string RawJson);

/// <summary>The flea observations retained for one item, reduced to a compact seven-day line.</summary>
public sealed record PriceHistorySummary(long LowRoubles, long AverageRoubles, long HighRoubles, int ObservationCount)
{
    public static PriceHistorySummary? From(IReadOnlyList<PriceHistoryPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var prices = points
            .Select(point => point.FleaPriceRoubles)
            .OfType<long>()
            .Where(price => price > 0)
            .ToArray();
        if (prices.Length == 0)
        {
            return null;
        }

        return new(
            prices.Min(),
            checked((long)Math.Round(prices.Average(value => (decimal)value), MidpointRounding.AwayFromZero)),
            prices.Max(),
            prices.Length);
    }
}

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
