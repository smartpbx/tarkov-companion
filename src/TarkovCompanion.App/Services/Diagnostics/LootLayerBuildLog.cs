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
            if (window >= Every)
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
}
