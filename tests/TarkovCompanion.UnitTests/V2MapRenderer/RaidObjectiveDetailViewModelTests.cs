using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Wiki;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [Package 35] What a selected objective says: the task, the objective, found in raid, the items
/// and how many are still needed, the floor, whether the player has it active, and the wiki as a
/// link out and no more.
/// </summary>
public sealed class RaidObjectiveDetailViewModelTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly RealQuestZones Zones = RealQuestZones.Load();

    private const string Wiki = "https://escapefromtarkov.fandom.com/wiki/Debut";

    [Fact]
    public void A_hand_in_says_what_to_hand_over_by_name_that_it_must_be_found_in_raid_and_how_many_are_still_needed()
    {
        // "Hand over any found in raid drinking water items": three of five kinds of water.
        var detail = DetailOf("customs", "6a541b89", recorded: 1);

        Assert.Equal(Zones.Objectives("customs").Single(item => item.Id.StartsWith("6a541b89", StringComparison.Ordinal)).ReadModel.Description, detail.Description);
        Assert.NotEmpty(detail.Task);
        Assert.Equal("Found in raid", detail.FoundInRaid);
        Assert.Equal("2 of 3 still needed", detail.Remaining);
        var items = Assert.Single(detail.Items);
        Assert.StartsWith("Hand in", items, StringComparison.Ordinal);
        Assert.Contains("Bottle of YMXC water", items, StringComparison.Ordinal);
        Assert.Contains("Emergency Water Ration", items, StringComparison.Ordinal);
        // Names, never the catalog's ids.
        Assert.DoesNotContain("6a3557f841667bc4bb00fea4", items, StringComparison.Ordinal);
        Assert.Equal("Active", detail.Status);
    }

    [Fact]
    public void A_key_is_named_under_what_to_bring()
    {
        var detail = DetailOf("customs", "5a3fc032");

        Assert.Contains(detail.Items, line => line == "Keys: Dorm room 214 key");
        Assert.Equal("Somewhere in this area", detail.Where);
        Assert.Equal("2nd Floor", detail.Floor);
        Assert.True(detail.HasFloor);
    }

    [Fact]
    public void An_item_the_catalog_does_not_name_is_never_shown_as_its_id()
    {
        // The console objective asks for a quest item, which the items table does not hold.
        var detail = DetailOf("interchange", "667a958e");

        var items = Assert.Single(detail.Items);
        Assert.Contains("item not in the catalog", items, StringComparison.Ordinal);
        Assert.DoesNotContain(Zones.Objectives("interchange").Single(item => item.Id.StartsWith("667a958e", StringComparison.Ordinal)).ReadModel.ItemTargets[0].ItemId, items, StringComparison.Ordinal);
    }

    [Fact]
    public void An_objective_that_is_not_on_the_map_says_so_and_why()
    {
        var detail = DetailOf("customs", "5968eb9b");

        Assert.Equal("No location", detail.Where);
        Assert.Equal("The catalog gives no position for it.", detail.NoLocationReason);
        Assert.False(detail.HasFloor);
        Assert.False(detail.HasNumber);
        Assert.Equal("15 of 15 still needed", detail.Remaining);
    }

    [Fact]
    public void A_pinned_quest_the_player_has_not_started_is_not_called_active()
    {
        var detail = DetailOf("customs", "5a3fc032", edit: model => model with
        {
            TaskState = RecordedTaskState.NotStarted,
            IsTaskPinned = true,
        });

        Assert.Equal("Not active · Pinned", detail.Status);
    }

    [Fact]
    public void The_wiki_link_opens_the_quests_page_through_the_browser_opener_and_says_whose_page_it_is()
    {
        var opened = new List<string?>();
        var detail = DetailOf("customs", "5a3fc032", edit: model => model with { WikiUri = Wiki }, openWiki: uri =>
        {
            opened.Add(uri);
            return true;
        });

        Assert.True(detail.HasWiki);
        detail.OpenWikiCommand.Execute(null);

        Assert.Equal(Wiki, Assert.Single(opened));
        Assert.Contains("Wiki", detail.WikiAttribution, StringComparison.Ordinal);
        Assert.Contains("browser", detail.WikiAttribution, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://escapefromtarkov.fandom.com/wiki/Debut")]
    [InlineData("https://example.com/wiki/Debut")]
    public void No_link_is_offered_where_the_catalog_has_none_or_it_is_not_the_wiki(string? uri)
    {
        var detail = DetailOf("customs", "5a3fc032", edit: model => model with { WikiUri = uri });

        Assert.False(detail.HasWiki);
        Assert.False(WikiLinkPolicy.IsAllowed(uri));
    }

    private static RaidObjectiveDetailViewModel DetailOf(
        string map,
        string objectiveStart,
        decimal? recorded = null,
        Func<QuestMapObjectiveReadModel, QuestMapObjectiveReadModel>? edit = null,
        Func<string?, bool>? openWiki = null)
    {
        var model = Zones.Model(map);
        var readModel = Zones.Objectives(map).Single(item => item.Id.StartsWith(objectiveStart, StringComparison.Ordinal)).ReadModel;
        if (recorded is not null)
        {
            readModel = readModel with { RecordedCount = recorded };
        }

        readModel = edit?.Invoke(readModel) ?? readModel;
        var query = new QuestMapObjectivesReadModel(RealQuestZones.Scope, 1, RealQuestZones.Provenance, [Zones.GameMapId(map)], [readModel], []);
        var projected = new QuestMapProjectionService()
            .Project(query, Zones.Location(map), model.Variant, null, Zones.MapProvenance)
            .Objectives;
        var entry = new QuestObjectiveSceneBuilder().Build(projected, model.Floors, null, NowUtc).Entries.Single();
        return new RaidObjectiveDetailViewModel(
            entry,
            id => Zones.ItemNames.GetValueOrDefault(id, id),
            openWiki ?? (_ => true),
            () => { });
    }
}
