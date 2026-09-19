using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.Services.V2.Notifications;
using TarkovCompanion.App.Services.V2.Profile;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.App.Views;
using TarkovCompanion.App.Views.V2.MapRenderer;

namespace TarkovCompanion.App;

public sealed class App(IServiceProvider services) : Avalonia.Application
{
    private static readonly TimeSpan InitializationDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource _stopping = new();
    private Task _initialization = Task.CompletedTask;
    private MainWindowViewModel? _mainViewModel;
    private TrayPresenceHost? _tray;
    private NotificationBridge? _notifications;
    private bool _closesToTray;

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

                var window = new MainWindow
                {
                    DataContext = viewModel,
                };
                desktop.MainWindow = window;
                // [V2 rough package 43 (#314)] The tray, and the five notifications behind it.
                // Attached after the window exists because closing to the tray only makes sense
                // when there is a tray to close to, and the pop-up needs a window to draw in.
                AttachNotifications(desktop, window, options);
                _initialization = viewModel.InitializeAsync(_stopping.Token);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Puts the companion in the system tray and starts deciding what is worth saying.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 43] Best-effort throughout. A platform with no tray leaves the tray host
    /// unavailable, the window then closes the way it always has, and the notifications still work
    /// — they simply have nowhere quiet to go. Nothing here is allowed to stop the application
    /// starting, which is why the whole thing is inside one catch: a missing tray is not worth a
    /// failed launch.
    /// </remarks>
    private void AttachNotifications(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        AppCommandLine? options)
    {
        try
        {
            _tray = services.GetRequiredService<TrayPresenceHost>();
            _tray.Attach(this, new(
                Show: () => Restore(window),
                OpenRaid: () => Restore(window, V2Routes.Raid),
                OpenTeam: () => Restore(window, V2Routes.Team),
                OpenSetup: () => Restore(window, V2Routes.Setup),
                Quit: () =>
                {
                    _closesToTray = false;
                    desktop.Shutdown();
                }));
            services.GetRequiredService<PopupNotificationHost>()
                .Attach(new WindowNotificationManager(window) { Position = NotificationPosition.BottomRight, MaxItems = 3 });

            // The window closing is the application going quiet, not the application stopping:
            // a companion that has to be relaunched to tell you anything cannot tell you anything.
            // Only for an ordinary player launch with a tray — verification and --page launches
            // still need CloseMainWindow to end the process (see CloseToTrayDecision).
            _closesToTray = CloseToTrayDecision.ShouldCloseToTray(_tray.IsAvailable, options);
            window.Closing += (_, args) =>
            {
                // Already hidden under OnExplicitShutdown: let a second close (or Quit) finish.
                if (!_closesToTray
                    || (desktop.ShutdownMode == ShutdownMode.OnExplicitShutdown && !window.IsVisible))
                {
                    return;
                }

                args.Cancel = true;
                window.Hide();
            };

            if (_closesToTray)
            {
                // Otherwise hiding the only window would end the process before the tray icon
                // had a chance to be pressed.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }

            _notifications = services.GetRequiredService<NotificationBridge>();
            _notifications.Raised += (_, _) => _tray?.Update(services
                .GetRequiredService<TarkovCompanion.Application.Services.Runtime.IRuntimeStateStore>().Current);
            var runtime = services.GetRequiredService<TarkovCompanion.Application.Services.Runtime.IRuntimeStateStore>();
            runtime.Changed += (_, _) => _tray?.Update(runtime.Current);
            _tray.Update(runtime.Current);
            _ = _notifications.InitializeAsync(_stopping.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _tray = null;
            _notifications = null;
            _closesToTray = false;
        }
    }

    /// <summary>Brings the window back from the tray, optionally on a named workspace.</summary>
    private void Restore(MainWindow window, V2RouteId? route = null)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
        _tray?.ClearUnread();
        if (route is { } destination && _mainViewModel?.PreviewShell is { } shell)
        {
            shell.GoTo(destination);
        }
    }

    /// <summary>
    /// Cancels startup work and waits a bounded time for it to unwind.
    /// </summary>
    /// <remarks>
    /// <see cref="MainWindowViewModel.InitializeAsync"/> runs on the UI thread and resumes on
    /// the Avalonia dispatcher. By the time shutdown runs the dispatcher has stopped, so those
    /// continuations can never complete and an unbounded await here would hang forever.
    /// </remarks>
    public async Task StopAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_mainViewModel?.PreviewShell is { } preview)
        {
            await preview.DisposeAsync().ConfigureAwait(false);
        }

        _mainViewModel?.Map.Dispose();
        _notifications?.Dispose();
        _tray?.Dispose();
        try
        {
            await _initialization.WaitAsync(InitializationDrainTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
        }
        finally
        {
            _stopping.Dispose();
        }
    }
}
