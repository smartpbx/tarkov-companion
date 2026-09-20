using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2;
using TarkovCompanion.App.Services.V2.Appearance;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.Services.V2.Notifications;
using TarkovCompanion.App.Services.V2.Profile;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.App.Views;
using TarkovCompanion.App.Views.V2.MapRenderer;
using TarkovCompanion.Application.Services.Personalization;

namespace TarkovCompanion.App;

public sealed class App(IServiceProvider services) : Avalonia.Application
{
    private readonly CancellationTokenSource _stopping = new();
    private Task _initialization = Task.CompletedTask;
    private MainWindowViewModel? _mainViewModel;
    private TrayPresenceHost? _tray;
    private NotificationBridge? _notifications;
    private bool _closesToTray;
    private V2AppearanceApplier? _appearance;
    private WorkspacePreferenceService? _preferences;
    private UiHangWatchdog? _hangWatchdog;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // [V2 rough package 60 — appearance] #266/#315. Before any window exists, so the
            // first frame is already the theme, the text scale and the density that were chosen
            // last time rather than the default repainted a moment later.
            ApplyStoredAppearance();

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
                if (_appearance is { } appearance && _preferences is { } preferences)
                {
                    appearance.Attach(window, preferences.Current);
                }

                desktop.MainWindow = window;
                // [V2 rough package 43 (#314)] The tray, and the five notifications behind it.
                // Attached after the window exists because closing to the tray only makes sense
                // when there is a tray to close to, and the pop-up needs a window to draw in.
                AttachNotifications(desktop, window, options);
                _initialization = viewModel.InitializeAsync(_stopping.Token);
                // [#453] From here on a dispatcher that stops answering for five seconds says so
                // in the log, with the route and the load that was running.
                _hangWatchdog = UiHangWatchdog.ForApplication();
                _hangWatchdog.Start();
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
            _notifications.InitializeAsync(_stopping.Token).Observe("notifications", "initialize");
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
    /// Reads the stored appearance and paints the application with it, now and on every change.
    /// </summary>
    /// <remarks>
    /// Best effort by design. A preferences file that cannot be read must not stop the companion
    /// opening, so a failure here leaves the application exactly as <c>App.axaml</c> authored it.
    /// The read is synchronous because the alternative is a window that opens in the wrong theme
    /// and flips a frame later, which is worse than a few milliseconds of file access.
    /// </remarks>
    private void ApplyStoredAppearance()
    {
        try
        {
            var preferences = services.GetService<WorkspacePreferenceService>();
            if (preferences is null)
            {
                return;
            }

            _preferences = preferences;
            var applier = new V2AppearanceApplier(this);
            _appearance = applier;
            // Loaded before subscribing, so the first paint happens once rather than twice.
            applier.Apply(preferences.LoadAsync(CancellationToken.None).GetAwaiter().GetResult());
            preferences.Changed += (_, current) => applier.Apply(current);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException)
        {
        }
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
    /// So the drain is gone rather than shortened: waiting on work that cannot finish is worth
    /// removing outright, not budgeting for. A startup that has already completed is still
    /// awaited, because that costs nothing and surfaces what it threw. The preview shell gets a
    /// deadline of its own, and what each step cost is in the log.
    /// </remarks>
    public async Task<string> StopAsync(TimeSpan budget)
    {
        var stages = new ShutdownStages(budget);
        // First: the dispatcher is about to stop answering on purpose, and that is not a hang.
        _hangWatchdog?.Dispose();
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_mainViewModel?.PreviewShell is { } preview)
        {
            await stages
                .RunAsync("preview-shell", () => preview.DisposeAsync().AsTask(), TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }

        _mainViewModel?.Map.Dispose();
        _notifications?.Dispose();
        _tray?.Dispose();
        // Observed, not waited for. Startup's continuations are posted to a dispatcher that has
        // already stopped running them, so a startup still in flight here can never finish and
        // every second spent waiting on it is a second bought for nothing — five of them, out of
        // a fifteen-second budget, before this. Awaiting a task that has *already* completed is
        // free and surfaces anything it threw, so that is all this does; the rest is recorded and
        // left behind with the process.
        if (_initialization.IsCompleted)
        {
            // Whatever is left of the budget, which it cannot spend: the task is already
            // complete, so this returns at once and only exists to surface what it threw.
            await stages
                .RunAsync("initialization", () => _initialization, stages.Remaining)
                .ConfigureAwait(false);
        }
        else
        {
            stages.Skip("initialization", "still running; its continuations cannot complete");
        }

        _stopping.Dispose();
        return stages.Report();
    }
}
