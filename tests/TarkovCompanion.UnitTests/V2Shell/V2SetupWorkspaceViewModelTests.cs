using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services.Workspaces;

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
    public void TheLastSectionSurvivesARecreatedWorkspaceAndResetReturnsToOverview()
    {
        var layout = new MemoryLayout();
        var first = new V2SetupWorkspaceViewModel(null, null, null, _ => { });
        first.AttachLayout(layout);
        first.Select(V2SetupSection.ProfileProgress);

        var afterRestart = new V2SetupWorkspaceViewModel(null, null, null, _ => { });
        afterRestart.AttachLayout(layout);

        Assert.Equal("ProfileProgress", layout.Get(WorkspaceLayoutKeys.SetupLastSection));
        Assert.True(afterRestart.IsProfileProgressSelected);

        layout.Replace(new Dictionary<string, string>());

        Assert.True(afterRestart.IsOverviewSelected);
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
            workspace.TrackerTitle, workspace.TrackerNote,
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

        // Test checklist is a ninth, last, only while its flag is on (dev and rough builds).
        string[] expected = ["Overview", "Game & Capture", "Profile & Progress", "Notifications", "Appearance & Window", "Data & Network", "Updates & Diagnostics", "About"];
        var offersChecklist = V2SetupWorkspaceViewModel.OffersTestChecklist;
        Assert.Equal(
            offersChecklist ? [.. expected, "Test checklist"] : expected,
            workspace.Sections.Select(section => section.Label));
        Assert.Equal(
            Enum.GetValues<V2SetupSection>().Where(section => offersChecklist || section != V2SetupSection.TestChecklist),
            workspace.Sections.Select(section => section.Section));
    }

    private sealed class MemoryLayout : IWorkspaceLayoutStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string value) => _values[key] = value;

        public IReadOnlyDictionary<string, string> Entries => _values;

        public event EventHandler? Replaced;

        public void Replace(IReadOnlyDictionary<string, string> entries)
        {
            _values.Clear();
            foreach (var entry in entries)
            {
                _values[entry.Key] = entry.Value;
            }

            Replaced?.Invoke(this, EventArgs.Empty);
        }
    }
}
