using System.Globalization;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// What the interface was last asked to do, kept where another thread can read it.
/// </summary>
/// <remarks>
/// A frozen window cannot be asked what it is doing: the thread that would answer is the one that
/// is stuck. So the answer is written down beforehand, every time, by whoever starts the work —
/// the route on each navigation, the workspace on each load, the page on each startup step. The
/// hang watchdog reads it from its own thread when the dispatcher stops answering, and the render
/// tool's stall meter reads it to name what a long dispatcher turn was doing.
///
/// Each note replaces one reference, so writing costs an allocation and reading takes no lock. It
/// is deliberately not a history: <see cref="CrashBreadcrumbs"/> is the history, and it is on disk.
/// </remarks>
public static class UiActivity
{
    private static volatile string _route = string.Empty;
    private static volatile Note? _load;
    private static volatile Note? _command;
    private static volatile string _map = string.Empty;
    private static readonly UiStep?[] Steps = new UiStep?[64];
    private static int _nextStep;

    /// <summary>The route the shell last navigated to, or empty before the first navigation.</summary>
    public static string Route => _route;

    /// <summary>The map last handed to the rasteriser, or empty if none has been this run.</summary>
    public static string LastMapRasterised => _map;

    public static void Navigated(string route) => _route = route ?? string.Empty;

    public static void MapRasterising(string map) => _map = map ?? string.Empty;

    /// <summary>A workspace or startup page began loading.</summary>
    public static void LoadStarted(string surface) => _load = new(surface, DateTimeOffset.UtcNow, null);

    /// <summary>The load that <see cref="LoadStarted"/> announced has returned, either way.</summary>
    public static void LoadFinished(string surface)
    {
        if (_load is { } current && string.Equals(current.Name, surface, StringComparison.Ordinal))
        {
            _load = current with { FinishedUtc = DateTimeOffset.UtcNow };
        }
    }

    /// <summary>Whether the load last announced has yet to return.</summary>
    public static bool IsLoading => _load is { FinishedUtc: null };

    public static void CommandStarted(string name) => _command = new(name, DateTimeOffset.UtcNow, null);

    /// <summary>Notes a point the interface thread has reached inside a load.</summary>
    /// <remarks>
    /// Kept in a small ring rather than written anywhere. Two readers want it: the render tool's
    /// stall meter lists the steps that fell inside a long dispatcher turn, which is what says
    /// where in a load the time went, and the hang watchdog lists the last few before a freeze.
    /// </remarks>
    public static void Step(string label)
    {
        var step = new UiStep(System.Diagnostics.Stopwatch.GetTimestamp(), label, Environment.CurrentManagedThreadId);
        lock (Steps)
        {
            Steps[_nextStep] = step;
            _nextStep = (_nextStep + 1) % Steps.Length;
        }
    }

    /// <summary>The remembered steps, oldest first.</summary>
    public static IReadOnlyList<UiStep> RecentSteps()
    {
        lock (Steps)
        {
            return [.. Steps.Skip(_nextStep).Concat(Steps.Take(_nextStep)).OfType<UiStep>()];
        }
    }

    /// <summary>One line naming the route, the last load and the last command.</summary>
    public static string Describe()
    {
        var route = _route;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"route '{(route.Length == 0 ? "none yet" : route)}'; last load {Describe(_load)}; last command {Describe(_command)}");
    }

    /// <summary>Forgets everything. For tests, because the state is static.</summary>
    internal static void Reset()
    {
        _route = string.Empty;
        _map = string.Empty;
        _load = null;
        _command = null;
        lock (Steps)
        {
            Array.Clear(Steps);
            _nextStep = 0;
        }
    }

    private static string Describe(Note? note)
    {
        if (note is null)
        {
            return "none";
        }

        var age = DateTimeOffset.UtcNow - note.StartedUtc;
        var state = note.FinishedUtc is null ? "still running" : "finished";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"'{note.Name}' ({state}, started {age.TotalSeconds:0.0}s ago)");
    }

    private sealed record Note(string Name, DateTimeOffset StartedUtc, DateTimeOffset? FinishedUtc);
}

/// <summary>One point reached inside a load: when, what, and on which thread.</summary>
public sealed record UiStep(long Timestamp, string Label, int ThreadId);
