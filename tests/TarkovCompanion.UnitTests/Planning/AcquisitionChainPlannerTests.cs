using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.UnitTests.Planning;

/// <summary>Recursive acquisition choices using item identities measured in the 2026-09-14 seed catalog.</summary>
public sealed class AcquisitionChainPlannerTests
{
    // Real seed items: Salewa, AI-2, Toilet paper, and the MCC craft whose output is also its
    // input. The test graph is deliberately small; the production service supplies all 214
    // crafts and 789 barters from the same normalized shapes.
    private const string Salewa = "544fb45d4bdc2dee738b4568";
    private const string Ai2 = "5755356824597772cb798962";
    private const string ToiletPaper = "5e2af4d286f7746d4159f07a";
    private const string Mcc = "62e910aaf957f2915e0a5e36";

    [Fact]
    public void Recurses_through_the_cheapest_inputs_and_counts_craft_overhead()
    {
        var plan = AcquisitionChainPlanner.Plan(
            Salewa,
            1,
            Items(
                Item(Salewa, "Salewa first aid kit", 42_000, 35_000),
                Item(Ai2, "AI-2 medkit", 12_000, 10_000),
                Item(ToiletPaper, "Toilet paper", 9_000, 8_000)),
            [
                Recipe("ai2-from-paper", AcquisitionChainMethod.Barter, Ai2, 1, [(ToiletPaper, 1, false)]),
                Recipe("salewa-from-ai2", AcquisitionChainMethod.Craft, Salewa, 1, [(Ai2, 2, false)], fuel: 1_500, time: 2_500),
            ]);

        Assert.NotNull(plan.Cheapest);
        Assert.Equal(AcquisitionChainMethod.Craft, plan.Cheapest.Method);
        Assert.Equal(24_000, plan.Cheapest.TotalRoubles);
        var ai2 = Assert.Single(plan.Cheapest.Inputs);
        Assert.Equal(AcquisitionChainMethod.Barter, ai2.Method);
        Assert.Equal(18_000, ai2.TotalRoubles);
        Assert.Equal(2_000, plan.Cheapest.InputOpportunityCostRoubles);
        Assert.All(ai2.Inputs, paper => Assert.Equal(AcquisitionChainMethod.Buy, paper.Method));
    }

    [Fact]
    public void Net_resale_opportunity_prevents_a_cheap_input_from_being_counted_below_what_using_it_gives_up()
    {
        var plan = AcquisitionChainPlanner.Plan(
            Salewa,
            1,
            Items(
                Item(Salewa, "Salewa first aid kit", 50_000, 40_000),
                Item(Ai2, "AI-2 medkit", 5_000, 18_000)),
            [Recipe("salewa", AcquisitionChainMethod.Craft, Salewa, 1, [(Ai2, 2, false)])]);

        Assert.Equal(36_000, plan.Cheapest!.TotalRoubles);
        Assert.Equal(26_000, plan.Cheapest.InputOpportunityCostRoubles);
    }

    [Fact]
    public void A_real_catalog_self_cycle_is_skipped_without_hiding_the_buy_route()
    {
        var plan = AcquisitionChainPlanner.Plan(
            Mcc,
            1,
            Items(Item(Mcc, "Microcontroller board", 120_000, 90_000)),
            [Recipe("6399c421d65735732c6ba765", AcquisitionChainMethod.Craft, Mcc, 1, [(Mcc, 1, false)])]);

        Assert.True(plan.CycleSkipped);
        Assert.Equal(AcquisitionChainMethod.Buy, plan.Cheapest!.Method);
        Assert.Equal(120_000, plan.Cheapest.TotalRoubles);
    }

    [Fact]
    public void A_cycle_with_no_direct_price_is_unknown_not_zero()
    {
        var plan = AcquisitionChainPlanner.Plan(
            Mcc,
            1,
            Items(Item(Mcc, "Microcontroller board", null, 90_000)),
            [Recipe("cycle", AcquisitionChainMethod.Craft, Mcc, 1, [(Mcc, 1, false)])]);

        Assert.True(plan.CycleSkipped);
        Assert.Null(plan.Cheapest);
    }

    [Fact]
    public void Depth_limit_keeps_the_known_buy_fallback()
    {
        var plan = AcquisitionChainPlanner.Plan(
            Salewa,
            1,
            Items(
                Item(Salewa, "Salewa first aid kit", 42_000, 35_000),
                Item(Ai2, "AI-2 medkit", 12_000, 10_000),
                Item(ToiletPaper, "Toilet paper", 9_000, 8_000)),
            [
                Recipe("salewa", AcquisitionChainMethod.Craft, Salewa, 1, [(Ai2, 1, false)]),
                Recipe("ai2", AcquisitionChainMethod.Barter, Ai2, 1, [(ToiletPaper, 1, false)]),
            ],
            maximumDepth: 1);

        Assert.True(plan.DepthLimitReached);
        Assert.Equal(AcquisitionChainMethod.Buy, Assert.Single(plan.Cheapest!.Inputs).Method);
    }

    [Fact]
    public void Reusable_tools_are_bought_once_when_several_craft_runs_are_needed()
    {
        var plan = AcquisitionChainPlanner.Plan(
            Salewa,
            3,
            Items(
                Item(Salewa, "Salewa first aid kit", 100_000, 90_000),
                Item(Ai2, "AI-2 medkit", 10_000, 9_000),
                Item(ToiletPaper, "Toilet paper", 8_000, 7_000)),
            [Recipe("salewa", AcquisitionChainMethod.Craft, Salewa, 1, [(Ai2, 1, false), (ToiletPaper, 1, true)])]);

        Assert.Equal([3, 1], plan.Cheapest!.Inputs.Select(input => input.Quantity));
        Assert.Equal(38_000, plan.Cheapest.TotalRoubles);
    }

    private static Dictionary<string, AcquisitionChainItem> Items(params AcquisitionChainItem[] items) =>
        items.ToDictionary(item => item.ItemId, StringComparer.Ordinal);

    private static AcquisitionChainItem Item(string id, string name, long? buy, long? opportunity) =>
        new(id, name, buy, buy is null ? null : "Flea", opportunity, new DateTimeOffset(2026, 9, 14, 23, 38, 0, TimeSpan.Zero));

    private static AcquisitionChainRecipe Recipe(
        string id,
        AcquisitionChainMethod method,
        string output,
        int outputCount,
        (string ItemId, int Count, bool Reusable)[] inputs,
        long fuel = 0,
        long time = 0) =>
        new(
            id,
            method,
            output,
            outputCount,
            [.. inputs.Select(input => new AcquisitionChainIngredient(input.ItemId, input.Count, input.Reusable))],
            method == AcquisitionChainMethod.Craft ? "Medstation" : "Therapist",
            method == AcquisitionChainMethod.Craft ? TimeSpan.FromMinutes(30) : null,
            fuel,
            time);
}
