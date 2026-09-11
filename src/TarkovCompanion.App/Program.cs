using Avalonia;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.App;

internal static class Program
{
    /// <summary>
    /// How long teardown may take before the process gives up and exits anyway.
    /// </summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(15);

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var options = AppCommandLine.Parse(args);
            CrashLog.Install(AppDataPaths.Resolve(demoMode: options.Demo).Logs);

            if (options.SelfTest)
            {
                if (string.IsNullOrWhiteSpace(options.OutputPath))
                {
                    throw new ArgumentException("--self-test requires --output <json>.");
                }

                var report = SelfTestRunner.RunAsync(
                        options.OutputPath,
                        options,
                        settings: null,
                        cancellationToken: CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                return report.Success ? 0 : 1;
            }

            if (options.Demo && options.Headless)
            {
                return RunHeadlessDemo(options);
            }

            var services = AppComposition.Build(options);
            var app = new App(services);
            var diagnosticChannel = DiagnosticCommandChannel.Start(
                options.DeveloperMode,
                options.DiagnosticChannelPath,
                services.GetRequiredService<IRuntimeScanUseCase>());
            // A UI-thread exception otherwise terminates the process outright. For a
            // second-monitor companion that is the worst possible failure: the window
            // vanishes mid-raid with nothing on screen to explain it. Log it, keep the
            // window, and let the page that failed report its own problem.
            Dispatcher.UIThread.UnhandledException += (_, arguments) =>
            {
                CrashLog.Write("dispatcher-exception", arguments.Exception);
                arguments.Handled = true;
            };

            CrashLog.Write("lifecycle", "Desktop lifetime starting.");
            try
            {
                var exitCode = BuildAvaloniaApp(app).StartWithClassicDesktopLifetime(args);
                CrashLog.Write("lifecycle", $"Desktop lifetime returned {exitCode}.");
                return exitCode;
            }
            finally
            {
                ShutDown(app, services, diagnosticChannel);
            }
        }
        catch (Exception exception)
        {
            CrashLog.Write("startup-failure", exception);
            Console.Error.WriteLine(exception.Message);
            return exception is ArgumentException
                or IOException
                or InvalidDataException
                or InvalidOperationException
                ? 2
                : 3;
        }
    }

    public static AppBuilder BuildAvaloniaApp(App app) =>
        AppBuilder.Configure(() => app)
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Tears the application down without letting it outlive its own window.
    /// </summary>
    /// <remarks>
    /// Teardown runs off the UI thread and under a deadline. View-model initialisation awaits
    /// with continuations posted to the Avalonia dispatcher, and the dispatcher stops running
    /// them once the desktop lifetime ends, so waiting for that work from the UI thread never
    /// returned: closing the window left the process alive until it was killed. Cancellation
    /// has already been requested by this point, and each data endpoint commits in its own
    /// transaction, so abandoning a slow teardown loses at most one in-flight refresh.
    /// </remarks>
    private static void ShutDown(App app, ServiceProvider services, DiagnosticCommandChannel? diagnosticChannel)
    {
        var teardown = Task.Run(async () =>
        {
            if (diagnosticChannel is not null)
            {
                await diagnosticChannel.DisposeAsync().ConfigureAwait(false);
            }

            await app.StopAsync().ConfigureAwait(false);
            await services.DisposeAsync().ConfigureAwait(false);
        });

        try
        {
            CrashLog.Write(
                "lifecycle",
                teardown.Wait(ShutdownTimeout)
                    ? "Teardown finished."
                    : $"Teardown did not finish within {ShutdownTimeout.TotalSeconds:0} seconds; exiting anyway.");
        }
        catch (AggregateException exception)
        {
            CrashLog.Write("shutdown-failure", exception);
        }
    }

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
