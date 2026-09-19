using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.UnitTests.V2Capture;

public sealed class FleaMarketFeeTests
{
    private static readonly FleaMarketRates Rates = new(0.05, 0.05, DateTimeOffset.UnixEpoch);

    [Fact]
    public void AskingExactlyTheBasePriceCostsTheBasePriceTimesBothRates()
    {
        // The one point on the curve that needs no trust in it: both exponents are zero.
        Assert.Equal(10_000, FleaMarketFee.Calculate(100_000, 100_000, 1, Rates));
    }

    [Theory]
    [InlineData(100_000, 134_497, 1, 12_007)]
    [InlineData(100_000, 50_000, 1, 8_952)]
    [InlineData(20_000, 35_000, 3, 9_235)]
    public void TheDocumentedFormulaIsReproduced(long basePrice, long asking, int quantity, long expected) =>
        Assert.Equal(expected, FleaMarketFee.Calculate(basePrice, asking, quantity, Rates));

    [Fact]
    public void TheRatesAreTheOnesGivenAndNotAConstant() =>
        Assert.Equal(7_204, FleaMarketFee.Calculate(100_000, 134_497, 1, new(0.03, 0.03, DateTimeOffset.UnixEpoch)));

    [Fact]
    public void AskingMoreCostsMoreThanProportionally()
    {
        var atBase = FleaMarketFee.Calculate(100_000, 100_000, 1, Rates);
        var doubled = FleaMarketFee.Calculate(100_000, 200_000, 1, Rates);
        Assert.True(doubled > 2 * atBase - (atBase / 2));
        Assert.True(doubled > atBase);
    }

    [Fact]
    public void AnItemPinIsReadWrittenAndRemovedWithoutDisturbingQuestPins()
    {
        var progress = new ProfileProgress(10, pins: [new("task", "gunsmith-1", 0, null)]);

        var pinned = LootScanProfileRules.WithPin(progress, "gpu", true);
        var unpinned = LootScanProfileRules.WithPin(pinned, "gpu", false);

        Assert.False(LootScanProfileRules.IsPinned(progress, "gpu"));
        Assert.True(LootScanProfileRules.IsPinned(pinned, "gpu"));
        Assert.False(LootScanProfileRules.IsPinned(pinned, "gunsmith-1"));
        Assert.Equal(2, pinned.Pins.Count);
        Assert.Equal("task", Assert.Single(unpinned.Pins).TargetKind);
    }

    [Theory]
    [InlineData("Take", LootScanItemRule.AlwaysTake)]
    [InlineData("EssentialKeep", LootScanItemRule.AlwaysTake)]
    [InlineData("keep", LootScanItemRule.AlwaysTake)]
    [InlineData("Leave", LootScanItemRule.AlwaysLeave)]
    [InlineData("DropFirst", LootScanItemRule.AlwaysLeave)]
    [InlineData("SellFlea", LootScanItemRule.None)]
    [InlineData("Protected", LootScanItemRule.None)]
    [InlineData("MeltForScrap", LootScanItemRule.Unrecognised)]
    public void AnItemRuleReadsAsWhatItMeansForPickingTheItemUp(string text, LootScanItemRule expected)
    {
        var progress = LootScanProfileRules.WithRule(new ProfileProgress(10), "gpu", text);

        Assert.Equal(expected, LootScanProfileRules.RuleFor(progress, "gpu"));
        Assert.Equal(text == "Protected", LootScanProfileRules.IsProtected(progress, "gpu"));
    }
}
