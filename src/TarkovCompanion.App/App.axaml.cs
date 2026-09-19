using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2;
using TarkovCompanion.App.Services.V2.Appearance;
using TarkovCompanion.App.Services.V2.Capture;
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
    private static readonly TimeSpan InitializationDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource _stopping = new();
    private Task _initialization = Task.CompletedTask;
    private MainWindowViewModel? _mainViewModel;
    private V2AppearanceApplier? _appearance;
    private WorkspacePreferenceService? _preferences;

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
                _initialization = viewModel.InitializeAsync(_stopping.Token);
            }
        }

        base.OnFrameworkInitializationCompleted();
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
