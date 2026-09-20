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
    private static double _busyMilliseconds;
    private static double _longest;
    private static int _turns;
    private static long _startedTimestamp;

    public static bool Enabled { get; private set; }

    public static double ThresholdMilliseconds { get; private set; } = 50;

    public static void Enable(double thresholdMilliseconds)
    {
        Enabled = true;
        ThresholdMilliseconds = thresholdMilliseconds;
        _startedTimestamp = Stopwatch.GetTimestamp();
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

    public static void Report(string phase)
    {
        if (!Enabled)
        {
            return;
        }

        var wall = Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"UI stalls [{phase}]: {Stalls.Count} turns over {ThresholdMilliseconds:0} ms; interface thread busy {_busyMilliseconds:0} ms of {wall:0} ms; longest turn {_longest:0} ms; {_turns} turns."));
        foreach (var (milliseconds, doing) in Stalls.OrderByDescending(stall => stall.Milliseconds).Take(15))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {milliseconds,7:0} ms  {doing}"));
        }

        Stalls.Clear();
        _busyMilliseconds = 0;
        _longest = 0;
        _turns = 0;
        _startedTimestamp = Stopwatch.GetTimestamp();
    }
}
