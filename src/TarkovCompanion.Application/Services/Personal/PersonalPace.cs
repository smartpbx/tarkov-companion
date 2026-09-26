using TarkovCompanion.Application.Services.Strategy.Prior;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Personal;

/// <summary>One stretch between two of the player's own screenshots in one raid.</summary>
/// <param name="Metres">Straight-line ground distance (world X and Z).</param>
/// <param name="Seconds">Real seconds between the two screenshots.</param>
public readonly record struct PaceLeg(int Raid, double Metres, double Seconds)
{
    public double SecondsPerMetre => Seconds / Metres;
}

/// <summary>
/// The player's own walking rate, measured from their own raid trails (#712 T8, "your pace").
/// </summary>
/// <param name="SecondsPerMetre">The distance-weighted median rate: the point estimate.</param>
/// <param name="FastSecondsPerMetre">The weighted 25th percentile, the quick end of the range.</param>
/// <param name="SlowSecondsPerMetre">The weighted 75th percentile, the slow end.</param>
/// <param name="Legs">Moving legs it was measured from.</param>
/// <param name="Raids">Raids those legs came from.</param>
public sealed record WalkPace(
    double SecondsPerMetre,
    double FastSecondsPerMetre,
    double SlowSecondsPerMetre,
    int Legs,
    int Raids)
{
    public double MetresPerSecond => 1 / SecondsPerMetre;
}

/// <summary>Walking minutes for a distance, and whether they came from the player's own pace.</summary>
public readonly record struct WalkMinutes(int Low, int High, bool IsPersonal);

/// <summary>
/// Measures <see cref="WalkPace"/> from recorded trails, and turns a distance into minutes with it.
/// </summary>
/// <remarks>
/// <para>
/// A leg is the straight line between two consecutive screenshots of one raid, so its rate already
/// holds the walls, doors and stops the fixed pace covers with <see cref="TrafficRoute.ObstacleAllowance"/>;
/// the allowance is not applied a second time.
/// </para>
/// <para>
/// Only moving legs count: 20 s to 10 min apart, at least 30 m, and at least 0.5 m/s in a straight
/// line. A pair of screenshots either side of three minutes spent looting a room says how long the
/// room took, not how fast the player walks. Measured on 304 of the owner's own screenshots
/// (2026-09-23/24, 39 raids): 74 moving legs by file time, 68 of them timed from what the app
/// stores, pace 1.33 m/s (PersonalPaceTests.ReportsWalkTimeErrorOnRealTrails).
/// </para>
/// <para>
/// Elapsed time comes from the in-game clock the game writes into every screenshot name when both
/// ends have it: that clock runs seven times real time and is written to 0.01 h, about five real
/// seconds, where the name's own time is to the minute. Measured on the same 148 pairs: the ratio
/// of in-game to real (file time) elapsed is 6.97.
/// </para>
/// </remarks>
public static class PersonalPace
{
    public const double MinimumLegSeconds = 20;
    public const double MaximumLegSeconds = 600;
    public const double MinimumLegMetres = 30;
    public const double MinimumMovingMetresPerSecond = 0.5;

    /// <summary>Below this many legs, or from fewer raids, the fixed pace is used instead.</summary>
    public const int MinimumLegs = 8;

    public const int MinimumRaids = 2;

    /// <summary>The game's in-raid clock runs this many times faster than real time.</summary>
    public const double InGameClockRate = 7;

    /// <summary>Moving legs of one raid's trail, oldest first.</summary>
    public static IReadOnlyList<PaceLeg> Legs(IReadOnlyList<ScreenshotPosition> trail, int raid = 0)
    {
        ArgumentNullException.ThrowIfNull(trail);
        var legs = new List<PaceLeg>();
        for (var index = 1; index < trail.Count; index++)
        {
            var from = trail[index - 1];
            var to = trail[index];
            if (Elapsed(from, to) is not { } seconds)
            {
                continue;
            }

            var metres = Math.Sqrt(Math.Pow(to.Position.X - from.Position.X, 2) + Math.Pow(to.Position.Z - from.Position.Z, 2));
            if (seconds is >= MinimumLegSeconds and <= MaximumLegSeconds
                && metres >= MinimumLegMetres
                && metres / seconds >= MinimumMovingMetresPerSecond)
            {
                legs.Add(new PaceLeg(raid, metres, seconds));
            }
        }

        return legs;
    }

    /// <summary>
    /// Real seconds between two screenshots: from the in-game clock when both carry it and it agrees
    /// with the names' minutes, else from the names' minutes; null when neither can be trusted.
    /// </summary>
    public static double? Elapsed(ScreenshotPosition from, ScreenshotPosition to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var named = (to.Timestamp - from.Timestamp).TotalSeconds;
        if (named < 0)
        {
            return null;
        }

        // The parser keeps the name's trailing number as seconds; it is the in-game hour of day.
        if (from.InGameTime is { } start && to.InGameTime is { } end)
        {
            var hours = end.TotalSeconds - start.TotalSeconds;
            if (hours < 0)
            {
                hours += 24;
            }

            var seconds = hours * 3600 / InGameClockRate;

            // The names are to the minute, so the two agree within a minute (and the clock's own 0.01 h step) or they are not one raid.
            if (Math.Abs(seconds - named) <= 75)
            {
                return seconds;
            }

            return null;
        }

        return named;
    }

    /// <summary>The pace across these raids' trails, or null when there is too little to go on.</summary>
    public static WalkPace? Measure(IEnumerable<IReadOnlyList<ScreenshotPosition>> trails)
    {
        ArgumentNullException.ThrowIfNull(trails);
        var legs = trails.SelectMany((trail, raid) => Legs(trail, raid)).ToArray();
        return Measure(legs);
    }

    public static WalkPace? Measure(IReadOnlyList<PaceLeg> legs)
    {
        ArgumentNullException.ThrowIfNull(legs);
        var raids = legs.Select(leg => leg.Raid).Distinct().Count();
        if (legs.Count < MinimumLegs || raids < MinimumRaids)
        {
            return null;
        }

        // Weighted by distance: the rate that minimises the absolute time error summed over the
        // legs is the distance-weighted median of their seconds per metre.
        var sorted = legs.OrderBy(leg => leg.SecondsPerMetre).ToArray();
        return new WalkPace(
            WeightedQuantile(sorted, 0.5),
            WeightedQuantile(sorted, 0.25),
            WeightedQuantile(sorted, 0.75),
            legs.Count,
            raids);
    }

    /// <summary>Minutes on foot: the player's own range when measured, else the fixed 1.8–3.2 m/s.</summary>
    public static WalkMinutes MinutesFor(double metres, WalkPace? pace)
    {
        if (pace is null)
        {
            var (low, high) = TrafficRoute.MinutesFor(metres);
            return new WalkMinutes(low, high, false);
        }

        var fast = Math.Max(1, (int)Math.Floor(metres * pace.FastSecondsPerMetre / 60));
        var slow = Math.Max(fast + 1, (int)Math.Ceiling(metres * pace.SlowSecondsPerMetre / 60));
        return new WalkMinutes(fast, slow, true);
    }

    /// <summary>One number of seconds for a distance, the estimate the acceptance is measured on.</summary>
    public static double Seconds(double metres, WalkPace? pace) =>
        pace is null
            ? metres * TrafficRoute.ObstacleAllowance / TrafficRoute.CarefulPace
            : metres * pace.SecondsPerMetre;

    private static double WeightedQuantile(IReadOnlyList<PaceLeg> sorted, double quantile)
    {
        var total = sorted.Sum(leg => leg.Metres);
        var running = 0.0;
        foreach (var leg in sorted)
        {
            running += leg.Metres;
            if (running >= total * quantile)
            {
                return leg.SecondsPerMetre;
            }
        }

        return sorted[^1].SecondsPerMetre;
    }
}
