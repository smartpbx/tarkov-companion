using System.Globalization;
using System.Text.RegularExpressions;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// Reads the line the game writes when matchmaking finishes, shortly before a raid loads.
/// </summary>
/// <remarks>
/// docs/research/EFT_LOG_FACTS.md names this line as present and lists it as usable: "Queue
/// time | application | MatchingCompleted:18.36 real:25.02 diff:6.66 — use real". Nothing read
/// it before this. <c>real</c> is the wall-clock figure, which is what a player waiting for a
/// raid actually experienced; <c>diff</c> is the gap between the game's own estimate and that,
/// and is not surfaced.
/// </remarks>
public static partial class LoadTimeParser
{
    private const string Marker = "MatchingCompleted";

    public static LoadTimeObservation? ParseLine(string? line, DateTimeOffset observedUtc)
    {
        if (string.IsNullOrEmpty(line) || !line.Contains(Marker, StringComparison.Ordinal))
        {
            return null;
        }

        var match = RealPattern().Match(line);
        if (!match.Success)
        {
            return null;
        }

        if (!double.TryParse(
                match.Groups["real"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var real) || real <= 0)
        {
            return null;
        }

        return new LoadTimeObservation(real, observedUtc.ToUniversalTime());
    }

    [GeneratedRegex(
        @"MatchingCompleted:[\d.]+\s+real:(?<real>[\d.]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex RealPattern();
}
