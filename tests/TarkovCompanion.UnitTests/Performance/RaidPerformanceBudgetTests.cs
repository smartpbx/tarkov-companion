using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.UnitTests.Runtime;
using TarkovCompanion.UnitTests.V2MapRenderer;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.Performance;

/// <summary>
/// What the app costs while a raid is on, held to numbers that were measured and are re-measured
/// by tools/RaidPerfHarness (see docs/PERFORMANCE.md).
/// </summary>
/// <remarks>
/// <para>
/// Allocation budgets are in bytes on one thread and do not move with the machine, so they are set
/// close to what was measured: about twice the measured cost. Time budgets do move with the
/// machine, and a shared CI runner is slower and noisier than the box they were measured on, so they
/// are set at roughly ten times the measured cost — wide enough never to flake, narrow enough that
/// a regression to the old behaviour (which was tens to hundreds of times worse) fails.
/// </para>
/// <para>
/// A budget that fails is a prompt to run the harness and find out why, not to raise the number.
/// </para>
/// </remarks>
public sealed class RaidPerformanceBudgetTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    // Measured 2026-09-18 on the dev box (8 cores, shared with other work), Release, .NET 10, with the
    // figure the old behaviour gave beside each so a regression can be recognised as itself.
    //
    //   unrelated store publication     432 B      (3,816 B before slices kept their instance)
    //   present, rebuilt scene, same    10,104 B   (453,078 B and every marker recreated before)
    //   present, one marker moved       453,062 B  (unchanged: the whole plan is still recreated; see docs/PERFORMANCE.md)
    //   first present, 250 + 120 + 1    1,249,960 B and 3-6 ms
    //   composition + view models       460-560 ms
    private const long UnrelatedPublicationBytes = 1_024;
    private const long UnchangedPresentBytes = 20_000;
    private const long ChangedPresentBytes = 900_000;
    private const long FirstPresentBytes = 2_500_000;
    private const double FirstPresentMilliseconds = 100;
    private const double ColdStartMilliseconds = 6_000;

    [Fact]
    public void Publishing_something_the_plan_does_not_draw_allocates_almost_nothing()
    {
        var store = Store();
        store.Update(current => current with
        {
            Raid = RuntimeStateStoreIdentityTests.Raid(trail: 240),
            Group = RuntimeStateStoreIdentityTests.Group(members: 6),
        });

        var bytes = Measure(500, () => store.Update(current => current with { DatabaseReady = !current.DatabaseReady }));

        output.WriteLine($"unrelated publication: {bytes} B");
        Assert.True(bytes <= UnrelatedPublicationBytes, $"An unrelated publication allocated {bytes} B; the budget is {UnrelatedPublicationBytes} B.");
    }

    [Fact]
    public void Presenting_a_rebuilt_but_unchanged_scene_costs_little_and_recreates_nothing()
    {
        var renderer = MapScenePresentReuseTests.Renderer(MapScenePresentReuseTests.FullScene(1, T0, markers: 150, labels: 60));
        var spatial = renderer.SpatialObjects;
        var scenes = Enumerable.Range(2, 300).Select(index => MapScenePresentReuseTests.FullScene(index, T0.AddSeconds(index), markers: 150, labels: 60)).ToArray();
        var next = 0;

        var bytes = Measure(scenes.Length - 20, () => renderer.Present(scenes[next++]), warmup: 20);

        output.WriteLine($"unchanged present: {bytes} B");
        Assert.Same(spatial, renderer.SpatialObjects);
        Assert.True(bytes <= UnchangedPresentBytes, $"Presenting an unchanged scene allocated {bytes} B; the budget is {UnchangedPresentBytes} B.");
    }

    [Fact]
    public void Presenting_a_scene_where_something_moved_stays_within_its_budget()
    {
        var renderer = MapScenePresentReuseTests.Renderer(MapScenePresentReuseTests.FullScene(1, T0, markers: 150, labels: 60));
        var scenes = Enumerable.Range(2, 120)
            .Select(index => MapScenePresentReuseTests.FullScene(index, T0, movedMarkerX: 10 + (index % 60), markers: 150, labels: 60))
            .ToArray();
        var next = 0;

        var bytes = Measure(scenes.Length - 10, () => renderer.Present(scenes[next++]), warmup: 10);

        output.WriteLine($"changed present: {bytes} B");
        Assert.True(bytes <= ChangedPresentBytes, $"Presenting a changed scene allocated {bytes} B; the budget is {ChangedPresentBytes} B.");
    }

    [Fact]
    public void The_first_present_of_a_full_scene_stays_within_its_budget()
    {
        // The part of "first map draw" that is not the renderer's own drawing: building the
        // projected markers, lines and labels for a map with a full layer set.
        var scene = MapScenePresentReuseTests.FullScene(1, T0, markers: 250, labels: 120);
        MapScenePresentReuseTests.Renderer(MapScenePresentReuseTests.FullScene(1, T0, markers: 20, labels: 5));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();

        var renderer = MapScenePresentReuseTests.Renderer(scene);

        var milliseconds = watch.Elapsed.TotalMilliseconds;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"first present: {bytes} B, {milliseconds:F1} ms");
        Assert.NotEmpty(renderer.SpatialObjects);
        Assert.True(bytes <= FirstPresentBytes, $"The first present allocated {bytes} B; the budget is {FirstPresentBytes} B.");
        Assert.True(milliseconds <= FirstPresentMilliseconds, $"The first present took {milliseconds:F0} ms; the budget is {FirstPresentMilliseconds:F0} ms.");
    }

    [Fact]
    public async Task The_composition_and_its_first_window_view_models_come_up_within_budget()
    {
        // Cold start as far as a process with no rendering platform can measure it: everything
        // between Main and a window that could be shown, with the V2 shell the app is moving to.
        // The window itself, and what it costs to draw, is in the harness's cold-start numbers.
        var watch = Stopwatch.StartNew();
        await using var services = AppComposition.Build(
            AppCommandLine.Parse(["--ui-shell", "v2-a"]) with { Demo = true },
            new(DataRoot: Path.Combine(Path.GetTempPath(), $"tarkov-perf-{Guid.NewGuid():N}"), Offline: true));
        services.GetRequiredService<MainWindowViewModel>();
        services.GetRequiredService<V2ShellViewModel>();
        var milliseconds = watch.Elapsed.TotalMilliseconds;

        output.WriteLine($"composition + view models: {milliseconds:F0} ms");
        Assert.True(milliseconds <= ColdStartMilliseconds, $"Composition took {milliseconds:F0} ms; the budget is {ColdStartMilliseconds:F0} ms.");
    }

    /// <summary>Bytes allocated per call on this thread, after a warm-up that pays for the JIT.</summary>
    private static long Measure(int iterations, Action work, int warmup = 50)
    {
        for (var index = 0; index < warmup; index++)
        {
            work();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
        {
            work();
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
    }

    private static RuntimeStateStore Store() => new(new(
        DemoMode: false,
        Offline: true,
        GameMode.Regular,
        "en",
        TimeSpan.FromHours(1),
        TimeSpan.FromSeconds(5)));
}
