using TarkovCompanion.App.ViewModels;

namespace TarkovCompanion.UnitTests.UI;

public sealed class MainWindowViewModelTests
{
    private static readonly string[] ExpectedDestinations =
    [
        "Raid",
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
    public void NavigationContainsEveryV1DestinationInOrder()
    {
        var viewModel = MainWindowViewModel.CreateFoundationDemo(demoMode: true);

        Assert.Equal(ExpectedDestinations, viewModel.Navigation.Select(item => item.Name));
        Assert.IsType<RaidPageViewModel>(viewModel.CurrentPage);
        Assert.Single(viewModel.Navigation, item => item.IsSelected);
    }

    [Theory]
    [InlineData("Scanner", typeof(ScannerPageViewModel))]
    [InlineData("Items", typeof(ItemsPageViewModel))]
    [InlineData("Settings", typeof(SettingsPageViewModel))]
    public void NavigateSelectsOneDestinationAndUpdatesThePage(string destination, Type expectedPageType)
    {
        var viewModel = MainWindowViewModel.CreateFoundationDemo(demoMode: true);

        var found = viewModel.Navigate(destination);

        Assert.True(found);
        Assert.IsType(expectedPageType, viewModel.CurrentPage);
        Assert.Equal(destination, Assert.Single(viewModel.Navigation, item => item.IsSelected).Name);
    }

    [Fact]
    public void UnknownDestinationLeavesTheCurrentPageUnchanged()
    {
        var viewModel = MainWindowViewModel.CreateFoundationDemo(demoMode: true);
        var originalPage = viewModel.CurrentPage;

        var found = viewModel.Navigate("Not a page");

        Assert.False(found);
        Assert.Same(originalPage, viewModel.CurrentPage);
        Assert.Equal("Raid", Assert.Single(viewModel.Navigation, item => item.IsSelected).Name);
    }

    [Fact]
    public void DemoModeExposesCompleteEvidenceStrip()
    {
        var viewModel = MainWindowViewModel.CreateFoundationDemo(demoMode: true);

        Assert.Equal(["EFT", "Map", "Raid", "Time", "Position", "Data", "Scan"], viewModel.Status.Select(status => status.Label));
        Assert.All(viewModel.Status, status => Assert.False(string.IsNullOrWhiteSpace(status.Evidence)));
        Assert.Contains("Linux-safe demo", viewModel.ModeLabel, StringComparison.Ordinal);
    }
}
