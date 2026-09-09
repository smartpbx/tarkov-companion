using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.Views;

namespace TarkovCompanion.App;

public sealed class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var demoMode = desktop.Args?.Contains("--demo", StringComparer.OrdinalIgnoreCase) ?? false;
            var viewModel = MainWindowViewModel.CreateFoundationDemo(demoMode);
            var window = new MainWindow
            {
                DataContext = viewModel,
            };
            window.Closed += (_, _) => viewModel.Map.Dispose();
            desktop.MainWindow = window;
            _ = viewModel.Map.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
