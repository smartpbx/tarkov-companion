using Avalonia;
using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var options = AppCommandLine.Parse(args);
            if (options.SelfTest)
            {
                if (string.IsNullOrWhiteSpace(options.OutputPath))
                {
                    throw new ArgumentException("--self-test requires --output <json>.");
                }

                var report = SelfTestRunner.RunAsync(options.OutputPath, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                return report.Success ? 0 : 1;
            }

            if (options.Demo && options.Headless)
            {
                return RunHeadlessDemo(options);
            }

            var diagnosticChannel = DiagnosticCommandChannel.Start(
                options.DeveloperMode,
                options.DiagnosticChannelPath);
            try
            {
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                diagnosticChannel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or InvalidDataException
                                          or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static int RunHeadlessDemo(AppCommandLine options)
    {
        var fixturePath = DemoRaidReplay.ResolveFixturePath(options.DemoFixturePath);
        var report = DemoRaidReplay.RunAsync(fixturePath, CancellationToken.None).GetAwaiter().GetResult();

        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            DemoRaidReplay.WriteAsync(report, Console.OpenStandardOutput(), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        else
        {
            var outputPath = Path.GetFullPath(options.OutputPath);
            var outputDirectory = Path.GetDirectoryName(outputPath)
                ?? throw new InvalidOperationException("The demo output path has no parent directory.");
            Directory.CreateDirectory(outputDirectory);
            using var output = File.Create(outputPath);
            DemoRaidReplay.WriteAsync(report, output, CancellationToken.None).GetAwaiter().GetResult();
        }

        return report.Complete ? 0 : 1;
    }
}
