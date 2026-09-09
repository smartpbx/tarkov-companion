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
            desktop.MainWindow = new MainWindow
            {
                DataContext = MainWindowViewModel.CreateFoundationDemo(demoMode),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
