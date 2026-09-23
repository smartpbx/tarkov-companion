using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests.V2Plan;

/// <summary>
/// The Plan workspace's decisions about which quests to show and what they ask the player to have,
/// covered as pure functions the way the bucketing already is.
/// </summary>
public sealed class PlanQuestRulesTests
{
    [Fact]
    public void ActiveKeepsStartedQuestsAndAnythingPinnedButNotAvailableOnes()
    {
        Assert.True(PlanQuestRules.Includes(Task("a", RecordedTaskState.Active), PlanQuestFilter.Active));
        Assert.True(PlanQuestRules.Includes(Task("p", RecordedTaskState.NotStarted, pinned: true), PlanQuestFilter.Active));
        Assert.True(PlanQuestRules.Includes(
            Task("o", RecordedTaskState.NotStarted, objectives: [Objective("o1", pinned: true)]),
            PlanQuestFilter.Active));
        Assert.False(PlanQuestRules.Includes(Task("n", RecordedTaskState.NotStarted), PlanQuestFilter.Active));
    }

    [Theory]
    [InlineData(RecordedTaskState.Unknown, QuestEligibilityState.Available, true)]
    [InlineData(RecordedTaskState.NotStarted, QuestEligibilityState.Available, true)]
    [InlineData(RecordedTaskState.NotStarted, QuestEligibilityState.Locked, false)]
    [InlineData(RecordedTaskState.NotStarted, QuestEligibilityState.Indeterminate, false)]
    [InlineData(RecordedTaskState.Active, QuestEligibilityState.Available, false)]
    [InlineData(RecordedTaskState.Completed, QuestEligibilityState.Available, false)]
    public void AvailableNowIsNotStartedAndEligible(RecordedTaskState state, QuestEligibilityState eligibility, bool expected) =>
        Assert.Equal(expected, PlanQuestRules.Includes(Task("q", state, eligibility: eligibility), PlanQuestFilter.Available));

    [Theory]
    [InlineData(RecordedTaskState.NotStarted, QuestEligibilityState.Locked, true)]
    [InlineData(RecordedTaskState.Unknown, QuestEligibilityState.Locked, true)]
    [InlineData(RecordedTaskState.NotStarted, QuestEligibilityState.Available, false)]
    [InlineData(RecordedTaskState.Completed, QuestEligibilityState.Locked, false)]
    public void LockedIsNotStartedAndGatedByLevelOrPrerequisites(RecordedTaskState state, QuestEligibilityState eligibility, bool expected) =>
        Assert.Equal(expected, PlanQuestRules.Includes(Task("q", state, eligibility: eligibility), PlanQuestFilter.Locked));

    [Fact]
    public void KappaKeepsTheQuestsTheCatalogSaysItNeedsUntilTheyAreDone()
    {
        Assert.True(PlanQuestRules.Includes(Task("k", RecordedTaskState.NotStarted, kappa: true), PlanQuestFilter.Kappa));
        Assert.True(PlanQuestRules.Includes(Task("k", RecordedTaskState.Active, kappa: true), PlanQuestFilter.Kappa));
        Assert.False(PlanQuestRules.Includes(Task("k", RecordedTaskState.Completed, kappa: true), PlanQuestFilter.Kappa));
        Assert.False(PlanQuestRules.Includes(Task("n", RecordedTaskState.Active, kappa: false), PlanQuestFilter.Kappa));
        Assert.False(PlanQuestRules.Includes(Task("u", RecordedTaskState.Active, kappa: null), PlanQuestFilter.Kappa));
    }

    [Fact]
    public void CompletedAndAllAreTheOnlyFiltersThatShowFinishedObjectives()
    {
        Assert.True(PlanQuestRules.Includes(Task("c", RecordedTaskState.Completed), PlanQuestFilter.Completed));
        Assert.False(PlanQuestRules.Includes(Task("a", RecordedTaskState.Active), PlanQuestFilter.Completed));
        Assert.True(PlanQuestRules.Includes(Task("f", RecordedTaskState.Failed), PlanQuestFilter.All));
        Assert.Equal(
            [PlanQuestFilter.Completed, PlanQuestFilter.All],
            Enum.GetValues<PlanQuestFilter>().Where(PlanQuestRules.ShowsFinishedObjectives));
    }

    [Fact]
    public void ARowSaysWhatAQuestIsDoingWhenItIsNotBeingPlayed()
    {
        Assert.Equal(string.Empty, PlanQuestRules.DescribeStatus(Task("a", RecordedTaskState.Active)));
        Assert.Equal("Completed", PlanQuestRules.DescribeStatus(Task("c", RecordedTaskState.Completed)));
        Assert.Equal("Failed", PlanQuestRules.DescribeStatus(Task("f", RecordedTaskState.Failed)));
        Assert.Equal("Available now", PlanQuestRules.DescribeStatus(Task("n", RecordedTaskState.NotStarted)));
        Assert.Equal(
            "Locked · Recorded player level 1 is below required level 19",
            PlanQuestRules.DescribeStatus(Task(
                "l",
                RecordedTaskState.NotStarted,
                eligibility: QuestEligibilityState.Locked,
                reason: "Recorded player level 1 is below required level 19")));
        Assert.Equal("Locked", PlanQuestRules.DescribeStatus(Task("l2", RecordedTaskState.NotStarted, eligibility: QuestEligibilityState.Locked)));
        Assert.Equal("Waiting on a timer", PlanQuestRules.DescribeStatus(Task("d", RecordedTaskState.NotStarted, eligibility: QuestEligibilityState.Delayed)));
    }

    [Fact]
    public void AnIndeterminateQuestReadsAsNothingRatherThanAWordNobodyCanActOn() =>
        Assert.Equal(
            string.Empty,
            PlanQuestRules.DescribeStatus(Task("i", RecordedTaskState.Unknown, eligibility: QuestEligibilityState.Indeterminate)));

    [Fact]
    public void AnEmptyBoardNamesWhichOfTheSearchTheTraderAndTheFilterEmptiedIt()
    {
        Assert.Equal("No quest matches “gunsmith” in Active.", PlanQuestRules.DescribeEmpty(PlanQuestFilter.Active, "gunsmith", false));
        Assert.Equal("No blocked quest for this trader.", PlanQuestRules.DescribeEmpty(PlanQuestFilter.Locked, string.Empty, true));
        Assert.Equal("No active quests. Try Next, or All.", PlanQuestRules.DescribeEmpty(PlanQuestFilter.Active, string.Empty, false));
        Assert.Equal("No quest is next right now.", PlanQuestRules.DescribeEmpty(PlanQuestFilter.Available, string.Empty, false));
        Assert.Equal("No quests recorded yet.", PlanQuestRules.DescribeEmpty(PlanQuestFilter.All, string.Empty, false));
    }

    [Fact]
    public void ABucketFiltersByStateTraderAndEverySearchWordAndKeepsUnfinishedObjectives()
    {
        var tasks = new[]
        {
            Task("gunsmith", RecordedTaskState.NotStarted, trader: "mechanic", objectives: [Objective("g1"), Objective("g2", done: true)]),
            Task("debut", RecordedTaskState.NotStarted, trader: "prapor", objectives: [Objective("d1")]),
            Task("locked", RecordedTaskState.NotStarted, trader: "prapor", eligibility: QuestEligibilityState.Locked, objectives: [Objective("l1")]),
        };

        var available = PlanWorkspaceViewModel.Bucket(tasks, PlanQuestFilter.Available, [], null, null);
        Assert.Equal(["g1", "d1"], available.Select(entry => entry.Objective.ObjectiveId));

        var prapor = PlanWorkspaceViewModel.Bucket(tasks, PlanQuestFilter.All, [], "prapor", null);
        Assert.Equal(["d1", "l1"], prapor.Select(entry => entry.Objective.ObjectiveId));

        var searched = PlanWorkspaceViewModel.Bucket(
            tasks,
            PlanQuestFilter.All,
            ["debut", "prapor"],
            null,
            task => $"{task.Name} {task.TraderId}");
        Assert.Equal(["d1"], searched.Select(entry => entry.Objective.ObjectiveId));

        // Every word must match, not any of them.
        Assert.Empty(PlanWorkspaceViewModel.Bucket(tasks, PlanQuestFilter.All, ["debut", "mechanic"], null, task => $"{task.Name} {task.TraderId}"));
    }

    [Fact]
    public void RequirementsCountWhatIsStillToHandInAgainstWhatIsHeld()
    {
        var objective = Objective(
            "o",
            target: 5,
            recorded: 2,
            fir: true,
            items: [Target("item-salewa", "items", 0, 5)]);
        var owned = new Dictionary<string, int>(StringComparer.Ordinal) { ["item-salewa"] = 1 };

        var row = Assert.Single(PlanQuestRules.BuildRequirements([objective], Name, owned));

        Assert.Equal("Salewa", row.ItemName);
        Assert.Equal("Find in raid", row.HandlingLabel);
        Assert.Equal(3, row.Need);
        Assert.Equal(1, row.Have);
        Assert.False(row.IsSatisfied);
        Assert.Equal("1 / 3", row.ProgressLabel);
    }

    [Fact]
    public void KeysAndGearAreBroughtOneEachAndOnlyThePlayerHoldingThemIsReady()
    {
        var objective = Objective("o", items:
        [
            Target("item-key", "requiredKeys", 0, 1),
            Target("item-marker", "markerItem", 1, 2),
        ]);
        var owned = new Dictionary<string, int>(StringComparer.Ordinal) { ["item-key"] = 1 };

        var rows = PlanQuestRules.BuildRequirements([objective], Name, owned);

        Assert.All(rows, row => Assert.Equal("Bring", row.HandlingLabel));
        Assert.Equal(["Marker", "Key"], rows.Select(row => row.ItemName));
        Assert.False(rows[0].IsSatisfied);
        Assert.True(rows[1].IsSatisfied);
        Assert.All(rows, row => Assert.Equal(1, row.Need));
    }

    [Fact]
    public void AlternativesAreOneRequirementNamedByTheFirstAndSatisfiedByAnyOfThem()
    {
        var objective = Objective("o", items:
        [
            Target("item-a", "items", 0, 1),
            Target("item-b", "items", 0, 2),
            Target("item-c", "items", 0, 3),
        ]);
        var owned = new Dictionary<string, int>(StringComparer.Ordinal) { ["item-c"] = 1 };

        var row = Assert.Single(PlanQuestRules.BuildRequirements([objective], Name, owned));

        Assert.Equal("A or 2 more", row.ItemName);
        Assert.True(row.IsSatisfied);
    }

    [Fact]
    public void TheSameItemAskedForTwiceIsOnePileAndNeedsBothAmounts()
    {
        var first = Objective("one", target: 3, items: [Target("item-bolts", "items", 0, 1, 3)]);
        var second = Objective("two", target: 4, items: [Target("item-bolts", "items", 0, 1, 4)]);

        var row = Assert.Single(PlanQuestRules.BuildRequirements([first, second], Name, new Dictionary<string, int>()));

        Assert.Equal(7, row.Need);
    }

    [Fact]
    public void FinishedObjectivesAndConditionsThatNameNothingToHaveAskForNothing()
    {
        var done = Objective("done", done: true, items: [Target("item-x", "items", 0, 1)]);
        var conditions = Objective("cond", items:
        [
            Target("item-y", "notWearing", 0, 1),
            Target("item-z", "containsAll", 1, 2),
        ]);

        Assert.Empty(PlanQuestRules.BuildRequirements([done, conditions], Name, new Dictionary<string, int>()));
    }

    [Fact]
    public void UnsatisfiedRequirementsComeFirstThenByName()
    {
        var objective = Objective("o", items:
        [
            Target("item-a", "items", 0, 1),
            Target("item-b", "items", 1, 2),
            Target("item-c", "items", 2, 3),
        ]);
        var owned = new Dictionary<string, int>(StringComparer.Ordinal) { ["item-a"] = 1 };

        var rows = PlanQuestRules.BuildRequirements([objective], Name, owned);

        Assert.Equal(["B", "C", "A"], rows.Select(row => row.ItemName));
    }

    private static string Name(string itemId) => itemId switch
    {
        "item-salewa" => "Salewa",
        "item-key" => "Key",
        "item-marker" => "Marker",
        _ => itemId.Replace("item-", string.Empty, StringComparison.Ordinal).ToUpperInvariant() is { Length: 1 } single
            ? single
            : itemId.Replace("item-", string.Empty, StringComparison.Ordinal),
    };

    private static QuestObjectiveItemTarget Target(string itemId, string field, int group, int ordinal, decimal? count = null) =>
        new(itemId, field, group, ordinal, count, null);

    private static QuestSummaryReadModel Task(
        string taskId,
        RecordedTaskState state,
        QuestEligibilityState eligibility = QuestEligibilityState.Available,
        string? reason = null,
        bool pinned = false,
        bool? kappa = null,
        string trader = "trader-1",
        IReadOnlyList<QuestObjectiveReadModel>? objectives = null) => new(
        taskId,
        Name: taskId,
        TraderId: trader,
        PrimaryMapId: null,
        RecordedState: state,
        ProgressSource: "test",
        ProgressModifiedUtc: null,
        Eligibility: new(eligibility, reason is null ? [] : [new("level", reason)]),
        RecordedObjectivesSatisfied: RecordedObjectivesSatisfaction.Indeterminate,
        IsPinned: pinned,
        Restartable: false,
        HasFailureConditions: false,
        FailureConditionNotes: [],
        Prerequisites: [],
        Objectives: objectives ?? [])
    {
        KappaRequired = kappa,
    };

    private static QuestObjectiveReadModel Objective(
        string objectiveId,
        bool done = false,
        bool pinned = false,
        decimal? target = null,
        decimal? recorded = null,
        bool? fir = null,
        IReadOnlyList<QuestObjectiveItemTarget>? items = null) => new(
        objectiveId,
        Description: $"Do {objectiveId}",
        Kind: QuestObjectiveKind.FindItem,
        IsOptional: false,
        IsUnsupported: false,
        RecordedState: done ? RecordedObjectiveState.Completed : RecordedObjectiveState.InProgress,
        RecordedCount: recorded,
        TargetCount: target,
        FoundInRaidRequired: fir,
        ProgressSource: "test",
        ProgressModifiedUtc: null,
        IsPinned: pinned,
        MapIds: [],
        ItemTargets: items ?? []);
}
