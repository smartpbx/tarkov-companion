using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.UnitTests.Intel;

/// <summary>The trader loyalty and quest gates retained on real catalog offer shapes.</summary>
public sealed class ItemObtainabilityRuleTests
{
    private const string M4 = "5447a9cd4bdc2dbd208b4567";
    private const string Mechanic = "5a7c2eca46aef81a7ca2145d";

    [Fact]
    public void SeedM4CashOfferIsLockedBelowMechanicLoyaltyThree()
    {
        var parsed = Assert.Single(SqliteItemCashOfferCatalog.ReadOffers(M4, """
            { "buyFromTrader": [
              { "trader": "5a7c2eca46aef81a7ca2145d", "priceRUB": 22997, "minTraderLevel": 3, "taskUnlock": null }
            ] }
            """));
        var offer = Offer(parsed, "Mechanic");

        var result = ItemObtainabilityRule.Evaluate(offer, Profile(level: 2));

        Assert.False(result.IsObtainable);
        Assert.Equal("LL3 Mechanic", result.RequirementLabel);
    }

    [Fact]
    public void ObjectTaskUnlockUsesItsIdAndNamesTheQuest()
    {
        var parsed = Assert.Single(SqliteItemCashOfferCatalog.ReadOffers("5d235b4d86f7742e017bc88a", """
            { "buyFromTrader": [
              { "trader": "5ac3b934156ae10c4430e83c", "price": 1, "currency": "RUB",
                "taskUnlock": { "id": "a-task" } }
            ] }
            """));
        var offer = Offer(parsed, "Ragman") with { TaskUnlockName = "The Stylish One" };

        var result = ItemObtainabilityRule.Evaluate(offer, Profile(completed: new HashSet<string>(StringComparer.Ordinal)));

        Assert.False(result.IsObtainable);
        Assert.Equal("after quest The Stylish One", result.RequirementLabel);
        Assert.Equal(1, parsed.PriceRoubles);
    }

    [Fact]
    public void MeetingBothCatalogRequirementsMakesTheSourceAvailableNow()
    {
        var offer = new ItemAcquisitionOffer(
            M4,
            ItemAcquisitionKind.Cash,
            Mechanic,
            "Mechanic",
            3,
            "gunsmith-1",
            "Gunsmith Part 1",
            22_997,
            []);

        var result = ItemObtainabilityRule.Evaluate(
            offer,
            Profile(3, new HashSet<string>(["gunsmith-1"], StringComparer.Ordinal)));

        Assert.True(result.IsObtainable);
        Assert.Equal("Available now", result.RequirementLabel);
    }

    private static ItemAcquisitionOffer Offer(ItemCashOffer cash, string traderName) => new(
        cash.ItemId,
        ItemAcquisitionKind.Cash,
        cash.TraderId,
        traderName,
        cash.MinimumTraderLevel,
        cash.TaskUnlockId,
        cash.TaskUnlockId,
        cash.PriceRoubles,
        []);

    private static PlayerProfile Profile(int level = 0, IReadOnlySet<string>? completed = null) => new(
        Guid.Parse("32ff7f9f-52c9-4bd7-ae82-8e9912bb20af"),
        "test",
        GameMode.Regular,
        30,
        Faction.Usec,
        null,
        new Dictionary<string, int>(StringComparer.Ordinal) { [Mechanic] = level },
        completed ?? new HashSet<string>(StringComparer.Ordinal),
        new Dictionary<string, int>(StringComparer.Ordinal),
        new Dictionary<string, int>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new Dictionary<string, int>(StringComparer.Ordinal),
        new Dictionary<string, EventItemState>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        DateTimeOffset.UnixEpoch);
}
