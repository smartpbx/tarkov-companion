using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>Reads the game's <c>Session mode:</c> line (#403, #712 decision 4).</summary>
/// <remarks>
/// <c>2026-01-01 20:00:00.000|1.1.5.1.47510|Info|application|Session mode: PvpSeason</c>, once per
/// launch in <c>application</c> and mirrored once into <c>output</c>. The mirror carries the same
/// value, so a caller that follows it acts once per value, not once per line.
/// </remarks>
public static class SessionModeParser
{
    private const string Marker = "|Session mode:";

    /// <summary>The game's word is one token; anything longer is not this line.</summary>
    private const int MaximumValueLength = 32;

    public static GameSessionMode? ParseLine(string? line, DateTimeOffset observedUtc)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var at = line.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var value = line[(at + Marker.Length)..].Trim();
        if (value.Length is 0 or > MaximumValueLength || !value.All(char.IsAsciiLetterOrDigit))
        {
            return null;
        }

        return new GameSessionMode(value, RaidContextRules.ModeOf(value), observedUtc.ToUniversalTime());
    }
}
