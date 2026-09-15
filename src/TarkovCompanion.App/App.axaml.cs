using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.App.Views;

namespace TarkovCompanion.App;

public sealed class App(IServiceProvider services) : Avalonia.Application
{
    private static readonly TimeSpan InitializationDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource _stopping = new();
    private Task _initialization = Task.CompletedTask;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = services.GetRequiredService<MainWindowViewModel>();

            // Opening straight onto a named page exists so the Windows verification job can
            // photograph every destination in turn. Seven pages were written and shipped
            // without anyone ever seeing them rendered, and a broken binding on one of them
            // only shows when somebody navigates there.
            var options = services.GetService<AppCommandLine>();
            if (options is { UiShell: var mode } && mode.IsPreview())
            {
                viewModel.PreviewShell = services.GetRequiredService<V2ShellViewModel>();
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
                Title = viewModel.PreviewShell?.Title ?? "Tarkov Companion",
            };
            _initialization = viewModel.InitializeAsync(_stopping.Token);
        }

        base.OnFrameworkInitializationCompleted();
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
        services.GetService<V2ShellViewModel>()?.Dispose();
        services.GetService<MapViewModel>()?.Dispose();
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
