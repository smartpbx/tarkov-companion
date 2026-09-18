using System.Globalization;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>
/// How long somebody who started at another spawn area could take to reach you.
/// </summary>
/// <remarks>
/// <para>
/// [V2 rough package 39] "Where the others started" answers where; this answers when, which is
/// the question that actually changes what you do in the first ninety seconds of a raid. A
/// spawn two hundred metres away across open ground and a spawn two hundred metres away through
/// a building are the same row until somebody says how long the walk is.
/// </para>
/// <para>
/// A band, never a number. Nobody runs to you in a straight line, nobody runs the whole way,
/// and this application cannot see anybody — so a figure like "47 s" would be a precision that
/// does not exist, dressed up as a fact. The two ends are a flat-out sprint straight at you and
/// a careful advance that is not straight at all, and both are rounded outwards onto a coarse
/// ladder so the band can only ever be wider than the arithmetic, never narrower.
/// </para>
/// <para>
/// The speeds are judgement, not measurement: a geared PMC sprints at roughly five and a half
/// metres a second and moves at roughly two when they are being careful about it. They are here
/// as named constants so the judgement is visible and can be corrected in one place.
/// </para>
/// </remarks>
public static class SpawnReach
{
    /// <summary>Flat out, straight at you, in metres per second.</summary>
    public const double SprintMetresPerSecond = 5.5;

    /// <summary>Carefully, and not in a straight line, in metres per second.</summary>
    public const double CarefulMetresPerSecond = 2.0;

    /// <summary>
    /// The only durations this ever says, in seconds.
    /// </summary>
    /// <remarks>
    /// Deliberately coarse and deliberately uneven: the difference between fifteen seconds and
    /// thirty changes what you do, and the difference between four minutes and five does not.
    /// Fine at the near end for the same reason — rounding a twenty-seven second sprint down to
    /// the next rung must not turn it into a claim that somebody could cover it in ten.
    /// </remarks>
    private static readonly int[] Ladder = [10, 15, 20, 30, 45, 60, 90, 120, 180, 240, 300, 420, 600];

    /// <summary>
    /// How long until somebody from a spawn area that far away could be standing here.
    /// </summary>
    /// <param name="metres">How far the area is, in a straight line.</param>
    /// <returns>A band such as "30–45 s away", or nothing for a distance that makes no sense.</returns>
    public static string Describe(double metres)
    {
        if (!double.IsFinite(metres) || metres < 0)
        {
            return string.Empty;
        }

        var fastest = RoundDown(metres / SprintMetresPerSecond);
        var slowest = RoundUp(metres / CarefulMetresPerSecond);
        return fastest == slowest
            ? string.Create(CultureInfo.CurrentCulture, $"about {Label(slowest)} away")
            : string.Create(CultureInfo.CurrentCulture, $"{Range(fastest, slowest)} away");
    }

    /// <summary>The largest rung at or below this many seconds.</summary>
    private static int RoundDown(double seconds)
    {
        var chosen = Ladder[0];
        foreach (var rung in Ladder)
        {
            if (rung <= seconds)
            {
                chosen = rung;
            }
        }

        return chosen;
    }

    /// <summary>The smallest rung at or above this many seconds.</summary>
    private static int RoundUp(double seconds)
    {
        foreach (var rung in Ladder)
        {
            if (rung >= seconds)
            {
                return rung;
            }
        }

        return Ladder[^1];
    }

    /// <summary>Where seconds stop reading better than minutes.</summary>
    private const int MinutesFrom = 120;

    /// <summary>"20–90 s", "1–3 min", "45 s – 3 min": the unit is written once where it can be.</summary>
    private static string Range(int fromSeconds, int toSeconds) =>
        fromSeconds < MinutesFrom && toSeconds < MinutesFrom
            ? string.Create(CultureInfo.CurrentCulture, $"{fromSeconds}–{toSeconds} s")
            : fromSeconds >= MinutesFrom && toSeconds >= MinutesFrom
                ? string.Create(CultureInfo.CurrentCulture, $"{fromSeconds / 60}–{toSeconds / 60} min")
                : string.Create(CultureInfo.CurrentCulture, $"{Label(fromSeconds)} – {Label(toSeconds)}");

    private static string Label(int seconds) => seconds < MinutesFrom
        ? string.Create(CultureInfo.CurrentCulture, $"{seconds} s")
        : string.Create(CultureInfo.CurrentCulture, $"{seconds / 60} min");
}
