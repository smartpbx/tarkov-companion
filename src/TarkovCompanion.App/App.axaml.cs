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

    public async Task StopAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        services.GetService<MapViewModel>()?.Dispose();
        try
        {
            await _initialization.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _stopping.Dispose();
        }
    }
}
