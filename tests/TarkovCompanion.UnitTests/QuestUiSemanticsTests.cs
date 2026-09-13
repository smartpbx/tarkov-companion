using Avalonia.Collections;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.Quests;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests;

public sealed class QuestUiSemanticsTests
{
    [Fact]
    public void ItemTextGroupsOnlyTrueAlternativesAndUsesObjectiveFirRule()
    {
        var targets = new QuestObjectiveItemTarget[]
        {
            new("item-a", "items", 0, 0, 3, true),
            new("item-b", "items", 0, 1, 3, false),
            new("item-c", "items", 1, 2, 3, false),
            new("item-d", "useItems", 0, 3, 1, false),
            new("key-a", "requiredKeys", 0, 4, null, null),
            new("key-b", "requiredKeys", 0, 5, null, null),
            new("key-c", "requiredKeys", 1, 6, null, null),
        };

        var explicitFir = QuestItemRequirementFormatter.DescribeForMap(targets, true);
        var explicitNonFir = QuestItemRequirementFormatter.DescribeForMap(targets, false);

        // Field names read as words now, and the ids would read as item names where the
        // catalog knows them. Nothing here supplies one, so the ids stand.
        Assert.Contains("Items: item-a or item-b; Items: item-c; Use items: item-d", explicitFir, StringComparison.Ordinal);
        Assert.Contains("Keys: key-a or key-b; key-c", explicitFir, StringComparison.Ordinal);
        Assert.Contains("found in raid", explicitFir, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("found in raid", explicitNonFir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InProgressDoesNotCreateCountForANonCountObjective()
    {
        var nonCount = Objective(targetCount: null, recordedCount: null);
        var countObjective = Objective(targetCount: 3, recordedCount: null);

        Assert.Null(QuestObjectiveViewModel.CountForInProgress(nonCount));
        Assert.Equal(0, QuestObjectiveViewModel.CountForInProgress(countObjective));
    }

    [Fact]
    public void QuestMarkerStylingCombinesPinAndHighlightAndCentersThePoint()
    {
        var normal = new QuestMapPointViewModel("normal", 100, 50, false, false);
        var pinnedAndHighlighted = new QuestMapPointViewModel("focused", 100, 50, true, true);
        var region = new QuestMapRegionViewModel("region", new AvaloniaList<Avalonia.Point>(), true, true);

        Assert.Equal(93, normal.Left);
        Assert.Equal(43, normal.Top);
        Assert.Equal(88, pinnedAndHighlighted.Left);
        Assert.Equal(38, pinnedAndHighlighted.Top);
        Assert.True(pinnedAndHighlighted.Size > normal.Size);
        Assert.NotEqual(pinnedAndHighlighted.FillColor, normal.FillColor);
        Assert.Equal(5, region.StrokeThickness);
    }

    [Fact]
    public void EverySearchWordMustMatchAndTheOrderTheyAreTypedInDoesNot()
    {
        const string quest = "Debut\nTrader: Prapor\nPrimary map: Customs\nEliminate Scavs on Customs";

        Assert.True(QuestsPageViewModel.MatchesEveryTerm(quest, QuestsPageViewModel.SearchTerms("debut")));
        Assert.True(QuestsPageViewModel.MatchesEveryTerm(quest, QuestsPageViewModel.SearchTerms("prapor debut")));
        Assert.True(QuestsPageViewModel.MatchesEveryTerm(quest, QuestsPageViewModel.SearchTerms("  customs   scavs ")));

        // The second word narrows. An any-word search would return this quest for "debut
        // shoreline", which is the opposite of what typing the second word was for.
        Assert.False(QuestsPageViewModel.MatchesEveryTerm(quest, QuestsPageViewModel.SearchTerms("debut shoreline")));

        // Nothing typed is not a filter, so it matches rather than excluding everything.
        Assert.Empty(QuestsPageViewModel.SearchTerms("   "));
        Assert.True(QuestsPageViewModel.MatchesEveryTerm(quest, QuestsPageViewModel.SearchTerms("   ")));
    }

    [Fact]
    public void AnEmptyBoardSaysWhichOfTheSearchAndTheFilterEmptiedIt()
    {
        Assert.Equal(string.Empty, QuestsPageViewModel.DescribeEmptyBoard(4, 9, 515, "debut", "All quests"));
        Assert.Equal("No quests loaded.", QuestsPageViewModel.DescribeEmptyBoard(0, 0, 0, "", "All quests"));
        Assert.Equal(
            "No quest matches “xyzzy”.",
            QuestsPageViewModel.DescribeEmptyBoard(0, 0, 515, "xyzzy", "Active / pinned"));
        Assert.Equal(
            "No quest is in “Failed”.",
            QuestsPageViewModel.DescribeEmptyBoard(0, 515, 515, "", "Failed"));

        // The case worth spelling out: the quest exists and the filter is hiding it, which
        // "Nothing matches this filter" reported as the quest not existing.
        Assert.Equal(
            "One quest matches “debut” and it is not in “Active / pinned”.",
            QuestsPageViewModel.DescribeEmptyBoard(0, 1, 515, "debut", "Active / pinned"));
        Assert.Equal(
            "3 quests match “gunsmith” and none are in “Active / pinned”.",
            QuestsPageViewModel.DescribeEmptyBoard(0, 3, 515, "gunsmith", "Active / pinned"));
    }

    private static QuestObjectiveReadModel Objective(decimal? targetCount, decimal? recordedCount) => new(
        "objective",
        "Visit the location",
        QuestObjectiveKind.Visit,
        false,
        false,
        RecordedObjectiveState.Unknown,
        recordedCount,
        targetCount,
        null,
        "No progress assertion",
        null,
        false,
        [],
        []);
}
