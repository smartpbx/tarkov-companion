using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// <c>--stall-tour</c>: every V2 route and the actions a player takes on them, in one process, with
/// the longest interface-thread turn of each. <c>--memory-tour N</c>: N map switches, with managed
/// and native memory after a forced collection every ten.
/// </summary>
/// <remarks>
/// [#453/#454] "It locks up" is a claim about the whole session, not one page: route after route,
/// map after map, for hours. A render of one route measures that route's first visit only, so this
/// walks the app the way a player does and prints one table row per step. Use with
/// <c>--ui-stalls 100</c>, the seed database, <c>--seed-active-quests 10</c> and <c>--raid-demo</c>.
/// Headless and CPU-rasterised: compare two runs made on the same box, not with a player's screen.
/// </remarks>
internal static class StallTour
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly string[] TourMaps = ["customs", "reserve", "streets-of-tarkov", "lighthouse"];

    public static void Run(Window window, MainWindowViewModel viewModel, V2ShellViewModel shell, IServiceProvider services, IReadOnlyList<string> args)
    {
        var raid = shell.RaidCockpit as RaidCockpitViewModel;
        // --stall-tour-only plan,intel: walk only those parts (maps, intel, plan, team, debrief,
        // setup, cycle, mapcycle), for a quicker loop on one slow step.
        var only = StringOption(args, "--stall-tour-only")?.Split(',');
        bool Wants(string part) => only is null || only.Contains(part, StringComparer.OrdinalIgnoreCase);
        UiStallMeter.Report("before the tour");

        Step("navigate raid", () => Navigate(shell, "raid"));
        if (raid is not null && Wants("maps"))
        {
            foreach (var map in TourMaps.Skip(1).Append("customs"))
            {
                Step($"map switch to {map} (photo)", () => SelectMap(raid, map), () => WaitForMap(viewModel, raid, map));
            }

            foreach (var map in TourMaps)
            {
                Step($"map switch to {map}", () => SelectMap(raid, map), () => WaitForMap(viewModel, raid, map));
                if (raid.HasArtworkChoice)
                {
                    Step($"drawing on {map}", () => raid.ToggleArtworkCommand.Execute(null), () => WaitForMap(viewModel, raid, map));
                    Step($"photo on {map}", () => raid.ToggleArtworkCommand.Execute(null), () => WaitForMap(viewModel, raid, map));
                }
            }

            if (raid.Renderer is { } renderer)
            {
                Console.WriteLine("Layers: " + string.Join(", ", renderer.Layers.Select(layer => layer.Layer.Id.Value + (layer.IsVisible ? "*" : string.Empty))));
                foreach (var layer in renderer.Layers.Where(layer => layer.Layer.Id.Value.Contains("traffic", StringComparison.OrdinalIgnoreCase) && !layer.IsVisible))
                {
                    Step($"layer {layer.Layer.Id.Value} on", () => layer.ToggleCommand.Execute(null));
                }
            }
        }

        if (Wants("intel"))
        {
        Step("navigate raid/loot", () => Navigate(shell, "raid/loot"));
        Step("navigate intel", () => Navigate(shell, "intel"));
        Step("intel: type 'graphics card'", () => { }, () => Type(text => shell.SearchText = text, "graphics card"));
        foreach (var route in new[] { "intel/ammo", "intel/keys", "intel/flea", "intel/stash", "intel/crafts" })
        {
            Step($"navigate {route}", () => Navigate(shell, route));
        }
        }

        var plan = services.GetRequiredService<PlanWorkspaceViewModel>();
        if (Wants("plan"))
        {
        Step("navigate plan", () => Navigate(shell, "plan"));
        foreach (var filter in Enum.GetValues<PlanQuestFilter>().Append(PlanQuestFilter.Active))
        {
            Step($"plan filter {filter}", () => plan.Filter = filter);
        }

        plan.Filter = Enum.GetValues<PlanQuestFilter>().Last();
        Pump();
        Step($"plan ({plan.Filter}): type 'graphics card'", () => { }, () => Type(text => plan.SearchText = text, "graphics card"));
        plan.SearchText = string.Empty;
        Pump();
        foreach (var route in new[] { "plan/hideout", "plan/keep", "plan/loadout", "plan/events" })
        {
            Step($"navigate {route}", () => Navigate(shell, route));
        }

        }

        foreach (var route in new[] { "team", "team/group", "team/tablet" }.Where(_ => Wants("team")))
        {
            Step($"navigate {route}", () => Navigate(shell, route));
        }

        var raids = Wants("debrief") ? IntOption(args, "--stall-tour-raids", 80) : 0;
        DrainUntilComplete(SeedRaidsAsync(services, raids));
        UiStallMeter.Report("seeding raids (not a player action)");
        if (Wants("debrief"))
        {
        Step($"navigate debrief ({raids} raids)", () => Navigate(shell, "debrief"));
        var debrief = services.GetRequiredService<TarkovCompanion.App.ViewModels.V2.Debrief.DebriefWorkspaceViewModel>();
        Step("debrief reload", () => _ = debrief.LoadAsync());
        if (debrief.Raids.Count > 5)
        {
            var pick = debrief.Raids[5].RaidId;
            Step("debrief select a raid", () => _ = debrief.SelectRaidAsync(pick, CancellationToken.None));
        }
        }

        if (Wants("setup"))
        {
        Step("navigate setup", () => Navigate(shell, "setup"));
        }

        if (Wants("setup") && shell.SetupWorkspace is { } setup)
        {
            foreach (var section in Enum.GetValues<V2SetupSection>())
            {
                Step($"setup {section}", () => setup.Select(section));
            }
        }

        var cycle = new[] { "raid", "plan", "intel", "debrief", "team", "setup", "plan/keep", "plan/hideout" };
        for (var round = 1; round <= (Wants("cycle") ? 3 : 0); round++)
        {
            foreach (var route in cycle)
            {
                Step($"route cycle {round}: {route}", () => Navigate(shell, route));
            }
        }

        if (raid is not null && Wants("mapcycle"))
        {
            Navigate(shell, "raid");
            Pump();
            for (var round = 1; round <= 2; round++)
            {
                foreach (var map in TourMaps.Append("factory"))
                {
                    Step($"map cycle {round}: {map}", () => SelectMap(raid, map), () => WaitForMap(viewModel, raid, map));
                }
            }
        }

        UiStallMeter.PrintSummary();
    }

    /// <summary>
    /// Switches maps <paramref name="switches"/> times over five maps with the photo artwork, and
    /// prints managed and process memory after a full collection every ten switches.
    /// </summary>
    public static void RunMemory(MainWindowViewModel viewModel, V2ShellViewModel shell, int switches)
    {
        if (shell.RaidCockpit is not RaidCockpitViewModel raid)
        {
            Console.Error.WriteLine("--memory-tour needs the Raid cockpit.");
            return;
        }

        Navigate(shell, "raid");
        Pump();
        string[] maps = ["customs", "reserve", "streets-of-tarkov", "lighthouse", "woods"];
        Memory(0);
        for (var i = 1; i <= switches; i++)
        {
            var map = maps[i % maps.Length];
            SelectMap(raid, map);
            WaitForMap(viewModel, raid, map);
            if (raid.PrefersDrawing)
            {
                raid.ToggleArtworkCommand.Execute(null);
                WaitForMap(viewModel, raid, map);
            }

            if (i % 10 == 0)
            {
                Memory(i);
            }
        }
    }

    /// <summary>
    /// A raid in real time: the squad publishes every <c>2 s</c> with each mate a few metres on, and
    /// the player takes a screenshot every <paramref name="screenshotSeconds"/>, for
    /// <paramref name="seconds"/>. Stalls are reported every 30 s.
    /// </summary>
    /// <remarks>
    /// [#453] Clayton's build 2.0.1353 froze for 5-237 s at a time, only on the Raid route, only in
    /// raid, with a squad sharing positions: what runs there is what the relay and the screenshots
    /// publish, so that is what this publishes, through the same runtime store.
    /// </remarks>
    public static void RunRaidSoak(IServiceProvider services, MainWindowViewModel viewModel, V2ShellViewModel shell, int seconds, int screenshotSeconds, int groupMilliseconds)
    {
        var store = services.GetRequiredService<TarkovCompanion.Application.Services.Runtime.IRuntimeStateStore>();
        // The composition's own group session republishes "not sharing" every five seconds in a
        // demo run, which would wipe the squad this publishes (the raid harness does the same).
        DrainUntilComplete(services.GetRequiredService<TarkovCompanion.Application.Services.Group.GroupSessionService>().DisposeAsync().AsTask());
        var squad = Program.RaidDemo(viewModel.Map.RenderModel).Group;
        Navigate(shell, "raid");
        Pump();
        UiStallMeter.Report("soak: before");
        var clock = Stopwatch.StartNew();
        var nextGroup = TimeSpan.Zero;
        var nextShot = TimeSpan.FromSeconds(screenshotSeconds);
        var nextReport = TimeSpan.FromSeconds(30);
        var tick = 0;
        var shots = 0;
        long? probePosted = null;
        double probeLongest = 0;
        var probeOver100 = 0;
        var probeCount = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (clock.Elapsed >= nextGroup)
            {
                nextGroup += TimeSpan.FromMilliseconds(groupMilliseconds);
                tick++;
                var step = tick;
                store.Update(snapshot => snapshot with
                {
                    Group = squad with
                    {
                        UpdatedUtc = DateTimeOffset.UtcNow,
                        Members = [.. squad.Members.Select((member, index) => member.Position is { } at
                            ? member with
                            {
                                Position = new(at.X + Wobble(step, index), at.Y, at.Z + Wobble(step + 7, index)),
                                HeadingDegrees = (step * 13 + index * 90) % 360,
                                PositionAge = TimeSpan.FromSeconds(1),
                            }
                            : member)],
                    },
                });
            }

            if (clock.Elapsed >= nextShot)
            {
                nextShot += TimeSpan.FromSeconds(screenshotSeconds);
                shots++;
                var shot = shots;
                store.Update(snapshot =>
                {
                    if (snapshot.Raid.LastKnownPosition is not { } last)
                    {
                        return snapshot;
                    }

                    var now = DateTimeOffset.UtcNow;
                    var next = last with
                    {
                        Timestamp = now,
                        Position = new(last.Position.X + 12 * Math.Cos(shot), last.Position.Y, last.Position.Z + 12 * Math.Sin(shot)),
                        Filename = $"soak-{shot}.png",
                    };
                    return snapshot with
                    {
                        Raid = snapshot.Raid with
                        {
                            UpdatedUtc = now,
                            LastKnownPosition = next,
                            PositionTrail = [.. snapshot.Raid.PositionTrail, next],
                        },
                    };
                });
            }

            // What the hang watchdog measures: how long a job posted at input priority waits.
            if (probePosted is null)
            {
                var posted = Stopwatch.GetTimestamp();
                probePosted = posted;
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        var waited = Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
                        probeLongest = Math.Max(probeLongest, waited);
                        probeOver100 += waited > 100 ? 1 : 0;
                        probeCount++;
                        probePosted = null;
                    },
                    DispatcherPriority.Input);
            }

            Turn();
            Thread.Sleep(10);
            if (clock.Elapsed >= nextReport)
            {
                nextReport += TimeSpan.FromSeconds(30);
                Console.WriteLine(string.Create(Invariant, $"input probe: {probeCount} answered, longest wait {probeLongest:0} ms, {probeOver100} waited over 100 ms"));
                (probeLongest, probeOver100, probeCount) = (0, 0, 0);
                UiStallMeter.Report(string.Create(Invariant, $"soak {clock.Elapsed.TotalSeconds:0} s ({tick} group ticks, {shots} screenshots)"));
            }
        }

        UiStallMeter.Report("soak: end");
        UiStallMeter.PrintSummary();
    }

    private static double Wobble(int step, int index) => 3 * Math.Sin((step * 0.37) + index);

    private static void Memory(int switches)
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        Pump();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        Console.WriteLine(string.Create(
            Invariant,
            $"[memory] after {switches,3} switches: managed {GC.GetTotalMemory(false) / 1048576.0,7:F1} MB, working set {process.WorkingSet64 / 1048576.0,7:F1} MB, private {process.PrivateMemorySize64 / 1048576.0,7:F1} MB, skia cache {SkiaSharp.SKGraphics.GetResourceCacheTotalBytesUsed() / 1048576.0,6:F1} MB"));
    }

    private static void Step(string name, Action act, Action? settle = null)
    {
        UiStallMeter.Time(act);
        if (settle is not null)
        {
            settle();
        }

        Pump();
        UiStallMeter.Report(name);
    }

    private static void Navigate(V2ShellViewModel shell, string route)
    {
        var result = shell.Router.NavigateToAddress(route);
        if (!result.Succeeded)
        {
            Console.Error.WriteLine($"The shell refused '{route}': {result.Failure}");
        }
    }

    private static void SelectMap(RaidCockpitViewModel raid, string map)
    {
        if (raid.MapPicker.FirstOrDefault(item => string.Equals(item.MapId, map, StringComparison.OrdinalIgnoreCase)) is { } item)
        {
            item.SelectCommand.Execute(null);
        }
        else
        {
            Console.Error.WriteLine($"No map '{map}'.");
        }
    }

    /// <summary>Until the map asked for is drawn with its artwork and nothing is loading, or 60 s.</summary>
    private static void WaitForMap(MainWindowViewModel viewModel, RaidCockpitViewModel raid, string map)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            Turn();
            if (raid.Renderer is { BackgroundImage: not null } renderer &&
                string.Equals(renderer.Scene.LocationId, map, StringComparison.OrdinalIgnoreCase) &&
                !viewModel.Map.Status.StartsWith("Loading", StringComparison.Ordinal))
            {
                return;
            }

            Thread.Sleep(10);
        }

        Console.Error.WriteLine($"'{map}' was not drawn within 60 s ('{viewModel.Map.Status}').");
    }

    /// <summary>A player typing at about eight characters a second.</summary>
    private static void Type(Action<string> set, string text)
    {
        for (var i = 1; i <= text.Length; i++)
        {
            var prefix = text[..i];
            UiStallMeter.Time(() => set(prefix));
            var until = Stopwatch.StartNew();
            while (until.ElapsedMilliseconds < 120)
            {
                Turn();
                Thread.Sleep(10);
            }
        }
    }

    private static void Turn()
    {
        UiStallMeter.RunJobs();
        UiStallMeter.Time(() => AvaloniaHeadlessPlatform.ForceRenderTimerTick());
    }

    /// <summary>Until nothing is loading and the dispatcher has been idle for six turns, or 15 s.</summary>
    private static void Pump()
    {
        var clock = Stopwatch.StartNew();
        var quiet = 0;
        while (quiet < 6 && clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            var turn = Stopwatch.GetTimestamp();
            Turn();
            var idle = Stopwatch.GetElapsedTime(turn) < TimeSpan.FromMilliseconds(3)
                && TarkovCompanion.Infrastructure.Persistence.SqliteConnectionFactory.OpenConnectionCount == 0
                && !TarkovCompanion.App.Services.Diagnostics.UiActivity.IsLoading;
            quiet = idle ? quiet + 1 : 0;
            Thread.Sleep(20);
        }
    }

    private static void DrainUntilComplete(Task task)
    {
        while (!task.IsCompleted)
        {
            UiStallMeter.RunJobs();
            Thread.Sleep(5);
        }

        task.GetAwaiter().GetResult();
    }

    /// <summary>Closed raids with a position trail each, written the way the runtime writes them.</summary>
    private static async Task SeedRaidsAsync(IServiceProvider services, int count)
    {
        IRaidHistoryService history = services.GetRequiredService<TarkovCompanion.Infrastructure.Persistence.Repositories.SqliteRaidHistoryService>();
        var profile = await services.GetRequiredService<IPlayerProfileService>().GetActiveAsync(CancellationToken.None).ConfigureAwait(false);
        string[] maps = ["customs", "woods", "factory4_day", "bigmap", "RezervBase", "Shoreline", "TarkovStreets", "Lighthouse"];
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < count; i++)
        {
            var started = now - TimeSpan.FromHours(3 * (i + 1));
            var id = await history.StartAsync(
                new(Guid.NewGuid(), profile.Id, maps[i % maps.Length], "Pmc", started, null, null, null),
                CancellationToken.None).ConfigureAwait(false);
            for (var p = 0; p < 30; p++)
            {
                var at = started + TimeSpan.FromMinutes(p);
                await history.RecordEventAsync(
                    id,
                    "position",
                    at,
                    string.Create(Invariant, $"{{\"x\":{p * 3},\"y\":0,\"z\":{p * 2},\"timestamp\":\"{at:O}\"}}"),
                    CancellationToken.None).ConfigureAwait(false);
            }

            await history.EndAsync(id, started + TimeSpan.FromMinutes(30), i % 3 == 0 ? "Killed" : "Survived", null, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static string? StringOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static int IntOption(IReadOnlyList<string> args, string name, int fallback)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == name && int.TryParse(args[i + 1], NumberStyles.Integer, Invariant, out var value))
            {
                return value;
            }
        }

        return fallback;
    }
}
