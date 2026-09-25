using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.App.ViewModels.V2.Shell;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>[#902 P9] Ctrl+K finds settings and lands on their row; developer commands stay out of it.</summary>
public sealed class V2ShellFindabilityTests
{
    [Fact]
    public async Task Typing_local_only_opens_data_and_network_on_the_local_only_switch()
    {
        await WithShellAsync(developerMode: false, shell =>
        {
            var requests = new List<V2FocusRequest>();
            shell.FocusRequested += (_, request) => requests.Add(request);
            shell.PaletteCommand.Execute(null);
            shell.PaletteQuery = "local only";

            var hit = Assert.Single(shell.FilteredCommandItems, item => item.Label == "Local only");
            Assert.Equal("Setup › Data & Network", hit.Hint);
            hit.InvokeCommand.Execute(null);

            Assert.False(shell.IsPaletteOpen);
            Assert.Equal(V2Routes.Setup, shell.Router.Current.Location.Route);
            Assert.True(shell.SetupWorkspace!.IsDataNetworkSelected);
            Assert.Equal(new V2FocusRequest("v2-setup-network-local-only", V2FocusReason.Setting), requests[^1]);
        });
    }

    [Fact]
    public async Task A_setting_is_found_by_its_other_words_and_opens_its_own_section()
    {
        await WithShellAsync(developerMode: false, shell =>
        {
            shell.PaletteQuery = "offline";
            Assert.Contains(shell.FilteredCommandItems, item => item.Label == "Local only");

            shell.PaletteQuery = "do not disturb";
            Assert.Single(shell.FilteredCommandItems, item => item.Label == "Quiet hours").InvokeCommand.Execute(null);
            Assert.True(shell.SetupWorkspace!.IsNotificationsSelected);

            // Settings are listed only once something is typed: the empty palette stays the command list.
            shell.PaletteQuery = string.Empty;
            Assert.DoesNotContain(shell.FilteredCommandItems, item => item.Label == "Quiet hours");
        });
    }

    [Fact]
    public async Task Loot_rules_opens_plan_keep_with_the_rules_showing()
    {
        await WithShellAsync(developerMode: false, shell =>
        {
            shell.PaletteQuery = "always leave";
            Assert.Single(shell.FilteredCommandItems, item => item.Label == "Loot rules").InvokeCommand.Execute(null);

            Assert.Equal(V2Routes.Keep, shell.Router.Current.Location.Route);
            Assert.True(Assert.IsType<KeepListWorkspaceViewModel>(shell.WorkspaceContent).LootRules!.IsOpen);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Address_pin_and_preview_reset_commands_need_developer_mode(bool developerMode)
    {
        await WithShellAsync(developerMode, shell =>
        {
            var ids = shell.FilteredCommandItems.Select(item => item.AutomationId).ToArray();

            foreach (var id in new[] { "copy-address", "pin", "reset-preview" })
            {
                Assert.Equal(developerMode, ids.Contains(V2ShellFocusTargets.Command(id)));
            }

            // What a player does use is still there either way.
            Assert.Contains(V2ShellFocusTargets.Command("capture-shortcut"), ids);
        });
    }

    private static async Task WithShellAsync(bool developerMode, Action<V2ShellViewModel> test)
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-findability-{Guid.NewGuid():N}");
        try
        {
            await using var services = AppComposition.Build(
                new AppCommandLine(false, true, false, developerMode, null, null, null) { UiShell = V2ShellMode.VariantA },
                new(DataRoot: root, Offline: true));
            var legacy = services.GetRequiredService<MainWindowViewModel>();
            var shell = services.GetRequiredService<V2ShellViewModel>();
            legacy.PreviewShell = shell;
            await legacy.InitializeAsync();
            test(shell);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
