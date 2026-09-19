using System.Text.Json;
using TarkovCompanion.IntegrationTests.DataV2;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.IntegrationTests;

/// <summary>
/// #282: the items payload carries the flea rates, the base price, the listing count and the
/// trader buy offers. The shape below is the payload's own, read off json.tarkov.dev on
/// 2026-09-19, with public ids and invented prices.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class ItemMarketFactPersistenceTests
{
    private static readonly DateTimeOffset Observed = new(2026, 9, 19, 15, 0, 0, TimeSpan.Zero);

    private const string Payload = """
        {
          "items": {
            "5c94bbff86f7747ee735c08f": {
              "id": "5c94bbff86f7747ee735c08f", "name": "Access keycard", "shortName": "Access",
              "width": 1, "height": 1, "basePrice": 100000, "avg24hPrice": 134497, "lastLowPrice": 135999,
              "lastOfferCount": 26, "types": ["keys"],
              "buyFromTrader": [
                { "trader": "54cb57776803fa99248b456e", "price": 208500, "priceRUB": 208500, "currency": "RUB", "minTraderLevel": 3, "taskUnlock": null }
              ],
              "sellToTrader": []
            },
            "5d235b4d86f7742e017bc88a": {
              "id": "5d235b4d86f7742e017bc88a", "name": "GP coin", "shortName": "GP",
              "width": 1, "height": 1, "basePrice": 0, "types": ["barter", "noFlea"],
              "buyFromTrader": [
                { "trader": "5ac3b934156ae10c4430e83c", "price": 1, "currency": "RUB", "taskUnlock": { "id": "a-task" } }
              ],
              "sellToTrader": []
            }
          },
          "fleaMarket": { "name": "FleaMarket", "minPlayerLevel": 15, "sellOfferFeeRate": 0.05, "sellRequirementFeeRate": 0.04 }
        }
        """;

    [Fact]
    public async Task AnItemsRefreshKeepsTheFleaRatesAndTheFactsAreReadBack()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var data = JsonSerializer.Deserialize<TarkovDevItemsData>(Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await new SqliteDataRefreshRepository(database.Factory).RefreshItemsAsync(data, Observed, TestContext.Current.CancellationToken);
        var source = new SqliteItemMarketFactSource(database.Factory);

        var rates = await source.GetFleaRatesAsync(TestContext.Current.CancellationToken);
        var keycard = await source.GetAsync("5c94bbff86f7747ee735c08f", TestContext.Current.CancellationToken);
        var coin = await source.GetAsync("5d235b4d86f7742e017bc88a", TestContext.Current.CancellationToken);

        Assert.NotNull(rates);
        Assert.Equal(0.05, rates.SellOfferFeeRate);
        Assert.Equal(0.04, rates.SellRequirementFeeRate);
        Assert.Equal(Observed, rates.ObservedUtc);
        Assert.NotNull(keycard);
        Assert.Equal(100_000, keycard.BasePriceRoubles);
        Assert.Equal(26, keycard.LastOfferCount);
        Assert.True(keycard.TraderSellsForCash);
        Assert.NotNull(coin);
        // A zero base price is no base price, an offer a quest unlocks is not an open one, and a
        // listing count the payload does not carry is unknown rather than zero.
        Assert.Null(coin.BasePriceRoubles);
        Assert.Null(coin.LastOfferCount);
        Assert.False(coin.TraderSellsForCash);
        Assert.Null(await source.GetAsync("not-an-item", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APayloadWithoutRatesLeavesTheLastOnesAndNoRatesReadAsNone()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var refresh = new SqliteDataRefreshRepository(database.Factory);
        var source = new SqliteItemMarketFactSource(database.Factory);
        Assert.Null(await source.GetFleaRatesAsync(TestContext.Current.CancellationToken));

        await refresh.RefreshItemsAsync(JsonSerializer.Deserialize<TarkovDevItemsData>(Payload, options)!, Observed, TestContext.Current.CancellationToken);
        var without = Payload[..Payload.LastIndexOf(",\n          \"fleaMarket\"", StringComparison.Ordinal)] + "}";
        await refresh.RefreshItemsAsync(JsonSerializer.Deserialize<TarkovDevItemsData>(without, options)!, Observed.AddHours(9), TestContext.Current.CancellationToken);

        var rates = await source.GetFleaRatesAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(rates);
        Assert.Equal(Observed, rates.ObservedUtc);
    }
}
