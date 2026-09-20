using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Profile;

namespace TarkovCompanion.UnitTests.Planning;

/// <summary>
/// The Keep list's computation on a fixed snapshot: which items, under which group, for how many.
/// The view model's tests cover the same behaviour through the screen; these pin the planner itself.
/// </summary>
public sealed class KeepListPlannerTests
{
    [Fact]
    public async Task Items_sort_into_the_first_reason_that_applies_and_the_groups_come_out_in_order()
    {
        var inputs = Inputs(
            Profile(),
            quests:
            [
                new("t-active", "o1", "item-active", 1, false),
                new("t-ahead", "o2", "item-ahead", 1, false),
            ],
            hideout: [new("workbench", 2, "item-bench", 1)],
            tracked: ["t-active"]);

        var plan = await KeepListPlanner.PlanAsync(inputs, Resolve(("item-value", "S")), CancellationToken.None);

        Assert.True(plan.HasData);
        Assert.Equal(
            [
                ("item-active", KeepGroupKind.ActiveQuest),
                ("item-ahead", KeepGroupKind.Quest),
                ("item-bench", KeepGroupKind.Hideout),
            ],
            plan.Entries.Select(entry => (entry.ItemId, entry.Group)));
    }

    [Fact]
    public async Task Recorded_progress_and_a_completed_quest_take_an_item_off_the_list()
    {
        var inputs = Inputs(
            Profile(progress: new Dictionary<string, int> { ["o-done"] = 2, ["o-part"] = 1 }, completed: ["t-completed"]),
            quests:
            [
                new("t-done", "o-done", "item-a", 2, false),
                new("t-part", "o-part", "item-b", 3, false),
                new("t-completed", "o-c", "item-c", 1, false),
            ]);

        var plan = await KeepListPlanner.PlanAsync(inputs, Resolve(), CancellationToken.None);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal("item-b", entry.ItemId);
        Assert.Equal(2, Assert.Single(entry.QuestNeeds).Remaining);
    }

    [Fact]
    public async Task Hideout_stock_is_taken_off_the_sum_across_stations_not_off_each_station()
    {
        var requirements = new HideoutItemRequirement[]
        {
            new("bitcoin-farm", 2, "item-battery", 2),
            new("workbench", 2, "item-battery", 1),
        };
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["bitcoin-farm"] = 1, ["workbench"] = 1 };

        var enough = await KeepListPlanner.PlanAsync(
            Inputs(Profile(levels: levels, owned: new Dictionary<string, int> { ["item-battery"] = 3 }), hideout: requirements),
            Resolve(),
            CancellationToken.None);
        var short2 = await KeepListPlanner.PlanAsync(
            Inputs(Profile(levels: levels, owned: new Dictionary<string, int> { ["item-battery"] = 2 }), hideout: requirements),
            Resolve(),
            CancellationToken.None);

        Assert.Empty(enough.Entries);
        var entry = Assert.Single(short2.Entries);
        Assert.Equal(["Bitcoin Farm", "Workbench"], entry.HideoutNeeds.Select(need => need.StationName));
        Assert.Equal([2, 1], entry.HideoutNeeds.Select(need => need.Required));
    }

    [Fact]
    public async Task A_high_value_item_nothing_needs_is_listed_alone_and_a_needed_one_keeps_its_group()
    {
        var inputs = Inputs(
            Profile(),
            quests: [new("t", "o", "item-gpu", 1, false)],
            keys: [Key("item-lonely")]);

        var plan = await KeepListPlanner.PlanAsync(
            inputs,
            Resolve(("item-gpu", "S"), ("item-lonely", "A")),
            CancellationToken.None);

        Assert.Equal(KeepGroupKind.Quest, plan.Entries.Single(entry => entry.ItemId == "item-gpu").Group);
        Assert.True(plan.Entries.Single(entry => entry.ItemId == "item-gpu").IsHighValue);
        Assert.Equal(KeepGroupKind.HighValue, plan.Entries.Single(entry => entry.ItemId == "item-lonely").Group);
    }

    [Fact]
    public async Task The_same_snapshot_gives_the_same_list_whatever_order_the_requirements_arrive_in()
    {
        QuestItemRequirement[] quests =
        [
            new("t1", "o1", "item-b", 2, false),
            new("t2", "o2", "item-a", 1, false),
            new("t1", "o3", "item-a", 1, false),
        ];

        var forward = await KeepListPlanner.PlanAsync(Inputs(Profile(), quests: quests), Resolve(), CancellationToken.None);
        var backward = await KeepListPlanner.PlanAsync(Inputs(Profile(), quests: quests.Reverse().ToArray()), Resolve(), CancellationToken.None);

        Assert.Equal(forward.Entries.Select(entry => entry.ItemId), backward.Entries.Select(entry => entry.ItemId));
        Assert.Equal(
            forward.Entries.SelectMany(entry => entry.QuestNeeds.Select(need => (entry.ItemId, need.TaskId, need.Remaining))),
            backward.Entries.SelectMany(entry => entry.QuestNeeds.Select(need => (entry.ItemId, need.TaskId, need.Remaining))));
    }

    [Fact]
    public async Task Synced_data_with_nothing_to_keep_is_an_empty_list_that_still_has_data()
    {
        var plan = await KeepListPlanner.PlanAsync(Inputs(Profile()), Resolve(), CancellationToken.None);

        Assert.True(plan.HasData);
        Assert.Empty(plan.Entries);
        Assert.False(KeepPlan.NoData.HasData);
    }

    [Fact]
    public async Task Quest_need_is_split_into_what_must_be_found_in_raid_and_the_rest_per_quest()
    {
        var inputs = Inputs(
            Profile(),
            quests:
            [
                new("t1", "o1", "item-a", 3, true),
                new("t1", "o2", "item-a", 2, false),
                new("t2", "o3", "item-a", 4, false),
            ]);

        var plan = await KeepListPlanner.PlanAsync(inputs, Resolve(), CancellationToken.None);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(
            [("t1", 5, 3, 2), ("t2", 4, 0, 4)],
            entry.QuestNeeds.Select(need => (need.TaskId, need.Remaining, need.FoundInRaid, need.NotFoundInRaid)));
        Assert.Equal(9, entry.QuestRemaining);
        Assert.Equal(3, entry.QuestFoundInRaid);
    }

    [Fact]
    public async Task Progress_recorded_against_a_found_in_raid_objective_comes_off_the_found_in_raid_part()
    {
        var inputs = Inputs(
            Profile(progress: new Dictionary<string, int> { ["o1"] = 2 }),
            quests:
            [
                new("t1", "o1", "item-a", 3, true),
                new("t1", "o2", "item-a", 2, false),
            ]);

        var plan = await KeepListPlanner.PlanAsync(inputs, Resolve(), CancellationToken.None);

        var need = Assert.Single(Assert.Single(plan.Entries).QuestNeeds);
        Assert.Equal((3, 1), (need.Remaining, need.FoundInRaid));
    }

    [Fact]
    public async Task The_hideout_total_counts_every_level_and_the_remaining_only_those_above_the_built_one()
    {
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["workbench"] = 1 };
        var inputs = Inputs(
            Profile(levels: levels),
            hideout:
            [
                new("workbench", 1, "item-bolts", 2),
                new("workbench", 2, "item-bolts", 3),
                new("workbench", 3, "item-bolts", 4),
            ]);

        var plan = await KeepListPlanner.PlanAsync(inputs, Resolve(), CancellationToken.None);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(7, entry.HideoutRemaining);
        Assert.Equal(9, entry.HideoutTotalBuild);
    }

    [Fact]
    public async Task An_item_the_hideout_no_longer_needs_carries_no_hideout_total()
    {
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["workbench"] = 3 };
        var inputs = Inputs(
            Profile(levels: levels),
            quests: [new("t", "o", "item-bolts", 1, false)],
            hideout: [new("workbench", 2, "item-bolts", 3)]);

        var plan = await KeepListPlanner.PlanAsync(inputs, Resolve(), CancellationToken.None);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(0, entry.HideoutRemaining);
        Assert.Equal(0, entry.HideoutTotalBuild);
    }

    private static Func<string, CancellationToken, Task<KeepItemFacts>> Resolve(params (string ItemId, string Tier)[] tiers)
    {
        var byId = tiers.ToDictionary(entry => entry.ItemId, entry => entry.Tier, StringComparer.Ordinal);
        return (itemId, _) => Task.FromResult(new KeepItemFacts(itemId, byId.GetValueOrDefault(itemId, "—")));
    }

    private static KeyFacts Key(string itemId) =>
        new(itemId, null, null, [], [], 5_000, 0, 0, false, 0, new DataProvenance("fixture", DateTimeOffset.UnixEpoch));

    private static KeepListInputs Inputs(
        PlayerProfile profile,
        IReadOnlyList<QuestItemRequirement>? quests = null,
        IReadOnlyList<HideoutItemRequirement>? hideout = null,
        IReadOnlyList<string>? tracked = null,
        IReadOnlyList<KeyFacts>? keys = null) => new(
        profile,
        quests ?? [],
        hideout ?? [],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["t"] = "Task",
            ["t1"] = "Alpha Task",
            ["t2"] = "Beta Task",
        },
        (tracked ?? []).ToHashSet(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bitcoin-farm"] = "Bitcoin Farm",
            ["workbench"] = "Workbench",
        },
        keys ?? []);

    private static PlayerProfile Profile(
        IReadOnlyDictionary<string, int>? progress = null,
        IReadOnlyDictionary<string, int>? levels = null,
        IReadOnlyDictionary<string, int>? owned = null,
        IReadOnlyList<string>? completed = null) => new(
        Guid.NewGuid(),
        "Tester",
        GameMode.Regular,
        Level: 10,
        Faction.Usec,
        Edition: null,
        TraderLevels: new Dictionary<string, int>(StringComparer.Ordinal),
        CompletedTaskIds: (completed ?? []).ToHashSet(StringComparer.Ordinal),
        ObjectiveProgress: progress ?? new Dictionary<string, int>(StringComparer.Ordinal),
        HideoutStationLevels: levels ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        WishlistItemIds: new HashSet<string>(StringComparer.Ordinal),
        OwnedItemCounts: owned ?? new Dictionary<string, int>(StringComparer.Ordinal),
        EventItemStates: new Dictionary<string, EventItemState>(StringComparer.Ordinal),
        ItemOverrides: new Dictionary<string, string>(StringComparer.Ordinal),
        UpdatedUtc: DateTimeOffset.UnixEpoch);
}
