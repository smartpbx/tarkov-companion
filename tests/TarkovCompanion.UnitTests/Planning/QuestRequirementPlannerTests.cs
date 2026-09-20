using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests.Planning;

/// <summary>
/// The arithmetic behind "what to bring and hand in", tested without a view model or an item
/// catalog: it answers in item ids, and naming them is the caller's job.
/// </summary>
public sealed class QuestRequirementPlannerTests
{
    private static readonly IReadOnlyDictionary<string, int> Nothing = new Dictionary<string, int>();

    [Fact]
    public void Bring_hand_in_and_find_in_raid_of_one_item_stay_three_requirements()
    {
        var requirements = QuestRequirementPlanner.Build(
            [
                Objective("carry", fir: null, target: null, items: [Target("item-a", "requiredKeys", 0, 0)]),
                Objective("give", fir: false, target: 2, items: [Target("item-a", "items", 0, 0)]),
                Objective("find", fir: true, target: 3, items: [Target("item-a", "items", 0, 0)]),
            ],
            Nothing);

        Assert.Equal(
            [RequirementHandling.Bring, RequirementHandling.HandIn, RequirementHandling.FindInRaid],
            requirements.Select(row => row.Handling).OrderBy(handling => handling));
        Assert.Equal(3, requirements.Count);
        Assert.Equal(1, requirements.Single(row => row.Handling == RequirementHandling.Bring).Need);
        Assert.Equal(2, requirements.Single(row => row.Handling == RequirementHandling.HandIn).Need);
        Assert.Equal(3, requirements.Single(row => row.Handling == RequirementHandling.FindInRaid).Need);
    }

    [Fact]
    public void One_item_asked_for_by_two_objectives_is_one_pile_but_a_carried_item_is_still_one()
    {
        var requirements = QuestRequirementPlanner.Build(
            [
                Objective("first", fir: false, target: 2, items: [Target("item-bolts", "items", 0, 0)]),
                Objective("second", fir: false, target: 3, items: [Target("item-bolts", "items", 0, 0)]),
                Objective("k1", fir: null, target: null, items: [Target("item-key", "requiredKeys", 0, 0)]),
                Objective("k2", fir: null, target: null, items: [Target("item-key", "requiredKeys", 0, 0)]),
            ],
            new Dictionary<string, int> { ["item-bolts"] = 4 });

        var bolts = requirements.Single(row => row.PrimaryItemId == "item-bolts");
        Assert.Equal(5, bolts.Need);
        Assert.Equal(4, bolts.Have);
        Assert.Equal(1, bolts.Remaining);
        Assert.Equal(1, requirements.Single(row => row.PrimaryItemId == "item-key").Need);
    }

    [Fact]
    public void Alternatives_are_one_requirement_met_by_whatever_the_player_holds_of_them()
    {
        var requirements = QuestRequirementPlanner.Build(
            [
                Objective(
                    "either",
                    fir: false,
                    target: 1,
                    items: [Target("item-b", "items", 0, 1), Target("item-a", "items", 0, 0)]),
            ],
            new Dictionary<string, int> { ["item-b"] = 1 });

        var row = Assert.Single(requirements);
        Assert.Equal(["item-a", "item-b"], row.ItemIds);
        Assert.Equal(1, row.AlternativeCount);
        Assert.True(row.IsSatisfied);
    }

    [Fact]
    public void Finished_objectives_and_conditions_ask_for_nothing()
    {
        var requirements = QuestRequirementPlanner.Build(
            [
                Objective("done", fir: false, target: 1, done: true, items: [Target("item-a", "items", 0, 0)]),
                Objective("cond", fir: null, target: null, items: [Target("item-b", "notWearing", 0, 0)]),
            ],
            Nothing);

        Assert.Empty(requirements);
    }

    [Fact]
    public void An_item_that_exists_only_inside_its_quest_is_not_something_to_have()
    {
        // The journal is picked up by one objective and handed over by the next. Neither is a
        // thing to buy or bring, and the real one beside it still is.
        var requirements = QuestRequirementPlanner.Build(
            [
                Objective("obtain", fir: null, target: 1, items: [Target("journal", "questItem", 0, 0)]),
                Objective("hand-over", fir: false, target: 1, items: [Target("journal", "questItem", 0, 0)]),
                Objective("real", fir: false, target: 2, items: [Target("item-a", "items", 0, 0)]),
            ],
            Nothing);

        Assert.Equal("item-a", Assert.Single(requirements).PrimaryItemId);
    }

    [Fact]
    public void The_order_does_not_depend_on_the_order_the_objectives_arrive_in()
    {
        QuestObjectiveReadModel[] objectives =
        [
            Objective("z", fir: false, target: 1, items: [Target("item-z", "items", 0, 0)]),
            Objective("a", fir: false, target: 1, items: [Target("item-a", "items", 0, 0)]),
            Objective("m", fir: false, target: 1, items: [Target("item-m", "items", 0, 0)]),
        ];
        var owned = new Dictionary<string, int> { ["item-a"] = 1 };

        var forward = QuestRequirementPlanner.Build(objectives, owned).Select(row => row.PrimaryItemId).ToArray();
        var backward = QuestRequirementPlanner.Build(objectives.Reverse(), owned).Select(row => row.PrimaryItemId).ToArray();

        // Unmet first, then by id; the satisfied one sinks to the end either way.
        Assert.Equal(["item-m", "item-z", "item-a"], forward);
        Assert.Equal(forward, backward);
    }

    private static QuestObjectiveItemTarget Target(string itemId, string field, int group, int ordinal, decimal? count = null) =>
        new(itemId, field, group, ordinal, count, null);

    private static QuestObjectiveReadModel Objective(
        string objectiveId,
        bool? fir,
        decimal? target,
        IReadOnlyList<QuestObjectiveItemTarget> items,
        bool done = false) => new(
        objectiveId,
        Description: $"Do {objectiveId}",
        Kind: QuestObjectiveKind.FindItem,
        IsOptional: false,
        IsUnsupported: false,
        RecordedState: done ? RecordedObjectiveState.Completed : RecordedObjectiveState.InProgress,
        RecordedCount: null,
        TargetCount: target,
        FoundInRaidRequired: fir,
        ProgressSource: "test",
        ProgressModifiedUtc: null,
        IsPinned: false,
        MapIds: [],
        ItemTargets: items);
}
