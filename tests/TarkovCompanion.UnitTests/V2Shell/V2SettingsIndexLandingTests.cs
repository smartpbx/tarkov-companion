using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.App.Views.V2.Setup;
using TarkovCompanion.UnitTests.V2MapRenderer;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>
/// [#902 P6] Every setting the palette offers in Setup lands on a row that is on screen, in the
/// section it names, in the real Setup view; and the section tabs stay put at the foot of a section.
/// </summary>
/// <remarks>
/// <c>V2ShellFindabilityTests</c> checks the palette opens the right section and asks for focus on
/// the right id. That passed for two entries whose control was hidden in the running app, which only
/// the view can show: the view is hosted here, composed as the app composes it.
/// </remarks>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class V2SettingsIndexLandingTests
{
    [Fact]
    public async Task Every_Setup_setting_in_the_palette_lands_on_a_visible_row_in_its_section()
    {
        var misses = await RunAsync((shell, window) =>
        {
            var missed = new List<string>();
            foreach (var entry in V2SettingsIndex.All.Where(entry => entry.Section is not null))
            {
                shell.OpenSetting(entry);
                Dispatcher.UIThread.RunJobs();
                if (shell.SetupWorkspace!.Selected != entry.Section)
                {
                    missed.Add($"{entry.Id}: opened {shell.SetupWorkspace.Selected}, not {entry.Section}");
                    continue;
                }

                var target = Find(window, entry.Target);
                if (target is null)
                {
                    missed.Add($"{entry.Id}: nothing is named '{entry.Target}' in {entry.Section}");
                }
                else if (!target.IsEffectivelyVisible)
                {
                    missed.Add($"{entry.Id}: '{entry.Target}' is hidden in {entry.Section}");
                }
            }

            return missed;
        });

        Assert.True(misses.Count == 0, string.Join(Environment.NewLine, misses));
    }

    [Fact]
    public async Task Review_privacy_on_Home_opens_Data_and_Network_with_Local_only_on_screen()
    {
        var landed = await RunAsync((shell, window) =>
        {
            var setup = shell.SetupWorkspace!;
            setup.Select(V2SetupSection.Overview);
            Dispatcher.UIThread.RunJobs();
            setup.Overview.ReviewPrivacyCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            return setup.IsDataNetworkSelected && Find(window, "v2-setup-network-local-only") is { IsEffectivelyVisible: true };
        });

        Assert.True(landed);
    }

    [Theory]
    [InlineData(V2SetupSection.UpdatesDiagnostics)]
    [InlineData(V2SetupSection.GameCapture)]
    [InlineData(V2SetupSection.ProfileProgress)]
    public async Task The_section_tabs_stay_in_view_at_the_foot_of_a_long_section(V2SetupSection section)
    {
        var result = await RunAsync((shell, window) =>
        {
            shell.SetupWorkspace!.Select(section);
            Dispatcher.UIThread.RunJobs();
            var tabs = Find(window, "v2-setup-sections")!;
            var scroller = Assert.IsType<ScrollViewer>(Find(window, "v2-setup-scroller"));
            var before = tabs.TranslatePoint(default, window);

            scroller.Offset = new Vector(0, scroller.Extent.Height - scroller.Viewport.Height);
            Dispatcher.UIThread.RunJobs();

            return (Scrolled: scroller.Offset.Y, Before: before, After: tabs.TranslatePoint(default, window), Visible: tabs.IsEffectivelyVisible);
        });

        // The section is longer than the window, so this really is its foot.
        Assert.True(result.Scrolled > 0, $"{section} did not scroll at 1920x1080.");
        Assert.Equal(result.Before, result.After);
        Assert.True(result.Visible);
    }

    private static Control? Find(Window window, string automationId) =>
        window.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == automationId);

    private static async Task<T> RunAsync<T>(Func<V2ShellViewModel, Window, T> test)
    {
        using var session = HeadlessSessions.StartNew(typeof(SetupViewApp));
        return await session.Dispatch(
            async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), $"tarkov-setup-landing-{Guid.NewGuid():N}");
                try
                {
                    await using var services = AppComposition.Build(
                        new AppCommandLine(false, true, false, false, null, null, null) { UiShell = V2ShellMode.VariantA },
                        new(DataRoot: root, Offline: true));
                    var legacy = services.GetRequiredService<MainWindowViewModel>();
                    var shell = services.GetRequiredService<V2ShellViewModel>();
                    legacy.PreviewShell = shell;
                    await legacy.InitializeAsync();
                    var window = new Window
                    {
                        Width = 1920,
                        Height = 1080,
                        Content = new V2SetupWorkspaceView { DataContext = shell.SetupWorkspace },
                    };
                    window.Show();
                    try
                    {
                        Dispatcher.UIThread.RunJobs();
                        return test(shell, window);
                    }
                    finally
                    {
                        window.Close();
                    }
                }
                finally
                {
                    try
                    {
                        Directory.Delete(root, recursive: true);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            },
            CancellationToken.None);
    }

    /// <summary>The application's own theme and V2 styles, as App.axaml merges them.</summary>
    public sealed class SetupViewApp : Avalonia.Application
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<SetupViewApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

        public override void Initialize()
        {
            var root = new Uri("avares://TarkovCompanion/");
            Resources.MergedDictionaries.Add(new ResourceInclude(root) { Source = new("avares://TarkovCompanion/Themes/V2/V2Resources.axaml") });
            Styles.Add(new FluentTheme());
            Styles.Add(new StyleInclude(root) { Source = new("avares://TarkovCompanion/Themes/InstrumentStyles.axaml") });
            Styles.Add(new StyleInclude(root) { Source = new("avares://TarkovCompanion/Themes/V2/V2PrimitiveStyles.axaml") });
            Styles.Add(new StyleInclude(root) { Source = new("avares://TarkovCompanion/Themes/V2/V2WorkspaceStyles.axaml") });
        }
    }
}
