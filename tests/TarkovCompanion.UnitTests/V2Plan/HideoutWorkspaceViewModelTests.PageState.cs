using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.UnitTests.V2Shell;

namespace TarkovCompanion.UnitTests.V2Plan;

/// <summary>[#902 P8] The target level, the station and the shopping scope outlive a refresh and a restart.</summary>
public sealed partial class HideoutWorkspaceViewModelTests
{
    [Fact]
    public async Task A_lowered_target_level_survives_a_refresh_a_level_edit_and_a_restart()
    {
        using var file = new LayoutFile();
        var requirements = new FakeRequirementCatalog
        {
            Stations =
            [
                new("generator", "Generator", [1, 2, 3]),
                new("lavatory", "Lavatory", [1, 2, 3]),
            ],
        };
        var viewModel = Hideout(requirements, file);
        await viewModel.RefreshAsync();
        viewModel.Select(viewModel.Stations.Single(station => station.StationId == "lavatory"));
        await WaitUntilAsync(() => viewModel.Upgrades.PathHeading.Contains('3', StringComparison.Ordinal));

        viewModel.Upgrades.LowerTargetCommand.Execute(null);
        Assert.Contains("2", viewModel.Upgrades.PathHeading, StringComparison.Ordinal);
        viewModel.Upgrades.Scopes.Single(scope => scope.Count == 10).SelectCommand.Execute(null);

        // Every visit refreshes the page and reads the stations again, as new objects. Compared by
        // reference, that reset the level to the top each time.
        await viewModel.RefreshAsync();
        Assert.Contains("2", viewModel.Upgrades.PathHeading, StringComparison.Ordinal);

        var restarted = Hideout(requirements, file);
        await restarted.RefreshAsync();

        Assert.Equal("Lavatory", restarted.SelectedStationName);
        Assert.Contains("2", restarted.Upgrades.PathHeading, StringComparison.Ordinal);
        Assert.True(restarted.Upgrades.Scopes.Single(scope => scope.Count == 10).IsSelected);
    }

    /// <summary>[#935] Reset everything replaced the layout: the shopping scope is back to its default at once.</summary>
    [Fact]
    public void A_reset_layout_puts_the_shopping_scope_back_without_a_restart()
    {
        using var file = new LayoutFile();
        var layout = file.Restart();
        var viewModel = new HideoutWorkspaceViewModel(
            new FakeRequirementCatalog(),
            new FakePlayerProfileService(TestProfile(
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, int>(StringComparer.Ordinal))),
            new FakeItemRepository(),
            learnMode: new LearnModeSetting(layout));
        viewModel.Upgrades.Scopes.Single(scope => scope.Count == 10).SelectCommand.Execute(null);

        layout.Replace(new Dictionary<string, string>());

        Assert.True(viewModel.Upgrades.Scopes.Single(scope => scope.Count == 5).IsSelected);
        Assert.False(viewModel.Upgrades.Scopes.Single(scope => scope.Count == 10).IsSelected);
    }

    private static HideoutWorkspaceViewModel Hideout(FakeRequirementCatalog requirements, LayoutFile file) => new(
        requirements,
        new FakePlayerProfileService(TestProfile(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.Ordinal))),
        new FakeItemRepository(),
        learnMode: new LearnModeSetting(file.Restart()));
}
