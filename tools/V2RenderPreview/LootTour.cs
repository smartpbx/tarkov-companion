using System.Diagnostics;
using System.Globalization;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.LootSpawns;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// <c>--loot-tour on|off</c>: the Raid route map by map with the high-value loot layer on or off,
/// timing what a player does there: switch map, drag, zoom, and sit idle in a raid.
/// </summary>
/// <remarks>
/// [#657] "The map is slow with the high-value loot turned on." Two runs of this, one with
/// <c>on</c> and one with <c>off</c>, on the same box, are the before-and-after the issue is
/// judged by. The idle phase publishes squad positions every two seconds and a screenshot every
/// twenty, the way a raid does, while an input-priority probe measures what the hang watchdog
/// measures. <c>--loot-tour-maps</c> (default customs,interchange,streets-of-tarkov) and
/// <c>--loot-tour-idle</c> seconds (default 60). Needs <c>--seed-loot-cache</c> with a real
/// publication, <c>--raid-demo</c> and <c>--ui-stalls 100</c>.
/// </remarks>
internal static class LootTour
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly List<string> Rows = [];
    private static InputProbe? _probe;
    private static TimeSpan _cpuAtStart;

    public static void Run(IServiceProvider services, MainWindowViewModel viewModel, V2ShellViewModel shell, bool lootOn, string[] maps, int idleSeconds)
    {
        if (shell.RaidCockpit is not RaidCockpitViewModel raid)
        {
            Console.Error.WriteLine("--loot-tour needs the Raid cockpit.");
            return;
        }

        // The composition's own group session republishes "not sharing" every five seconds in a
        // demo run, which would wipe the squad the idle phase publishes.
        Drain(services.GetRequiredService<TarkovCompanion.Application.Services.Group.GroupSessionService>().DisposeAsync().AsTask());
        shell.Router.NavigateToAddress("raid");
        Pump();
        UiStallMeter.Report("loot tour: before");
        foreach (var map in maps)
        {
            Phase($"{map}: map switch", () => Select(raid, map), () => WaitForMap(viewModel, raid, map));
            Phase($"{map}: loot layer {(lootOn ? "on" : "off")}", () => SetLoot(raid, lootOn));
            Console.WriteLine(string.Create(Invariant, $"[loot] {map}: layer on {LootOn(raid)}, loot markers {raid.Renderer?.LootMarkers.Count}, badges {raid.Renderer?.LootBadges.Count}, spatial {raid.Renderer?.SpatialObjects.Count}"));
            ReportLootFloors(services, raid, map);
            Phase($"{map}: drag x6", () => { }, () => Drag(raid, 6));
            Phase($"{map}: zoom in x4", () => { }, () => Zoom(raid, 1, 4));
            Phase($"{map}: drag zoomed x6", () => { }, () => Drag(raid, 6));
            Phase($"{map}: zoom out x4", () => { }, () => Zoom(raid, -1, 4));
            Idle(services, viewModel, map, idleSeconds);
        }

        Console.WriteLine($"| Loot {(lootOn ? "ON" : "OFF")}: step | UI busy % | process CPU % of one core | UI busy ms | wall ms | longest turn ms | turns over {UiStallMeter.ThresholdMilliseconds:0} ms | input waits > 100 ms |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var row in Rows)
        {
            Console.WriteLine(row);
        }
    }

    private static void Phase(string name, Action act, Action? then = null)
    {
        var probe = _probe = new InputProbe();
        UiStallMeter.Report(name + " (start)");
        _cpuAtStart = Cpu();
        UiStallMeter.Time(act);
        then?.Invoke();
        Pump();
        Record(name, probe);
        _probe = null;
    }

    private static void Record(string name, InputProbe probe)
    {
        var (busy, wall, longest, over) = UiStallMeter.Snapshot();
        var cpu = (Cpu() - _cpuAtStart).TotalMilliseconds;
        UiStallMeter.Report(name);
        Rows.Add(string.Create(Invariant, $"| {name} | {(wall > 0 ? 100 * busy / wall : 0):0.0} | {(wall > 0 ? 100 * cpu / wall : 0):0} | {busy:0} | {wall:0} | {longest:0} | {over} | {probe.Over100} of {probe.Count} (longest {probe.Longest:0} ms) |"));
    }

    /// <summary>All threads' processor time: the whole process, not only the interface thread.</summary>
    private static TimeSpan Cpu()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    private static void SetLoot(RaidCockpitViewModel raid, bool on)
    {
        Console.WriteLine("[loot] layers: " + string.Join(", ", raid.Renderer?.Layers.Select(layer => $"{layer.Layer.Id.Value}={layer.Count}{(layer.IsVisible ? "*" : string.Empty)}") ?? []) +
            $" | loot panel unavailable {raid.Renderer?.HighValueLoot?.IsUnavailable}: {raid.Renderer?.HighValueLoot?.StateMessage}");
        // The gem button ("High-value loot only"): the layer's own switch is disabled while the
        // layer is off, because an off loot layer counts no objects.
        if (on && LootOn(raid) != true)
        {
            raid.Renderer?.HighValueLootPresetCommand.Execute(null);
        }
    }

    private static bool? LootOn(RaidCockpitViewModel raid) =>
        raid.Renderer?.Layers.FirstOrDefault(layer => layer.Layer.Id == HighValueLootLayerService.LayerId)?.IsVisible;

    private static void ReportLootFloors(IServiceProvider services, RaidCockpitViewModel raid, string map)
    {
        if (raid.Renderer is not { HighValueLoot: { } loot } renderer)
        {
            return;
        }

        var before = services.GetRequiredService<IHighValueLootRuntimeSource>().Build(new(
            renderer.Scene.LocationId,
            renderer.Scene.TransformVersion,
            renderer.Scene.Bounds,
            DateTimeOffset.UtcNow,
            loot.FilterState.Filter,
            renderer.Scene.FloorIds,
            OverviewFloorIds: []));
        var beforeObjects = before.Objects.ToArray();
        var afterObjects = renderer.Scene.Objects
            .Where(item => item.LayerId == HighValueLootLayerService.LayerId)
            .ToArray();
        Console.WriteLine("| Loot map | floor | before eligible | before drawn | after eligible | after drawn |");
        Console.WriteLine("| --- | --- | ---: | ---: | ---: | ---: |");
        Console.WriteLine(string.Create(Invariant, $"| {map} | all | {beforeObjects.Length} | {beforeObjects.Length} | {afterObjects.Length} | {afterObjects.Length} |"));
        foreach (var floor in renderer.Scene.FloorIds)
        {
            var beforeDrawn = DrawnOn(beforeObjects, floor);
            var afterDrawn = DrawnOn(afterObjects, floor);
            var label = renderer.Floors.FirstOrDefault(item =>
                string.Equals(item.Id, floor, StringComparison.OrdinalIgnoreCase))?.Name ?? floor;
            Console.WriteLine(string.Create(Invariant, $"| {map} | {label} | {beforeObjects.Length} | {beforeDrawn} | {afterObjects.Length} | {afterDrawn} |"));
        }
    }

    private static int DrawnOn(IReadOnlyList<TarkovCompanion.Core.Domain.Maps.Scene.MapSceneObject> objects, string floor) =>
        objects.Count(item =>
            item.FloorIds.Count == 0 ||
            item.FloorIds.Contains(floor, StringComparer.OrdinalIgnoreCase));

    /// <summary>A drag of <paramref name="drags"/> strokes, each twelve pointer moves at about 60 Hz.</summary>
    private static void Drag(RaidCockpitViewModel raid, int drags)
    {
        for (var d = 0; d < drags; d++)
        {
            var renderer = raid.Renderer!;
            var direction = d % 2 == 0 ? 1 : -1;
            UiStallMeter.Time(renderer.BeginPan);
            for (var i = 1; i <= 12; i++)
            {
                var step = i;
                UiStallMeter.Time(() => renderer.UpdatePan(direction * step * 15, direction * step * 6));
                Turns(16);
            }

            UiStallMeter.Time(renderer.CommitPan);
            Turns(120);
        }
    }

    private static void Zoom(RaidCockpitViewModel raid, int direction, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            // A mouse wheel: about 60 ms between notches, about the middle of the plan.
            UiStallMeter.Time(() => raid.Renderer!.RequestZoomAt(direction, 700, 450));
            Turns(60);
        }
    }

    /// <summary>A raid with nothing pressed: squad every 2 s, a screenshot every 20 s.</summary>
    private static void Idle(IServiceProvider services, MainWindowViewModel viewModel, string map, int seconds)
    {
        if (seconds <= 0)
        {
            return;
        }

        var store = services.GetRequiredService<TarkovCompanion.Application.Services.Runtime.IRuntimeStateStore>();
        var squad = Program.RaidDemo(viewModel.Map.RenderModel).Group;
        var probe = _probe = new InputProbe();
        UiStallMeter.Report($"{map}: idle (start)");
        _cpuAtStart = Cpu();
        var clock = Stopwatch.StartNew();
        var nextGroup = TimeSpan.Zero;
        var nextShot = TimeSpan.FromSeconds(20);
        var tick = 0;
        var shots = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (clock.Elapsed >= nextGroup)
            {
                nextGroup += TimeSpan.FromSeconds(2);
                var step = ++tick;
                store.Update(snapshot => snapshot with
                {
                    Group = squad with
                    {
                        UpdatedUtc = DateTimeOffset.UtcNow,
                        Members = [.. squad.Members.Select((member, index) => member.Position is { } at
                            ? member with
                            {
                                Position = new(at.X + (3 * Math.Sin((step * 0.37) + index)), at.Y, at.Z + (3 * Math.Cos((step * 0.37) + index))),
                                HeadingDegrees = (step * 13 + index * 90) % 360,
                                PositionAge = TimeSpan.FromSeconds(1),
                            }
                            : member)],
                    },
                });
            }

            if (clock.Elapsed >= nextShot)
            {
                nextShot += TimeSpan.FromSeconds(20);
                var shot = ++shots;
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
                        Filename = $"loot-tour-{shot}.png",
                    };
                    return snapshot with { Raid = snapshot.Raid with { UpdatedUtc = now, LastKnownPosition = next, PositionTrail = [.. snapshot.Raid.PositionTrail, next] } };
                });
            }

            Turn();
            Thread.Sleep(10);
        }

        _probe = null;
        Record(string.Create(Invariant, $"{map}: idle {seconds} s ({tick} squad ticks, {shots} screenshots)"), probe);
    }

    private static void Select(RaidCockpitViewModel raid, string map)
    {
        if (raid.MapPicker.FirstOrDefault(item => string.Equals(item.MapId, map, StringComparison.OrdinalIgnoreCase)) is { } item)
        {
            item.SelectCommand.Execute(null);
        }
        else
        {
            Console.Error.WriteLine($"No map '{map}'. Maps: {string.Join(", ", raid.MapPicker.Select(item => item.MapId))}");
        }
    }

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

    private static void Turns(int milliseconds)
    {
        var until = Stopwatch.StartNew();
        do
        {
            Turn();
            Thread.Sleep(4);
        }
        while (until.ElapsedMilliseconds < milliseconds);
    }

    private static void Turn()
    {
        _probe?.Poll();
        UiStallMeter.RunJobs();
        UiStallMeter.Time(() => AvaloniaHeadlessPlatform.ForceRenderTimerTick());
    }

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

    private static void Drain(Task task)
    {
        while (!task.IsCompleted)
        {
            UiStallMeter.RunJobs();
            Thread.Sleep(5);
        }

        task.GetAwaiter().GetResult();
    }

    /// <summary>What the hang watchdog measures: how long a job posted at input priority waits.</summary>
    private sealed class InputProbe
    {
        private long? _posted;

        public int Count { get; private set; }

        public int Over100 { get; private set; }

        public double Longest { get; private set; }

        public void Poll()
        {
            if (_posted is not null)
            {
                return;
            }

            var posted = Stopwatch.GetTimestamp();
            _posted = posted;
            Dispatcher.UIThread.Post(
                () =>
                {
                    var waited = Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
                    Longest = Math.Max(Longest, waited);
                    Over100 += waited > 100 ? 1 : 0;
                    Count++;
                    _posted = null;
                },
                DispatcherPriority.Input);
        }
    }
}
