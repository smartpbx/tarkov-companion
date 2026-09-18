using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>Static host checks complement router tests: they prevent a preview from quietly becoming V1 again.</summary>
public sealed class V2ShellHostContractTests
{
    [Theory]
    [InlineData(null, V2ShellMode.VariantA)]
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
        Assert.Contains("Content=\"{Binding LegacyPage}\"", shell, StringComparison.Ordinal);
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
        Assert.Contains("AutomationProperties.AutomationId=\"v2-shell-dialog\"", shell, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding DialogAutomationName}\"", shell, StringComparison.Ordinal);
        foreach (var state in Enum.GetNames<V2SurfaceStateKind>()) Assert.Contains($"V2SurfaceStateKind.{state}", states, StringComparison.Ordinal);
    }

    [Fact]
    public void A_global_problem_has_exactly_one_presentation_in_the_shell()
    {
        // V2 rough package 20: the shell drew the surface state twice — a compact banner under
        // the top bar and an identical card in the page body below it. On Raid the second copy
        // cost the map roughly 400px of height on a 1080-high window.
        var shell = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Views", "V2", "Shell", "V2ShellView.axaml"));

        Assert.Equal(1, shell.Split("AutomationProperties.AutomationId=\"v2-shell-surface-state\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("IsVisible=\"{Binding ShowsSurfaceBanner}\"", shell, StringComparison.Ordinal);
        // One line: the detail, the recovery actions, a dismiss. No remainder sub-line, no dashed
        // outline, and it never wraps to a second row.
        Assert.Contains("Text=\"{Binding SurfaceBannerText}\"", shell, StringComparison.Ordinal);
        Assert.Contains("v2-shell-surface-state-dismiss", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("v2-banner-outline", shell, StringComparison.Ordinal);
        // The rest of the story lives behind the status pill instead.
        Assert.Contains("v2-shell-health-surface", shell, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowsHealthSurface}\"", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void The_raid_map_draws_icons_and_never_a_placeholder_question_mark()
    {
        // V2 rough package 20: Clayton's Streets screenshot showed "P/S ?", "arrow ?" and "S ?"
        // in yellow boxes over the plan — text glyphs and letter badges, not markers.
        var map = File.ReadAllText(V2ShellTestData.RepositoryPath(
            "src", "TarkovCompanion.App", "Views", "V2", "MapRenderer", "MapSceneRendererView.axaml"));
        var raid = File.ReadAllText(V2ShellTestData.RepositoryPath(
            "src", "TarkovCompanion.App", "Views", "V2", "Raid", "RaidCockpitView.axaml"));

        // V2 rough package 22: the glyph chip is now ShowsGlyphIcon, because a person on the
        // plan (you, a squadmate) is drawn as a dot with a facing cone instead of a chip.
        Assert.Contains("IsVisible=\"{Binding ShowsGlyphIcon}\"", map, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsPersonIcon}\"", map, StringComparison.Ordinal);
        Assert.Contains("Data=\"{Binding ConeGeometry}\"", map, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource V2.Icon.Exit}", map, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding HasMarkerNumber}\"", map, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding FactionGlyph}\"", map, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding OfferGlyph}\"", map, StringComparison.Ordinal);
        // The four-sentence dense-scene notice is a chip with its wording in a tooltip.
        Assert.Contains("Text=\"{Binding DenseSceneChip}\"", map, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"{Binding DenseSceneNotice}\"", map, StringComparison.Ordinal);
        // Every mark has a visible Remove, whether or not any marks exist yet.
        Assert.Contains("v2-raid-mark-remove", raid, StringComparison.Ordinal);
        Assert.DoesNotContain("IsVisible=\"{Binding HasMarks}\"", raid, StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_context_persistence_and_suggestions_are_real_bound_shell_surfaces()
    {
        // V2 rough package 17: search and suggestions moved into the Intel workspace view, which
        // binds the same shell view model.
        var shell = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Views", "V2", "Shell", "V2ShellView.axaml")) +
            File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Views", "V2", "Intel", "IntelWorkspaceView.axaml"));

        foreach (var binding in new[]
        {
            "CaptureIntents", "CaptureProgressItems", "CaptureAttentionActions", "CaptureReviewActions",
            "CaptureReference", "ProfileContextLabel", "LocalTimeLabel", "RaidContextLabel", "PlanContextLabel",
            "TeamContextLabel", "DeviceContextLabel", "SelectionContextLabel", "FilteredSuggestionItems",
            "SuggestionFilters", "PersistenceFailure", "RetryPersistenceCommand",
        })
        {
            Assert.Contains($"{{Binding {binding}}}", shell, StringComparison.Ordinal);
        }

        Assert.Contains("Mode=TwoWay", shell, StringComparison.Ordinal);
        Assert.Contains("v2-shell-capture-arm", shell, StringComparison.Ordinal);
        Assert.Contains("v2-shell-capture-attention", shell, StringComparison.Ordinal);
        Assert.Contains("v2-shell-capture-review", shell, StringComparison.Ordinal);
        Assert.Contains("v2-shell-persistence-retry", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void Variant_placements_and_measured_narrow_reflow_are_bound_into_the_host()
    {
        var shell = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Views", "V2", "Shell", "V2ShellView.axaml")) +
            File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Views", "V2", "Intel", "IntelWorkspaceView.axaml"));
        var model = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "ViewModels", "V2", "Shell", "V2ShellViewModel.cs"));
        var view = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Views", "V2", "Shell", "V2ShellView.axaml.cs"));

        foreach (var binding in new[]
        {
            "UsesRailNavigation", "UsesRowNavigation", "ShowsHeaderSetup", "ShowsSeparatedSetup",
            "ShowsHeaderSearch", "ShowsWorkspaceSearch", "ShowsPrimaryContent", "ShellBodyRowSpan",
            "IntelColumn", "IntelColumnSpan",
        })
        {
            Assert.Contains($"{{Binding {binding}}}", shell, StringComparison.Ordinal);
        }

        Assert.Contains("shell.UpdateEffectiveWidth(Bounds.Width, focusedAutomationId)", view, StringComparison.Ordinal);
        Assert.Contains("V2ShellAdaptation.Classify(effectiveWidth)", model, StringComparison.Ordinal);
        Assert.Contains("v2-shell-navigation-rail", shell, StringComparison.Ordinal);
        Assert.Contains("v2-shell-navigation-row", shell, StringComparison.Ordinal);
        Assert.Contains("<Style Selector=\"Button.v2-destination\">", shell, StringComparison.Ordinal);
        Assert.Contains("Button.v2-destination /template/ ContentPresenter#PART_ContentPresenter", shell, StringComparison.Ordinal);
        Assert.Contains("Changing border geometry", shell, StringComparison.Ordinal);
        Assert.Contains("<ContentControl Grid.Row=\"1\" IsVisible=\"{Binding ShowsLegacyPage}\"", shell, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding SectionItems}\"", shell, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"v2-shell-sections\"", shell, StringComparison.Ordinal);
        // V2 rough package 21: the primary destinations row dropped DisplayLabel's "› " current
        // marker (it changed that button's measured content length inside the same WrapPanel the
        // "Changing border geometry" comment above already warns about, and Variant B's own
        // landing page — Home — starts current, so this row hit the hazard on its very first
        // narrow layout). Setup's separate, non-wrapping entries still carry the marker.
        Assert.Contains("Content=\"{Binding SetupDestination.DisplayLabel}\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"{Binding DisplayLabel}\"", shell, StringComparison.Ordinal);
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
        Assert.Contains("_wiredShell?.FocusFallbackTarget", view, StringComparison.Ordinal);
        Assert.Contains("focusedAutomationId", view, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding Name}\"", File.ReadAllText(
            V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Views", "MainWindow.axaml")), StringComparison.Ordinal);
    }

    [Fact]
    public void Readiness_actions_are_contextual_focusable_buttons_with_a_heading_fallback()
    {
        var shell = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Views", "V2", "Shell", "V2ShellView.axaml"));
        var model = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "ViewModels", "V2", "Shell", "V2ShellViewModel.cs"));

        var readinessStart = shell.IndexOf("<ItemsControl ItemsSource=\"{Binding ReadinessItems}\">", StringComparison.Ordinal);
        var readinessEnd = shell.IndexOf("<StackPanel Classes=\"v2-stack\" IsVisible=\"{Binding ShowsContinue}\">", readinessStart, StringComparison.Ordinal);
        var readinessTemplate = shell[readinessStart..readinessEnd];
        Assert.Contains("<Button Grid.Column=\"2\"", readinessTemplate, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"{Binding AutomationId}\"", readinessTemplate, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding AutomationName}\"", readinessTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain("<Border Classes=\"v2-card\" Margin=\"0,0,0,8\"\n                              AutomationProperties", readinessTemplate, StringComparison.Ordinal);
        Assert.Contains("V2ShellFocusTargets.ReadinessTarget(check.Id)", model, StringComparison.Ordinal);
        Assert.Contains("Readiness.RequiredCount", model, StringComparison.Ordinal);
        Assert.DoesNotContain("Readiness.Checks.Count,", model, StringComparison.Ordinal);
    }

    [Fact]
    public void Window_title_and_reset_close_lifecycle_are_live_and_dispatcher_independent()
    {
        var main = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Views", "MainWindow.axaml"));
        var legacyModel = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "ViewModels", "MainWindowViewModel.cs"));
        var shellModel = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "ViewModels", "V2", "Shell", "V2ShellViewModel.cs"));

        Assert.Contains("Title=\"{Binding WindowTitle}\"", main, StringComparison.Ordinal);
        Assert.Contains("_previewShell.PropertyChanged += PreviewShellPropertyChanged", legacyModel, StringComparison.Ordinal);
        Assert.Contains("nameof(WindowTitle)", legacyModel, StringComparison.Ordinal);
        Assert.Contains("await _persistence.ResetAsync().ConfigureAwait(false)", shellModel, StringComparison.Ordinal);
        Assert.Contains("_lifetime.Cancel()", shellModel, StringComparison.Ordinal);
        Assert.Contains("suppressFinalSave: resetWasInProgress", shellModel, StringComparison.Ordinal);
        Assert.DoesNotContain("_saveSuspended", shellModel, StringComparison.Ordinal);
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
        Assert.Contains("CaptureState.CorrelationId ?? continuity.CaptureCorrelationId", model, StringComparison.Ordinal);
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
        Assert.Contains("shell-v2-a-stale-focus", gallery, StringComparison.Ordinal);
        Assert.Contains("shell-v2-a-tablet-link", gallery, StringComparison.Ordinal);
        Assert.Contains("shell-v2-b-tablet-link", gallery, StringComparison.Ordinal);
        Assert.Contains("shell-v2-reset-close", gallery, StringComparison.Ordinal);
        Assert.Contains("v2-shell-readiness-profile", gallery, StringComparison.Ordinal);
        Assert.Contains("v2-shell-section-plan.loadout", gallery, StringComparison.Ordinal);
        Assert.Contains("expectedFocusAutomationId", gallery, StringComparison.Ordinal);
        Assert.Contains("expectedDialogName", gallery, StringComparison.Ordinal);
        Assert.Contains("gracefulShutdown", gallery, StringComparison.Ordinal);
        Assert.Contains("interactionSmoke", gallery, StringComparison.Ordinal);
        Assert.Contains("Save-ScreenImage", gallery, StringComparison.Ordinal);
    }
}
