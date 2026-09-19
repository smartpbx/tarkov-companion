using System.Text.RegularExpressions;
using System.Xml.Linq;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Raid;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// [V2 rough package 46] The chrome that was taken off the map cannot grow back.
/// </summary>
/// <remarks>
/// Clayton, from a raid: "The left sidebar should be collapsible, the right one is too wide,
/// maybe we can make it adjustable or something. More map is better. Maybe we can reduce the
/// bottom filters as well to a simple menu or half the height so there is more map space
/// available."
///
/// Measured at 1920x1080 with tools/V2RenderPreview, the map card went from 1231x785 — 46.6% of
/// the window — to 1344x865 (56.1%) with the rail labelled and the panel at its new default, and
/// to 1854x865 (77.4%) with both put away. Every pixel of that came from three numbers and one
/// layout, and each of them is a number somebody could quietly restore. The Windows page gallery
/// holds the floor on the card itself (a fraction of the window, so the runner's scaling cannot
/// decide it); these hold the pieces, on the side of CI that blocks a merge.
/// </remarks>
public sealed class RaidMapSpaceTests
{
    private const string CockpitView = "src/TarkovCompanion.App/Views/V2/Raid/RaidCockpitView.axaml";
    private const string ShellView = "src/TarkovCompanion.App/Views/V2/Shell/V2ShellView.axaml";

    [Fact]
    public void The_navigation_rail_collapses_to_icons_and_then_away()
    {
        Assert.Equal(V2NavigationRail.Icons, Cycle(V2NavigationRail.Labels));
        Assert.Equal(V2NavigationRail.Hidden, Cycle(V2NavigationRail.Icons));
        Assert.Equal(V2NavigationRail.Labels, Cycle(V2NavigationRail.Hidden));

        // Round-trips through the remembered preview state, including a file that predates it.
        foreach (var rail in new[] { V2NavigationRail.Labels, V2NavigationRail.Icons, V2NavigationRail.Hidden })
        {
            Assert.Equal(rail, V2NavigationRailTokens.Parse(rail.ToToken()));
        }

        Assert.Equal(V2NavigationRail.Labels, V2NavigationRailTokens.Parse(null));
        Assert.Equal(V2NavigationRail.Labels, V2NavigationRailTokens.Parse("something else entirely"));

        static V2NavigationRail Cycle(V2NavigationRail from) => from switch
        {
            V2NavigationRail.Labels => V2NavigationRail.Icons,
            V2NavigationRail.Icons => V2NavigationRail.Hidden,
            _ => V2NavigationRail.Labels,
        };
    }

    [Fact]
    public void Ctrl_B_is_the_documented_way_to_collapse_the_rail()
    {
        // In the command table rather than a bare KeyBinding, so the command palette lists it and
        // a player who has never pressed it can still find it.
        var commands = V2ShellCommands.For(V2ShellVariants.For(V2ShellMode.VariantA));
        var rail = Assert.Single(commands, command => command.Kind == V2ShellCommandKind.CycleNavigationRail);
        Assert.Equal("Ctrl+B", rail.Gesture);
    }

    [Fact]
    public void The_raid_context_panel_starts_narrower_than_it_could_ever_be_before()
    {
        // It was a proportional column with MinWidth 420, so on a 1920-wide window it never gave
        // the map less than 420 pixels and in practice took about 460.
        Assert.True(
            RaidCockpitViewModel.DefaultContextPanelWidth < 420,
            $"The default panel width is {RaidCockpitViewModel.DefaultContextPanelWidth}, no narrower than the floor it replaced.");
        Assert.True(RaidCockpitViewModel.MinimumContextPanelWidth < RaidCockpitViewModel.DefaultContextPanelWidth);
        Assert.True(RaidCockpitViewModel.MaximumContextPanelWidth > RaidCockpitViewModel.DefaultContextPanelWidth);

        var view = XDocument.Load(RepositoryFile(CockpitView));
        // The column itself is Auto now: a star column would size the panel from the window and
        // hand the remembered width back to the layout.
        var columns = view.Descendants()
            .First(element => element.Name.LocalName == "Grid" && element.Attribute("ColumnDefinitions") is not null)
            .Attribute("ColumnDefinitions")!.Value;
        Assert.Equal("*,Auto,Auto", columns);
        Assert.Contains("Width=\"{Binding ContextPanelWidth}\"", File.ReadAllText(RepositoryFile(CockpitView)), StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowsContextPanel}\"", File.ReadAllText(RepositoryFile(CockpitView)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_remembered_width_is_clamped_rather_than_trusted()
    {
        // The file is the player's to edit and a relay is not involved, but a width of zero or a
        // width of a million is still a map nobody can see, and a truncated file is ordinary.
        Assert.Equal(RaidCockpitViewModel.MaximumContextPanelWidth, RaidCockpitViewModel.ClampContextPanelWidth(10_000));
        Assert.Equal(RaidCockpitViewModel.MinimumContextPanelWidth, RaidCockpitViewModel.ClampContextPanelWidth(-5));
        Assert.Equal(RaidCockpitViewModel.MinimumContextPanelWidth, RaidCockpitViewModel.ClampContextPanelWidth(0));
        Assert.Equal(RaidCockpitViewModel.DefaultContextPanelWidth, RaidCockpitViewModel.ClampContextPanelWidth(double.NaN));
        Assert.Equal(RaidCockpitViewModel.DefaultContextPanelWidth, RaidCockpitViewModel.ClampContextPanelWidth(double.PositiveInfinity));
        Assert.Equal(400, RaidCockpitViewModel.ClampContextPanelWidth(400));
    }

    [Fact]
    public void The_bottom_strip_is_one_row_with_the_layers_behind_a_button()
    {
        // Fourteen switches in a WrapPanel wrapped to two rows and took 110 pixels of a 1080-tall
        // window. The button that replaced them says how many layers are on, so folding them away
        // did not also fold away what is drawn.
        var view = File.ReadAllText(RepositoryFile(CockpitView));
        var strip = Regex.Match(view, @"Classes=""v2-raid-strip"".*?</Border>", RegexOptions.Singleline);
        Assert.True(strip.Success, "The bottom strip is not where this test expects it.");
        Assert.DoesNotContain("<WrapPanel", strip.Value, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"v2-raid-layers\"", strip.Value, StringComparison.Ordinal);
        Assert.Contains("<Flyout", strip.Value, StringComparison.Ordinal);
        Assert.Contains("{Binding LayersMenuLabel}", strip.Value, StringComparison.Ordinal);
        // The switches themselves are still there, one press away, with their own state on them.
        Assert.Contains("Classes=\"v2-layer-switch\"", strip.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void The_windows_gallery_holds_a_floor_under_the_map_card()
    {
        // The measurement the Windows page gallery makes, kept honest here so a change to the
        // script's numbers is a change somebody had to make deliberately. 0.641 x 0.727 was the
        // old layout at 1920x1080, so both floors are above it.
        var gallery = File.ReadAllText(RepositoryFile("scripts/windows-page-gallery.ps1"));
        Assert.Contains("minimumWindowWidthFraction = 0.66", gallery, StringComparison.Ordinal);
        Assert.Contains("minimumWindowHeightFraction = 0.75", gallery, StringComparison.Ordinal);
        Assert.Contains("automationId = \"v2-map-plan\"", gallery, StringComparison.Ordinal);
    }

    [Fact]
    public void Collapsing_the_rail_leaves_every_destination_reachable()
    {
        var shell = File.ReadAllText(RepositoryFile(ShellView));
        // The launcher that stands in for the rail carries the same list it does.
        var launcher = Regex.Match(
            shell,
            @"AutomationId=""v2-shell-navigation-launcher"".*?</Button>",
            RegexOptions.Singleline);
        Assert.True(launcher.Success, "The navigation launcher is not where this test expects it.");
        Assert.Contains("ItemsSource=\"{Binding PrimaryDestinations}\"", launcher.Value, StringComparison.Ordinal);
        Assert.Contains("{Binding SetupDestination.NavigateCommand}", launcher.Value, StringComparison.Ordinal);
        Assert.Contains("v2-shell-navigation-show-rail", launcher.Value, StringComparison.Ordinal);
    }

    private static string RepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TarkovCompanion.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, relativePath);
        Assert.True(File.Exists(path), $"{relativePath} is not where this test expects it.");
        return path;
    }
}
