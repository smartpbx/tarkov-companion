using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Shell;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>
/// V2 rough package 30 (acceptance sweep): the Intel workspace shows something once a search has
/// found something.
/// </summary>
/// <remarks>
/// Built from the real composition rather than a stub shell, because what is under test is the
/// hand-off between the V1 item search the workspace runs on (<c>Legacy.Items</c>) and the V2
/// router that owns selection — the two halves a stubbed shell does not have.
///
/// Why it matters: the workspace reserves a fixed context column for the selected item, and until
/// something was selected that column was drawn empty. On a search that found twelve items the
/// player was looking at a list, a large blank pane and a blank stripe, where
/// docs/design/v2/v2-intel-workspace-concept.png shows the first match open.
/// </remarks>
public sealed class V2IntelWorkspaceSelectionTests
{
    [Fact]
    public async Task A_search_opens_its_first_result_and_the_context_panel_with_it()
    {
        await using var services = CreateServices();
        var shell = await StartShellAsync(services);
        shell.Router.NavigateToAddress("#/intel");
        Assert.True(shell.ShowsIntelWorkspace);
        Assert.False(shell.HasIntelSelection);

        shell.SearchText = "graphics";
        await shell.SearchAsync();

        Assert.NotEmpty(shell.IntelResults);
        Assert.True(shell.HasIntelSelection);
        Assert.Equal(shell.IntelResults[0].ItemId, shell.IntelItem);
        Assert.True(shell.IntelResults[0].IsSelected);
    }

    [Fact]
    public async Task A_search_that_finds_nothing_selects_nothing()
    {
        await using var services = CreateServices();
        var shell = await StartShellAsync(services);
        shell.Router.NavigateToAddress("#/intel");

        shell.SearchText = "zzzz-no-such-item";
        await shell.SearchAsync();

        Assert.Empty(shell.IntelResults);
        Assert.False(shell.HasIntelSelection);
        Assert.False(shell.ShowsIntelContextPanel);
    }

    [Fact]
    public async Task An_item_this_install_has_never_heard_of_draws_no_context_column()
    {
        await using var services = CreateServices();
        var shell = await StartShellAsync(services);

        // A deep link from another install, or from before a wipe of the local catalog.
        shell.Router.NavigateToAddress("#/intel/item/000000000000000000000000");
        await WaitUntilAsync(() => !shell.IntelIsLoading);

        Assert.True(shell.HasIntelSelection);
        Assert.False(shell.IntelHasResult);
        Assert.False(shell.ShowsIntelContextPanel);
    }

    [Fact]
    public async Task A_second_search_leaves_the_item_the_player_already_chose_showing()
    {
        await using var services = CreateServices();
        var shell = await StartShellAsync(services);
        shell.Router.NavigateToAddress("#/intel");
        shell.SearchText = "graphics";
        await shell.SearchAsync();
        var chosen = shell.IntelItem;
        Assert.NotEqual(string.Empty, chosen);

        shell.SearchText = "card";
        await shell.SearchAsync();

        Assert.Equal(chosen, shell.IntelItem);
    }

    /// <summary>
    /// V2 rough package 30 (acceptance sweep): the sweep photographs every Variant A address, and
    /// keeps doing so when a package adds one.
    /// </summary>
    /// <remarks>
    /// Written the day after the sweep landed, because merging main brought `plan/keep` with it
    /// and the PowerShell route table did not know. A sweep that claims "every route" and quietly
    /// covers all but the newest one is worse than one that says it covers fifteen.
    /// </remarks>
    [Fact]
    public void The_Windows_route_sweep_photographs_every_Variant_A_address()
    {
        var gallery = File.ReadAllText(V2ShellTestData.RepositoryPath("scripts", "windows-page-gallery.ps1"));

        foreach (var address in V2ShellVariants.A.Addresses)
        {
            // The item route is addressed with an item; the sweep names a real one of its own.
            var expected = address.Key == V2Routes.Item
                ? "#/intel/item/"
                : $"address = \"#/{address.Value}\"";
            Assert.True(
                gallery.Contains(expected, StringComparison.Ordinal),
                $"scripts/windows-page-gallery.ps1 does not photograph '{address.Value}'. " +
                "Add it to $V2AcceptanceRoutes, with a bound only if the number means something.");
        }
    }

    /// <summary>
    /// V2 rough package 30: every heading and every control the Windows sweep asserts is one the
    /// application actually produces.
    /// </summary>
    /// <remarks>
    /// Written after a Windows run failed on both: packages 28 and 25 replaced five hosted V1
    /// pages with native workspaces while this branch was open, so assertions naming V1 buttons
    /// had nothing to find, and one route's heading was "Item" against the shell's "Item details".
    /// Neither could fail on Linux, and the sweep only runs on Windows, so the first sign of
    /// either was a red merge. This is the same re-derivation, done by a test.
    /// </remarks>
    [Fact]
    public void The_Windows_route_sweep_asserts_headings_and_controls_that_exist()
    {
        var gallery = File.ReadAllText(V2ShellTestData.RepositoryPath("scripts", "windows-page-gallery.ps1"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var registry = V2RouteRegistry.Default;
        var variant = V2ShellVariants.A;
        var ids = CollectAutomationIds();

        foreach (var route in variant.Addresses)
        {
            // The sweep addresses the item route with an id nothing can resolve, on purpose.
            var address = route.Value.Replace("{item}", "000000000000000000000000", StringComparison.Ordinal);
            var entry = System.Text.RegularExpressions.Regex.Match(
                gallery,
                $"address = \"#/{System.Text.RegularExpressions.Regex.Escape(address)}\"; heading = \"([^\"]*)\"");
            Assert.True(entry.Success, $"the sweep does not photograph '{route.Value}'.");

            var definition = registry[route.Key];
            var root = registry.RootOf(route.Key);
            var destination = variant.DestinationLabelKey(root);
            var expected = route.Key == root && destination is not null
                ? V2ShellText.Get(destination)
                : V2ShellText.Get(definition.HeadingKey);
            Assert.Equal(expected, entry.Groups[1].Value);
        }

        // Only this sweep's own route table: the rest of the gallery belongs to other packages,
        // and several of their ids are built at runtime from a check or a scene object rather
        // than declared in any view.
        var table = gallery[gallery.IndexOf("$V2AcceptanceRoutes = @(", StringComparison.Ordinal)..];
        table = table[..table.IndexOf("foreach ($Route in $V2AcceptanceRoutes)", StringComparison.Ordinal)];
        foreach (System.Text.RegularExpressions.Match named in System.Text.RegularExpressions.Regex.Matches(
            table, "\"(v2-[a-z0-9-]+)\""))
        {
            Assert.True(
                ids.Contains(named.Groups[1].Value),
                $"the sweep names '{named.Groups[1].Value}', which no view sets. A package probably " +
                "replaced the surface it was on; point the assertion at what holds it now.");
        }
    }

    private static HashSet<string> CollectAutomationIds()
    {
        var views = Directory.GetFiles(
            V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Views"), "*.axaml", SearchOption.AllDirectories);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var view in views)
        {
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                File.ReadAllText(view), "AutomationProperties.AutomationId=\"(v2-[a-z0-9-]+)\""))
            {
                ids.Add(match.Groups[1].Value);
            }
        }

        // Ids the map renderer builds from a scene object's own identity rather than declaring.
        foreach (var generated in new[] { "v2-map-plan", "v2-map-renderer" })
        {
            ids.Add(generated);
        }

        return ids;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static async Task<V2ShellViewModel> StartShellAsync(ServiceProvider services)
    {
        var legacy = services.GetRequiredService<MainWindowViewModel>();
        var shell = services.GetRequiredService<V2ShellViewModel>();
        legacy.PreviewShell = shell;
        await legacy.InitializeAsync();
        return shell;
    }

    private static ServiceProvider CreateServices() => AppComposition.Build(
        new AppCommandLine(false, true, false, false, null, null, null) { UiShell = V2ShellMode.VariantA },
        new(DataRoot: Path.Combine(Path.GetTempPath(), $"tarkov-intel-{Guid.NewGuid():N}"), Offline: true));
}
