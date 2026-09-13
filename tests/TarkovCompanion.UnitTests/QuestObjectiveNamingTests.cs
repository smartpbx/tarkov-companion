using TarkovCompanion.App.ViewModels.Quests;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What an objective says it needs, in the words a player recognises.
/// </summary>
/// <remarks>
/// Reported with a screenshot: an objective read "markerItem: 5991b51486f77447b112d44f" where
/// it meant an MS2000 Marker. Those are the upstream field name and the upstream item id,
/// printed straight through.
/// </remarks>
public sealed class QuestObjectiveNamingTests
{
    [Fact]
    public void ItemsAreNamedWhenTheCatalogKnowsThem()
    {
        var described = QuestItemRequirementFormatter.DescribeForQuest(
            Objective(Target("5991b51486f77447b112d44f", "markerItem")),
            RecordedTaskState.Active,
            id => id == "5991b51486f77447b112d44f" ? "MS2000 Marker" : id);

        Assert.Contains("Marker: MS2000 Marker", described, StringComparison.Ordinal);
        Assert.DoesNotContain("5991b51486f77447b112d44f", described, StringComparison.Ordinal);
    }

    /// <summary>An id the catalog has never seen is shown, not hidden.</summary>
    [Fact]
    public void AnUnknownItemKeepsItsIdRatherThanGoingBlank()
    {
        var described = QuestItemRequirementFormatter.DescribeForQuest(
            Objective(Target("not-synced", "items")),
            RecordedTaskState.Active);

        Assert.Contains("not-synced", described, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("markerItem", "Marker")]
    [InlineData("requiredKeys", "Keys")]
    [InlineData("usingWeapon", "Weapon")]
    [InlineData("notWearing", "Not wearing")]
    public void FieldNamesReadAsWords(string field, string expected) =>
        Assert.Equal(expected, QuestItemRequirementFormatter.Humanise(field));

    /// <summary>A field upstream adds later reads as words rather than disappearing.</summary>
    [Fact]
    public void AnUnmappedFieldIsSpacedOutRatherThanInvented() =>
        Assert.Equal("Some new field", QuestItemRequirementFormatter.Humanise("someNewField"));

    [Fact]
    public void NoItemRequirementStillSaysSo() =>
        Assert.Equal(
            "No item requirement",
            QuestItemRequirementFormatter.DescribeForQuest(Objective(), RecordedTaskState.Active));

    private static QuestObjectiveItemTarget Target(string itemId, string sourceField) =>
        new(itemId, sourceField, 0, 0, 1, null);

    private static QuestObjectiveReadModel Objective(params QuestObjectiveItemTarget[] targets) => new(
        "objective-1",
        "Mark Artyom's car with an MS2000 Marker",
        QuestObjectiveKind.Mark,
        false,
        false,
        RecordedObjectiveState.Unknown,
        null,
        1,
        null,
        "Manual",
        null,
        false,
        [],
        targets);
}
