using System.Globalization;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests.V2Plan;

/// <summary>
/// The exported plan, its shopping list and its critical path (#288, #315).
/// </summary>
/// <remarks>
/// The two parts worth holding are the arithmetic nobody can check by eye — needs add up across
/// maps while held counts must not — and the graph walk, which reads a catalog this repository
/// does not control and must not recurse off a cycle in it.
/// </remarks>
public sealed class PlanExportTests
{
    private static readonly DateTimeOffset Generated = new(2026, 9, 20, 14, 32, 0, TimeSpan.Zero);

    [Fact]
    public void TheSameItemOnTwoMapsIsOnePileOfNeedAndOneOfHeld()
    {
        var list = PlanExport.ShoppingList(
        [
            new("watch", "Bronze pocket watch", "Hand in", 3, 1),
            new("watch", "Bronze pocket watch", "Hand in", 2, 1),
        ]);

        var item = Assert.Single(list);
        // Two maps asking for three and two is five to carry...
        Assert.Equal(5, item.Need);
        // ...but the one held is the same one held, counted once, not twice.
        Assert.Equal(1, item.Have);
        Assert.Equal(4, item.Short);
    }

    [Fact]
    public void HandlingSeparatesOtherwiseIdenticalRows()
    {
        var list = PlanExport.ShoppingList(
        [
            new("salewa", "Salewa", "Hand in", 2, 0),
            new("salewa", "Salewa", "Find in raid", 1, 0),
        ]);

        Assert.Equal(2, list.Count);
        Assert.Contains(list, item => item.Handling == "Find in raid" && item.Need == 1);
    }

    [Fact]
    public void WhatIsAlreadyHeldIsNotOnTheShoppingList()
    {
        var list = PlanExport.ShoppingList(
        [
            new("watch", "Bronze pocket watch", "Hand in", 2, 5),
            new("gunpowder", "Gunpowder", "Hand in", 4, 1),
        ]);

        Assert.Equal("Gunpowder", Assert.Single(list).ItemName);
    }

    [Fact]
    public void TwoItemsTheCatalogCannotNameStayTwoRows()
    {
        // Both read "Item not in the catalog"; grouped by that name they became one row asking
        // for two of a thing that does not exist.
        var list = PlanExport.ShoppingList(
        [
            new("unknown-a", "Item not in the catalog", "Hand in", 1, 0),
            new("unknown-b", "Item not in the catalog", "Hand in", 1, 0),
        ]);

        Assert.Equal(2, list.Count);
        Assert.All(list, item => Assert.Equal(1, item.Need));
    }

    [Fact]
    public void AQuestWithNothingInTheWayIsNotListedAsBlocked()
    {
        var path = PlanExport.CriticalPath([Quest("a", "Debut")]);

        Assert.Empty(path);
    }

    [Fact]
    public void TheDeepestChainComesFirstAndCountsEveryIncompleteStep()
    {
        // c waits on b, b waits on a, a waits on nothing: c is two deep.
        var path = PlanExport.CriticalPath(
        [
            Quest("a", "Debut"),
            Quest("b", "Checking", ("a", RecordedTaskState.Active)),
            Quest("c", "Shootout picnic", ("b", RecordedTaskState.NotStarted)),
        ]);

        Assert.Equal(["Shootout picnic", "Checking"], path.Select(blocked => blocked.Quest));
        Assert.Equal(2, path[0].Depth);
        Assert.Equal(1, path[1].Depth);
        Assert.Equal(["Checking"], path[0].WaitingOn);
    }

    [Fact]
    public void ACompletedPrerequisiteIsNotInTheWay()
    {
        var path = PlanExport.CriticalPath(
        [
            Quest("a", "Debut"),
            Quest("b", "Checking", ("a", RecordedTaskState.Completed)),
        ]);

        Assert.Empty(path);
    }

    [Fact]
    public void APrerequisiteTheChainDoesNotContainStillCounts()
    {
        // The plan holds only b; the quest it waits on is off-screen and no less in the way.
        var path = PlanExport.CriticalPath([Quest("b", "Checking", ("a", RecordedTaskState.NotStarted))]);

        var blocked = Assert.Single(path);
        Assert.Equal(1, blocked.Depth);
        // With no name for it, the id is what gets printed rather than a blank.
        Assert.Equal(["a"], blocked.WaitingOn);
    }

    [Fact]
    public void ACatalogThatSaysAQuestNeedsItselfStillDrawsAPage()
    {
        // External data. A cycle must come back with an answer, not a stack overflow.
        var path = PlanExport.CriticalPath(
        [
            Quest("a", "Debut", ("b", RecordedTaskState.NotStarted)),
            Quest("b", "Checking", ("a", RecordedTaskState.NotStarted)),
        ]);

        Assert.Equal(2, path.Count);
        Assert.All(path, blocked => Assert.True(blocked.Depth >= 1));
    }

    [Fact]
    public void AnEmptyPlanSaysSoRatherThanPrintingAnEmptyDocument()
    {
        var markdown = Document([], [], []).ToMarkdown(CultureInfo.InvariantCulture);

        Assert.Contains("Nothing is planned", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDocumentSaysWhenItWasMadeAndWhatItWasMadeFrom()
    {
        var markdown = Document(
            [new("Customs", [new(1, "Debut", "Prapor", "Kill 5 scavs", "Find in raid", "1 of 5")], ["4x Bronze pocket watch"])],
            [new("Bronze pocket watch", "Hand in", 5, 1)],
            [new("Checking", 1, ["Debut"])]).ToMarkdown(CultureInfo.InvariantCulture);

        // A plan pasted into a chat three hours later must not read as a current one.
        Assert.Contains("Generated", markdown, StringComparison.Ordinal);
        Assert.Contains("Regular · level 24", markdown, StringComparison.Ordinal);
        Assert.Contains("showing Actionable", markdown, StringComparison.Ordinal);
        Assert.Contains("## Customs", markdown, StringComparison.Ordinal);
        Assert.Contains("1. **Debut** (Prapor) — Kill 5 scavs · Find in raid · 1 of 5", markdown, StringComparison.Ordinal);
        Assert.Contains("Still needed here: 4x Bronze pocket watch", markdown, StringComparison.Ordinal);
        Assert.Contains("## Shopping list", markdown, StringComparison.Ordinal);
        Assert.Contains("4x Bronze pocket watch (hand in)", markdown, StringComparison.Ordinal);
        Assert.Contains("1 of 5 already held", markdown, StringComparison.Ordinal);
        Assert.Contains("## What is in the way", markdown, StringComparison.Ordinal);
        Assert.Contains("**Checking** — waiting on Debut", markdown, StringComparison.Ordinal);
        Assert.Contains("data through yesterday", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ASearchIsRecordedBecauseItChangesWhatThePlanContains()
    {
        var document = Document([], [], []) with { Search = "watch" };

        Assert.Contains("search \"watch\"", document.ToMarkdown(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static PlanExportDocument Document(
        IReadOnlyList<PlanExportGroup> groups,
        IReadOnlyList<PlanExportItem> shopping,
        IReadOnlyList<PlanExportBlocked> blocked) => new(
            Generated,
            "Regular · level 24",
            "Actionable",
            null,
            groups,
            shopping,
            blocked,
            "data through yesterday");

    private static QuestSummaryReadModel Quest(
        string id,
        string name,
        params (string RequiredTaskId, RecordedTaskState State)[] prerequisites) => new(
            id,
            name,
            "prapor",
            "customs",
            RecordedTaskState.Active,
            QuestProgressSources.Manual,
            null,
            new QuestEligibility(QuestEligibilityState.Available, []),
            RecordedObjectivesSatisfaction.NotSatisfied,
            false,
            null,
            false,
            [],
            [.. prerequisites.Select(prerequisite =>
                new QuestPrerequisiteReadModel(prerequisite.RequiredTaskId, ["complete"], prerequisite.State))],
            []);
}
