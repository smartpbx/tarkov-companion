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
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;
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
        var options = AppCommandLine.Parse(args) with { Demo = true };

        var dataRoot = Path.Combine(Path.GetTempPath(), $"v2-render-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);

        // V2 rough package 17: the demo fixture seeds one item and no quests or hideout, so a
        // Plan render showed only empty states. --seed-database copies an existing synced
        // database (any earlier real run's tarkov-companion.db) into the throwaway data root;
        // startup migrates it forward, and it is deleted with the root afterwards.
        if (StringOption(args, "--seed-database") is { } seedDatabase)
        {
            var databaseDirectory = AppDataPaths.Resolve(dataRoot, demoMode: true).Database;
            Directory.CreateDirectory(databaseDirectory);
            File.Copy(seedDatabase, Path.Combine(databaseDirectory, "tarkov-companion.db"));
        }
        try
        {
            await using var services = AppComposition.Build(options, new AppCompositionSettings(DataRoot: dataRoot, Offline: true));

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

            // A fresh profile has no quest recorded as active, so the Plan page has nothing to
            // plan. --seed-active-quests marks that many available quests active through the same
            // command service the page itself uses, then reloads the page.
            if (IntOption(args, "--seed-active-quests", 0) is var questCount and > 0)
            {
                DrainUntilComplete(SeedActiveQuestsAsync(services, questCount));
                DrainUntilComplete(services.GetRequiredService<PlanWorkspaceViewModel>().RefreshAsync());
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

                // A page other than Raid picks its own map (Plan follows its selected map group),
                // so only a Raid render, or an explicit --map, chooses one here.
                var picked = mapId is null && options.StartPage is not null
                    ? null
                    : mapId is null
                    ? raid.MapPicker.FirstOrDefault()
                    : raid.MapPicker.FirstOrDefault(item => string.Equals(item.MapId, mapId, StringComparison.OrdinalIgnoreCase));
                if (picked is not null)
                {
                    picked.SelectCommand.Execute(null);
                    Pump(40);
                }
                else if (raid.MapPicker.Count == 0)
                {
                    Console.Error.WriteLine("No map available to select; the map picker stayed empty.");
                }
                else
                {
                    Pump(80);
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

            SaveFrame(window, outputPath, width, height);
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
        }
    }

    private static async Task SeedActiveQuestsAsync(IServiceProvider services, int count)
    {
        var profile = await services.GetRequiredService<IPlayerProfileService>().GetActiveAsync(CancellationToken.None);
        var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
        var board = await services.GetRequiredService<IQuestReadService>().GetQuestBoardAsync(scope, CancellationToken.None);
        var commands = services.GetRequiredService<IQuestProgressCommandService>();
        // Preview data only: the first few quests with map objectives, spread over three maps
        // so the page has more than one map bundle. Eligibility is ignored; a fresh profile's is
        // mostly indeterminate.
        var picked = board.Tasks
            .Where(task => task.Objectives.Any(objective => objective.MapIds.Count > 0))
            .GroupBy(task => task.Objectives.First(objective => objective.MapIds.Count > 0).MapIds[0], StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .Take(3)
            .SelectMany(group => group.Take((count + 2) / 3))
            .Take(count)
            .ToArray();
        foreach (var task in picked)
        {
            await commands.SetTaskStateAsync(scope, task.TaskId, RecordedTaskState.Active, CancellationToken.None);
        }

        Console.WriteLine($"Seeded {picked.Length} active quest(s) of {board.Tasks.Count}.");
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
