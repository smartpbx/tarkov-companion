using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>Static host checks complement router tests: they prevent a preview from quietly becoming V1 again.</summary>
public sealed class V2ShellHostContractTests
{
    [Theory]
    [InlineData(null, V2ShellMode.Legacy)]
    [InlineData("legacy", V2ShellMode.Legacy)]
    [InlineData("v2-a", V2ShellMode.VariantA)]
    [InlineData("v2-b", V2ShellMode.VariantB)]
    public void ProcessStartSelectsExactlyOneShell(string? token, V2ShellMode expected)
    {
        string[] arguments = token is null ? [] : ["--ui-shell", token];
        Assert.Equal(expected, AppCommandLine.Parse(arguments).UiShell);
    }

    [Fact]
    public void PreviewHostUsesTheSharedRouterAndKeepsTheV1TransformInItsOwnHost()
    {
        var main = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Views", "MainWindow.axaml"));
        var shell = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Views", "V2", "Shell", "V2ShellView.axaml"));
        var model = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "ViewModels", "V2", "Shell", "V2ShellViewModel.cs"));

        Assert.Contains("LegacyPageHost", main, StringComparison.Ordinal);
        Assert.Contains("V2ShellView", main, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsPreviewShell}\"", main, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding Legacy.CurrentPage}\"", shell, StringComparison.Ordinal);
        Assert.Contains("new V2ShellRouter(Variant, Registry)", model, StringComparison.Ordinal);
        Assert.DoesNotContain("LayoutTransformControl", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void DialogHostAndAllRequiredSurfaceStatesAreExplicitInThePreviewHost()
    {
        var shell = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Views", "V2", "Shell", "V2ShellView.axaml"));
        var states = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Services", "V2", "Shell", "V2SurfaceStates.cs"));

        foreach (var id in new[]
        {
            "v2-shell-capture-dialog-title",
            "v2-shell-palette-dialog-title",
            "v2-shell-health-dialog-title",
        })
        {
            Assert.Equal(1, shell.Split(id, StringSplitOptions.None).Length - 1);
        }
        Assert.Contains("IsVisible=\"{Binding HasOpenDialog}\"", shell, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding HasNoDialog}\"", shell, StringComparison.Ordinal);
        foreach (var state in Enum.GetNames<V2SurfaceStateKind>()) Assert.Contains($"V2SurfaceStateKind.{state}", states, StringComparison.Ordinal);
    }

    [Fact]
    public void Variant_placements_and_measured_narrow_reflow_are_bound_into_the_host()
    {
        var shell = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Views", "V2", "Shell", "V2ShellView.axaml"));
        var model = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "ViewModels", "V2", "Shell", "V2ShellViewModel.cs"));
        var view = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Views", "V2", "Shell", "V2ShellView.axaml.cs"));

        foreach (var binding in new[]
        {
            "UsesRailNavigation", "UsesRowNavigation", "ShowsHeaderSetup", "ShowsSeparatedSetup",
            "ShowsHeaderSearch", "ShowsWorkspaceSearch", "ShowsPrimaryContent", "IntelColumn", "IntelColumnSpan",
        })
        {
            Assert.Contains($"{{Binding {binding}}}", shell, StringComparison.Ordinal);
        }

        Assert.Contains("shell.UpdateEffectiveWidth(Bounds.Width)", view, StringComparison.Ordinal);
        Assert.Contains("V2ShellAdaptation.Classify(effectiveWidth)", model, StringComparison.Ordinal);
        Assert.Contains("v2-shell-navigation-rail", shell, StringComparison.Ordinal);
        Assert.Contains("v2-shell-navigation-row", shell, StringComparison.Ordinal);
        Assert.Contains("<Style Selector=\"Button.v2-destination\">", shell, StringComparison.Ordinal);
        Assert.Contains("Changing border geometry", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_lifecycle_keyboard_and_focus_stay_inside_the_preview_boundary()
    {
        var app = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "App.axaml.cs"));
        var window = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Views", "MainWindow.axaml.cs"));
        var view = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Views", "V2", "Shell", "V2ShellView.axaml.cs"));

        Assert.DoesNotContain("GetService<V2ShellViewModel>()", app, StringComparison.Ordinal);
        Assert.Contains("_mainViewModel?.PreviewShell", app, StringComparison.Ordinal);
        Assert.Contains("await preview.DisposeAsync()", app, StringComparison.Ordinal);
        var keyHandler = window.IndexOf("private void WindowKeyDown", StringComparison.Ordinal);
        var previewBoundary = window.IndexOf("if (viewModel.PreviewShell is { } preview)", keyHandler, StringComparison.Ordinal);
        var legacyKeys = window.IndexOf("if (eventArgs.KeyModifiers == KeyModifiers.Control)", previewBoundary, StringComparison.Ordinal);
        Assert.InRange(previewBoundary, 0, legacyKeys - 1);
        Assert.Contains("return;", window[previewBoundary..legacyKeys], StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.GetAutomationId", window, StringComparison.Ordinal);
        Assert.Contains("_wiredShell.FocusRequested -= FocusRequested", view, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.GetAutomationId(control)", view, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding Name}\"", File.ReadAllText(
            V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Views", "MainWindow.axaml")), StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_search_surface_policy_and_continuity_are_wired_to_real_shell_actions()
    {
        var model = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "ViewModels", "V2", "Shell", "V2ShellViewModel.cs"));

        Assert.Contains("CoalescingDispatch", model, StringComparison.Ordinal);
        Assert.Contains("Legacy.Items.SearchCommand.ExecuteAsync()", model, StringComparison.Ordinal);
        Assert.Contains("Surface.Policy.PlayerAction", model, StringComparison.Ordinal);
        Assert.Contains("Surface.Policy.FocusesOnPlayerAction", model, StringComparison.Ordinal);
        Assert.Contains("ProfileMode = snapshot.Profile?.GameMode.ToString()", model, StringComparison.Ordinal);
        Assert.Contains("RaidId = snapshot.Raid.RaidId", model, StringComparison.Ordinal);
        Assert.Contains("TeamMemberKeys = snapshot.Squad.Members", model, StringComparison.Ordinal);
        Assert.Contains("CaptureCorrelationId = continuity.CaptureCorrelationId", model, StringComparison.Ordinal);
    }

    [Fact]
    public void Packaged_gallery_interacts_with_and_captures_all_three_shell_modes()
    {
        var gallery = File.ReadAllText(V2ShellTestData.RepositoryPath("scripts", "windows-page-gallery.ps1"));

        Assert.Contains("UIAutomationClient", gallery, StringComparison.Ordinal);
        Assert.Contains("[System.Windows.Automation.InvokePattern]::Pattern", gallery, StringComparison.Ordinal);
        Assert.Contains("shell-legacy", gallery, StringComparison.Ordinal);
        Assert.Contains("shell-v2-a", gallery, StringComparison.Ordinal);
        Assert.Contains("shell-v2-b", gallery, StringComparison.Ordinal);
        Assert.Contains("shell-v2-a-narrow", gallery, StringComparison.Ordinal);
        Assert.Contains("interactionSmoke", gallery, StringComparison.Ordinal);
        Assert.Contains("Save-ScreenImage", gallery, StringComparison.Ordinal);
    }
}
