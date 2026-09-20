using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Application.Services.Profile;

namespace TarkovCompanion.UnitTests.Planning;

/// <summary>What the next level of each station asks for, what can be started now, and what is short overall.</summary>
public sealed class HideoutPlannerTests
{
    private static readonly HideoutStationSummary Farm = new("bitcoin-farm", "Bitcoin Farm", [1, 2, 3]);
    private static readonly HideoutStationSummary Bench = new("workbench", "Workbench", [1, 2]);

    [Fact]
    public void The_next_level_is_the_lowest_above_the_built_one_and_only_its_items_are_needed()
    {
        var plans = HideoutPlanner.Plan(
            [Farm],
            Levels(("bitcoin-farm", 1)),
            [
                new("bitcoin-farm", 1, "item-old", 1),
                new("bitcoin-farm", 2, "item-battery", 3),
                new("bitcoin-farm", 3, "item-later", 9),
            ],
            new Dictionary<string, int> { ["item-battery"] = 1 });

        var plan = Assert.Single(plans);
        Assert.Equal(1, plan.BuiltLevel);
        Assert.Equal(3, plan.MaximumLevel);
        Assert.Equal(2, plan.NextLevel);
        var need = Assert.Single(plan.NextLevelNeeds);
        Assert.Equal(("item-battery", 3, 1), (need.ItemId, need.Required, need.Owned));
        Assert.Equal(2, need.Remaining);
        Assert.False(plan.CanBuildNow);
        Assert.Equal(1, plan.MissingItemCount);
    }

    [Fact]
    public void A_station_can_be_built_now_only_when_every_item_of_its_next_level_is_held()
    {
        var requirements = new HideoutItemRequirement[]
        {
            new("workbench", 1, "item-a", 2),
            new("workbench", 1, "item-b", 1),
        };

        var short1 = Assert.Single(HideoutPlanner.Plan([Bench], Levels(), requirements, new Dictionary<string, int> { ["item-a"] = 2 }));
        var enough = Assert.Single(HideoutPlanner.Plan([Bench], Levels(), requirements, new Dictionary<string, int> { ["item-a"] = 5, ["item-b"] = 1 }));

        Assert.False(short1.CanBuildNow);
        Assert.Equal(1, short1.MissingItemCount);
        Assert.True(enough.CanBuildNow);
    }

    [Fact]
    public void A_fully_built_station_has_no_next_level_and_cannot_be_built()
    {
        var plan = Assert.Single(HideoutPlanner.Plan(
            [Bench],
            Levels(("workbench", 2)),
            [new("workbench", 2, "item-a", 1)],
            new Dictionary<string, int> { ["item-a"] = 9 }));

        Assert.False(plan.HasNextLevel);
        Assert.Equal(0, plan.NextLevel);
        Assert.Empty(plan.NextLevelNeeds);
        Assert.False(plan.CanBuildNow);
    }

    [Fact]
    public void A_station_that_needs_nothing_is_ready_and_a_station_with_no_levels_is_not_a_station_to_build()
    {
        var levels = Assert.Single(HideoutPlanner.Plan([Bench], Levels(), [], new Dictionary<string, int>()));
        var none = Assert.Single(HideoutPlanner.Plan([new("ghost", "Ghost", [])], Levels(), [], new Dictionary<string, int>()));

        Assert.True(levels.CanBuildNow);
        Assert.False(none.HasNextLevel);
        Assert.Equal(0, none.MaximumLevel);
    }

    [Fact]
    public void The_shortfall_sums_an_item_across_stations_and_takes_the_holding_off_once()
    {
        var owned = new Dictionary<string, int> { ["item-battery"] = 2, ["item-bolts"] = 5 };
        var plans = HideoutPlanner.Plan(
            [Farm, Bench],
            Levels(("bitcoin-farm", 1), ("workbench", 1)),
            [
                new("bitcoin-farm", 2, "item-battery", 2),
                new("workbench", 2, "item-battery", 1),
                new("workbench", 2, "item-bolts", 5),
                new("bitcoin-farm", 3, "item-later", 9),
            ],
            owned);

        var shortfall = Assert.Single(HideoutPlanner.Shortfall(plans, owned));

        // Two batteries serve one station or the other, not both: 3 needed, 2 held. The bolts are
        // covered and the level-3 item belongs to a level that is not next.
        Assert.Equal(("item-battery", 3, 2), (shortfall.ItemId, shortfall.Need, shortfall.Have));
        Assert.Equal(1, shortfall.Remaining);
    }

    [Fact]
    public void The_shortfall_is_ordered_by_what_is_most_missing_then_by_id_whatever_the_input_order()
    {
        var owned = new Dictionary<string, int>();
        var requirements = new HideoutItemRequirement[]
        {
            new("workbench", 1, "item-b", 2),
            new("workbench", 1, "item-c", 9),
            new("workbench", 1, "item-a", 2),
        };

        var forward = HideoutPlanner.Shortfall(HideoutPlanner.Plan([Bench], Levels(), requirements, owned), owned);
        var backward = HideoutPlanner.Shortfall(HideoutPlanner.Plan([Bench], Levels(), requirements.Reverse(), owned), owned);

        Assert.Equal(["item-c", "item-a", "item-b"], forward.Select(row => row.ItemId));
        Assert.Equal(forward, backward);
    }

    private static Dictionary<string, int> Levels(params (string Station, int Level)[] built) =>
        built.ToDictionary(entry => entry.Station, entry => entry.Level, StringComparer.OrdinalIgnoreCase);
}
