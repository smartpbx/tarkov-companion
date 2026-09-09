using Avalonia;

namespace TarkovCompanion.EftSimulator;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<SimulatorApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
