using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace TarkovCompanion.EftSimulator;

public sealed class SimulatorApp : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new SimulatorWindow(desktop.Args ?? []);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
