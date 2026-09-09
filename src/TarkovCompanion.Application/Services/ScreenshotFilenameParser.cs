using System.Globalization;
using System.Text.RegularExpressions;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services;

public sealed partial class ScreenshotFilenameParser : IScreenshotFilenameParser
{
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

    public static double HeadingDegrees(QuaternionOrientation orientation)
    {
        var q = orientation.Normalize();
        var sinYawCosPitch = 2 * ((q.W * q.Y) + (q.X * q.Z));
        var cosYawCosPitch = 1 - (2 * ((q.Y * q.Y) + (q.Z * q.Z)));
        var degrees = Math.Atan2(sinYawCosPitch, cosYawCosPitch) * (180 / Math.PI);
        return (degrees + 360) % 360;
    }

    private static bool TryDouble(Match match, string group, out double value) =>
        double.TryParse(match.Groups[group].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    [GeneratedRegex(
        @"^(?<date>\d{4}-\d{2}-\d{2})\[(?<time>\d{2}-\d{2})\]_(?<x>-?\d+(?:\.\d+)?),\s*(?<y>-?\d+(?:\.\d+)?),\s*(?<z>-?\d+(?:\.\d+)?)_(?<qx>-?\d+(?:\.\d+)?),\s*(?<qy>-?\d+(?:\.\d+)?),\s*(?<qz>-?\d+(?:\.\d+)?),\s*(?<qw>-?\d+(?:\.\d+)?)(?:_(?<gameTime>\d+(?:\.\d+)?))?(?:\s+\((?<duplicate>\d+)\))?\.(?:png|jpg|jpeg)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FilenamePattern();
}
