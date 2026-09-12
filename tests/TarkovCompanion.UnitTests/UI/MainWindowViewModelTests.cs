using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels;

namespace TarkovCompanion.UnitTests.UI;

public sealed class MainWindowViewModelTests
{
    private static readonly string[] ExpectedDestinations =
    [
        "Raid",
        "Squad",
        "Scanner",
        "Items",
        "Ammo",
        "Keys",
        "Flea",
        "Quests",
        "Hideout",
        "Events",
        "Loadout",
        "History",
        "Settings",
    ];

    [Fact]
    public async Task NavigationContainsEveryV1DestinationInOrder()
    {
        await using var services = CreateServices();
        var viewModel = services.GetRequiredService<MainWindowViewModel>();

        Assert.Equal(ExpectedDestinations, viewModel.Navigation.Select(item => item.Name));
        Assert.IsType<RaidPageViewModel>(viewModel.CurrentPage);
        Assert.Single(viewModel.Navigation, item => item.IsSelected);
    }

    [Theory]
    [InlineData("Scanner", typeof(ScannerPageViewModel))]
    [InlineData("Items", typeof(ItemsPageViewModel))]
    [InlineData("Settings", typeof(SettingsPageViewModel))]
    public async Task NavigateSelectsOneDestinationAndUpdatesThePage(string destination, Type expectedPageType)
    {
        await using var services = CreateServices();
        var viewModel = services.GetRequiredService<MainWindowViewModel>();

        var found = viewModel.Navigate(destination);

        Assert.True(found);
        Assert.IsType(expectedPageType, viewModel.CurrentPage);
        Assert.Equal(destination, Assert.Single(viewModel.Navigation, item => item.IsSelected).Name);
    }

    [Fact]
    public async Task UnknownDestinationLeavesTheCurrentPageUnchanged()
    {
        await using var services = CreateServices();
        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        var originalPage = viewModel.CurrentPage;

        var found = viewModel.Navigate("Not a page");

        Assert.False(found);
        Assert.Same(originalPage, viewModel.CurrentPage);
        Assert.Equal("Raid", Assert.Single(viewModel.Navigation, item => item.IsSelected).Name);
    }

    [Fact]
    public async Task InitialDemoStateIsExplicitlyFixtureBackedAndContainsNoFabricatedObservations()
    {
        await using var services = CreateServices();
        var viewModel = services.GetRequiredService<MainWindowViewModel>();

        Assert.Equal(["EFT", "Map", "Raid", "Position", "Data", "Scan"], viewModel.Status.Select(status => status.Label));
        Assert.Contains("no live game access", viewModel.ModeLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("No item scanned", viewModel.LastScanName);
        Assert.Contains("fixture", viewModel.LastScanEvidence, StringComparison.OrdinalIgnoreCase);
        Assert.All(viewModel.Status, status => Assert.False(string.IsNullOrWhiteSpace(status.Evidence)));
    }

    private static ServiceProvider CreateServices() => AppComposition.Build(
        new AppCommandLine(false, true, false, false, null, null, null),
        new(DataRoot: Path.Combine(Path.GetTempPath(), $"tarkov-ui-{Guid.NewGuid():N}"), Offline: true));
}
