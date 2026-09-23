using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.Core.Abstractions;

/// <summary>
/// The catalog facts a take-or-leave call needs that a price alone does not carry.
/// </summary>
/// <remarks>
/// Its own small interface rather than two more members on <see cref="IItemRepository"/>, which
/// a dozen test doubles implement and which everything that only wants a name would have to
/// carry. Both answers are null where the catalog has not been synced, never a default.
/// </remarks>
public interface IItemMarketFactSource
{
    Task<ItemMarketFacts?> GetAsync(string itemId, CancellationToken cancellationToken);

    Task<FleaMarketRates?> GetFleaRatesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Rouble prices for the currencies the catalog currently sells, keyed by ISO-style short
    /// name (RUB, USD, EUR). RUB is always one; a missing foreign currency remains unread.
    /// </summary>
    Task<IReadOnlyDictionary<string, CurrencyRoubleRate>> GetCurrencyRoubleRatesAsync(
        CancellationToken cancellationToken);
}
