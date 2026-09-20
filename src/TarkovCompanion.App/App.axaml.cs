using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.Services.V2.Profile;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.App.Views;
using TarkovCompanion.App.Views.V2.MapRenderer;

namespace TarkovCompanion.App;

public sealed class App(IServiceProvider services) : Avalonia.Application
{
    /// <summary>
    /// How long a startup that is still running is given to notice it has been cancelled.
    /// </summary>
    /// <remarks>
    /// One second, down from five. The remark on <see cref="StopAsync"/> explains why more is
    /// waste: these continuations are posted to a dispatcher that has already stopped, so five
    /// seconds bought nothing and spent a third of the whole shutdown budget doing it.
    /// </remarks>
    private static readonly TimeSpan InitializationDrainTimeout = TimeSpan.FromSeconds(1);
    private readonly CancellationTokenSource _stopping = new();
    private Task _initialization = Task.CompletedTask;
    private MainWindowViewModel? _mainViewModel;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            _mainViewModel = viewModel;

            // Opening straight onto a named page exists so the Windows verification job can
            // photograph every destination in turn. Seven pages were written and shipped
            // without anyone ever seeing them rendered, and a broken binding on one of them
            // only shows when somebody navigates there.
            var options = services.GetService<AppCommandLine>();
            if (options?.MapRendererGallery == true)
            {
                desktop.MainWindow = new MapSceneRendererGalleryWindow(
                    options.MapRendererLargeText,
                    options.MapRendererLootOffline);
            }
            else
            {
                if (options is { UiShell: var mode } && mode.IsPreview())
                {
                    viewModel.PreviewShell = services.GetRequiredService<V2ShellViewModel>();
                    // Wires #271's capture sessions and the #274/#282 Loot Scan decision path into
                    // this shell instance. Resolved (not merely registered) so it starts observing
                    // for the life of the process; legacy launches never build it.
                    services.GetRequiredService<V2ShellCaptureBridge>();
                    // [V2 rough package 24] Same reason: resolved so the paired tablets' map
                    // starts following this shell's own raid map for the life of the process.
                    services.GetRequiredService<TabletMapSurfacePublisher>();
                    // One-time, best-effort: gives #269's profile context something real to
                    // report without a v1/v2 profile migration UI. See the bootstrap's own remarks.
                    _ = services.GetRequiredService<LegacyProfileContextBootstrap>()
                        .EnsureSeededAsync(_stopping.Token);
                }
                else if (options?.StartPage is { } startPage && !viewModel.Navigate(startPage))
                {
                    throw new ArgumentException($"No destination is named '{startPage}'.");
                }

                // And onto a named map, floor and view, for the same reason. The map is the most
                // complex thing here and the hardest to see from anywhere but Windows, and the
                // gallery photographed it in exactly one state: cold launch, default map, base
                // floor, flat. Every map defect reported so far was found by looking at a picture.
                if (options is { } launch)
                {
                    viewModel.Map.OpenOn(launch.MapId, launch.MapFloor, launch.StacksFloors);
                }

                desktop.MainWindow = new MainWindow
                {
                    DataContext = viewModel,
                };
                _initialization = viewModel.InitializeAsync(_stopping.Token);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Cancels startup work and unwinds the interface, every step under its own deadline.
    /// </summary>
    /// <remarks>
    /// <see cref="MainWindowViewModel.InitializeAsync"/> runs on the UI thread and resumes on
    /// the Avalonia dispatcher. By the time shutdown runs the dispatcher has stopped, so those
    /// continuations can never complete and an unbounded await here would hang forever.
    ///
    /// That was true of the preview shell too, and it was not bounded. Closing the window raises
    /// <c>Closing</c>, <c>MainWindow.RememberLayout</c> records the window's bounds, and that
    /// enqueues a save — so the close creates the work that this method then waited on, through a
    /// queue whose own drain awaits its writer with <see cref="CancellationToken.None"/>. One
    /// slow file write on the way out and the process never reached its own exit.
    ///
    /// Now the drain the remark says can never complete gets a second rather than five, the
    /// preview shell gets a deadline of its own, and what each step cost is in the log.
    /// </remarks>
    public async Task<string> StopAsync(TimeSpan budget)
    {
        var stages = new ShutdownStages(budget);
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_mainViewModel?.PreviewShell is { } preview)
        {
            await stages
                .RunAsync("preview-shell", () => preview.DisposeAsync().AsTask(), TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }

        _mainViewModel?.Map.Dispose();
        // A second, not five. Cancellation has already been requested and these continuations are
        // posted to a dispatcher that has stopped running them, so this is only long enough to
        // collect work that had already left the interface thread.
        await stages
            .RunAsync("initialization-drain", () => _initialization, InitializationDrainTimeout)
            .ConfigureAwait(false);
        _stopping.Dispose();
        return stages.Report();
    }
}
