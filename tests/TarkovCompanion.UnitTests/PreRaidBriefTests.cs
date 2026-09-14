using TarkovCompanion.App.ViewModels.Quests;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What to have on you, said in the minute it can still be acted on.
/// </summary>
/// <remarks>
/// The map arrives on <c>profileStatus</c> about a minute before <c>GameStarted</c>, and that
/// minute is the last one in which a player can change what they are carrying. The quest panel
/// beside the map carried no requirement data at all, while the formatter that builds "Keys: …"
/// had been there the whole time.
///
/// The split between the two lines is the part worth guarding. A key you forgot is a raid you
/// cannot finish; an item you meant to hand in is a raid you finish and then repeat. Those are
/// different mistakes, made at different moments, so they are not one list.
/// </remarks>
public sealed class PreRaidBriefTests
{
    [Fact]
    public void A_required_key_is_something_to_bring()
    {
        var bring = QuestItemRequirementFormatter.DescribeBring([Target("dorm-214", "requiredKeys")], Name);

        Assert.Contains("Dorm room 214 key", bring, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_is_never_something_to_hand_in()
    {
        Assert.Empty(QuestItemRequirementFormatter.DescribeHandIn([Target("dorm-214", "requiredKeys")], null, Name));
    }

    [Fact]
    public void An_item_the_quest_wants_is_handed_in_rather_than_brought()
    {
        var targets = new[] { Target("bronze-watch", "items") };

        Assert.Empty(QuestItemRequirementFormatter.DescribeBring(targets, Name));
        Assert.Contains("Bronze pocket watch", QuestItemRequirementFormatter.DescribeHandIn(targets, null, Name), StringComparison.Ordinal);
    }

    [Fact]
    public void A_marker_is_carried_in_and_left_behind_rather_than_handed_over()
    {
        var targets = new[] { Target("ms2000", "markerItem") };

        Assert.Contains("MS2000 Marker", QuestItemRequirementFormatter.DescribeBring(targets, Name), StringComparison.Ordinal);
        Assert.Empty(QuestItemRequirementFormatter.DescribeHandIn(targets, null, Name));
    }

    [Fact]
    public void What_you_must_be_wearing_and_what_you_must_not_are_both_decisions_made_at_the_same_screen()
    {
        var bring = QuestItemRequirementFormatter.DescribeBring(
            [Target("gas-mask", "wearing"), Target("armour", "notWearing")],
            Name);

        Assert.Contains("Gas mask", bring, StringComparison.Ordinal);
        Assert.Contains("Not wearing", bring, StringComparison.Ordinal);
    }

    [Fact]
    public void Found_in_raid_is_said_on_the_line_it_changes()
    {
        // It changes what somebody packs, and it changes it only for the things being handed
        // over: a key does not have to be found in raid.
        var handIn = QuestItemRequirementFormatter.DescribeHandIn(
            [Target("bronze-watch", "items")],
            foundInRaidRequired: true,
            Name);

        Assert.Contains("found in raid", handIn, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quest_asking_for_nothing_says_nothing_on_either_line()
    {
        // Rather than "No item requirement" twice. These are two optional lines on a card, and
        // a card saying that twice says nothing twice.
        Assert.Empty(QuestItemRequirementFormatter.DescribeBring([], Name));
        Assert.Empty(QuestItemRequirementFormatter.DescribeHandIn([], null, Name));
    }

    [Fact]
    public void A_quest_asking_for_both_gets_both_lines()
    {
        var targets = new[] { Target("dorm-214", "requiredKeys"), Target("bronze-watch", "items") };

        Assert.NotEmpty(QuestItemRequirementFormatter.DescribeBring(targets, Name));
        Assert.NotEmpty(QuestItemRequirementFormatter.DescribeHandIn(targets, null, Name));
    }

    [Fact]
    public void Alternatives_are_still_said_as_alternatives()
    {
        // Through the same Describe the one-line form uses, so the two cannot drift apart in
        // how they group alternatives or name a field.
        var bring = QuestItemRequirementFormatter.DescribeBring(
            [Target("dorm-214", "requiredKeys"), Target("dorm-220", "requiredKeys")],
            Name);

        Assert.Contains(" or ", bring, StringComparison.Ordinal);
    }

    [Fact]
    public void An_item_nobody_has_named_is_printed_as_its_id_rather_than_dropped()
    {
        // Which is what the objective lines beside it already do. A missing name is something
        // to notice; a missing row is not.
        var bring = QuestItemRequirementFormatter.DescribeBring([Target("5c1d0c5f86f7744bb2683cf0", "requiredKeys")]);

        Assert.Contains("5c1d0c5f86f7744bb2683cf0", bring, StringComparison.Ordinal);
    }

    private static QuestObjectiveItemTarget Target(string itemId, string sourceField) =>
        new(itemId, sourceField, 0, 0, null, null);

    private static string Name(string itemId) => itemId switch
    {
        "dorm-214" => "Dorm room 214 key",
        "dorm-220" => "Dorm room 220 key",
        "bronze-watch" => "Bronze pocket watch",
        "ms2000" => "MS2000 Marker",
        "gas-mask" => "Gas mask",
        "armour" => "Body armour",
        _ => itemId,
    };
}
