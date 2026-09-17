using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Setup;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>
/// #292's section switching and the readiness-to-section map that lets a Home checklist item
/// open the part of Setup that actually fixes it.
/// </summary>
public sealed class V2SetupWorkspaceViewModelTests
{
    [Fact]
    public void TheOverviewIsSelectedFirst()
    {
        var workspace = new V2SetupWorkspaceViewModel(null, null, null, _ => { });

        Assert.True(workspace.IsOverviewSelected);
        Assert.False(workspace.IsGameProfileSelected);
        Assert.True(Assert.Single(workspace.Sections, section => section.Section == V2SetupSection.Overview).IsCurrent);
        Assert.All(workspace.Sections.Where(section => section.Section != V2SetupSection.Overview), section => Assert.False(section.IsCurrent));
    }

    [Theory]
    [InlineData(V2SetupSection.GameProfile)]
    [InlineData(V2SetupSection.Recognition)]
    [InlineData(V2SetupSection.Data)]
    [InlineData(V2SetupSection.TeamDevices)]
    [InlineData(V2SetupSection.Privacy)]
    [InlineData(V2SetupSection.Diagnostics)]
    public void SelectingASectionMakesItTheOnlyCurrentOne(V2SetupSection section)
    {
        var workspace = new V2SetupWorkspaceViewModel(null, null, null, _ => { });

        workspace.Select(section);

        Assert.Equal(section, Assert.Single(workspace.Sections, tab => tab.IsCurrent).Section);
    }

    [Fact]
    public void EachReadinessCheckThatNamesSetupMapsToTheSectionThatFixesIt()
    {
        Assert.True(V2SetupWorkspaceViewModel.TryMapReadinessCheck("game-log", out var gameLog));
        Assert.Equal(V2SetupSection.GameProfile, gameLog);

        Assert.True(V2SetupWorkspaceViewModel.TryMapReadinessCheck("screenshots", out var screenshots));
        Assert.Equal(V2SetupSection.GameProfile, screenshots);

        Assert.True(V2SetupWorkspaceViewModel.TryMapReadinessCheck("text-recognition", out var recognition));
        Assert.Equal(V2SetupSection.Recognition, recognition);

        Assert.True(V2SetupWorkspaceViewModel.TryMapReadinessCheck("game-data", out var data));
        Assert.Equal(V2SetupSection.Data, data);
    }

    [Fact]
    public void AReadinessCheckThatDoesNotNameSetupHasNoSection() =>
        Assert.False(V2SetupWorkspaceViewModel.TryMapReadinessCheck("profile", out _));

    [Fact]
    public void OpeningTeamNavigatesToTheTeamRoute()
    {
        V2RouteId? navigated = null;
        var workspace = new V2SetupWorkspaceViewModel(null, null, null, route => navigated = route);

        workspace.OpenTeamCommand.Execute(null);

        Assert.Equal(V2Routes.Team, navigated);
    }

    [Fact]
    public void TheScaleCommandsDoNothingWithoutALegacyGraphInsteadOfThrowing()
    {
        var workspace = new V2SetupWorkspaceViewModel(null, null, null, _ => { });

        workspace.DecreaseScaleCommand.Execute(null);
        workspace.IncreaseScaleCommand.Execute(null);
        workspace.ResetScaleCommand.Execute(null);
    }
}
