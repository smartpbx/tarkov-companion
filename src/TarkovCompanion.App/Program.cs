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
    /// <remarks>
    /// Eight seconds, down from fifteen, and the number is not arbitrary. The Windows page
    /// gallery closes the main window and allows the process twenty seconds to be gone — a window
    /// that has to cover the close, the whole of teardown and the process ending. Fifteen was
    /// also exactly what the two things teardown waits on add up to (a ten-second feature-lifecycle
    /// stop beside a ten-second supervisor stop, then five more for initialisation work that
    /// cannot complete once the dispatcher has stopped), so a shutdown that used its budget used
    /// all of it and left about five seconds of margin. That is why "The packaged app required
    /// forced termination after '&lt;page&gt;'" moved from page to page between runs instead of
    /// naming one broken route, and why Clayton's own logs show runs that never reached their own
    /// shutdown.
    ///
    /// Eight seconds is a budget the steps are now taken *from* rather than nested inside; see
    /// <see cref="ShutdownStages"/>.
    /// </remarks>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(8);

    /// <summary>How long after teardown the process has to be gone before it is ended outright.</summary>
    /// <remarks>
    /// Returning from <c>Main</c> ends a process with no foreground threads, and this application
    /// starts none — so on the ordinary path this watchdog is a background thread that is never
    /// woken and dies with everything else. It exists because "ordinarily" is not a guarantee: a
    /// native thread from Skia, SQLite or the Windows media OCR stack, or a managed one created by
    /// a dependency, is enough to leave a companion running invisibly with its window gone. That
    /// is worse for a second-monitor application than an abrupt exit, and it is what the gallery
    /// has been killing.
    /// </remarks>
    private static readonly TimeSpan ExitWatchdog = TimeSpan.FromSeconds(4);

    [STAThread]
    public static int Main(string[] args)
    {
        // Before Velopack, before the command line, before anything: this is not the application
        // starting, it is the application being used as a map rasteriser by an application that is
        // already running. It draws one picture and exits. Nothing else in this method may run —
        // an install hook, a single-instance guard or a window would all be wrong for a child that
        // lives for two seconds.
        //
        // Why a child process at all: rasterising a drawing killed the application outright on
        // 2026-09-19 with a native access violation inside Skia. A native fault cannot be caught,
        // so the only way to survive one is for it to happen somewhere else. See
        // OutOfProcessSvgRasterizer.
        if (MapRasterizerHost.TryRun(args) is { } rasterizerExitCode)
        {
            return rasterizerExitCode;
        }

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
            var logDirectory = AppDataPaths.Resolve(demoMode: options.Demo).Logs;
            CrashLog.Install(logDirectory);

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

            // After the single-instance guard, and only for an ordinary launch. A second instance
            // that exits immediately would otherwise roll the running instance's breadcrumbs
            // aside and leave a marker nobody clears; a self-test, page gallery or headless demo
            // is killed on purpose by the tool driving it, and each would be reported to the next
            // player as a run that died.
            if (IsOrdinaryLaunch(options))
            {
                CrashBreadcrumbs.Install(logDirectory);
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
                CrashBreadcrumbs.Drop("dispatcher-exception", arguments.Exception.GetType().Name);
                arguments.Handled = true;
            };

            CrashLog.Write("lifecycle", "Desktop lifetime starting.");
            var lifetimeExitCode = 0;
            try
            {
                lifetimeExitCode = BuildAvaloniaApp(app).StartWithClassicDesktopLifetime(args);
                CrashLog.Write("lifecycle", $"Desktop lifetime returned {lifetimeExitCode}.");
                return lifetimeExitCode;
            }
            finally
            {
                ShutDown(app, services, diagnosticChannel, lifetimeExitCode);
            }
        }
        catch (Exception exception)
        {
            CrashLog.Write("startup-failure", exception);
            // The cause is in the log, so there is nothing for the next launch to add. Left
            // standing, the marker would make it announce a run that died without saying why,
            // immediately under the entry saying exactly why.
            CrashBreadcrumbs.MarkCleanExit();
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
    private static void ShutDown(App app, ServiceProvider services, DiagnosticCommandChannel? diagnosticChannel, int exitCode)
    {
        var stages = new ShutdownStages(ShutdownTimeout);
        var report = "not reached";
        var teardown = Task.Run(async () =>
        {
            if (diagnosticChannel is not null)
            {
                await stages
                    .RunAsync("diagnostic-channel", diagnosticChannel.DisposeAsync().AsTask, TimeSpan.FromSeconds(1))
                    .ConfigureAwait(false);
            }

            // The interface reports its own steps, because it is the half that knows what they
            // were. Its budget comes out of this one, so the two cannot add up to more than the
            // whole.
            var interfaceReport = string.Empty;
            await stages
                .RunAsync(
                    "interface",
                    async () => interfaceReport = await app.StopAsync(stages.Remaining).ConfigureAwait(false),
                    TimeSpan.FromSeconds(4))
                .ConfigureAwait(false);
            await stages
                .RunAsync("services", services.DisposeAsync().AsTask, stages.Remaining)
                .ConfigureAwait(false);
            report = $"{stages.Report()}{(interfaceReport.Length == 0 ? string.Empty : $" · interface: {interfaceReport}")}";
        });

        try
        {
            // Every step is already bounded, so this wait is the belt to that brace rather than
            // the thing doing the bounding. A little slack over the budget, so an ordinary
            // shutdown reports its own numbers instead of this line.
            var finished = teardown.Wait(ShutdownTimeout + TimeSpan.FromSeconds(1));
            CrashLog.Write(
                "lifecycle",
                finished
                    ? $"Teardown finished: {report}"
                    : $"Teardown did not finish within {ShutdownTimeout.TotalSeconds:0} seconds; exiting anyway.");
        }
        catch (AggregateException exception)
        {
            CrashLog.Write("shutdown-failure", exception);
        }
        finally
        {
            // Last thing, and in a finally, because the question the next launch asks is only
            // "did this run reach its own shutdown". A teardown that timed out still did.
            CrashBreadcrumbs.MarkCleanExit();
            ArmExitWatchdog(exitCode);
        }
    }

    /// <summary>
    /// Ends the process if returning from <c>Main</c> does not.
    /// </summary>
    /// <remarks>
    /// A background thread, so on the ordinary path — which is every path the application
    /// controls — it is destroyed with the process before it ever wakes, and costs a thread for a
    /// few seconds at exit. When it does wake, the process is still alive after teardown finished
    /// and after <c>Main</c> should have returned, which means something outside this
    /// application's own bookkeeping is holding it: a native thread, or a managed one a dependency
    /// created. There is nothing useful left to wait for at that point, and a companion still
    /// running with no window is the failure a second-monitor application can least afford.
    ///
    /// It says so in the log before it goes, because an exit nobody can account for is how this
    /// class of bug stays invisible.
    /// </remarks>
    private static void ArmExitWatchdog(int exitCode)
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(ExitWatchdog);
            CrashLog.Write(
                "lifecycle",
                $"The process was still running {ExitWatchdog.TotalSeconds:0} seconds after teardown; ending it.");
            Environment.Exit(exitCode);
        })
        {
            IsBackground = true,
            Name = "tarkov-companion-exit-watchdog",
        };
        watchdog.Start();
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
