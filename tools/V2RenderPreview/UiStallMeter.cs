using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// Times every turn of the dispatcher this tool runs, and names the long ones.
/// </summary>
/// <remarks>
/// "The app freezes" (#453) was closed once on an assumption, because nothing measured how long
/// the interface thread was held. This tool owns the only dispatcher loop there is in a render, so
/// it can time each <see cref="Dispatcher.RunJobs()"/> exactly: a turn that takes 400 ms is a window
/// that would not have repainted or answered a click for 400 ms. <c>--ui-stalls 50</c> prints
/// every turn longer than the threshold with what <see cref="UiActivity"/> said was
/// running, then the total. It reports; it does not fail the run.
/// </remarks>
internal static class UiStallMeter
{
    private static readonly List<(double Milliseconds, string Doing)> Stalls = [];
    private static readonly List<(string Phase, int Count, double Longest, double InputWait)> Summary = [];
    private static double _busyMilliseconds;
    private static double _longest;
    private static int _turns;
    private static long _startedTimestamp;
    private static double _longestInputWait;
    private static Timer? _probe;
    private static long _probePosted;
    private static readonly List<(double At, double Waited)> LongWaits = [];

    public static bool Enabled { get; private set; }

    public static double ThresholdMilliseconds { get; private set; } = 50;

    public static void Enable(double thresholdMilliseconds)
    {
        Enabled = true;
        AllocationTicks.StartIfAsked();
        ThresholdMilliseconds = thresholdMilliseconds;
        _startedTimestamp = Stopwatch.GetTimestamp();
        // [#453] A turn here is everything RunJobs drained, which includes work a view model split
        // into several jobs behind input on purpose; the real dispatcher answers input and paints
        // between those jobs. So a probe posted at input priority from another thread every 10 ms
        // says how long a click would actually have waited, which is what the hang watchdog
        // measures on a player's machine.
        _probe = new Timer(static _ =>
        {
            if (Interlocked.CompareExchange(ref _probePosted, Stopwatch.GetTimestamp(), 0) != 0)
            {
                return;
            }

            Dispatcher.UIThread.Post(static () =>
            {
                var posted = Interlocked.Exchange(ref _probePosted, 0);
                var waited = Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
                if (waited > _longestInputWait)
                {
                    _longestInputWait = waited;
                }

                // [#678] When in the step the long waits began, to line up with a trace.
                if (waited >= ThresholdMilliseconds)
                {
                    LongWaits.Add((Stopwatch.GetElapsedTime(_startedTimestamp, posted).TotalMilliseconds, waited));
                }
            }, DispatcherPriority.Input);
        }, null, 10, 10);
    }

    /// <summary>Runs whatever the dispatcher has queued, timing it when the meter is on.</summary>
    public static void RunJobs() => Time(static () => Dispatcher.UIThread.RunJobs());

    /// <summary>Times work this tool does on the interface thread itself, outside the dispatcher.</summary>
    public static void Time(Action work)
    {
        if (!Enabled)
        {
            work();
            return;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            work();
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _turns++;
            _busyMilliseconds += elapsed;
            _longest = Math.Max(_longest, elapsed);
            if (elapsed >= ThresholdMilliseconds)
            {
                // The steps that fell inside this turn, each with how far into the turn it came.
                // A turn is one thread running without a break, so the gap between two of them is
                // time the interface thread spent between those two lines and nowhere else.
                var ended = Stopwatch.GetTimestamp();
                var inside = UiActivity.RecentSteps()
                    .Where(step => step.Timestamp >= started && step.Timestamp <= ended
                        && step.ThreadId == Environment.CurrentManagedThreadId)
                    .Select(step => string.Create(
                        CultureInfo.InvariantCulture,
                        $"{step.Label}@{Stopwatch.GetElapsedTime(started, step.Timestamp).TotalMilliseconds:0}"));
                Stalls.Add((elapsed, UiActivity.Describe() + " | steps: " + string.Join(' ', inside)));
            }
        }
    }

    /// <summary>Busy and wall milliseconds, longest turn and long-turn count since the last report.</summary>
    public static (double Busy, double Wall, double Longest, int Over) Snapshot() =>
        (_busyMilliseconds, Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds, _longest, Stalls.Count);

    private static (int, int, int, double) _gc;

    public static void Report(string phase)
    {
        if (!Enabled)
        {
            return;
        }

        var wall = Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"UI stalls [{phase}]: {Stalls.Count} turns over {ThresholdMilliseconds:0} ms; interface thread busy {_busyMilliseconds:0} ms of {wall:0} ms; longest turn {_longest:0} ms; {_turns} turns; longest input wait {_longestInputWait:0} ms."));
        // [#678] What the collector cost in the same span: a switch was as much GC as work.
        var gc = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration().TotalMilliseconds);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  GC: gen0 {gc.Item1 - _gc.Item1}, gen1 {gc.Item2 - _gc.Item2}, gen2 {gc.Item3 - _gc.Item3}, paused {gc.Item4 - _gc.Item4:0} ms, heap {GC.GetTotalMemory(false) / 1048576} MB"));
        _gc = gc;
        AllocationTicks.Report();
        foreach (var (milliseconds, doing) in Stalls.OrderByDescending(stall => stall.Milliseconds).Take(15))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {milliseconds,7:0} ms  {doing}"));
        }

        if (LongWaits.Count > 0)
        {
            Console.WriteLine("  input waits over threshold (ms into step, ms waited): " + string.Join(", ",
                LongWaits.Select(wait => string.Create(CultureInfo.InvariantCulture, $"{wait.At:0}+{wait.Waited:0}"))));
            LongWaits.Clear();
        }

        Summary.Add((phase, Stalls.Count, _longest, _longestInputWait));
        _longestInputWait = 0;
        Stalls.Clear();
        _busyMilliseconds = 0;
        _longest = 0;
        _turns = 0;
        _startedTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>One row per reported phase: the table <c>--stall-tour</c> exists to print.</summary>
    public static void PrintSummary()
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| Route or action | Longest UI turn (ms) | Turns over {ThresholdMilliseconds:0} ms | Longest input wait (ms) |"));
        Console.WriteLine("| --- | ---: | ---: | ---: |");
        foreach (var (phase, count, longest, inputWait) in Summary)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {phase} | {longest:0} | {count} | {inputWait:0} |"));
        }
    }
}
