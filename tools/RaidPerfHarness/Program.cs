using System.Diagnostics;
using System.Text.Json;
using Avalonia.Threading;

namespace TarkovCompanion.RaidPerfHarness;

/// <summary>
/// Measures what the companion does over a raid: cold start, first map draw, pan and zoom frame
/// times, and CPU, memory and allocation across a raid with screenshots, logs and a squad.
/// </summary>
/// <remarks>
/// A dev tool, not part of scripts/build.sh or scripts/test.sh, in the same way as the render
/// preview beside it. The numbers are a headless CPU-rasterized run: there is no GPU and no
/// Windows compositor, so an absolute frame time is not what a player sees. What holds is the
/// comparison between two builds on the same box, and the counts (rebuilds, allocations, heap)
/// that do not depend on the renderer. Run it with no arguments for the standard table; see
/// docs/PERFORMANCE.md.
/// </remarks>
internal static class Program
{
    public static int Main(string[] args)
    {
        var scenarios = (StringOption(args, "--scenarios") ?? "cold,map,pan,raid").Split(',');
        var width = IntOption(args, "--width", 1920);
        var height = IntOption(args, "--height", 1080);
        var minutes = IntOption(args, "--raid-minutes", 40);
        var accelerate = DoubleOption(args, "--accelerate", 1);
        var group = IntOption(args, "--group", 6);
        var jsonPath = StringOption(args, "--json");
        if (IntOption(args, "--cold-repeat", 0) is var repeat and > 0)
        {
            return ColdRepeat(args, repeat);
        }

        var report = new Dictionary<string, object>
        {
            ["machine"] = new Dictionary<string, object>
            {
                ["cores"] = Environment.ProcessorCount,
                ["runtime"] = Environment.Version.ToString(),
                ["server-gc"] = System.Runtime.GCSettings.IsServerGC,
                ["viewport"] = $"{width}x{height}",
                ["load-average-at-start"] = LoadAverage(),
            },
            ["options"] = new Dictionary<string, object>
            {
                ["group-size"] = group,
                ["raid-minutes"] = minutes,
                ["accelerate"] = accelerate,
                ["seeded"] = StringOption(args, "--seed-database") is not null,
            },
        };

        HarnessHost? host = null;
        var sampler = args.Contains("--alloc-types") ? new AllocationSampler() : null;
        try
        {
            host = HarnessHost.Boot(width, height, StringOption(args, "--seed-database"));
            report["cold-start-ms"] = host.ColdStart;
            Console.WriteLine("cold-start-ms " + JsonSerializer.Serialize(host.ColdStart));

            if (scenarios.Contains("plansearch"))
            {
                // Package 45: typing in the Plan workspace's quest search. Needs no map, so it is
                // measured before the map scenarios and runs on its own in seconds.
                var planSearch = Scenarios.PlanSearch(host, StringOption(args, "--plan-query") ?? "graphics card");
                report["plan-search"] = planSearch;
                Console.WriteLine("plan-search " + JsonSerializer.Serialize(planSearch));
            }

            RaidScript? script = null;
            if (scenarios.Contains("map") || scenarios.Contains("pan") || scenarios.Contains("raid") || scenarios.Contains("bisect"))
            {
                var (result, built) = Scenarios.FirstMapDraw(host, StringOption(args, "--map"), group);
                script = built;
                report["first-map-draw"] = result;
                Console.WriteLine("first-map-draw " + JsonSerializer.Serialize(result));
            }

            if (script is not null && (scenarios.Contains("pan") || scenarios.Contains("raid") || scenarios.Contains("bisect")))
            {
                host.StopGroupSession();
                var driver = new RaidDriver(host, script, group, accelerate);
                driver.Begin();
                // Two shots and an exchange in first, so the plan has the player, a trail and the squad on it.
                for (var warm = 0; warm < 3; warm++)
                {
                    driver.Step();
                    Thread.Sleep(5);
                }

                // What each kind of event sets off, one at a time, so a rebuild storm has a name.
                var attribution = new Dictionary<string, object>();
                var tally = new Tally(host);
                foreach (var kind in new[] { "exchange", "log", "screenshot" })
                {
                    var before = tally.Snapshot();
                    var rebuildsBefore = driver.Rebuilds;
                    driver.MakeDue(kind);
                    driver.Step();
                    attribution[kind] = new Dictionary<string, object>
                    {
                        ["scene-rebuilds"] = driver.Rebuilds - rebuildsBefore,
                        ["fired"] = Tally.Since(before, tally.Snapshot()),
                    };
                }

                if (args.Contains("--scene-diff"))
                {
                    foreach (var kind in new[] { "log", "exchange", "screenshot" })
                    {
                        var diff = Scenarios.SceneDifference(host, driver, kind);
                        Console.WriteLine($"scene-diff[{kind}] " + JsonSerializer.Serialize(diff));
                    }
                }

                report["attribution"] = attribution;
                Console.WriteLine("attribution " + JsonSerializer.Serialize(attribution));
                report["scene"] = Scenarios.DescribeScene(host);
                Console.WriteLine("scene " + JsonSerializer.Serialize(report["scene"]));

                if (scenarios.Contains("bisect"))
                {
                    var bisect = Scenarios.IdleBisect(host, new Tally(host), sampler);
                    report["idle-bisect"] = bisect;
                    Console.WriteLine("idle-bisect " + JsonSerializer.Serialize(bisect));
                }

                if (scenarios.Contains("pan"))
                {
                    var quiet = Scenarios.PanZoom(host, null, 150, 40);
                    report["pan-zoom"] = quiet;
                    Console.WriteLine("pan-zoom " + JsonSerializer.Serialize(quiet));
                    // Ten times the real cadence, so a short drag sees several exchanges land in it.
                    var busyDriver = new RaidDriver(host, script, group, 10);
                    var busy = Scenarios.PanZoom(host, busyDriver, 150, 40);
                    report["pan-zoom-while-raid-runs-10x"] = busy;
                    Console.WriteLine("pan-zoom-while-raid-runs-10x " + JsonSerializer.Serialize(busy));
                }

                if (scenarios.Contains("raid"))
                {
                    var raid = Scenarios.Raid(host, new RaidDriver(host, script, group, accelerate), new Tally(host), sampler, minutes, accelerate);
                    report["raid"] = raid;
                    Console.WriteLine("raid " + JsonSerializer.Serialize(raid));
                }
            }
        }
        finally
        {
            report["load-average-at-end"] = LoadAverage();
            if (jsonPath is not null)
            {
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Wrote {jsonPath}");
            }

            host?.Cleanup();
            Console.Out.Flush();
            // The composition's background services keep foreground threads alive.
            Environment.Exit(0);
        }

        return 0;
    }

    /// <summary>
    /// Cold start is only cold once per process, so it is measured in a fresh one each time and
    /// the median is reported, with the spread beside it.
    /// </summary>
    private static int ColdRepeat(string[] args, int repeat)
    {
        var dll = typeof(Program).Assembly.Location;
        var forwarded = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] is "--cold-repeat" or "--json" or "--scenarios")
            {
                index++;
                continue;
            }

            forwarded.Add(args[index]);
        }

        var runs = new List<Dictionary<string, double>>();
        for (var run = 0; run < repeat; run++)
        {
            var json = Path.Combine(Path.GetTempPath(), $"cold-{Guid.NewGuid():N}.json");
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, UseShellExecute = false };
            start.ArgumentList.Add(dll);
            start.ArgumentList.Add("--scenarios");
            start.ArgumentList.Add("cold");
            start.ArgumentList.Add("--json");
            start.ArgumentList.Add(json);
            foreach (var argument in forwarded)
            {
                start.ArgumentList.Add(argument);
            }

            using var child = Process.Start(start)!;
            child.StandardOutput.ReadToEnd();
            child.WaitForExit();
            using var document = JsonDocument.Parse(File.ReadAllText(json));
            File.Delete(json);
            runs.Add(document.RootElement.GetProperty("cold-start-ms").EnumerateObject()
                .ToDictionary(item => item.Name, item => item.Value.GetDouble()));
        }

        var summary = runs[0].Keys.ToDictionary(
            key => key,
            key =>
            {
                var values = runs.Select(item => item[key]).Order().ToArray();
                return new Dictionary<string, double>
                {
                    ["median"] = values[values.Length / 2],
                    ["min"] = values[0],
                    ["max"] = values[^1],
                };
            });
        var result = new Dictionary<string, object> { ["cold-start-ms"] = summary, ["runs"] = repeat, ["load-average"] = LoadAverage() };
        Console.WriteLine("cold-start-ms " + JsonSerializer.Serialize(summary));
        if (StringOption(args, "--json") is { } path)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }

        return 0;
    }

    private static string LoadAverage() =>
        File.Exists("/proc/loadavg") ? string.Join(' ', File.ReadAllText("/proc/loadavg").Split(' ').Take(3)) : "n/a";

    private static int IntOption(string[] args, string name, int fallback) =>
        StringOption(args, name) is { } value ? int.Parse(value) : fallback;

    private static double DoubleOption(string[] args, string name, double fallback) =>
        StringOption(args, name) is { } value ? double.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;

    private static string? StringOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
