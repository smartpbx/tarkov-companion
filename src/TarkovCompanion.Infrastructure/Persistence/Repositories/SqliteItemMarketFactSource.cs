using System.Globalization;
using System.Text.Json;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>
/// Reads what the synced catalog says about getting and selling an item.
/// </summary>
/// <remarks>
/// The base price has had its own column since the first migration and nothing read it. The
/// offer count and the trader buy offers have no column: they are kept in the item's retained
/// source row, which the refresh writes whole, unknown fields included, so they are read from
/// there rather than a migration being spent on two values one caller wants.
/// </remarks>
public sealed class SqliteItemMarketFactSource(SqliteConnectionFactory connectionFactory) : IItemMarketFactSource
{
    private static readonly IReadOnlyDictionary<string, string> CurrencyItemIds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["USD"] = "5696686a4bdc2da3298b456a",
            ["EUR"] = "569668774bdc2da2298b4568",
        };

    public async Task<ItemMarketFacts?> GetAsync(string itemId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT base_price, raw_json, source_updated_utc FROM items WHERE id = $itemId;";
        command.Parameters.AddWithValue("$itemId", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        long? basePrice = reader.IsDBNull(0) ? null : reader.GetInt64(0);
        var raw = reader.IsDBNull(1) ? null : reader.GetString(1);
        var observedUtc = ParseTimestamp(reader.GetString(2));
        await reader.DisposeAsync().ConfigureAwait(false);
        var (offers, traderSells) = ReadSourceRow(raw);
        return new(
            itemId,
            basePrice is > 0 ? basePrice : null,
            offers,
            traderSells,
            observedUtc,
            await ReadCatalogSyncedAsync(connection, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<DateTimeOffset?> ReadCatalogSyncedAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MAX(last_success_utc)
            FROM sync_state
            WHERE source_key = 'items' AND last_success_utc IS NOT NULL;
            """;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string text
            ? ParseTimestamp(text)
            : null;
    }

    public async Task<FleaMarketRates?> GetFleaRatesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT sell_offer_fee_rate, sell_requirement_fee_rate, observed_utc FROM flea_market_settings WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(reader.GetDouble(0), reader.GetDouble(1), ParseTimestamp(reader.GetString(2)))
            : null;
    }

    public async Task<IReadOnlyDictionary<string, CurrencyRoubleRate>> GetCurrencyRoubleRatesAsync(
        CancellationToken cancellationToken)
    {
        var rates = new Dictionary<string, CurrencyRoubleRate>(StringComparer.Ordinal)
        {
            ["RUB"] = new("RUB", 1, new DataProvenance("roubles", DateTimeOffset.UnixEpoch)),
        };
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, raw_json, source_updated_utc FROM items WHERE id IN ($usd, $eur);";
        command.Parameters.AddWithValue("$usd", CurrencyItemIds["USD"]);
        command.Parameters.AddWithValue("$eur", CurrencyItemIds["EUR"]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var itemId = reader.GetString(0);
            var currency = CurrencyItemIds.Single(pair => pair.Value == itemId).Key;
            var raw = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (ReadRoublePurchaseRate(raw) is not { } rate)
            {
                continue;
            }

            var observedUtc = ParseTimestamp(reader.GetString(2));
            rates[currency] = new(
                currency,
                rate,
                new DataProvenance("json.tarkov.dev/items", observedUtc, observedUtc, Confidence: Confidence.Certain));
        }

        return rates;
    }

    /// <summary>
    /// Reads the catalog's rouble purchase offer for a currency item. A base price is not an
    /// exchange rate, and a foreign-denominated offer cannot convert itself, so both are refused.
    /// </summary>
    internal static long? ReadRoublePurchaseRate(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (!document.RootElement.TryGetProperty("buyFromTrader", out var offers) ||
                offers.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var offer in offers.EnumerateArray())
            {
                if (offer.ValueKind != JsonValueKind.Object ||
                    !offer.TryGetProperty("currency", out var currency) ||
                    !string.Equals(currency.GetString(), "RUB", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (offer.TryGetProperty("priceRUB", out var priceRub) &&
                    priceRub.ValueKind == JsonValueKind.Number &&
                    priceRub.TryGetInt64(out var rate) &&
                    rate > 0)
                {
                    return rate;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The listing count, and whether any trader sells the item for money with no quest gating
    /// the offer. External JSON: anything missing or misshapen reads as unknown or as no offer.
    /// </summary>
    internal static (int? LastOfferCount, bool TraderSellsForCash) ReadSourceRow(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return (null, false);
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, false);
            }

            int? offers = root.TryGetProperty("lastOfferCount", out var count) &&
                          count.ValueKind == JsonValueKind.Number &&
                          count.TryGetInt32(out var parsed) && parsed >= 0
                ? parsed
                : null;
            var traderSells = root.TryGetProperty("buyFromTrader", out var buy) &&
                              buy.ValueKind == JsonValueKind.Array &&
                              buy.EnumerateArray().Any(IsOpenCashOffer);
            return (offers, traderSells);
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }

    /// <summary>Any currency counts: Peacekeeper's dollars buy the item as surely as roubles do.</summary>
    private static bool IsOpenCashOffer(JsonElement offer) =>
        offer.ValueKind == JsonValueKind.Object &&
        (!offer.TryGetProperty("taskUnlock", out var unlock) || unlock.ValueKind == JsonValueKind.Null);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
