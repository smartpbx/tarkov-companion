using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.App.Views;
using AppClass = TarkovCompanion.App.App;

namespace TarkovCompanion.RaidPerfHarness;

/// <summary>
/// The real composition, the real V2 window and the real Raid workspace, in a headless Avalonia
/// with Skia doing the drawing, and a way to wait for it that costs nothing while it waits.
/// </summary>
/// <remarks>
/// Nothing here is a stand-in for the app: it is the same boot the render preview does, timed.
/// What it is not is a GPU, a real compositor or the Windows window manager, so an absolute
/// number is a CPU-rasterized one and the comparison between two builds is the useful part.
/// </remarks>
internal sealed class HarnessHost
{
    public required ServiceProvider Services { get; init; }

    public required MainWindow Window { get; init; }

    public required MainWindowViewModel Main { get; init; }

    public required V2ShellViewModel Shell { get; init; }

    public required RaidCockpitViewModel Raid { get; init; }

    public required string DataRoot { get; init; }

    /// <summary>Milliseconds since the process started, at each step of getting to a usable window.</summary>
    public Dictionary<string, double> ColdStart { get; } = [];

    public static HarnessHost Boot(int width, int height, string? seedDatabase)
    {
        var process = Process.GetCurrentProcess();
        var epoch = process.StartTime;
        var watch = Stopwatch.StartNew();
        // How long the runtime took to reach Main, which the stopwatch below cannot see.
        var untilMain = (DateTime.Now - epoch).TotalMilliseconds;
        double Since() => untilMain + watch.Elapsed.TotalMilliseconds;

        var marks = new Dictionary<string, double> { ["runtime-to-main"] = untilMain };
        var dataRoot = Path.Combine(Path.GetTempPath(), $"raid-perf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        if (seedDatabase is not null)
        {
            var databaseDirectory = AppDataPaths.Resolve(dataRoot, demoMode: true).Database;
            Directory.CreateDirectory(databaseDirectory);
            File.Copy(seedDatabase, Path.Combine(databaseDirectory, "tarkov-companion.db"));
        }

        var options = AppCommandLine.Parse(["--ui-shell", "v2-a"]) with { Demo = true };
        var services = AppComposition.Build(options, new AppCompositionSettings(DataRoot: dataRoot, Offline: true));
        marks["composition-built"] = Since();

        AppBuilder.Configure(() => new AppClass(services))
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();
        marks["platform-ready"] = Since();

        var main = services.GetRequiredService<MainWindowViewModel>();
        var shell = services.GetRequiredService<V2ShellViewModel>();
        main.PreviewShell = shell;
        services.GetRequiredService<TarkovCompanion.App.Services.V2.Capture.V2ShellCaptureBridge>();
        var seeding = services.GetRequiredService<TarkovCompanion.App.Services.V2.Profile.LegacyProfileContextBootstrap>()
            .EnsureSeededAsync(CancellationToken.None);

        var window = new MainWindow { DataContext = main, Width = width, Height = height };
        window.Show();
        marks["window-shown"] = Since();

        // The window is on screen and drawn before its data is: the real app shows it while
        // initialization runs, so this is the first thing a player sees.
        using (window.CaptureRenderedFrame())
        {
        }

        marks["first-paint"] = Since();

        DrainUntilComplete(main.InitializeAsync());
        DrainUntilComplete(seeding);
        marks["data-ready"] = Since();

        using (window.CaptureRenderedFrame())
        {
        }

        marks["usable"] = Since();

        var host = new HarnessHost
        {
            Services = services,
            Window = window,
            Main = main,
            Shell = shell,
            Raid = shell.RaidCockpit as RaidCockpitViewModel
                ?? throw new InvalidOperationException("The V2 shell has no Raid cockpit."),
            DataRoot = dataRoot,
        };
        foreach (var (key, value) in marks)
        {
            host.ColdStart[key] = Math.Round(value, 1);
        }

        return host;
    }

    /// <summary>Runs the dispatcher until <paramref name="done"/>, without sleeping through it.</summary>
    public static bool RunUntil(Func<bool> done, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > timeout)
            {
                return false;
            }

            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }

        return true;
    }

    public static void DrainUntilComplete(Task task)
    {
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }

        task.GetAwaiter().GetResult();
    }

    /// <summary>Draws one frame and says how long it took.</summary>
    public double Frame()
    {
        var watch = Stopwatch.StartNew();
        Dispatcher.UIThread.RunJobs();
        using (Window.CaptureRenderedFrame())
        {
        }

        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Stops the composition's own group session, so the harness is the only thing publishing the
    /// group.
    /// </summary>
    /// <remarks>
    /// Sharing is off in a demo run, so the real service republishes "not sharing" on every
    /// exchange interval, which wipes the simulated squad off the plan until the next simulated
    /// exchange puts it back. Left running it made the scene lose and regain its squad every five
    /// seconds and charged that to whichever event happened to be in flight.
    /// </remarks>
    public void StopGroupSession() =>
        DrainUntilComplete(Services.GetRequiredService<TarkovCompanion.Application.Services.Group.GroupSessionService>()
            .DisposeAsync().AsTask());

    public void Cleanup()
    {
        try
        {
            Directory.Delete(DataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
