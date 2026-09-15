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
        Assert.Contains("Content=\"{Binding Legacy.CurrentPage}\"", shell, StringComparison.Ordinal);
        Assert.Contains("new V2ShellRouter(Variant, Registry)", model, StringComparison.Ordinal);
        Assert.DoesNotContain("LayoutTransformControl", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void DialogHostAndAllRequiredSurfaceStatesAreExplicitInThePreviewHost()
    {
        var shell = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Views", "V2", "Shell", "V2ShellView.axaml"));
        var states = File.ReadAllText(V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Services", "V2", "Shell", "V2SurfaceStates.cs"));

        Assert.Contains("v2-shell-dialog-title", shell, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Capture dialog\"", shell, StringComparison.Ordinal);
        foreach (var state in Enum.GetNames<V2SurfaceStateKind>()) Assert.Contains($"V2SurfaceStateKind.{state}", states, StringComparison.Ordinal);
    }
}
