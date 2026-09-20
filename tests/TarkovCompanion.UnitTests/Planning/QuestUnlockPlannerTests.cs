using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests.Planning;

public sealed class QuestUnlockPlannerTests
{
    [Fact]
    public void ALockedQuestSaysWhatToDoQuestsBeforeLevels()
    {
        var task = Locked(
            [
                new("player-level", "Recorded player level 4 is below required level 15."),
                new("prerequisite-state", "Debut must be complete and is recorded Active.", "debut"),
                new("unknown-prerequisite-state", "Shortage has no explicit recorded state.", "shortage"),
            ],
            [new("debut", ["complete"], RecordedTaskState.Active), new("shortage", ["active", "complete"], RecordedTaskState.Unknown)]);

        var steps = QuestUnlockPlanner.Steps(task, id => id == "debut" ? "Debut" : null);

        Assert.Equal(["finish Debut", "start shortage", "reach level 15"], steps.Select(step => step.Label));
        Assert.Equal("debut", steps[0].TaskId);
        Assert.Equal("finish Debut · start shortage · +1 more", QuestUnlockPlanner.Summarise(steps));
    }

    [Fact]
    public void AReasonItDoesNotKnowIsKeptInItsOwnWords()
    {
        var task = Locked([new("faction", "The task is for BEAR and the profile is USEC.")], []);

        var step = Assert.Single(QuestUnlockPlanner.Steps(task, _ => null));

        Assert.Equal(QuestUnlockKind.Other, step.Kind);
        Assert.Equal("The task is for BEAR and the profile is USEC", step.Label);
    }

    [Fact]
    public void AQuestThatOnlyOpensWhenAnotherFailsSaysSo()
    {
        var task = Locked(
            [new("prerequisite-state", "Chemical Part 4 must be failed and is recorded Active.", "chem4")],
            [new("chem4", ["failed"], RecordedTaskState.Active)]);

        Assert.Equal("fail Chemical Part 4", Assert.Single(QuestUnlockPlanner.Steps(task, _ => "Chemical Part 4")).Label);
    }

    [Fact]
    public void TheNextRaidIsTheMapThatMovesTheMostQuests()
    {
        NextRaidCandidate[] maps =
        [
            new(string.Empty, "Any map", 9, 30),
            new("woods", "Woods", 2, 11),
            new("customs", "Customs", 3, 4),
            new("factory", "Factory", 3, 6),
            new("labs", "The Lab", 5, 0),
        ];

        Assert.Equal(["factory", "customs", "woods"], NextRaidPlanner.Rank(maps).Select(map => map.MapKey));
        Assert.Equal("factory", NextRaidPlanner.Suggest(maps)?.MapKey);
        Assert.Null(NextRaidPlanner.Suggest([maps[0]]));
    }

    private static QuestSummaryReadModel Locked(
        IReadOnlyList<QuestEligibilityReason> reasons,
        IReadOnlyList<QuestPrerequisiteReadModel> prerequisites) => new(
        "task",
        "Task",
        null,
        null,
        RecordedTaskState.NotStarted,
        "None",
        null,
        new(QuestEligibilityState.Locked, reasons),
        default,
        false,
        null,
        false,
        [],
        prerequisites,
        []);
}
