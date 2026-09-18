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
