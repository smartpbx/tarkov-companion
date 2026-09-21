using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// Says whether a log line belongs to the raid that is open, or to another one.
/// </summary>
/// <remarks>
/// <para>
/// Escape from Tarkov writes no shutdown marker: every log folder simply stops, after a clean exit
/// exactly as after a crash, so "the log stopped" says nothing. When the game process dies inside a
/// raid no <c>userMatchOver</c> is ever written, and the companion used to stay in that raid for
/// the rest of the day, holding the next raid on the dead raid's identity (#568).
/// </para>
/// <para>
/// What the logs do carry is the game's own short id for the raid (<c>shortId</c>), on
/// <c>userConfirmed</c>, on <c>userMatchOver</c> and on the <c>profileStatus</c> line. Measured on
/// one player's day of thirteen game launches: nine confirmations, eight ends, every id pairing
/// except the one raid the process died in. Three of those raids were reconnected into from a
/// later launch, and their end arrived in a log folder up to three launches after their start, so
/// a newer log folder alone does not end a raid: only a different id does, or, where a line has
/// no id at all, a raid beginning in a later launch.
/// </para>
/// </remarks>
public static class RaidIdentity
{
    /// <summary>Both ids are known and they match.</summary>
    public static bool SameRaid(string? openKey, string? lineKey) =>
        openKey is { Length: > 0 } && lineKey is { Length: > 0 }
        && string.Equals(openKey, lineKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>Both ids are known and they differ.</summary>
    public static bool DifferentRaid(string? openKey, string? lineKey) =>
        openKey is { Length: > 0 } && lineKey is { Length: > 0 }
        && !string.Equals(openKey, lineKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a line was written by a later launch of the game than the raid's own.</summary>
    /// <remarks>Folder names begin with the launch time, so they sort in launch order.</remarks>
    public static bool IsLaterSession(string? openSession, string? lineSession) =>
        openSession is { Length: > 0 } && lineSession is { Length: > 0 }
        && string.Compare(lineSession, openSession, StringComparison.OrdinalIgnoreCase) > 0;

    /// <summary>
    /// Whether in-raid evidence, arriving while a raid is open, is a different raid beginning.
    /// </summary>
    /// <remarks>
    /// By id where both have one. The folder is only the fallback for a line that names a map and
    /// carries no id: a bare <c>GameStarted</c> in a later launch is what a reconnect looks like,
    /// and must not split the raid in two.
    /// </remarks>
    public static bool IsAnotherRaid(RaidSnapshot open, RaidEvidence line)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(line);
        if (DifferentRaid(open.RaidKey, line.RaidKey))
        {
            return true;
        }

        if (SameRaid(open.RaidKey, line.RaidKey))
        {
            return false;
        }

        // An id on the line and none on the open raid: the raid was picked up from lines that
        // carry none (a screenshot, a bare GameStarted), and this is most likely its own id
        // arriving. Adopt it rather than split the raid.
        if (line.RaidKey is { Length: > 0 })
        {
            return IsLaterSession(open.LogSession, line.LogSession);
        }

        return line.MapId is not null && IsLaterSession(open.LogSession, line.LogSession);
    }
}
