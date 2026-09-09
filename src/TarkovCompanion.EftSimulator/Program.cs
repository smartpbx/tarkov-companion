using Avalonia;

namespace TarkovCompanion.EftSimulator;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var options = SimulatorCommandLine.Parse(args);
            if (options.ListScenarios)
            {
                SimulatorFixtureEmitter.WriteScenarioListAsync(Console.OpenStandardOutput(), CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                return 0;
            }

            if (!options.DeveloperMode)
            {
                throw new InvalidOperationException("The simulator runs only with explicit --developer-mode.");
            }

            var scenario = SimulatorScenarioCatalog.Get(options.Scenario);
            SimulatorFixtureEmitter.EmitAsync(options, scenario, CancellationToken.None).GetAwaiter().GetResult();
            return options.EmitFixturesOnly
                ? 0
                : BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or InvalidOperationException
                                          or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<SimulatorApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
