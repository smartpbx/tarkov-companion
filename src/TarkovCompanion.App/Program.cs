using Avalonia;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Runtime;
using Velopack;

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
        // First, before anything. Velopack runs the install, update and uninstall hooks here
        // and exits the process for some of them, so any work done before this call is work
        // done during an install the user is waiting on, and any window shown before it is a
        // window that flashes up during an upgrade.
        //
        // This replaces a hand-written batch script that mirrored the install directory in
        // place. That approach could not be made to work: the application lived in a
        // OneDrive-synced folder where the sync client holds file handles continuously, so
        // the swap either failed on a locked executable or refused to start at all. The fix
        // is not a better script, it is not installing there.
        VelopackApp.Build().Run();

        try
        {
            var options = AppCommandLine.Parse(args);
            CrashLog.Install(AppDataPaths.Resolve(demoMode: options.Demo).Logs);

            // Said out loud rather than swallowed. An option that has not shipped yet used to
            // be indistinguishable from an option that had no effect, and somebody drew the
            // wrong conclusion from exactly that. Reported and then ignored: a flag from a
            // newer build is a mistake worth naming, not a reason to refuse to start.
            foreach (var unknown in options.UnknownOptions)
            {
                Console.Error.WriteLine($"Ignoring '{unknown}': this build does not have that option.");
            }

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

            if (options.OcrProbePath is { Length: > 0 } probePath)
            {
                return RunOcrProbe(probePath, options);
            }

            // Only the ordinary launch is guarded. A self-test, a headless demo, a page
            // screenshot and a developer build are all deliberate, short-lived, and sometimes
            // run beside each other on purpose; refusing those would break verification to
            // prevent a problem none of them have.
            using var instance = IsOrdinaryLaunch(options)
                ? SingleInstance.TryAcquire("TarkovCompanion.SingleInstance")
                : null;
            if (IsOrdinaryLaunch(options) && instance is null)
            {
                const string Message =
                    "Tarkov Companion is already running. Look for its window on your other "
                    + "monitor; it may be behind the game.";
                CrashLog.Write("lifecycle", Message);
                Console.Error.WriteLine(Message);
                return 0;
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

    /// <summary>Whether this is a player starting the companion, rather than a tool running it.</summary>
    private static bool IsOrdinaryLaunch(AppCommandLine options) =>
        !options.SelfTest
        && !options.Headless
        && !options.Demo
        && !options.DeveloperMode
        && options.StartPage is null
        && options.OcrProbePath is null
        && !options.MapRendererGallery;

    /// <summary>
    /// Builds the application, recording the toolkit's warnings when a tool asks for them.
    /// </summary>
    /// <remarks>
    /// <c>LogToTrace</c> has always been here and had nowhere to write, so a binding to a
    /// property that no longer exists was reported to nobody. The listener is added before the
    /// builder so that warnings raised while the first page loads are caught too.
    /// </remarks>
    public static AppBuilder BuildAvaloniaApp(App app)
    {
        InterfaceWarningLog.TryStart();
        return AppBuilder.Configure(() => app)
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }

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

    /// <summary>
    /// Runs the OCR probe with a cancellation Ctrl+C can actually reach.
    /// </summary>
    /// <remarks>
    /// The probe used to be handed <see cref="CancellationToken.None"/>, so a long cell run could
    /// only be stopped by killing the process, mid-way through whatever native OCR it was in.
    /// The first Ctrl+C now cancels the run at its next bounded check and exits with 130; a
    /// second one is left to terminate the process the ordinary way.
    /// </remarks>
    private static int RunOcrProbe(string probePath, AppCommandLine options)
    {
        using var cancellation = new CancellationTokenSource();
        void Cancel(object? sender, ConsoleCancelEventArgs arguments)
        {
            try
            {
                if (!cancellation.IsCancellationRequested)
                {
                    arguments.Cancel = true;
                    cancellation.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
                // The probe already finished; let the key press end the process.
            }
        }

        Console.CancelKeyPress += Cancel;
        try
        {
            var probe = options.OcrProbeCells
                ? OcrProbe.RunCellsAsync(probePath, options, cancellation.Token)
                : OcrProbe.RunAsync(probePath, options, cancellation.Token);
            return probe
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("OCR probe cancelled; no report was written.");
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= Cancel;
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
