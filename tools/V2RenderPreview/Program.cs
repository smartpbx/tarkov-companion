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
        // V2 rough package 17: land on a workspace other than the variant's landing page
        // ("intel", "intel/item/<id>"), and optionally run a search there first.
        var route = StringOption(args, "--route");
        var search = StringOption(args, "--search");
        var options = AppCommandLine.Parse(args) with { Demo = true };

        var rendered = false;
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
            Task? seeding = null;
            if (options.UiShell.IsPreview())
            {
                shell = services.GetRequiredService<V2ShellViewModel>();
                viewModel.PreviewShell = shell;
                services.GetRequiredService<V2ShellCaptureBridge>();
                // [V2 rough package 22] Started, not awaited: since #9f7c369 this waits for the
                // database feature, which only becomes ready inside MainWindowViewModel
                // .InitializeAsync below. Draining it here deadlocked every render run — the
                // drain loop pumped the dispatcher forever for a task whose gate had not been
                // opened yet. It is drained after initialization instead.
                seeding = services.GetRequiredService<LegacyProfileContextBootstrap>().EnsureSeededAsync(CancellationToken.None);
            }
            else if (options.StartPage is { } startPage && !viewModel.Navigate(startPage))
            {
                throw new ArgumentException($"No destination is named '{startPage}'.");
            }

            var window = new MainWindow { DataContext = viewModel, Width = width, Height = height };
            window.Show();
            DrainUntilComplete(viewModel.InitializeAsync());
            if (seeding is not null)
            {
                DrainUntilComplete(seeding);
            }

            // A fresh profile has no quest recorded as active, so the Plan page has nothing to
            // plan. --seed-active-quests marks that many available quests active through the same
            // command service the page itself uses, then reloads the page.
            if (IntOption(args, "--seed-active-quests", 0) is var questCount and > 0)
            {
                DrainUntilComplete(SeedActiveQuestsAsync(services, questCount));
                DrainUntilComplete(services.GetRequiredService<PlanWorkspaceViewModel>().RefreshAsync());
            }

            if (shell is not null && route is not null)
            {
                var result = shell.Router.NavigateToAddress(route);
                if (!result.Succeeded)
                {
                    throw new ArgumentException($"The shell refused '{route}': {result.Failure}");
                }

                Pump(20);
            }

            // V2 rough package 36: Setup opens on its overview, so a render of any other section
            // (--route setup --setup-section Updates) presses that section's own tab.
            if (shell?.SetupWorkspace is { } setup && StringOption(args, "--setup-section") is { } sectionName)
            {
                var tab = setup.Sections.FirstOrDefault(section =>
                        section.Section.ToString().Equals(sectionName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Setup has no section named '{sectionName}'.");
                tab.SelectCommand.Execute(null);
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
                    // V2 rough package 20: the scene rebuild and the artwork decode are async, so
                    // 40 dispatcher turns was enough for the catalog's first map and not for a
                    // switch to another one — a --map streets run rendered Customs. Wait for the
                    // renderer to actually be showing the map that was asked for.
                    for (var i = 0; i < 400; i++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        if (string.Equals(raid.Renderer?.Scene.LocationId, picked.MapId, StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        Thread.Sleep(25);
                    }

                    Pump(40);
                    if (!string.Equals(raid.Renderer?.Scene.LocationId, picked.MapId, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Error.WriteLine(
                            $"The Raid map never became '{picked.MapId}' (showing '{raid.Renderer?.Scene.LocationId}').");
                    }
                }
                else if (raid.MapPicker.Count == 0)
                {
                    Console.Error.WriteLine("No map available to select; the map picker stayed empty.");
                }
                else
                {
                    Pump(80);
                }

                // V2 rough package 20: which maps a --map value can name, so a render run that
                // asks for one that is not in this install's catalog says so instead of quietly
                // rendering whichever map came first.
                Console.WriteLine("Maps: " + string.Join(", ", raid.MapPicker.Select(item => item.MapId)));
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
                var demo = TeamDemoGroup(viewModel.Map.RenderModel);
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

            // [V2 rough package 22] A render-only raid: a player position with a heading, the
            // trail behind it, and a squad standing around. Everything downstream of it is the
            // real path — the runtime store, MainWindowViewModel.Apply, MapViewModel.ShowPlayer,
            // the cockpit's scene build — so a render proves the wiring, not a fixture.
            if (shell is not null && args.Contains("--raid-demo"))
            {
                var store = services.GetRequiredService<TarkovCompanion.Application.Services.Runtime.IRuntimeStateStore>();
                var demo = RaidDemo(viewModel.Map.RenderModel);
                for (var i = 0; i < 8; i++)
                {
                    store.Update(snapshot => snapshot with { Raid = demo.Raid, Group = demo.Group });
                    Pump(2);
                }

                Pump(20);
            }

            // Package 17 (scan): render-only fixtures so the Loot decision and Stash scan
            // workspaces can be seen populated. Both go through the real services (the loot
            // planner, the snapshot store), so nothing here invents presentation state.
            if (shell is not null && args.Contains("--loot-demo"))
            {
                var profile = services.GetRequiredService<TarkovCompanion.Application.Services.Runtime.IRuntimeStateStore>()
                    .Current.Profile ?? throw new InvalidOperationException("The demo composition has no profile.");
                var scope = new TarkovCompanion.Core.Domain.Inventory.InventoryProfileScope(
                    profile.Id, profile.ProfileGeneration, profile.GameMode.ToString());
                shell.ShowLootScanResult(new TarkovCompanion.App.ViewModels.V2.LootScan.LootScanViewModel(
                    ScanDemo.LootResult(scope)));
                Pump(20);
            }

            if (shell is not null && args.Contains("--stash-demo"))
            {
                var profile = services.GetRequiredService<TarkovCompanion.Application.Services.Runtime.IRuntimeStateStore>()
                    .Current.Profile ?? throw new InvalidOperationException("The demo composition has no profile.");
                var scope = new TarkovCompanion.Core.Domain.Inventory.InventoryProfileScope(
                    profile.Id, profile.ProfileGeneration, profile.GameMode.ToString());
                var store = services.GetRequiredService<TarkovCompanion.Core.Domain.Stash.IStashSnapshotStore>();
                DrainUntilComplete(store.SaveAsync(ScanDemo.StashRecord(scope), CancellationToken.None));
                DrainUntilComplete(services.GetRequiredService<TarkovCompanion.App.ViewModels.V2.StashScan.StashScanWorkspaceViewModel>()
                    .LoadAsync());
                Pump(20);
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

    /// <summary>
    /// [V2 rough package 22] A raid in progress on the shown map: where the player is, which way
    /// they are facing, where they have walked, and two squadmates with their own paths.
    /// </summary>
    /// <remarks>
    /// The positions are world positions found by probing the map's own transform, the same way
    /// <see cref="TeamDemoGroup"/> does, so they land on the plan rather than off its edge on
    /// whichever map is being rendered.
    /// </remarks>
    private static (TarkovCompanion.Core.Domain.Raids.RaidSnapshot Raid, TarkovCompanion.Application.Services.Group.GroupSnapshot Group) RaidDemo(
        TarkovCompanion.Application.Services.Maps.MapRenderModel? model)
    {
        var now = DateTimeOffset.UtcNow;
        var mapId = model?.Location.Id ?? "customs";
        var candidates = new List<(double X, double Z, double PlanX, double PlanY)>();
        if (model is not null)
        {
            for (var x = -1200.0; x <= 1200; x += 10)
            {
                for (var z = -1200.0; z <= 1200; z += 10)
                {
                    if (TryPlanPercent(model, x, z, out var planX, out var planY) &&
                        planX is > 4 and < 96 && planY is > 4 and < 96)
                    {
                        candidates.Add((x, z, planX, planY));
                    }
                }
            }
        }

        TarkovCompanion.Core.Domain.Maps.WorldPosition At(double planX, double planY)
        {
            if (candidates.Count == 0)
            {
                return new(0, 0, 0);
            }

            var best = candidates.MinBy(item => Math.Pow(item.PlanX - planX, 2) + Math.Pow(item.PlanY - planY, 2));
            return new(best.X, 0, best.Z);
        }

        TarkovCompanion.Core.Domain.Maps.ScreenshotPosition Step(double planX, double planY, double heading, int secondsAgo) =>
            new(now.AddSeconds(-secondsAgo), At(planX, planY), default, heading, null, null, $"demo-{secondsAgo}.png");

        // A walk across the middle of the plan, oldest first, ending where the player is now.
        // Deliberately not a straight line: a trail drawn from collinear points is
        // indistinguishable from one long segment, which tells a reviewer nothing.
        var trail = new[]
        {
            Step(30, 78, 350, 330),
            Step(28, 68, 20, 290),
            Step(36, 64, 80, 250),
            Step(46, 66, 95, 210),
            Step(52, 58, 20, 170),
            Step(48, 50, 330, 130),
            Step(54, 44, 40, 90),
            Step(62, 42, 80, 50),
            Step(64, 34, 10, 15),
        };
        var raid = new TarkovCompanion.Core.Domain.Raids.RaidSnapshot(
            Guid.NewGuid(),
            TarkovCompanion.Core.Domain.Raids.RaidLifecycleState.InRaid,
            mapId,
            now.AddMinutes(-14),
            now,
            new(0.9),
            trail[^1],
            [],
            false)
        {
            Side = "PMC",
            PositionTrail = trail,
        };

        TarkovCompanion.Application.Services.Group.GroupMemberView Mate(string name, double planX, double planY, double heading, int secondsAgo) =>
            new(
                name,
                mapId,
                TarkovCompanion.Core.Domain.Raids.RaidLifecycleState.InRaid,
                "PMC",
                At(planX, planY),
                heading,
                TimeSpan.FromSeconds(secondsAgo),
                [],
                [])
            {
                Since = TimeSpan.FromSeconds(secondsAgo),
                Trail = [Leg(planX - 9, planY + 7, 90), Leg(planX - 5, planY + 4, 45), Leg(planX, planY, secondsAgo)],
            };

        TarkovCompanion.Application.Services.Group.GroupTrailPointView Leg(double planX, double planY, int secondsAgo)
        {
            var at = At(planX, planY);
            return new(at.X, at.Z, TimeSpan.FromSeconds(secondsAgo));
        }

        var group = new TarkovCompanion.Application.Services.Group.GroupSnapshot(
            true,
            [Mate("Geo", 72, 34, 300, 6), Mate("Riley", 44, 71, 120, 25)],
            "Sharing as Clay · 2 others here",
            now);
        return (raid, group);
    }

    /// <summary>
    /// [V2 rough package 23] Where a world position sits on the plan, as a percentage of it.
    /// </summary>
    /// <remarks>
    /// The demos below want to put a marker "a third of the way across"; the transform answers in
    /// Leaflet map units, whose range is different on every map (Customs runs 54-114 across, not
    /// 0-100). Probing against 0-100 therefore picked whatever slice of the real map happened to
    /// overlap that square, which put the render's player and squad off the artwork's corner.
    /// </remarks>
    private static bool TryPlanPercent(
        TarkovCompanion.Application.Services.Maps.MapRenderModel model,
        double x,
        double z,
        out double planX,
        out double planY)
    {
        planX = 0;
        planY = 0;
        if (TarkovCompanion.Application.Services.Maps.MapPlanProjection.For(model) is not { IsValid: true } rect ||
            !model.TryMapPosition(new(x, 0, z), out var point))
        {
            return false;
        }

        planX = (point.X - rect.MinimumX) / rect.Width * 100;
        planY = (point.Y - rect.MinimumY) / rect.Height * 100;
        return double.IsFinite(planX) && double.IsFinite(planY);
    }

    private static TarkovCompanion.Application.Services.Group.GroupSnapshot TeamDemoGroup(
        TarkovCompanion.Application.Services.Maps.MapRenderModel? model)
    {
        var now = DateTimeOffset.UtcNow;
        var mapId = model?.Location.Id ?? "customs";

        // World positions that land at chosen spots on the selected map's plan, found by probing
        // its own transform, so the demo marks sit on the map rather than off its edge.
        var candidates = new List<(double X, double Z, double PlanX, double PlanY)>();
        if (model is not null)
        {
            for (var x = -1200.0; x <= 1200; x += 15)
            {
                for (var z = -1200.0; z <= 1200; z += 15)
                {
                    if (TryPlanPercent(model, x, z, out var planX, out var planY) &&
                        planX is > 5 and < 95 && planY is > 5 and < 95)
                    {
                        candidates.Add((x, z, planX, planY));
                    }
                }
            }
        }

        (double X, double Z) At(double planX, double planY) => candidates.Count == 0
            ? (0, 0)
            : candidates.MinBy(item => Math.Pow(item.PlanX - planX, 2) + Math.Pow(item.PlanY - planY, 2)) is var best ? (best.X, best.Z) : (0, 0);

        TarkovCompanion.Application.Services.Group.GroupMemberView Member(string name, TimeSpan since, params string[] quests) =>
            new(name, mapId, TarkovCompanion.Core.Domain.Raids.RaidLifecycleState.InRaid, "PMC", null, null, null, [], quests) { Since = since };
        TarkovCompanion.Application.Services.Group.GroupWaypointView Waypoint(long id, string by, double planX, double planY, string? label, string? reached, int minutesAgo)
        {
            var (x, z) = At(planX, planY);
            return new(id, by, mapId, x, 0, z, label, reached) { CreatedUtc = now.AddMinutes(-minutesAgo) };
        }

        var (pingX, pingZ) = At(55, 30);
        return new(true,
            [
                Member("Geo", TimeSpan.FromSeconds(4), "Delivery from the Past", "Debut"),
                Member("Riley", TimeSpan.FromSeconds(9), "Delivery from the Past"),
                Member("Sam", TimeSpan.FromMinutes(2), "Shortage"),
            ],
            "Sharing as Clay · 3 others here",
            now)
        {
            Waypoints =
            [
                Waypoint(1, "Geo", 22, 35, "Dorms", "Riley", 6),
                Waypoint(2, "Riley", 38, 58, null, null, 4),
                Waypoint(3, "Geo", 60, 50, "Old gas station", null, 2),
                Waypoint(4, "Clay", 78, 68, "RUAF roadblock", null, 1),
            ],
            Pings = [new(5, "Sam", mapId, pingX, 0, pingZ, null, now.AddSeconds(-12))],
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
