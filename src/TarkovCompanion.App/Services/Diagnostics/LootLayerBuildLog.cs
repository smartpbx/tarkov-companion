using System.Diagnostics;
using System.Globalization;
using TarkovCompanion.Application.Services.LootSpawns;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// One breadcrumb a minute at most: how often the Raid map asked for the high-value loot layer,
/// how often it got a fresh build rather than the one it already had, and what that cost.
/// </summary>
/// <remarks>
/// [#657] "The map is slow with the high-value loot on" arrived with a log that said nothing about
/// the loot layer at all: not one line in its whole history. This is the line it needed, cheap
/// enough to leave on: a counter per call and one write a minute while the layer is being asked for.
/// </remarks>
public static class LootLayerBuildLog
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    /// <summary>[#893] How long a window with nothing to report may run before it is written anyway.</summary>
    internal static readonly TimeSpan QuietEvery = TimeSpan.FromMinutes(10);

    /// <summary>[#893] A frame's worth: a build that took longer than this is worth a line of its own.</summary>
    internal const double NotableMilliseconds = 16;
    private static readonly Lock Gate = new();
    private static HighValueLootLayerResult? _last;
    private static int _built;
    private static int _reused;
    private static double _milliseconds;
    private static double _longest;
    private static long _windowStarted;

    public static HighValueLootLayerResult Time(Func<HighValueLootLayerResult> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var started = Stopwatch.GetTimestamp();
        var result = build();
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        string? line = null;
        lock (Gate)
        {
            if (ReferenceEquals(result, _last))
            {
                _reused++;
            }
            else
            {
                _built++;
            }

            _last = result;
            _milliseconds += elapsed;
            _longest = Math.Max(_longest, elapsed);
            if (_windowStarted == 0)
            {
                _windowStarted = started;
            }

            var window = Stopwatch.GetElapsedTime(_windowStarted);
            if (ShouldWrite(_built, _longest, window))
            {
                line = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{result.MapId}: {_built} built, {_reused} reused in {window.TotalSeconds:0} s; {_milliseconds:0} ms in all, longest {_longest:0} ms; {result.Objects.Count} spawns, {result.Status.Completeness}");
                (_built, _reused, _milliseconds, _longest, _windowStarted) = (0, 0, 0, 0, 0);
            }
        }

        if (line is not null)
        {
            CrashBreadcrumbs.Drop("loot-layer", line);
        }

        return result;
    }

    /// <summary>
    /// [#893] Whether the window's line goes into the breadcrumbs now.
    /// </summary>
    /// <remarks>
    /// A minute in which the layer was only reused, and cheaply, is a heartbeat. Written every
    /// minute, those heartbeats were 415 of 478 lines in the owner's breadcrumb file, and the forty
    /// lines replayed after a killed run were all "0 built, 175 reused": no navigation, no hang,
    /// nothing about what the player was doing. A quiet window now keeps counting and is written
    /// once in ten minutes, so the reuse rate is still on record; a minute with a build or a slow
    /// call is written as before.
    /// </remarks>
    internal static bool ShouldWrite(int built, double longestMilliseconds, TimeSpan window) =>
        window >= QuietEvery
        || (window >= Every && (built > 0 || longestMilliseconds > NotableMilliseconds));
}
