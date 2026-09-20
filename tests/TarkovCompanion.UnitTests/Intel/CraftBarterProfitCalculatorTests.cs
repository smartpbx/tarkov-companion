using TarkovCompanion.Application.Services.Intel;

namespace TarkovCompanion.UnitTests.Intel;

/// <summary>
/// The Crafts &amp; barters tab's profit maths (#287): inputs at their cheapest known buy price
/// against an output at its best sell price, flea fee included when selling on the flea.
/// </summary>
public sealed class CraftBarterProfitCalculatorTests
{
    [Fact]
    public void A_craft_with_two_inputs_sums_both_at_their_buy_price()
    {
        var cost = CraftBarterProfitCalculator.InputCost(
        [
            new(Count: 2, UnitPriceRoubles: 1_000),
            new(Count: 1, UnitPriceRoubles: 5_000),
        ]);

        Assert.Equal(7_000, cost);
    }

    [Fact]
    public void A_barter_whose_output_sells_better_to_a_trader_than_on_the_flea_prices_the_trader_side()
    {
        var value = CraftBarterProfitCalculator.OutputValue(
            count: 1,
            fleaPriceRoubles: 10_000,
            fleaFeeRoubles: 1_500,
            bestTraderRoubles: 12_000);

        // Net flea (10,000 - 1,500 = 8,500) loses to the trader's 12,000.
        Assert.Equal(12_000, value);
    }

    [Fact]
    public void Selling_on_the_flea_nets_the_listing_fee_out_first()
    {
        var value = CraftBarterProfitCalculator.OutputValue(
            count: 1,
            fleaPriceRoubles: 10_000,
            fleaFeeRoubles: 1_500,
            bestTraderRoubles: 6_000);

        Assert.Equal(8_500, value);
    }

    [Fact]
    public void An_unknown_flea_fee_falls_back_to_the_gross_flea_price_rather_than_zero()
    {
        var value = CraftBarterProfitCalculator.OutputValue(
            count: 1,
            fleaPriceRoubles: 10_000,
            fleaFeeRoubles: null,
            bestTraderRoubles: 6_000);

        Assert.Equal(10_000, value);
    }

    [Fact]
    public void A_missing_input_price_gives_unknown_cost_not_zero()
    {
        var cost = CraftBarterProfitCalculator.InputCost(
        [
            new(Count: 1, UnitPriceRoubles: 1_000),
            new(Count: 1, UnitPriceRoubles: null),
        ]);

        Assert.Null(cost);
    }

    [Fact]
    public void A_missing_output_price_gives_unknown_value_not_zero()
    {
        var value = CraftBarterProfitCalculator.OutputValue(1, null, null, null);

        Assert.Null(value);
    }

    [Fact]
    public void Profit_is_output_value_minus_input_cost()
    {
        Assert.Equal(3_000, CraftBarterProfitCalculator.Profit(7_000, 10_000));
    }

    [Fact]
    public void Profit_is_unknown_rather_than_zero_when_either_half_is_unknown()
    {
        Assert.Null(CraftBarterProfitCalculator.Profit(null, 10_000));
        Assert.Null(CraftBarterProfitCalculator.Profit(7_000, null));
        Assert.Null(CraftBarterProfitCalculator.Profit(null, null));
    }

    [Fact]
    public void An_output_of_more_than_one_multiplies_the_per_item_value()
    {
        var value = CraftBarterProfitCalculator.OutputValue(
            count: 3,
            fleaPriceRoubles: 1_000,
            fleaFeeRoubles: 100,
            bestTraderRoubles: null);

        Assert.Equal(2_700, value);
    }

    [Fact]
    public void A_zero_or_negative_count_is_treated_as_one()
    {
        Assert.Equal(1_000, CraftBarterProfitCalculator.OutputValue(0, 1_000, 0, null));
        Assert.Equal(1_000, CraftBarterProfitCalculator.OutputValue(-4, 1_000, 0, null));
        Assert.Equal(
            1_000,
            CraftBarterProfitCalculator.InputCost([new IntelTradeInput(Count: 0, UnitPriceRoubles: 1_000)]));
    }
}
