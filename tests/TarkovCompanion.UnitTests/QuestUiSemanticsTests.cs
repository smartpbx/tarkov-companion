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

        Assert.Contains("items: item-a or item-b; items: item-c; useItems: item-d", explicitFir, StringComparison.Ordinal);
        Assert.Contains("Required keys: key-a or key-b; key-c", explicitFir, StringComparison.Ordinal);
        Assert.Contains("found in raid required", explicitFir, StringComparison.OrdinalIgnoreCase);
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
