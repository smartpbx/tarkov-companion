using TarkovCompanion.App.Services.V2;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.V2Shell;

public sealed class TabletWorkspaceNavigationTests
{
    [Theory]
    [InlineData(WorkspaceKind.Raid, "raid")]
    [InlineData(WorkspaceKind.Intel, "intel")]
    [InlineData(WorkspaceKind.Plan, "plan")]
    [InlineData(WorkspaceKind.Team, "team")]
    [InlineData(WorkspaceKind.Debrief, "debrief")]
    public async Task EveryTabletWorkspaceNavigatesTheRealV2Shell(WorkspaceKind workspace, string address)
    {
        var config = V2ShellTestData.TemporaryDirectory();
        try
        {
            var runtime = new RuntimeStateStore(new RuntimeOptions(
                false,
                false,
                GameMode.Regular,
                "en",
                TimeSpan.FromHours(9),
                TimeSpan.FromMinutes(5)));
            await using var shell = new V2ShellViewModel(V2ShellMode.VariantA, config, runtime);
            var navigator = new TabletWorkspaceNavigation(
                () => shell.Router.Current.Location.Route,
                shell.GoTo);

            Assert.True(navigator.TryNavigate(workspace));
            Assert.Equal($"#/{address}", shell.CurrentAddress);
        }
        finally
        {
            Directory.Delete(config, recursive: true);
        }
    }

    [Fact]
    public async Task AnUnknownWorkspaceCannotMoveTheDesktop()
    {
        var config = V2ShellTestData.TemporaryDirectory();
        try
        {
            var runtime = new RuntimeStateStore(new RuntimeOptions(
                false,
                false,
                GameMode.Regular,
                "en",
                TimeSpan.FromHours(9),
                TimeSpan.FromMinutes(5)));
            await using var shell = new V2ShellViewModel(V2ShellMode.VariantA, config, runtime);
            var navigator = new TabletWorkspaceNavigation(
                () => shell.Router.Current.Location.Route,
                shell.GoTo);
            var before = shell.CurrentAddress;

            Assert.False(navigator.TryNavigate((WorkspaceKind)999));
            Assert.Equal(before, shell.CurrentAddress);
        }
        finally
        {
            Directory.Delete(config, recursive: true);
        }
    }
}
