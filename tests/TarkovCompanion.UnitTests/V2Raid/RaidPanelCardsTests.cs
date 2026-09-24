using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>The Raid side panel's cards: which start closed, and that the player's choice is remembered.</summary>
public sealed class RaidPanelCardsTests
{
    [Fact]
    public void Reference_cards_start_closed_and_what_a_raid_needs_starts_open()
    {
        var cards = new RaidPanelCards(new MemoryLayout());

        Assert.True(cards.Summary.IsExpanded);
        Assert.True(cards.Squad.IsExpanded);
        Assert.True(cards.Objectives.IsExpanded);
        Assert.True(cards.Extracts.IsExpanded);
        Assert.True(cards.Route.IsExpanded);
        Assert.True(cards.Marks.IsExpanded);
        foreach (var closed in new[] { cards.SpawnAreas, cards.Spawns, cards.WaysOut, cards.LootNearby, cards.LootFilters, cards.Corrections })
        {
            Assert.False(closed.IsExpanded, closed.Id);
            Assert.True(closed.IsCollapsed, closed.Id);
        }
    }

    [Fact]
    public void Opening_or_closing_a_card_is_remembered_for_the_next_launch()
    {
        var layout = new MemoryLayout();
        var first = new RaidPanelCards(layout);
        first.SpawnAreas.ToggleCommand.Execute(null);
        first.Objectives.IsExpanded = false;

        var next = new RaidPanelCards(layout);

        Assert.True(next.SpawnAreas.IsExpanded);
        Assert.False(next.Objectives.IsExpanded);
        Assert.Equal("open", layout.Get(WorkspaceLayoutKeys.RaidCard("spawn-areas")));
    }

    [Fact]
    public void A_card_the_app_opens_is_not_remembered_as_the_players_choice()
    {
        var layout = new MemoryLayout();
        var first = new RaidPanelCards(layout);
        var changed = new List<string?>();
        first.LootFilters.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        first.LootFilters.Reveal();

        Assert.True(first.LootFilters.IsExpanded);
        Assert.Contains(nameof(RaidPanelCardViewModel.IsCollapsed), changed);
        Assert.False(new RaidPanelCards(layout).LootFilters.IsExpanded);
    }

    [Theory]
    [InlineData(0, "None")]
    [InlineData(1, "1 area")]
    [InlineData(38, "38 areas")]
    public void A_closed_cards_line_counts_what_is_inside(int count, string expected) =>
        Assert.Equal(expected, RaidCockpitViewModel.Counted(count, TarkovCompanion.App.Localization.RaidText.AreaCount));

    private sealed class MemoryLayout : IWorkspaceLayoutStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string value) => _values[key] = value;
    }
}
