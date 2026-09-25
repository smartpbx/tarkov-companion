using System.Globalization;
using System.Text.RegularExpressions;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services;

/// <summary>
/// What one of the game's screenshot names is, before trying to read a position out of it.
/// </summary>
/// <remarks>
/// [V2 rough package 43a] Escape from Tarkov writes the coordinate and rotation blocks only for a
/// shot taken in a raid. A menu, hideout or post-raid screenshot is named
/// <c>2026-09-18[19-03]_19.67.png</c> — date, time, and the in-game clock, with nowhere for a
/// position to be. Refusing to parse that one is correct, and reporting it as a fault is not: the
/// difference is the whole of the complaint this classification exists to answer.
/// </remarks>
public enum ScreenshotNameKind
{
    /// <summary>Not one of the game's screenshot names at all.</summary>
    Unrecognized,

    /// <summary>The game's name for a shot taken outside a raid. It carries no position by design.</summary>
    OutsideRaid,

    /// <summary>Shaped like an in-raid shot: the position blocks are there to be read.</summary>
    InRaid,
}

/// <param name="relayClock">
/// [#891] The PC clock's error as the relay measured it. The game names a screenshot by the real
/// time, so on a PC four hours fast a shot taken now read as four hours old: faded on the owner's
/// own map, and published to the squad with an age of four hours.
/// </param>
public sealed partial class ScreenshotFilenameParser(
    TimeProvider? timeProvider = null,
    Devices.RelayClockOffsetTracker? relayClock = null) : IScreenshotFilenameParser
{
    /// <summary>The widest a name's minute and a zone's quarter hours can leave a live shot from a whole-hour error.</summary>
    private static readonly TimeSpan WholeHourSlack = TimeSpan.FromMinutes(3);

    /// <summary>The largest clock or zone error in whole hours the name's time is read through.</summary>
    private const int MaximumWholeHourError = 14;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Devices.RelayClockOffsetTracker? _relayClock = relayClock;

    public bool TryParse(string filename, TimeSpan localUtcOffset, out ScreenshotPosition? position)
    {
        position = null;
        var name = Path.GetFileName(filename);
        var match = FilenamePattern().Match(name);
        if (!match.Success || localUtcOffset < TimeSpan.FromHours(-14) || localUtcOffset > TimeSpan.FromHours(14))
        {
            return false;
        }

        var hasTimestamp = DateTime.TryParseExact(
                $"{match.Groups["date"].Value} {match.Groups["time"].Value}",
                "yyyy-MM-dd HH-mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localTimestamp);
        if (!hasTimestamp
            || !TryDouble(match, "x", out var x)
            || !TryDouble(match, "y", out var y)
            || !TryDouble(match, "z", out var z)
            || !TryDouble(match, "qx", out var qx)
            || !TryDouble(match, "qy", out var qy)
            || !TryDouble(match, "qz", out var qz)
            || !TryDouble(match, "qw", out var qw))
        {
            return false;
        }

        var orientation = new QuaternionOrientation(qx, qy, qz, qw);
        QuaternionOrientation normalized;
        try
        {
            normalized = orientation.Normalize();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        TimeSpan? inGameTime = null;
        if (match.Groups["gameTime"].Success &&
            double.TryParse(match.Groups["gameTime"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            inGameTime = TimeSpan.FromSeconds(seconds);
        }

        int? duplicate = null;
        if (match.Groups["duplicate"].Success &&
            int.TryParse(match.Groups["duplicate"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var duplicateValue))
        {
            duplicate = duplicateValue;
        }

        position = new(
            new DateTimeOffset(DateTime.SpecifyKind(localTimestamp, DateTimeKind.Unspecified), localUtcOffset),
            new WorldPosition(x, y, z),
            normalized,
            HeadingDegrees(normalized),
            inGameTime,
            duplicate,
            name);
        return true;
    }

    /// <inheritdoc />
    public bool TryParseFile(string path, TimeSpan localUtcOffset, out ScreenshotPosition? position)
    {
        if (!TryParse(path, localUtcOffset, out position) || position is null)
        {
            return false;
        }

        // The game's name is the capture clock. Filesystem stamps can be rewritten by sync,
        // copied profiles, clock corrections, or daylight-saving transitions and are never an
        // ordering cursor. The name's time must still not be ahead of this clock: a future
        // position would never be overtaken by a real, later screenshot. It would sit at the
        // front of the trail forever and hold the raid's "last seen" time in the future while
        // real time caught up to it.
        var now = _timeProvider.GetUtcNow();
        position = position with { Timestamp = OnThisPcsClock(position.Timestamp, now) };
        if (position.Timestamp > now)
        {
            position = position with { Timestamp = now };
        }

        return true;
    }

    /// <summary>
    /// [#891] The name's real time moved onto this PC's clock, which every age is measured on.
    /// </summary>
    /// <remarks>
    /// With the relay's measurement, by exactly that. Without one (no relay, or not heard from
    /// yet), only by a whole number of hours, and only when the name is that many hours from now
    /// to within a few minutes. The watcher reports a screenshot within moments of the game
    /// writing it, so such a gap is a PC clock or time zone that is hours out, never a shot that
    /// old. The minutes and seconds are kept either way: a real age is never corrected away.
    /// </remarks>
    internal DateTimeOffset OnThisPcsClock(DateTimeOffset named, DateTimeOffset pcNow)
    {
        if (_relayClock?.CorrectionAt(pcNow) is { } correction && correction != TimeSpan.Zero)
        {
            // The relay's time minus this PC's: the PC reads real time minus that.
            return named - correction;
        }

        var lag = pcNow - named;
        var hours = Math.Round(lag.TotalHours, MidpointRounding.AwayFromZero);
        if (hours == 0 || Math.Abs(hours) > MaximumWholeHourError)
        {
            return named;
        }

        var wholeHours = TimeSpan.FromHours(hours);
        return (lag - wholeHours).Duration() <= WholeHourSlack ? named + wholeHours : named;
    }

    public static double HeadingDegrees(QuaternionOrientation orientation)
    {
        var q = orientation.Normalize();
        var sinYawCosPitch = 2 * ((q.W * q.Y) + (q.X * q.Z));
        var cosYawCosPitch = 1 - (2 * ((q.Y * q.Y) + (q.Z * q.Z)));
        var degrees = Math.Atan2(sinYawCosPitch, cosYawCosPitch) * (180 / Math.PI);
        return (degrees + 360) % 360;
    }

    /// <summary>
    /// Which kind of screenshot name this is, without attempting to read a position.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 43a] Deliberately a separate question from <see cref="TryParse"/>. "This
    /// will not parse" has two very different causes — a shot taken in the menu, which never had a
    /// position, and a shot taken in a raid whose name this build cannot read, which is a real
    /// defect. Everything that reports to a person needs to tell those apart.
    ///
    /// <see cref="ScreenshotNameKind.InRaid"/> means the blocks are present and the right shape,
    /// not that they parse: a name that reaches here and still fails <see cref="TryParse"/> is the
    /// case worth calling broken.
    /// </remarks>
    public static ScreenshotNameKind Classify(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);
        var name = Path.GetFileName(filename);
        if (FilenamePattern().IsMatch(name))
        {
            return ScreenshotNameKind.InRaid;
        }

        return OutsideRaidPattern().IsMatch(name)
            ? ScreenshotNameKind.OutsideRaid
            : ScreenshotNameKind.Unrecognized;
    }

    private static bool TryDouble(Match match, string group, out double value) =>
        double.TryParse(match.Groups[group].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    [GeneratedRegex(
        @"^(?<date>\d{4}-\d{2}-\d{2})\[(?<time>\d{2}-\d{2})\]_(?<x>-?\d+(?:\.\d+)?),\s*(?<y>-?\d+(?:\.\d+)?),\s*(?<z>-?\d+(?:\.\d+)?)_(?<qx>-?\d+(?:\.\d+)?),\s*(?<qy>-?\d+(?:\.\d+)?),\s*(?<qz>-?\d+(?:\.\d+)?),\s*(?<qw>-?\d+(?:\.\d+)?)(?:_(?<gameTime>\d+(?:\.\d+)?))?(?:\s+\((?<duplicate>\d+)\))?\.(?:png|jpg|jpeg)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FilenamePattern();

    /// <summary>
    /// The game's own name with the position blocks absent: a menu, hideout or post-raid shot.
    /// </summary>
    /// <remarks>
    /// The same date and time the in-raid name opens with, then anything that is not a coordinate
    /// triple. The trailing " (1)" a synced folder adds is allowed here for the same reason it is
    /// allowed above — a duplicate suffix does not change what the shot is.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<date>\d{4}-\d{2}-\d{2})\[(?<time>\d{2}-\d{2})\](?:_(?<gameTime>\d+(?:\.\d+)?))?(?:\s+\((?<duplicate>\d+)\))?\.(?:png|jpg|jpeg)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OutsideRaidPattern();
}
