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
        Assert.False(workspace.IsGameCaptureSelected);
        Assert.True(Assert.Single(workspace.Sections, section => section.Section == V2SetupSection.Overview).IsCurrent);
        Assert.All(workspace.Sections.Where(section => section.Section != V2SetupSection.Overview), section => Assert.False(section.IsCurrent));
    }

    [Theory]
    [InlineData(V2SetupSection.GameCapture)]
    [InlineData(V2SetupSection.ProfileProgress)]
    [InlineData(V2SetupSection.Notifications)]
    [InlineData(V2SetupSection.AppearanceWindow)]
    [InlineData(V2SetupSection.DataNetwork)]
    [InlineData(V2SetupSection.UpdatesDiagnostics)]
    [InlineData(V2SetupSection.About)]
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
        Assert.Equal(V2SetupSection.GameCapture, gameLog);

        Assert.True(V2SetupWorkspaceViewModel.TryMapReadinessCheck("screenshots", out var screenshots));
        Assert.Equal(V2SetupSection.GameCapture, screenshots);

        Assert.True(V2SetupWorkspaceViewModel.TryMapReadinessCheck("text-recognition", out var recognition));
        Assert.Equal(V2SetupSection.GameCapture, recognition);

        Assert.True(V2SetupWorkspaceViewModel.TryMapReadinessCheck("game-data", out var data));
        Assert.Equal(V2SetupSection.DataNetwork, data);
    }

    [Fact]
    public void AReadinessCheckThatDoesNotNameSetupHasNoSection() =>
        Assert.False(V2SetupWorkspaceViewModel.TryMapReadinessCheck("profile", out _));

    [Fact]
    public void OpeningTeamNavigatesToWhereTheSharingSwitchesAre()
    {
        V2RouteId? navigated = null;
        var workspace = new V2SetupWorkspaceViewModel(null, null, null, route => navigated = route);

        workspace.OpenTeamCommand.Execute(null);

        Assert.Equal(V2Routes.Group, navigated);
    }

    [Fact]
    public void TheScaleCommandsDoNothingWithoutALegacyGraphInsteadOfThrowing()
    {
        var workspace = new V2SetupWorkspaceViewModel(null, null, null, _ => { });

        workspace.DecreaseScaleCommand.Execute(null);
        workspace.IncreaseScaleCommand.Execute(null);
        workspace.ResetScaleCommand.Execute(null);
    }

    [Fact]
    public void ProgressIsASectionWhereV1KeptTheQuestExchangeAndTarkovTracker()
    {
        var workspace = new V2SetupWorkspaceViewModel(null, null, null, _ => { });

        Assert.Contains(workspace.Sections, section => section.Section == V2SetupSection.ProfileProgress);
        Assert.False(workspace.IsProfileProgressSelected);

        workspace.Select(V2SetupSection.ProfileProgress);

        Assert.True(workspace.IsProfileProgressSelected);
        Assert.False(workspace.IsOverviewSelected);
    }

    [Fact]
    public void QuestOnboardingOfferOpensProgress()
    {
        var workspace = new V2SetupWorkspaceViewModel(null, null, null, _ => { });

        workspace.OpenQuestSyncCommand.Execute(null);

        Assert.True(workspace.IsProfileProgressSelected);
    }

    [Fact]
    public void EverySectionOfV1SettingsHasAHomeInSetup()
    {
        var workspace = new V2SetupWorkspaceViewModel(null, null, null, _ => { });

        // A label that has no text in the table throws when read, so this also catches a control that
        // was added to the view without its copy.
        var labels = new[]
        {
            workspace.FolderPlaceholder, workspace.ScanHint, workspace.OfflineNote, workspace.RetentionValueLabel,
            workspace.RecycleNote, workspace.ScaleHint, workspace.ScaleScope, workspace.LogLabel, workspace.RelayNote,
            workspace.ExchangeTitle, workspace.ExchangeNote, workspace.ExchangePathPlaceholder, workspace.ExportLabel,
            workspace.PreviewImportLabel, workspace.KeepLocalLabel, workspace.UseIncomingLabel, workspace.ApplyImportLabel,
            workspace.UndoImportLabel, workspace.ImportHistoryTitle, workspace.TrackerTitle, workspace.TrackerNote,
            workspace.TrackerTokenPlaceholder, workspace.TrackerConnectLabel, workspace.TrackerRefreshLabel,
            workspace.TrackerDisconnectLabel,
        };

        Assert.All(labels, label => Assert.False(string.IsNullOrWhiteSpace(label)));
        Assert.All(labels, label => Assert.True(label.Length <= 120, $"'{label}' is longer than a label may be."));
    }

    /// <summary>[#902 P6] Eight sections, in the proposal's order, each named on its tab.</summary>
    [Fact]
    public void SetupHasEightSectionsInOrder()
    {
        var workspace = new V2SetupWorkspaceViewModel(null, null, null, _ => { });

        Assert.Equal(
            ["Overview", "Game & Capture", "Profile & Progress", "Notifications", "Appearance & Window", "Data & Network", "Updates & Diagnostics", "About"],
            workspace.Sections.Select(section => section.Label));
        Assert.Equal(Enum.GetValues<V2SetupSection>(), workspace.Sections.Select(section => section.Section));
    }
}
