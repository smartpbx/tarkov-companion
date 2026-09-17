using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.Services.V2.Profile;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.App.Views;
using AppClass = TarkovCompanion.App.App;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// V2 rough package 15: a dev-only tool that boots the real composition (demo fixture data, the
/// real MainWindow/V2ShellView, no game or network touched) under Avalonia's headless platform
/// and saves one PNG of the rendered window. It exists so a V2 UI change can be compared against
/// docs/design/v2's concept renders on Linux, where there is no display and the Windows-only
/// windows-page-gallery.ps1 script cannot run. Not part of scripts/build.sh or scripts/test.sh.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var width = IntOption(args, "--width", 1920);
        var height = IntOption(args, "--height", 1080);
        var outputPath = StringOption(args, "--out") ?? throw new ArgumentException("--out <path.png> is required.");
        var mapId = StringOption(args, "--map");
        // V2 rough package 17: land on a workspace other than the variant's landing page
        // ("intel", "intel/item/<id>"), and optionally run a search there first.
        var route = StringOption(args, "--route");
        var search = StringOption(args, "--search");
        var options = AppCommandLine.Parse(args) with { Demo = true };

        var rendered = false;
        var dataRoot = Path.Combine(Path.GetTempPath(), $"v2-render-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        try
        {
            // Not disposed: some services' DisposeAsync continues on the UI dispatcher, which
            // nothing pumps once the frame is saved, so awaiting it hung the process after
            // "Saved" (package 17). The process exits right after the finally block instead.
            var services = AppComposition.Build(options, new AppCompositionSettings(DataRoot: dataRoot, Offline: true));

            AppBuilder.Configure(() => new AppClass(services))
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                // Binding and layout warnings are exactly what the Windows page gallery fails on;
                // printing them here lets a Linux run catch the same faults before CI does.
                .LogToTextWriter(Console.Out, Avalonia.Logging.LogEventLevel.Warning, Avalonia.Logging.LogArea.Binding, Avalonia.Logging.LogArea.Layout)
                .SetupWithoutStarting();

            if (options.MapRendererGallery)
            {
                var gallery = new TarkovCompanion.App.Views.V2.MapRenderer.MapSceneRendererGalleryWindow(
                    options.MapRendererLargeText, options.MapRendererLootOffline) { Width = width, Height = height };
                gallery.Show();
                Pump(40);
                var ids = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(gallery)
                    .Select(Avalonia.Automation.AutomationProperties.GetAutomationId)
                    .Where(id => id is not null && (id.Contains("cluster", StringComparison.Ordinal) || id is "v2-map-zoom-in" or "v2-map-loot-preset"))
                    .Distinct();
                Console.WriteLine("Automation ids: " + string.Join(", ", ids));
                SaveFrame(gallery, outputPath, width, height);
                rendered = true;
                return 0;
            }

            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            V2ShellViewModel? shell = null;
            if (options.UiShell.IsPreview())
            {
                shell = services.GetRequiredService<V2ShellViewModel>();
                viewModel.PreviewShell = shell;
                services.GetRequiredService<V2ShellCaptureBridge>();
                DrainUntilComplete(services.GetRequiredService<LegacyProfileContextBootstrap>().EnsureSeededAsync(CancellationToken.None));
            }
            else if (options.StartPage is { } startPage && !viewModel.Navigate(startPage))
            {
                throw new ArgumentException($"No destination is named '{startPage}'.");
            }

            var window = new MainWindow { DataContext = viewModel, Width = width, Height = height };
            window.Show();
            DrainUntilComplete(viewModel.InitializeAsync());

            if (shell is not null && route is not null)
            {
                var result = shell.Router.NavigateToAddress(route);
                if (!result.Succeeded)
                {
                    throw new ArgumentException($"The shell refused '{route}': {result.Failure}");
                }

                Pump(20);
            }

            if (shell is not null && search is not null)
            {
                shell.SearchText = search;
                DrainUntilComplete(shell.SearchAsync());
                Pump(20);
            }

            // The Raid workspace's map follows whatever the legacy MapViewModel is already
            // showing; a headless run has nobody at the V1 Raid page to have selected one, so
            // pick a map here the same way the map picker's own SelectCommand does, once the
            // map catalog itself has finished loading.
            if (shell?.RaidCockpit is TarkovCompanion.App.ViewModels.V2.Raid.RaidCockpitViewModel raid)
            {
                for (var i = 0; i < 200 && raid.MapPicker.Count == 0; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(25);
                }

                var picked = mapId is null
                    ? raid.MapPicker.FirstOrDefault()
                    : raid.MapPicker.FirstOrDefault(item => string.Equals(item.MapId, mapId, StringComparison.OrdinalIgnoreCase));
                if (picked is not null)
                {
                    picked.SelectCommand.Execute(null);
                    Pump(40);
                }
                else
                {
                    Console.Error.WriteLine("No map available to select; the map picker stayed empty.");
                }
            }

            // A handful of extra dispatcher turns for layout, DynamicResource resolution, and
            // the map scene's own async asset resolution to settle after data arrives.
            Pump(20);

            // The map canvas sizes itself from PlanViewport.Bounds via a SizeChanged handler.
            // At larger widths that handler's first firing can land before the window's own
            // layout has settled at its final requested size, leaving the canvas sized to an
            // earlier, smaller pass. A nudge-and-restore forces one more SizeChanged once
            // everything else (map data, the details-panel toggle) has already settled.
            window.Width = width - 1;
            Pump(5);
            window.Width = width;
            Pump(10);

            // Package 17 (team): a render-only group, so the Team workspace can be seen populated.
            // A headless run has no relay to join, and the offline group session republishes
            // "not sharing" on its own tick, so this goes straight to the view model last.
            if (shell is not null && args.Contains("--team-demo"))
            {
                var store = services.GetRequiredService<TarkovCompanion.Application.Services.Runtime.IRuntimeStateStore>();
                var demo = TeamDemoGroup();
                for (var i = 0; i < 6; i++)
                {
                    // The shell re-applies the store's snapshot on every refresh (the raid clock
                    // alone ticks once a second), so the store carries the demo group too.
                    store.Update(snapshot => snapshot with { Group = demo });
                    services.GetRequiredService<TarkovCompanion.App.ViewModels.V2.Team.TeamWorkspaceViewModel>()
                        .Apply(store.Current);
                    Pump(1);
                }
            }

            SaveFrame(window, outputPath, width, height);
            rendered = true;
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(dataRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: this is a throwaway temp directory for one render.
            }

            // Headless Avalonia and the composition's background services keep foreground
            // threads alive; a finished render is one frame, so end the process explicitly.
            if (rendered)
            {
                Console.Out.Flush();
                Environment.Exit(0);
            }
        }
    }

    private static TarkovCompanion.Application.Services.Group.GroupSnapshot TeamDemoGroup()
    {
        var now = DateTimeOffset.UtcNow;
        TarkovCompanion.Application.Services.Group.GroupMemberView Member(string name, string map, TimeSpan since, params string[] quests) =>
            new(name, map, TarkovCompanion.Core.Domain.Raids.RaidLifecycleState.InRaid, "PMC", null, null, null, [], quests) { Since = since };
        return new(true,
            [
                Member("Geo", "customs", TimeSpan.FromSeconds(4), "Delivery from the Past", "Debut"),
                Member("Riley", "customs", TimeSpan.FromSeconds(9), "Delivery from the Past"),
                Member("Sam", "customs", TimeSpan.FromMinutes(2), "Shortage"),
            ],
            "Sharing as Clay · 3 others here",
            now)
        {
            Waypoints =
            [
                new(1, "Geo", "customs", 0, 0, 0, "Dorms", "Riley") { CreatedUtc = now.AddMinutes(-6) },
                new(2, "Riley", "customs", 0, 0, 0, null, null) { CreatedUtc = now.AddMinutes(-4) },
                new(3, "Geo", "customs", 0, 0, 0, "Old gas station", null) { CreatedUtc = now.AddMinutes(-2) },
                new(4, "Clay", "customs", 0, 0, 0, "RUAF roadblock", null) { CreatedUtc = now.AddMinutes(-1) },
            ],
            Pings = [new(5, "Sam", "customs", 0, 0, 0, null, now.AddSeconds(-12))],
        };
    }

    private static void SaveFrame(Window window, string outputPath, int width, int height)
    {
        using var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("The headless platform produced no frame.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using (var stream = File.Create(outputPath))
        {
            frame.Save(stream, new PngBitmapEncoderOptions());
        }

        Console.WriteLine($"Saved {outputPath} ({width}x{height}).");
    }

    private static void Pump(int turns)
    {
        for (var i = 0; i < turns; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }
    }

    private static void DrainUntilComplete(Task task)
    {
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        task.GetAwaiter().GetResult();
    }

    private static int IntOption(string[] args, string name, int fallback)
    {
        var value = StringOption(args, name);
        return value is null ? fallback : int.Parse(value);
    }

    private static string? StringOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
