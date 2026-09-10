using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.Maps;
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
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
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
