using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.UnitTests.V2Capture;

public sealed class FleaCurrencyRateTests
{
    [Fact]
    public void CatalogCurrencyUsesItsRoublePurchaseOffer()
    {
        const string json = """
            {
              "basePrice": 140,
              "buyFromTrader": [
                { "price": 222, "priceRUB": 222, "currency": "RUB" }
              ]
            }
            """;

        Assert.Equal(222, SqliteItemMarketFactSource.ReadRoublePurchaseRate(json));
    }

    [Theory]
    [InlineData("{\"basePrice\":140}")]
    [InlineData("{\"buyFromTrader\":[{\"priceRUB\":1,\"currency\":\"EUR\"}]}")]
    [InlineData("not-json")]
    public void MissingOrNonRoublePurchaseOfferIsNotGuessed(string json) =>
        Assert.Null(SqliteItemMarketFactSource.ReadRoublePurchaseRate(json));
}
