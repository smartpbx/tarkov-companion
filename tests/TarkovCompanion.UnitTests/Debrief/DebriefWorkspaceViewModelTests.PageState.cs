using TarkovCompanion.App.ViewModels.V2.Debrief;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.UnitTests.V2Shell;

namespace TarkovCompanion.UnitTests.Debrief;

/// <summary>[#902 P8] Debrief comes back as it was left, and an emptied list offers the way back.</summary>
public sealed partial class DebriefWorkspaceViewModelTests
{
    [Fact]
    public async Task The_view_and_filters_survive_a_restart_but_the_notes_search_does_not()
    {
        using var file = new LayoutFile();
        var service = TwoMaps();
        var before = new DebriefWorkspaceViewModel(service, TestPaths(), layoutStore: file.Restart());
        await before.LoadAsync();
        before.SelectedMapFilterOption = before.MapFilterOptions.Single(option => option.MapId == "woods");
        before.OutcomeFilter = DebriefOutcomeFilter.Survived;
        before.SearchText = "night";
        before.IsStatsView = true;

        var after = new DebriefWorkspaceViewModel(service, TestPaths(), layoutStore: file.Restart());
        await after.LoadAsync();

        Assert.Equal("woods", after.SelectedMapFilterOption.MapId);
        Assert.Equal(DebriefOutcomeFilter.Survived, after.OutcomeFilter);
        Assert.True(after.IsStatsView);
        Assert.Equal(string.Empty, after.SearchText);
        Assert.Equal("woods", Assert.Single(after.Raids).MapLabel);
    }

    [Fact]
    public async Task A_remembered_filter_that_matches_nothing_says_so_and_one_click_clears_it()
    {
        using var file = new LayoutFile();
        var service = TwoMaps();
        var before = new DebriefWorkspaceViewModel(service, TestPaths(), layoutStore: file.Restart());
        await before.LoadAsync();
        before.OutcomeFilter = DebriefOutcomeFilter.Died;

        var after = new DebriefWorkspaceViewModel(service, TestPaths(), layoutStore: file.Restart());
        await after.LoadAsync();

        Assert.Empty(after.Raids);
        Assert.True(after.ShowsEmptyReset);
        Assert.Equal("No raids match these filters.", after.NoRaidsMessage);
        Assert.Equal("Clear filters", after.EmptyResetLabel);

        after.ShowEverythingCommand.Execute(null);

        Assert.Equal(2, after.Raids.Count);
        Assert.False(after.ShowsEmptyReset);
    }

    private static FakeRaidHistoryService TwoMaps()
    {
        var service = new FakeRaidHistoryService();
        var profile = Guid.NewGuid();
        service.Seed(new RaidHistoryEntry(Guid.NewGuid(), profile, "customs", "Regular", Started, Started.AddMinutes(20), "Survived", null));
        service.Seed(new RaidHistoryEntry(Guid.NewGuid(), profile, "woods", "Regular", Started.AddHours(-1), Started.AddHours(-1).AddMinutes(25), "Survived", null));
        return service;
    }
}
