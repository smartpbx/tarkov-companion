using TarkovCompanion.App.Services.TestChecklist;
using TarkovCompanion.App.ViewModels.V2.Setup;

namespace TarkovCompanion.UnitTests.TestChecklist;

/// <summary>The rail, the collapsed rows, the need filters, search and Next untested, on the page model.</summary>
public sealed class TestChecklistNavigationTests : IDisposable
{
    private const string Items = """
        { "version": 1, "items": [
          { "id": "raid.one", "area": "Raid", "feature": "Follow", "needs": ["game", "in-raid"], "steps": ["Start a raid"], "expect": "It centres" },
          { "id": "raid.two", "area": "Raid", "feature": "Layers", "needs": [], "steps": ["Open the layer list"], "expect": "It lists" },
          { "id": "raid.three", "area": "Raid", "feature": "Squad dots", "needs": ["squad", "relay"], "steps": ["Join a group"], "expect": "Dots" },
          { "id": "intel.search", "area": "Intel", "feature": "Item search", "needs": [], "steps": ["Type salewa"], "expect": "Rows" },
          { "id": "intel.flea", "area": "Intel", "feature": "Flea screen", "needs": ["flea-screenshot"], "steps": ["Take a picture"], "expect": "Prices" },
          { "id": "team.tablet", "area": "Team", "feature": "Tablet pairing", "needs": ["tablet"], "steps": ["Scan the code"], "expect": "Paired" } ] }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tarkov-checklist-nav-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private TestChecklistViewModel Page(TestChecklistResultsStore? store = null)
    {
        Directory.CreateDirectory(_root);
        var catalog = TestChecklistCatalog.Parse([("10-all.json", Items)]);
        return new TestChecklistViewModel(catalog, store ?? new TestChecklistResultsStore(Path.Combine(_root, TestChecklistResultsStore.FileName)), "2.0.1400");
    }

    private static TestChecklistItemViewModel Item(TestChecklistViewModel page, string id) => page.Items.Single(item => item.Id == id);

    private static IEnumerable<string> ShownIds(TestChecklistViewModel page) => page.Rows.OfType<TestChecklistItemViewModel>().Select(item => item.Id);

    [Fact]
    public void No_game_needed_keeps_items_that_name_no_game_or_game_screenshot_and_squad_or_tablet_keeps_those_two()
    {
        var page = Page();

        page.SelectNeed(TestChecklistNeedFilter.NoGame);
        Assert.Equal(["raid.two", "raid.three", "intel.search", "team.tablet"], ShownIds(page));

        page.SelectNeed(TestChecklistNeedFilter.SquadOrTablet);
        Assert.Equal(["raid.three", "team.tablet"], ShownIds(page));
        Assert.Equal(["Raid", "Team"], page.Areas.Select(area => area.Name));

        page.SelectNeed(TestChecklistNeedFilter.Any);
        Assert.Equal(6, ShownIds(page).Count());
        Assert.Equal(["Any setup", "No game needed 4", "Squad or tablet 2"], page.NeedFilters.Select(chip => chip.Label));
    }

    [Fact]
    public void Search_matches_feature_steps_and_id_in_any_case_and_every_word_must_match()
    {
        var page = Page();

        page.Search = "SALEWA";
        Assert.Equal(["intel.search"], ShownIds(page));
        page.Search = "team.tab";
        Assert.Equal(["team.tablet"], ShownIds(page));
        page.Search = "squad dots";
        Assert.Equal(["raid.three"], ShownIds(page));
        page.Search = "squad salewa";
        Assert.True(page.ShowsNothing);
        page.Search = "  ";
        Assert.Equal(6, ShownIds(page).Count());
    }

    [Fact]
    public void Status_chips_count_within_the_need_filter_and_the_status_filter_combines_with_it()
    {
        var page = Page();
        Item(page, "raid.two").Mark(TestStatus.Works);
        page.SelectNeed(TestChecklistNeedFilter.NoGame);

        Assert.Equal("Untested 3", page.Filters.Single(chip => chip.Filter == TestChecklistFilter.Untested).Label);
        page.Select(TestChecklistFilter.Untested);
        Assert.Equal(["raid.three", "intel.search", "team.tablet"], ShownIds(page));
    }

    [Fact]
    public void Works_and_Skipped_fold_to_a_row_while_Broken_Needs_work_and_Retest_stay_open()
    {
        Directory.CreateDirectory(_root);
        var store = new TestChecklistResultsStore(Path.Combine(_root, TestChecklistResultsStore.FileName));
        store.Set("team.tablet", new(TestStatus.Works, null, "2.0.1399", DateTimeOffset.UtcNow));
        var page = Page(store);

        Assert.All(page.Items, item => Assert.False(item.IsCompact));
        Item(page, "raid.one").Mark(TestStatus.Works);
        Item(page, "raid.two").Mark(TestStatus.Skipped);
        Item(page, "raid.three").Mark(TestStatus.Broken);
        Item(page, "intel.search").Mark(TestStatus.NeedsWork);

        Assert.True(Item(page, "raid.one").IsCompact);
        Assert.True(Item(page, "raid.two").IsCompact);
        Assert.False(Item(page, "raid.three").IsCompact);
        Assert.False(Item(page, "intel.search").IsCompact);
        Assert.False(Item(page, "team.tablet").IsCompact);
        Assert.True(Item(page, "team.tablet").NeedsRetest);
    }

    [Fact]
    public void Edit_opens_a_row_a_new_mark_folds_it_again_and_Expand_all_opens_every_row()
    {
        var page = Page();
        var item = Item(page, "raid.one");
        item.Mark(TestStatus.Works);

        item.ToggleCommand.Execute(null);
        Assert.False(item.IsCompact);
        Assert.True(item.CanFold);
        item.SkipCommand.Execute(null);
        Assert.True(item.IsCompact);

        page.ExpandAllCommand.Execute(null);
        Assert.True(page.ExpandAll);
        Assert.False(item.IsCompact);
        Assert.False(item.CanFold);
        page.ExpandAllCommand.Execute(null);
        Assert.True(item.IsCompact);
    }

    [Fact]
    public void Next_untested_walks_the_shown_items_in_order_skipping_tested_ones_and_wraps()
    {
        var page = Page();
        var scrolled = new List<int>();
        page.ScrollRequested += scrolled.Add;
        Item(page, "raid.two").Mark(TestStatus.Works);
        Item(page, "raid.three").Mark(TestStatus.Broken);

        // Marking made raid.three current, so the walk starts after it.
        page.NextUntested();
        Assert.Equal("intel.search", page.Current?.Id);
        Assert.True(page.Current!.IsCurrent);
        // Rows: [Raid, one, two, three, Intel, search, flea, Team, tablet]
        Assert.Equal(5, scrolled[^1]);

        page.NextUntested();
        Assert.Equal("intel.flea", page.Current?.Id);
        page.NextUntested();
        Assert.Equal("team.tablet", page.Current?.Id);
        Assert.Equal(8, scrolled[^1]);
        page.NextUntested();
        Assert.Equal("raid.one", page.Current?.Id);
        Assert.False(Item(page, "team.tablet").IsCurrent);
        Assert.Equal(1, scrolled[^1]);
    }

    [Fact]
    public void Next_untested_says_so_when_the_view_has_none_left()
    {
        var page = Page();
        page.SelectNeed(TestChecklistNeedFilter.SquadOrTablet);
        Item(page, "raid.three").Mark(TestStatus.Works);
        Item(page, "team.tablet").Mark(TestStatus.Skipped);
        var scrolled = 0;
        page.ScrollRequested += _ => scrolled++;

        page.NextUntested();

        Assert.Equal(0, scrolled);
        Assert.Equal("Nothing untested in this view.", page.ActionStatus);
    }

    [Fact]
    public void The_rail_counts_tested_broken_and_needs_work_over_the_whole_area_whatever_the_filter()
    {
        var page = Page();
        Item(page, "raid.one").Mark(TestStatus.Broken);
        Item(page, "raid.two").Mark(TestStatus.NeedsWork);
        Item(page, "raid.three").Mark(TestStatus.Broken);
        Item(page, "team.tablet").Mark(TestStatus.Works);
        page.Select(TestChecklistFilter.Untested);

        var raid = page.Rail.Single(area => area.Name == "Raid");
        Assert.Equal("3/3", raid.RailCount);
        Assert.Equal("2 broken", raid.BrokenLine);
        Assert.Equal("1 need work", raid.NeedsWorkLine);
        Assert.True(raid.HasBroken);
        Assert.False(raid.IsShown);
        Assert.True(raid.IsComplete);

        var team = page.Rail.Single(area => area.Name == "Team");
        Assert.Equal("1/1", team.RailCount);
        Assert.False(team.HasBroken);
        Assert.False(team.IsShown);

        var intel = page.Rail.Single(area => area.Name == "Intel");
        Assert.Equal("0/2", intel.RailCount);
        Assert.True(intel.IsShown);
        Assert.Equal(["Raid", "Intel", "Team"], page.Rail.Select(area => area.Name));
    }

    [Fact]
    public void An_area_jump_scrolls_to_its_heading_and_a_hidden_area_does_nothing()
    {
        var page = Page();
        var scrolled = new List<int>();
        page.ScrollRequested += scrolled.Add;

        page.Rail.Single(area => area.Name == "Team").JumpCommand.Execute(null);
        Assert.Equal([7], scrolled);
        Assert.Equal("team.tablet", page.Current?.Id);

        page.Search = "salewa";
        page.Rail.Single(area => area.Name == "Team").JumpCommand.Execute(null);
        Assert.Equal([7], scrolled);
        page.Rail.Single(area => area.Name == "Intel").JumpCommand.Execute(null);
        Assert.Equal([7, 0], scrolled);
    }
}
