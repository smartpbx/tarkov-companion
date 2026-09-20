using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests.Planning;

/// <summary>
/// What the Keep list reads off the quest board. Each rule here was counted on the real catalog
/// before it was written; the remarks on <see cref="QuestItemNeedPlanner"/> carry the numbers.
/// </summary>
public sealed class QuestItemNeedPlannerTests
{
    [Fact]
    public void Finding_three_and_handing_over_the_same_three_is_three()
    {
        // "Shortage": find 3 Salewa in raid, hand over 3 Salewa. Added up it told the player six.
        var needs = QuestItemNeedPlanner.FromBoard(
        [
            Quest("shortage",
                Objective("find", QuestObjectiveKind.FindItem, 3, fir: true, Target("salewa")),
                Objective("give", QuestObjectiveKind.GiveItem, 3, fir: true, Target("salewa"))),
        ]);

        var row = Assert.Single(needs.Requirements);
        Assert.Equal("give", row.ObjectiveId);
        Assert.Equal(3, row.Required);
        Assert.True(row.FoundInRaidRequired);
    }

    [Fact]
    public void Found_but_not_yet_handed_over_is_still_held_and_still_kept()
    {
        var needs = QuestItemNeedPlanner.FromBoard(
        [
            Quest("shortage",
                Objective("find", QuestObjectiveKind.FindItem, 3, fir: true, Target("salewa")) with
                {
                    RecordedState = RecordedObjectiveState.Completed,
                },
                Objective("give", QuestObjectiveKind.GiveItem, 3, fir: true, Target("salewa"))),
        ]);

        Assert.Equal(3, Assert.Single(needs.Requirements).Required);
    }

    [Fact]
    public void A_find_objective_with_no_hand_over_beside_it_still_asks()
    {
        var needs = QuestItemNeedPlanner.FromBoard(
        [
            Quest("solo", Objective("find", QuestObjectiveKind.FindItem, 2, fir: true, Target("item-a"))),
        ]);

        Assert.Equal(2, Assert.Single(needs.Requirements).Required);
    }

    [Fact]
    public void Once_handed_over_neither_objective_asks_for_anything()
    {
        var needs = QuestItemNeedPlanner.FromBoard(
        [
            Quest("shortage",
                Objective("find", QuestObjectiveKind.FindItem, 3, fir: true, Target("salewa")),
                Objective("give", QuestObjectiveKind.GiveItem, 3, fir: true, Target("salewa")) with
                {
                    RecordedState = RecordedObjectiveState.Completed,
                }),
        ]);

        Assert.Empty(needs.Requirements);
    }

    [Fact]
    public void A_few_alternatives_are_each_named_and_say_how_many_would_do()
    {
        var needs = QuestItemNeedPlanner.FromBoard(
        [
            Quest("aquarius", Objective("give", QuestObjectiveKind.GiveItem, 3, fir: true,
                Target("water-a", ordinal: 0), Target("water-b", ordinal: 1), Target("water-c", ordinal: 2))),
        ]);

        Assert.Equal(3, needs.Requirements.Count);
        Assert.All(needs.Requirements, row => Assert.Equal(3, row.Required));
        Assert.Equal(3, needs.InterchangeableCounts[("aquarius", "water-b")]);
    }

    [Fact]
    public void A_whole_category_names_no_item()
    {
        // "Sell any items to Ragman" is 3,535 targets on the real catalog.
        var category = Enumerable
            .Range(0, QuestItemNeedPlanner.MaximumNamedAlternatives + 1)
            .Select(index => Target($"item-{index}", ordinal: index))
            .ToArray();

        var needs = QuestItemNeedPlanner.FromBoard(
        [
            Quest("scavenger", Objective("give", QuestObjectiveKind.GiveItem, 1, fir: true, category)),
        ]);

        Assert.Empty(needs.Requirements);
    }

    [Fact]
    public void The_largest_named_set_is_still_named()
    {
        var named = Enumerable
            .Range(0, QuestItemNeedPlanner.MaximumNamedAlternatives)
            .Select(index => Target($"item-{index}", ordinal: index))
            .ToArray();

        var needs = QuestItemNeedPlanner.FromBoard(
        [
            Quest("figurines", Objective("give", QuestObjectiveKind.GiveItem, 1, fir: false, named)),
        ]);

        Assert.Equal(QuestItemNeedPlanner.MaximumNamedAlternatives, needs.Requirements.Count);
    }

    [Fact]
    public void A_key_is_one_to_carry_and_never_found_in_raid()
    {
        var needs = QuestItemNeedPlanner.FromBoard(
        [
            Quest("dorms", Objective("visit", QuestObjectiveKind.Visit, 5, fir: true, Target("key", field: "requiredKeys"))),
        ]);

        var row = Assert.Single(needs.Requirements);
        Assert.Equal(1, row.Required);
        Assert.False(row.FoundInRaidRequired);
    }

    [Fact]
    public void A_key_behind_three_objectives_of_one_quest_is_asked_for_once()
    {
        var needs = QuestItemNeedPlanner.FromBoard(
        [
            Quest("dorms",
                Objective("room-1", QuestObjectiveKind.Visit, null, fir: null, Target("key", field: "requiredKeys")),
                Objective("room-2", QuestObjectiveKind.Visit, null, fir: null, Target("key", field: "requiredKeys")),
                Objective("room-3", QuestObjectiveKind.Visit, null, fir: null, Target("key", field: "requiredKeys"))),
        ]);

        Assert.Single(needs.Requirements);
        Assert.Contains(("dorms", "key"), needs.Reusable);
    }

    [Fact]
    public void A_key_the_quest_also_hands_over_is_counted_by_the_hand_over_alone()
    {
        // "Against the Conscience": open the room with the key, then hand the key in. One key.
        var needs = QuestItemNeedPlanner.FromBoard(
        [
            Quest("conscience",
                Objective("room", QuestObjectiveKind.Visit, null, fir: null, Target("key", field: "requiredKeys")),
                Objective("give", QuestObjectiveKind.GiveItem, 1, fir: false, Target("key"))),
        ]);

        Assert.Equal("give", Assert.Single(needs.Requirements).ObjectiveId);
        Assert.Empty(needs.Reusable);
    }

    [Fact]
    public void Four_places_to_mark_are_four_markers()
    {
        var needs = QuestItemNeedPlanner.FromBoard(
        [
            Quest("directions",
                Objective("mark-1", QuestObjectiveKind.Mark, null, fir: null, Target("marker", field: "markerItem")),
                Objective("mark-2", QuestObjectiveKind.Mark, null, fir: null, Target("marker", field: "markerItem")),
                Objective("mark-3", QuestObjectiveKind.Mark, null, fir: null, Target("marker", field: "markerItem")),
                Objective("mark-4", QuestObjectiveKind.Mark, null, fir: null, Target("marker", field: "markerItem"))),
        ]);

        Assert.Equal(4, needs.Requirements.Count);
        Assert.Empty(needs.Reusable);
    }

    [Fact]
    public void A_quest_only_the_other_faction_is_offered_asks_for_nothing()
    {
        var needs = QuestItemNeedPlanner.FromBoard(
        [
            Quest("textile-bear", Objective("give", QuestObjectiveKind.GiveItem, 5, fir: true, Target("fabric"))) with
            {
                Eligibility = new QuestEligibility(
                    QuestEligibilityState.Locked,
                    [new QuestEligibilityReason("faction", "Task requires Bear but the profile is Usec.")]),
            },
            Quest("textile-usec", Objective("give", QuestObjectiveKind.GiveItem, 5, fir: true, Target("fabric"))),
        ]);

        Assert.Equal("textile-usec", Assert.Single(needs.Requirements).TaskId);
    }

    [Fact]
    public void Finished_quests_selling_optional_steps_and_quest_only_items_ask_for_nothing()
    {
        var needs = QuestItemNeedPlanner.FromBoard(
        [
            Quest("done", Objective("give", QuestObjectiveKind.GiveItem, 1, fir: false, Target("item-a"))) with
            {
                RecordedState = RecordedTaskState.Completed,
            },
            Quest("open",
                Objective("sell", QuestObjectiveKind.SellItem, 1, fir: false, Target("item-b")),
                Objective("optional", QuestObjectiveKind.GiveItem, 1, fir: false, Target("item-c")) with { IsOptional = true },
                Objective("journal", QuestObjectiveKind.GiveQuestItem, 1, fir: false, Target("journal", field: "questItem"))),
        ]);

        Assert.Empty(needs.Requirements);
    }

    [Fact]
    public void Recorded_progress_is_reported_so_it_is_taken_off_once()
    {
        var needs = QuestItemNeedPlanner.FromBoard(
        [
            Quest("shortage", Objective("give", QuestObjectiveKind.GiveItem, 3, fir: true, Target("salewa")) with
            {
                RecordedCount = 2m,
            }),
        ]);

        // The full three is reported with the two beside it; the Keep planner does the taking off.
        Assert.Equal(3, Assert.Single(needs.Requirements).Required);
        Assert.Equal(2, needs.RecordedProgress["give"]);
    }

    private static QuestObjectiveItemTarget Target(string itemId, string field = "items", int ordinal = 0) =>
        new(itemId, field, 0, ordinal, null, null);

    private static QuestObjectiveReadModel Objective(
        string objectiveId,
        QuestObjectiveKind kind,
        decimal? count,
        bool? fir,
        params QuestObjectiveItemTarget[] items) => new(
        objectiveId,
        Description: $"Do {objectiveId}",
        Kind: kind,
        IsOptional: false,
        IsUnsupported: false,
        RecordedState: RecordedObjectiveState.InProgress,
        RecordedCount: null,
        TargetCount: count,
        FoundInRaidRequired: fir,
        ProgressSource: "test",
        ProgressModifiedUtc: null,
        IsPinned: false,
        MapIds: [],
        ItemTargets: items);

    private static QuestSummaryReadModel Quest(string taskId, params QuestObjectiveReadModel[] objectives) => new(
        taskId,
        Name: taskId,
        TraderId: null,
        PrimaryMapId: null,
        RecordedState: RecordedTaskState.Active,
        ProgressSource: "test",
        ProgressModifiedUtc: null,
        Eligibility: new QuestEligibility(QuestEligibilityState.Available, []),
        RecordedObjectivesSatisfied: RecordedObjectivesSatisfaction.NotSatisfied,
        IsPinned: false,
        Restartable: null,
        HasFailureConditions: false,
        FailureConditionNotes: [],
        Prerequisites: [],
        Objectives: objectives);
}
