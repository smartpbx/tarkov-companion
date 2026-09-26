using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.App.Views.V2.Shell;
using TarkovCompanion.UnitTests.V2MapRenderer;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>
/// [#881 reopened] "It also didn't fix the sidebar settings being cut off on the icon." In the
/// real shell view, with an update waiting, the gear glyph and its dot are drawn whole
/// (<see cref="RailGearFit"/>): in the labelled and the icons-only rail, at 1920x1080 and at
/// 1920x1009 (about what a maximised window gets on a 1080p screen with a taskbar).
/// </summary>
/// <remarks>
/// Before the fix all four failed: the icons-only rail cut the glyph and the dot to a 15 px
/// ContentControl, and the labelled rail cut the dot's ring at its 24 px icon box.
/// </remarks>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class RailGearClipTests
{
    [Theory]
    [InlineData("labels", 1080)]
    [InlineData("labels", 1009)]
    [InlineData("icons", 1080)]
    [InlineData("icons", 1009)]
    public async Task The_gear_and_its_update_dot_are_drawn_whole_inside_the_button_and_the_rail(string rail, int height)
    {
        var (faults, dotShowing) = await RunAsync(rail, height, window =>
            (RailGearFit.Faults(window), window.GetVisualDescendants().OfType<Ellipse>().Any(IsGearDot)));

        // The dot is part of what is measured, so a run that never drew it proves nothing.
        Assert.True(dotShowing, "the update dot was not drawn");
        Assert.True(faults.Count == 0, $"{rail} rail at 1920x{height}:{Environment.NewLine}{string.Join(Environment.NewLine, faults)}");
    }

    private static bool IsGearDot(Ellipse ellipse) =>
        ellipse.IsEffectivelyVisible && ellipse.GetVisualAncestors().OfType<Control>()
            .Any(control => AutomationProperties.GetAutomationId(control) == RailGearFit.GearAutomationId);

    private static async Task<T> RunAsync<T>(string rail, int height, Func<Window, T> test)
    {
        using var session = HeadlessSessions.StartNew(typeof(V2SettingsIndexLandingTests.SetupViewApp));
        return await session.Dispatch(
            async () =>
            {
                var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tarkov-rail-gear-{Guid.NewGuid():N}");
                try
                {
                    await using var services = AppComposition.Build(
                        new AppCommandLine(false, true, false, false, null, null, null) { UiShell = V2ShellMode.VariantA },
                        new(DataRoot: root, Offline: true));
                    var legacy = services.GetRequiredService<MainWindowViewModel>();
                    var shell = services.GetRequiredService<V2ShellViewModel>();
                    legacy.PreviewShell = shell;
                    await legacy.InitializeAsync();

                    var wanted = V2NavigationRailTokens.Parse(rail);
                    for (var guard = 0; guard < 3 && shell.NavigationRail != wanted; guard++)
                    {
                        shell.CycleNavigationRail();
                    }

                    Assert.Equal(wanted, shell.NavigationRail);
                    shell.SetupDestination.HasNotice = true;
                    var window = new Window
                    {
                        Width = 1920,
                        Height = height,
                        Content = new V2ShellView { DataContext = shell },
                    };
                    window.Show();
                    try
                    {
                        Dispatcher.UIThread.RunJobs();
                        return test(window);
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
}
