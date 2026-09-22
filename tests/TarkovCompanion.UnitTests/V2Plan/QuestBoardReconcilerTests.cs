using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests.V2Plan;

/// <summary>
/// [#453] Every return to Plan re-reads the quest board; a board that has not changed must compose
/// to the very same groups and rows, and one changed quest must rebuild only its own rows.
/// </summary>
public sealed class QuestBoardReconcilerTests
{
    [Fact]
    public void A_board_read_again_unchanged_is_the_previous_board_and_keeps_every_group()
    {
        var first = Board(Quest("a", 1), Quest("b", 2, "woods"));
        var again = Board(Quest("a", 1), Quest("b", 2, "woods"));
        var groups = Compose(first, []);

        var reconciled = QuestBoardReconciler.Reconcile(first, again);

        Assert.Same(first, reconciled);
        Assert.Same(groups, Compose(reconciled, groups));
    }

    [Fact]
    public void One_changed_quest_rebuilds_only_its_own_map_group()
    {
        var first = Board(Quest("a", 1), Quest("b", 2, "woods"));
        var groups = Compose(first, []);
        var changed = Board(Quest("a", 1), Quest("b", 3, "woods"));

        var reconciled = QuestBoardReconciler.Reconcile(first, changed);
        var composed = Compose(reconciled, groups);

        Assert.NotSame(first, reconciled);
        Assert.Same(first.Tasks[0], reconciled.Tasks[0]);
        Assert.NotSame(first.Tasks[1], reconciled.Tasks[1]);
        Assert.Same(groups.Single(group => group.MapId == "customs"), composed.Single(group => group.MapId == "customs"));
        Assert.NotSame(groups.Single(group => group.MapId == "woods"), composed.Single(group => group.MapId == "woods"));
    }

    [Fact]
    public void A_difference_inside_a_list_is_a_difference()
    {
        var quest = Quest("a", 1);
        Assert.True(QuestBoardReconciler.SameTask(quest, Quest("a", 1)));
        Assert.False(QuestBoardReconciler.SameTask(quest, Quest("a", 1, "woods")));
        Assert.False(QuestBoardReconciler.SameTask(quest, quest with
        {
            Eligibility = new(QuestEligibilityState.Available, [new("level", "Level 10")]),
        }));
        Assert.False(QuestBoardReconciler.SameTask(quest, quest with
        {
            Prerequisites = [new("other", ["complete"], RecordedTaskState.Completed)],
        }));
        Assert.False(QuestBoardReconciler.SameTask(quest, quest with { WikiUri = "https://example.invalid/a" }));
    }

    [Fact]
    public void A_quest_that_left_the_board_is_not_carried_over()
    {
        var first = Board(Quest("a", 1), Quest("b", 2));
        var reconciled = QuestBoardReconciler.Reconcile(first, Board(Quest("a", 1)));

        var only = Assert.Single(reconciled.Tasks);
        Assert.Same(first.Tasks[0], only);
    }

    [Fact]
    public void A_map_draws_one_page_of_rows_until_the_player_asks_for_all_of_them()
    {
        // Outside the interface thread there is nothing to spread the rows over, so they arrive at once.
        var board = Board([.. Enumerable.Range(0, 45).Select(index => Quest($"q{index:D2}", 1))]);
        var group = Assert.Single(Compose(board, []));

        Assert.Equal(PlanMapGroupViewModel.ObjectivePageSize, group.VisibleObjectives.Count);
        Assert.True(group.HasMoreObjectives);
        var shown = group.VisibleObjectives;

        group.ShowAllObjectivesCommand.Execute(null);

        Assert.Same(shown, group.VisibleObjectives);
        Assert.Equal(45, group.VisibleObjectives.Count);
        Assert.Equal(group.Objectives, group.VisibleObjectives);
        Assert.False(group.HasMoreObjectives);
    }

    private static IReadOnlyList<PlanMapGroupViewModel> Compose(QuestBoardReadModel board, IReadOnlyList<PlanMapGroupViewModel> previous) =>
        PlanWorkspaceViewModel.ComposeGroups(PlanWorkspaceViewModel.Bucket(board.Tasks, showAll: true), previous, map => map);

    private static QuestBoardReadModel Board(params QuestSummaryReadModel[] tasks) => new(
        new(Guid.Parse("30000000-0000-0000-0000-000000000453"), GameMode.Regular, "1"),
        7,
        null,
        [.. tasks],
        []);

    // Built fresh on every call, lists included, the way every board read builds them.
    private static QuestSummaryReadModel Quest(string taskId, decimal count, string map = "customs") => new(
        taskId,
        Name: taskId,
        TraderId: "trader-1",
        PrimaryMapId: null,
        RecordedState: RecordedTaskState.Active,
        ProgressSource: "test",
        ProgressModifiedUtc: null,
        Eligibility: new(QuestEligibilityState.Available, [.. Array.Empty<QuestEligibilityReason>()]),
        RecordedObjectivesSatisfied: RecordedObjectivesSatisfaction.Indeterminate,
        IsPinned: false,
        Restartable: false,
        HasFailureConditions: false,
        FailureConditionNotes: [.. Array.Empty<string>()],
        Prerequisites: [.. Array.Empty<QuestPrerequisiteReadModel>()],
        Objectives:
        [
            new(
                $"{taskId}-objective",
                Description: $"Find {taskId}",
                Kind: QuestObjectiveKind.FindItem,
                IsOptional: false,
                IsUnsupported: false,
                RecordedState: RecordedObjectiveState.InProgress,
                RecordedCount: count,
                TargetCount: 5,
                FoundInRaidRequired: true,
                ProgressSource: "test",
                ProgressModifiedUtc: null,
                IsPinned: false,
                MapIds: [map],
                ItemTargets: [new($"{taskId}-item", "items", 0, 0, 5, true)]),
        ]);
}
