using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.Views.V2.Shell;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// [#881] The <see cref="GallerySceneKind.UpdateWaiting"/> scene: the rail's gear with its update
/// dot and the "Update ready" card, as a player with a build waiting sees them.
/// </summary>
/// <remarks>
/// A clean Windows runner never has a build waiting, so no capture had ever shown the dot; the
/// player's screenshot was the first, with the dot cut to a sliver and the gear's teeth cut with
/// it. The condition "gear" answers <see cref="RailGearFit"/> in the packaged app, on the
/// runner's real display scaling, and fails the step with the measured rectangles when anything
/// is cut. Nothing is written: the flags are the view models' own and go with the process.
/// </remarks>
internal static class GalleryUpdateWaitingScene
{
    public const string GearCondition = "gear";

    public static async Task RunAsync(MainWindowViewModel main, GalleryReadiness readiness, CancellationToken cancellationToken)
    {
        try
        {
            if (main.PreviewShell is not { } shell)
            {
                throw new InvalidOperationException("no V2 shell");
            }

            shell.SetupDestination.HasNotice = true;
            shell.UpdateReadyNotice.PresentForPreview();
            // One layout and render pass, so the capture that follows "ready" has the dot on it.
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            cancellationToken.ThrowIfCancellationRequested();
            readiness.AfterStep = (condition, token) => Dispatcher.UIThread.InvokeAsync(() => Answer(condition)).GetTask();
            readiness.Ready($"update waiting on {shell.CurrentAddress}, rail {shell.NavigationRail}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            readiness.Failed($"update waiting: {exception.Message}");
        }
    }

    private static string Answer(string condition)
    {
        if (!string.Equals(condition, GearCondition, StringComparison.OrdinalIgnoreCase))
        {
            // "settled" and the like: the scene has nothing that keeps changing.
            return "update waiting";
        }

        var window = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
            ?? throw new InvalidOperationException("no main window to measure the gear in");
        var faults = RailGearFit.Faults(window);
        // GalleryReadiness.WaitAsync turns this exception into a "not ready" answer carrying it.
        return faults.Count == 0
            ? $"gear and its dot drawn whole at {window.RenderScaling * 100:0}% display scaling"
            : throw new InvalidOperationException(
                $"at {window.RenderScaling * 100:0}% display scaling: {string.Join("; ", faults)}");
    }
}
