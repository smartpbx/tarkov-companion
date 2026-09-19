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

public sealed partial class ScreenshotFilenameParser(TimeProvider? timeProvider = null) : IScreenshotFilenameParser
{
    /// <summary>How far the file's write time may disagree with its name before it is not trusted.</summary>
    /// <remarks>
    /// The write time is ordinarily seconds behind the name, because the game names the file
    /// and the filesystem stamps it in the same instant. A OneDrive-synced, copied or restored
    /// Screenshots folder can rewrite that stamp to whenever the sync happened, hours away from
    /// when the shot was actually taken. A disagreement this large is the sync artifact, not a
    /// better clock, so the name's own time stands instead.
    /// </remarks>
    private static readonly TimeSpan MaximumClockDrift = TimeSpan.FromHours(1);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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

        // A missing or unreadable file leaves the name's own time in place. That is worse
        // than the file's, but it is the only one available and the position is still real.
        var written = LastWrittenUtc(path);
        if (written is { } moment && (moment - position.Timestamp).Duration() <= MaximumClockDrift)
        {
            position = position with { Timestamp = moment };
        }

        // Whichever clock won, it must not be ahead of this one. A future timestamp would
        // never be overtaken by a real, later screenshot: it would sit at the front of the
        // trail forever and hold the raid's "last seen" time in the future while real time
        // caught up to it.
        var now = _timeProvider.GetUtcNow();
        if (position.Timestamp > now)
        {
            position = position with { Timestamp = now };
        }

        return true;
    }

    private static DateTimeOffset? LastWrittenUtc(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
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
