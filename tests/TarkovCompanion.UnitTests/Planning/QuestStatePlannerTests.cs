using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests.Planning;

public sealed class QuestStatePlannerTests
{
    [Theory]
    [InlineData(RecordedTaskState.Active, QuestEligibilityState.Available, QuestPlanState.Current)]
    [InlineData(RecordedTaskState.NotStarted, QuestEligibilityState.Available, QuestPlanState.Next)]
    [InlineData(RecordedTaskState.NotStarted, QuestEligibilityState.Delayed, QuestPlanState.Future)]
    [InlineData(RecordedTaskState.NotStarted, QuestEligibilityState.Locked, QuestPlanState.Blocked)]
    [InlineData(RecordedTaskState.Completed, QuestEligibilityState.Locked, QuestPlanState.Completed)]
    [InlineData(RecordedTaskState.Unknown, QuestEligibilityState.Indeterminate, QuestPlanState.Unknown)]
    public void EveryQuestGetsOneNamedPlanningState(
        RecordedTaskState recorded,
        QuestEligibilityState eligibility,
        QuestPlanState expected)
    {
        var result = QuestStatePlanner.Derive(Task(recorded, eligibility), new Dictionary<string, int>());

        Assert.Equal(expected, result.State);
    }

    [Fact]
    public void ABlockedQuestExplainsLevelAndPrerequisiteInActionableWords()
    {
        var task = Task(
            RecordedTaskState.NotStarted,
            QuestEligibilityState.Locked,
            reasons:
            [
                new("player-level", "Recorded player level 4 is below required level 15."),
                new("prerequisite-state", "Debut must be complete and is recorded Active.", "debut"),
            ],
            prerequisites: [new("debut", ["complete"], RecordedTaskState.Active)]);

        var result = QuestStatePlanner.Derive(task, new Dictionary<string, int>(), id => id == "debut" ? "Debut" : null);

        Assert.Equal(QuestPlanState.Blocked, result.State);
        Assert.Collection(
            result.Blockers,
            blocker =>
            {
                Assert.Equal(QuestPlanBlockerKind.Prerequisite, blocker.Kind);
                Assert.Equal("finish Debut", blocker.Label);
            },
            blocker =>
            {
                Assert.Equal(QuestPlanBlockerKind.PlayerLevel, blocker.Kind);
                Assert.Equal("reach level 15", blocker.Label);
            });
    }

    [Fact]
    public void AnActiveQuestBlockedByTraderLoyaltyDoesNotTreatMissingAsLevelZero()
    {
        var objective = Objective() with
        {
            RequiredTraderId = "mechanic",
            RequiredTraderLevel = 3,
        };
        var task = Task(RecordedTaskState.Active, QuestEligibilityState.Available, objectives: [objective]);

        var unknown = QuestStatePlanner.Derive(task, new Dictionary<string, int>());
        var low = QuestStatePlanner.Derive(task, new Dictionary<string, int> { ["mechanic"] = 2 });
        var ready = QuestStatePlanner.Derive(task, new Dictionary<string, int> { ["mechanic"] = 3 });

        Assert.Equal(QuestPlanState.Blocked, unknown.State);
        Assert.Contains("loyalty not recorded", Assert.Single(unknown.Blockers).Label, StringComparison.Ordinal);
        Assert.Contains("recorded LL2", Assert.Single(low.Blockers).Label, StringComparison.Ordinal);
        Assert.Equal(QuestPlanState.Current, ready.State);
    }

    private static QuestSummaryReadModel Task(
        RecordedTaskState recorded,
        QuestEligibilityState eligibility,
        IReadOnlyList<QuestEligibilityReason>? reasons = null,
        IReadOnlyList<QuestPrerequisiteReadModel>? prerequisites = null,
        IReadOnlyList<QuestObjectiveReadModel>? objectives = null) => new(
        "task",
        "Task",
        "trader",
        null,
        recorded,
        "test",
        null,
        new(eligibility, reasons ?? []),
        RecordedObjectivesSatisfaction.Indeterminate,
        false,
        false,
        false,
        [],
        prerequisites ?? [],
        objectives ?? []);

    private static QuestObjectiveReadModel Objective() => new(
        "objective",
        "Reach loyalty",
        QuestObjectiveKind.TraderLevel,
        false,
        false,
        RecordedObjectiveState.InProgress,
        null,
        null,
        null,
        "test",
        null,
        false,
        [],
        []);
}
