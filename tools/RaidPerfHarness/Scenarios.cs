using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels.V2.Plan;

namespace TarkovCompanion.RaidPerfHarness;

internal static class Scenarios
{
    /// <summary>Opens the Raid workspace and times it to the first frame that has the map in it.</summary>
    /// <remarks>
    /// The clock starts at the navigation, not at the map: the Raid page's view is only built when
    /// it is first shown, and a player waits for that as much as for the artwork. A second map is
    /// then opened, which is the case where the artwork really has to be found and drawn.
    /// </remarks>
    public static (Dictionary<string, object> Result, RaidScript? Script) FirstMapDraw(HarnessHost host, string? mapId, int groupSize)
    {
        var result = new Dictionary<string, object>();
        var allocated = GC.GetTotalAllocatedBytes(false);
        var watch = Stopwatch.StartNew();
        var navigated = host.Shell.Router.NavigateToAddress("raid");
        if (!navigated.Succeeded)
        {
            throw new InvalidOperationException($"The shell refused 'raid': {navigated.Failure}");
        }

        if (!HarnessHost.RunUntil(() => host.Raid.MapPicker.Count > 0, TimeSpan.FromSeconds(60)))
        {
            result["error"] = "The map picker stayed empty.";
            return (result, null);
        }

        var picked = (mapId is null ? null : host.Raid.MapPicker.FirstOrDefault(item => string.Equals(item.MapId, mapId, StringComparison.OrdinalIgnoreCase)))
            ?? host.Raid.MapPicker.FirstOrDefault(item => item.MapId == "customs")
            ?? host.Raid.MapPicker[0];
        result["map"] = picked.MapId;
        var pickerMs = watch.Elapsed.TotalMilliseconds;
        picked.SelectCommand.Execute(null);
        var sceneReady = HarnessHost.RunUntil(
            () => string.Equals(host.Raid.Renderer?.Scene.LocationId, picked.MapId, StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(120));
        result["picker-ms"] = Math.Round(pickerMs, 1);
        result["scene-ms"] = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
        result["scene-ready"] = sceneReady;

        // The artwork decodes off the UI thread, so the scene can be up long before the picture is.
        var artwork = HarnessHost.RunUntil(() => host.Raid.Renderer?.HasBackgroundImage == true, TimeSpan.FromSeconds(30));
        result["artwork-ms"] = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
        result["artwork-ready"] = artwork;

        var firstFrame = host.Frame();
        result["first-draw-ms"] = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
        result["first-frame-ms"] = Math.Round(firstFrame, 1);
        result["second-frame-ms"] = Math.Round(host.Frame(), 1);
        result["allocated-mb"] = Math.Round((GC.GetTotalAllocatedBytes(false) - allocated) / 1048576.0, 1);

        // Then a different map: its scene, its artwork and the frame that draws them.
        var other = host.Raid.MapPicker.FirstOrDefault(item => item.MapId != picked.MapId && item.MapId is "woods" or "shoreline" or "interchange" or "reserve")
            ?? host.Raid.MapPicker.FirstOrDefault(item => item.MapId != picked.MapId);
        if (other is not null)
        {
            allocated = GC.GetTotalAllocatedBytes(false);
            var switchWatch = Stopwatch.StartNew();
            other.SelectCommand.Execute(null);
            HarnessHost.RunUntil(
                () => string.Equals(host.Raid.Renderer?.Scene.LocationId, other.MapId, StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(120));
            var sceneMs = switchWatch.Elapsed.TotalMilliseconds;
            HarnessHost.RunUntil(() => host.Raid.Renderer?.HasBackgroundImage == true, TimeSpan.FromSeconds(30));
            var artworkMs = switchWatch.Elapsed.TotalMilliseconds;
            host.Frame();
            result["switch-map"] = other.MapId;
            result["switch-scene-ms"] = Math.Round(sceneMs, 1);
            result["switch-artwork-ms"] = Math.Round(artworkMs, 1);
            result["switch-draw-ms"] = Math.Round(switchWatch.Elapsed.TotalMilliseconds, 1);
            result["switch-allocated-mb"] = Math.Round((GC.GetTotalAllocatedBytes(false) - allocated) / 1048576.0, 1);
            // Back to the first, so the pan and raid numbers are for the map that was asked for.
            picked.SelectCommand.Execute(null);
            HarnessHost.RunUntil(
                () => string.Equals(host.Raid.Renderer?.Scene.LocationId, picked.MapId, StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(120));
            HarnessHost.RunUntil(() => host.Raid.Renderer?.HasBackgroundImage == true, TimeSpan.FromSeconds(30));
            host.Frame();
        }

        // Let a scripted raid put the whole layer set on the plan so later numbers are for a full map.
        var model = host.Main.Map.RenderModel;
        if (model is null)
        {
            return (result, null);
        }

        return (result, new RaidScript(model, groupSize));
    }

    /// <summary>
    /// What differs between the scene before and after an event that should have changed nothing,
    /// object by object and field by field.
    /// </summary>
    public static Dictionary<string, object> SceneDifference(HarnessHost host, RaidDriver driver, string kind)
    {
        var before = host.Raid.Renderer?.Scene;
        driver.MakeDue(kind);
        driver.Step();
        var after = host.Raid.Renderer?.Scene;
        if (before is null || after is null)
        {
            return new() { ["error"] = "no scene" };
        }

        var fields = new Dictionary<string, int>();
        var examples = new Dictionary<string, string>();
        void Note(string field, string id)
        {
            fields[field] = fields.GetValueOrDefault(field) + 1;
            examples.TryAdd(field, id);
        }

        var previous = before.Objects.ToDictionary(item => item.Id.Value, item => item);
        foreach (var current in after.Objects)
        {
            if (!previous.TryGetValue(current.Id.Value, out var old))
            {
                Note("added", current.Id.Value);
                continue;
            }

            if (old.HasSameDisplayAs(current))
            {
                continue;
            }

            if (old.LayerId != current.LayerId) Note("layer", current.Id.Value);
            if (old.Kind != current.Kind) Note("kind", current.Id.Value);
            if (old.Truth != current.Truth) Note("truth", current.Id.Value);
            if (old.Label != current.Label) Note("label", current.Id.Value);
            if (old.Detail != current.Detail) Note("detail", current.Id.Value);
            if (!old.Geometry.HasSamePointsAs(current.Geometry)) Note("geometry", current.Id.Value);
            if (!old.FloorIds.SequenceEqual(current.FloorIds)) Note("floorIds", current.Id.Value);
            if (!Equals(old.Estimate, current.Estimate)) Note("estimate", current.Id.Value);
            if (old.Provenance.Source != current.Provenance.Source) Note("provenance.source", current.Id.Value);
            if (old.Provenance.SourceUpdatedUtc != current.Provenance.SourceUpdatedUtc) Note("provenance.sourceUpdated", current.Id.Value);
            if (old.Provenance.Reference != current.Provenance.Reference) Note("provenance.reference", current.Id.Value);
            if (!Equals(old.Provenance.Confidence, current.Provenance.Confidence)) Note("provenance.confidence", current.Id.Value);
            if (old.Faction != current.Faction) Note("faction", current.Id.Value);
            if (old.OfferState != current.OfferState) Note("offerState", current.Id.Value);
            if (old.HeadingDegrees != current.HeadingDegrees) Note("heading", current.Id.Value);
        }

        return new()
        {
            ["objects-before"] = before.Objects.Count,
            ["objects-after"] = after.Objects.Count,
            ["differing-fields"] = fields,
            ["first-example-id"] = examples,
            ["layers-equal"] = before.Layers.SequenceEqual(after.Layers),
            ["view-layers-equal"] = before.View.Layers.SequenceEqual(after.View.Layers),
            ["assets-equal"] = before.Assets.SequenceEqual(after.Assets),
            ["bounds-equal"] = before.Bounds == after.Bounds,
            ["camera-equal"] = before.View.Camera == after.View.Camera,
            ["floors-equal"] = before.FloorIds.SequenceEqual(after.FloorIds),
        };
    }

    /// <summary>What is actually on the plan, so "a full layer set" is a number and not a claim.</summary>
    public static Dictionary<string, object> DescribeScene(HarnessHost host)
    {
        var renderer = host.Raid.Renderer;
        if (renderer is null)
        {
            return new() { ["renderer"] = "none" };
        }

        return new()
        {
            ["layers"] = renderer.Layers.Count,
            ["scene-objects"] = renderer.Scene.Objects.Count,
            ["spatial-objects"] = renderer.SpatialObjects.Count,
            ["geometry-objects"] = renderer.GeometryObjects.Count,
            ["label-objects"] = renderer.LabelObjects.Count,
            ["layer-ids"] = string.Join(",", renderer.Scene.Layers.Select(layer => layer.Id.Value)),
        };
    }

    /// <summary>
    /// Drags and zooms the plan with real pointer events and draws a frame after each one.
    /// </summary>
    /// <remarks>
    /// Real events through the real view, not calls on the view model, so anything the view does
    /// per move (hit-testing, layout, a rebuild the pointer handler should not trigger) is in the
    /// number. <paramref name="whileRaidRuns"/> keeps delivering the raid's own events at the
    /// same time, which is what actually happens: a squad's exchange lands while a hand is on
    /// the mouse.
    /// </remarks>
    public static Dictionary<string, object> PanZoom(HarnessHost host, RaidDriver? whileRaidRuns, int dragSteps, int wheelNotches)
    {
        var viewport = host.Window.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == "PlanViewport");
        if (viewport is null || host.Raid.Renderer is null)
        {
            return new() { ["error"] = "No plan viewport is on screen." };
        }

        var origin = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), host.Window)
            ?? throw new InvalidOperationException("The plan viewport is not in the window.");
        for (var warm = 0; warm < 5; warm++)
        {
            host.Frame();
        }

        var rebuilds = host.Raid.Renderer is null ? 0 : whileRaidRuns?.Rebuilds ?? 0;
        var rebuildsCounter = 0;
        void Count(object? sender, EventArgs args) => rebuildsCounter++;
        host.Raid.SceneRebuilt += Count;
        var allocated = GC.GetTotalAllocatedBytes(false);

        var pan = new Samples();
        var zoomIn = new Samples();
        var zoomOut = new Samples();
        var idle = new Samples();
        for (var index = 0; index < 20; index++)
        {
            idle.Add(host.Frame());
        }

        host.Window.MouseDown(origin, MouseButton.Left);
        for (var step = 1; step <= dragSteps; step++)
        {
            whileRaidRuns?.Step(settle: false);
            var point = new Point(origin.X + (Math.Sin(step / 15.0) * 220), origin.Y + (Math.Cos(step / 21.0) * 130));
            var watch = Stopwatch.StartNew();
            host.Window.MouseMove(point);
            host.Frame();
            pan.Add(watch.Elapsed.TotalMilliseconds);
        }

        host.Window.MouseUp(origin, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        for (var notch = 0; notch < wheelNotches; notch++)
        {
            whileRaidRuns?.Step(settle: false);
            var watch = Stopwatch.StartNew();
            host.Window.MouseWheel(origin, new Vector(0, 1));
            host.Frame();
            zoomIn.Add(watch.Elapsed.TotalMilliseconds);
        }

        for (var notch = 0; notch < wheelNotches; notch++)
        {
            whileRaidRuns?.Step(settle: false);
            var watch = Stopwatch.StartNew();
            host.Window.MouseWheel(origin, new Vector(0, -1));
            host.Frame();
            zoomOut.Add(watch.Elapsed.TotalMilliseconds);
        }

        host.Raid.SceneRebuilt -= Count;
        var all = new Samples();
        foreach (var samples in new[] { pan, zoomIn, zoomOut })
        {
            all.Add(samples.Mean);
        }

        var frames = pan.Count + zoomIn.Count + zoomOut.Count;
        return new()
        {
            ["frame-floor-ms"] = idle.Summary("-ms"),
            ["pan-ms"] = pan.Summary("-ms"),
            ["zoom-in-ms"] = zoomIn.Summary("-ms"),
            ["zoom-out-ms"] = zoomOut.Summary("-ms"),
            ["frames-over-16.7ms"] = pan.CountAbove(16.7) + zoomIn.CountAbove(16.7) + zoomOut.CountAbove(16.7),
            ["frames-over-33ms"] = pan.CountAbove(33) + zoomIn.CountAbove(33) + zoomOut.CountAbove(33),
            ["frames"] = frames,
            ["scene-rebuilds-during"] = rebuildsCounter,
            ["allocated-kb-per-frame"] = Math.Round((GC.GetTotalAllocatedBytes(false) - allocated) / 1024.0 / Math.Max(1, frames), 1),
            ["rebuilds-attributed-to-raid-events"] = whileRaidRuns is null ? 0 : whileRaidRuns.Rebuilds - rebuilds,
        };
    }

    /// <summary>
    /// Idle allocation in three settings, to say whether the harness, the raid page or the shell
    /// is what allocates while nothing is happening.
    /// </summary>
    public static Dictionary<string, object> IdleBisect(HarnessHost host, Tally tally, AllocationSampler? sampler)
    {
        var result = new Dictionary<string, object>();
        Dictionary<string, object> Measure(string label, bool frames)
        {
            sampler?.Reset();
            sampler?.Start();
            var before = tally.Snapshot();
            var allocated = GC.GetTotalAllocatedBytes(false);
            var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var watch = Stopwatch.StartNew();
            var nextFrame = TimeSpan.FromSeconds(1);
            while (watch.Elapsed < TimeSpan.FromSeconds(6))
            {
                Dispatcher.UIThread.RunJobs();
                if (frames && watch.Elapsed >= nextFrame)
                {
                    nextFrame += TimeSpan.FromSeconds(1);
                    host.Frame();
                }

                Thread.Sleep(4);
            }

            sampler?.Stop();
            process.Refresh();
            return new()
            {
                ["allocated-kb-per-second"] = Math.Round((GC.GetTotalAllocatedBytes(false) - allocated) / 1024.0 / watch.Elapsed.TotalSeconds, 1),
                ["cpu-percent-of-one-core"] = Math.Round((process.TotalProcessorTime - cpu).TotalMilliseconds / watch.Elapsed.TotalMilliseconds * 100, 1),
                ["fired"] = Tally.Since(before, tally.Snapshot()),
                ["top-allocated-types-mb"] = sampler?.Top(5) ?? [],
            };
        }

        result["raid-page-no-frames"] = Measure("raid-page-no-frames", false);
        result["raid-page-frame-per-second"] = Measure("raid-page-frame-per-second", true);
        host.Shell.Router.NavigateToAddress("intel");
        HarnessHost.RunUntil(() => false, TimeSpan.FromMilliseconds(500));
        result["intel-page-no-frames"] = Measure("intel-page-no-frames", false);
        host.Shell.Router.NavigateToAddress("raid");
        HarnessHost.RunUntil(() => false, TimeSpan.FromMilliseconds(500));
        return result;
    }

    /// <summary>
    /// A raid, start to finish, sampling the process once a raid-minute.
    /// </summary>
    public static Dictionary<string, object> Raid(HarnessHost host, RaidDriver driver, Tally tally, AllocationSampler? sampler, int minutes, double accelerate)
    {
        var process = Process.GetCurrentProcess();
        driver.Begin();
        // A quiet stretch first, so the cost of merely being open is its own number.
        var idleAllocated = GC.GetTotalAllocatedBytes(false);
        var idleRebuilds = driver.Rebuilds;
        var tallyBefore = tally.Snapshot();
        var idleCpu = process.TotalProcessorTime;
        var idleWatch = Stopwatch.StartNew();
        sampler?.Reset();
        sampler?.Start();
        var idleFrames = new Samples();
        var nextIdleFrame = TimeSpan.FromSeconds(1);
        while (idleWatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            Dispatcher.UIThread.RunJobs();
            if (idleWatch.Elapsed >= nextIdleFrame)
            {
                // The raid timer's label changes once a second, so a frame is drawn once a second.
                nextIdleFrame += TimeSpan.FromSeconds(1);
                idleFrames.Add(host.Frame());
            }

            Thread.Sleep(4);
        }

        sampler?.Stop();
        process.Refresh();
        var idleFired = Tally.Since(tallyBefore, tally.Snapshot());
        var idle = new Dictionary<string, object>
        {
            ["fired-in-ten-seconds"] = idleFired,
            ["scene-rebuilds"] = driver.Rebuilds - idleRebuilds,
            ["cpu-percent"] = Math.Round((process.TotalProcessorTime - idleCpu).TotalMilliseconds / idleWatch.Elapsed.TotalMilliseconds * 100 / Environment.ProcessorCount * Environment.ProcessorCount, 2),
            ["allocated-kb-per-second"] = Math.Round((GC.GetTotalAllocatedBytes(false) - idleAllocated) / 1024.0 / idleWatch.Elapsed.TotalSeconds, 1),
            ["frame-ms"] = idleFrames.Summary("-ms"),
            ["top-allocated-types-mb"] = sampler?.Top(14) ?? [],
        };

        var timeline = new List<Dictionary<string, object>>();
        var startCpu = process.TotalProcessorTime;
        var startAllocated = GC.GetTotalAllocatedBytes(false);
        var wall = Stopwatch.StartNew();
        var total = TimeSpan.FromSeconds(minutes * 60 / accelerate);
        var nextSample = TimeSpan.FromSeconds(60 / accelerate);
        var minute = 0;
        var lastCpu = startCpu;
        var lastWall = TimeSpan.Zero;
        var frame = new Samples();
        var renderTicks = Stopwatch.StartNew();
        while (wall.Elapsed < total)
        {
            driver.Step();
            Dispatcher.UIThread.RunJobs();
            if (renderTicks.ElapsedMilliseconds >= 1000)
            {
                // The raid timer's label: one changed frame a second, whatever else is quiet.
                renderTicks.Restart();
                frame.Add(host.Frame());
            }

            Thread.Sleep(4);
            if (wall.Elapsed < nextSample)
            {
                continue;
            }

            nextSample += TimeSpan.FromSeconds(60 / accelerate);
            minute++;
            process.Refresh();
            var cpu = process.TotalProcessorTime;
            var sliceCpu = (cpu - lastCpu).TotalMilliseconds;
            var sliceWall = (wall.Elapsed - lastWall).TotalMilliseconds;
            lastCpu = cpu;
            lastWall = wall.Elapsed;
            var heap = GC.GetTotalMemory(forceFullCollection: true);
            timeline.Add(new()
            {
                ["minute"] = minute,
                ["managed-heap-mb"] = Math.Round(heap / 1048576.0, 2),
                ["working-set-mb"] = Math.Round(process.WorkingSet64 / 1048576.0, 1),
                ["cpu-percent-of-one-core"] = Math.Round(sliceCpu / sliceWall * 100, 2),
                ["scene-rebuilds-total"] = driver.Rebuilds,
                ["gen2-collections"] = GC.CollectionCount(2),
                ["threads"] = process.Threads.Count,
            });
        }

        process.Refresh();
        var elapsed = wall.Elapsed.TotalSeconds;
        return new()
        {
            ["idle-ten-seconds"] = idle,
            ["cpu-percent-of-one-core-mean"] = Math.Round((process.TotalProcessorTime - startCpu).TotalSeconds / elapsed * 100, 2),
            ["allocated-mb-per-raid-minute"] = Math.Round((GC.GetTotalAllocatedBytes(false) - startAllocated) / 1048576.0 / (elapsed * accelerate / 60), 2),
            ["clock-frame-ms"] = frame.Summary("-ms"),
            ["exchange"] = Describe(driver.Exchange),
            ["screenshot"] = Describe(driver.Screenshot),
            ["log"] = Describe(driver.Log),
            ["scene-rebuilds-total"] = driver.Rebuilds,
            ["timeline"] = timeline,
            ["heap-growth-mb-per-raid-minute"] = Math.Round(Slope(timeline), 4),
        };
    }

    /// <summary>
    /// What typing in the Plan workspace's quest search costs: one keystroke, and a whole query
    /// typed at speed, over whatever quest catalog the run was seeded with.
    /// </summary>
    /// <remarks>
    /// The filter is set to "All" first, because that is the case the cost is in: the search reads
    /// every quest the filter keeps, and a board with four active quests hides the problem.
    ///
    /// Two models of typing, because they cost differently. "Slow" sets one character and lets the
    /// dispatcher settle before the next, which is the cost of one keystroke. "Burst" sets every
    /// character with no turn of the dispatcher in between, which is what a typist does and what a
    /// coalescing filter is for: the keystrokes are handled first and the filter runs once, at the
    /// end. The number a player feels is burst's typing-ms — how long the box itself made them wait.
    /// </remarks>
    public static Dictionary<string, object> PlanSearch(HarnessHost host, string query)
    {
        var plan = host.Services.GetRequiredService<PlanWorkspaceViewModel>();
        HarnessHost.DrainUntilComplete(plan.LoadAsync());
        plan.Filter = PlanQuestFilter.All;
        Quiesce();
        var result = new Dictionary<string, object>
        {
            ["query"] = query,
            ["filter"] = plan.Filter.ToString(),
            ["quests-on-board"] = plan.Groups.SelectMany(group => group.Objectives).Select(row => row.TaskName).Distinct().Count(),
            ["objectives-on-board"] = plan.Groups.Sum(group => group.Objectives.Count),
            ["groups-on-board"] = plan.Groups.Count,
        };

        // Whether the filter runs on the keystroke's own stack. The box must not wait for it: what
        // the typist feels is this, not what the filter costs once it runs.
        plan.SearchText = string.Empty;
        Quiesce();
        var unfiltered = plan.Groups.Sum(group => group.Objectives.Count);
        plan.SearchText = query;
        result["filter-ran-inline"] = plan.Groups.Sum(group => group.Objectives.Count) != unfiltered;
        Quiesce();

        // Slow typing: the cost of one keystroke, start to settled, sampled over the whole query
        // twice (the first pass pays for the JIT and for the first filtered result set).
        var setter = new Samples();
        var settle = new Samples();
        var bytes = new Samples();
        for (var pass = 0; pass < 2; pass++)
        {
            plan.SearchText = string.Empty;
            Quiesce();
            for (var length = 1; length <= query.Length; length++)
            {
                var allocated = GC.GetTotalAllocatedBytes(false);
                var watch = Stopwatch.StartNew();
                plan.SearchText = query[..length];
                var typed = watch.Elapsed.TotalMilliseconds;
                Dispatcher.UIThread.RunJobs();
                if (pass == 0)
                {
                    continue;
                }

                setter.Add(typed);
                settle.Add(watch.Elapsed.TotalMilliseconds);
                bytes.Add(GC.GetTotalAllocatedBytes(false) - allocated);
            }
        }

        result["keystroke-setter-ms"] = setter.Summary("-ms");
        result["keystroke-settled-ms"] = settle.Summary("-ms");
        result["keystroke-allocated-kb"] = Math.Round(bytes.Mean / 1024, 1);

        // A burst: every character, then one settle. Quiesced first, so the reads the reset set off
        // land before the clock starts rather than inside the burst.
        plan.SearchText = string.Empty;
        Quiesce();
        var burstAllocated = GC.GetTotalAllocatedBytes(false);
        var burst = Stopwatch.StartNew();
        for (var length = 1; length <= query.Length; length++)
        {
            plan.SearchText = query[..length];
        }

        var typing = burst.Elapsed.TotalMilliseconds;
        Dispatcher.UIThread.RunJobs();
        result["burst-typing-ms"] = Math.Round(typing, 3);
        result["burst-settled-ms"] = Math.Round(burst.Elapsed.TotalMilliseconds, 3);
        result["burst-allocated-kb"] = Math.Round((GC.GetTotalAllocatedBytes(false) - burstAllocated) / 1024.0, 1);
        result["burst-results"] = plan.Groups.Sum(group => group.Objectives.Count);

        // What one more keystroke rebuilt, over a query whose result set it does not change.
        plan.SearchText = query[..^1];
        Quiesce();
        var listBefore = plan.Groups;
        var groupsBefore = plan.Groups.ToArray();
        var rowsBefore = groupsBefore.SelectMany(group => group.Objectives).ToArray();
        plan.SearchText = query;
        Dispatcher.UIThread.RunJobs();
        var rowsAfter = plan.Groups.SelectMany(group => group.Objectives).ToArray();
        result["last-keystroke"] = new Dictionary<string, object>
        {
            ["rows-before"] = rowsBefore.Length,
            ["rows-after"] = rowsAfter.Length,
            ["rows-kept"] = rowsAfter.Count(row => rowsBefore.Any(other => ReferenceEquals(row, other))),
            ["groups-kept"] = plan.Groups.Count(group => groupsBefore.Any(other => ReferenceEquals(group, other))),
            ["group-list-kept"] = ReferenceEquals(plan.Groups, listBefore),
        };
        return result;
    }

    /// <summary>
    /// Runs the dispatcher until the work already in flight has finished landing, so one
    /// measurement is not charged for what the one before it set off.
    /// </summary>
    private static void Quiesce()
    {
        for (var pass = 0; pass < 25; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
    }

    private static Dictionary<string, object> Describe(EventProbe probe) => new()
    {
        ["events"] = probe.AllocatedBytes.Count,
        ["allocated-kb"] = probe.AllocatedBytes.Count == 0
            ? new Dictionary<string, double>()
            : new Dictionary<string, double>
            {
                ["mean"] = Math.Round(probe.AllocatedBytes.Mean / 1024, 1),
                ["p95"] = Math.Round(probe.AllocatedBytes.Percentile(0.95) / 1024, 1),
                ["max"] = Math.Round(probe.AllocatedBytes.Max / 1024, 1),
            },
        ["publish-ms"] = probe.PublishMs.Summary("-ms"),
        ["ui-ms"] = probe.UiMs.Summary("-ms"),
        ["new-plan-view-models-per-event"] = Math.Round(probe.NewViewModels.Mean, 1),
        ["ui-jobs-ms-mean"] = Math.Round(probe.JobsMs.Mean, 1),
        ["ui-frame-ms-mean"] = Math.Round(probe.FrameMs.Mean, 1),
        ["scene-rebuilds-per-event"] = Math.Round(probe.Rebuilds.Mean, 2),
    };

    /// <summary>Least-squares slope of the managed heap against raid-minutes, skipping the warm-up.</summary>
    private static double Slope(List<Dictionary<string, object>> timeline)
    {
        var points = timeline
            .Where(row => (int)row["minute"] >= Math.Min(5, Math.Max(1, timeline.Count / 4)))
            .Select(row => (X: (double)(int)row["minute"], Y: (double)row["managed-heap-mb"]))
            .ToArray();
        if (points.Length < 3)
        {
            return 0;
        }

        var meanX = points.Average(point => point.X);
        var meanY = points.Average(point => point.Y);
        var denominator = points.Sum(point => Math.Pow(point.X - meanX, 2));
        return denominator == 0 ? 0 : points.Sum(point => (point.X - meanX) * (point.Y - meanY)) / denominator;
    }
}
