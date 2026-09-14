using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// What to do with the raid rows a previous run of the companion left open.
/// </summary>
/// <param name="Adopt">The raid this session is the continuation of, or null to start fresh.</param>
/// <param name="Close">Every other open row, which is not this raid and never will be.</param>
public sealed record RaidResumption(RaidHistoryEntry? Adopt, IReadOnlyList<Guid> Close)
{
    public static RaidResumption Nothing { get; } = new(null, []);
}

/// <summary>
/// Decides whether a raid already in progress is the one this machine was recording.
/// </summary>
/// <remarks>
/// <para>
/// The state lives in memory, so a companion that restarts mid-raid starts again from nothing:
/// the replay hands back a synthetic "a raid is already running", the state service sees no
/// raid, mints a new id and a new start time, and the row the last run opened keeps
/// <c>end_utc NULL</c> for ever. History shows it as in progress until the end of the wipe,
/// and the map draws an empty trail over screenshots that are already on disk.
/// </para>
/// <para>
/// A crash is the obvious case, but the ordinary ones are a Windows update and the restart
/// Velopack asks for after it installs an update — which this application does on launch, so
/// the update that arrives while somebody is loading into a raid is the common path here.
/// </para>
/// <para>
/// What must not happen is adopting a row from a raid that ended while the companion was
/// closed, because that draws the last raid's trail across this one and dates this raid to
/// whenever that one started. Three things guard it: the map must match, the row must have
/// started before now, and it must have started recently enough that the raid could still be
/// running. The bound is the map's own duration where the catalog knows it, because a
/// forty-minute Reserve raid and a fifty-minute Streets raid are different claims.
/// </para>
/// </remarks>
public static class RaidResume
{
    /// <summary>
    /// How far past a map's stated duration a raid may still be running.
    /// </summary>
    /// <remarks>
    /// The stated duration is when the raid ends, not when the player leaves: extracting takes
    /// a countdown, the end notification arrives after that, and the clock this is measured
    /// against is the companion's rather than the game's. Ten minutes covers the gap without
    /// reaching into the raid before.
    /// </remarks>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The bound used when the map catalog has no duration for this map.
    /// </summary>
    /// <remarks>
    /// The longest raid in the game rather than an average, because being too generous costs a
    /// row that gets closed a few minutes late and being too tight costs the resume entirely.
    /// </remarks>
    public static readonly TimeSpan LongestRaid = TimeSpan.FromMinutes(50);

    /// <summary>
    /// Picks the open raid this session continues, and names the rest to be closed.
    /// </summary>
    /// <param name="history">Every raid on record, as the history service lists them.</param>
    /// <param name="mapId">The map the game says is running now, or null if it did not say.</param>
    /// <param name="nowUtc">The companion's clock.</param>
    /// <param name="mapDuration">How long a raid on this map lasts, where the catalog knows.</param>
    public static RaidResumption Choose(
        IReadOnlyList<RaidHistoryEntry> history,
        string? mapId,
        DateTimeOffset nowUtc,
        TimeSpan? mapDuration = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        var open = history.Where(raid => raid.EndedUtc is null).ToArray();
        if (open.Length == 0)
        {
            return RaidResumption.Nothing;
        }

        var within = (mapDuration ?? LongestRaid) + Margin;
        // No map means no adoption. A raid whose map the replay could not establish could be
        // any of them, and picking the newest open row would be picking by recency alone —
        // which is exactly the mistake that draws the last raid's trail over this one.
        var adopt = mapId is null
            ? null
            : open
                .Where(raid => string.Equals(raid.MapId, mapId, StringComparison.OrdinalIgnoreCase))
                .Where(raid => CouldStillBeRunning(raid, nowUtc, within))
                .OrderByDescending(raid => raid.StartedUtc)
                .FirstOrDefault();
        return new(
            adopt,
            [.. open.Where(raid => raid.Id != adopt?.Id).Select(raid => raid.Id)]);
    }

    /// <summary>
    /// The open rows that no raid on any map could still be, so they can be closed on sight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Choose"/> only runs when the game says a raid is in progress, and most
    /// launches are into the menu. A player who crashed mid-raid on Tuesday and opened the
    /// companion to look at the flea on Wednesday would leave Tuesday's row open for ever,
    /// with History showing it as in progress for the rest of the wipe.
    /// </para>
    /// <para>
    /// Deliberately the conservative half of the same question, so this can run at startup
    /// without racing the resume: anything recent enough to be adopted is left alone, whatever
    /// map it is on, and the resume decides what happens to it. What is left is a row that
    /// started longer ago than any raid lasts, started in a future this machine has not reached
    /// yet, or never recorded a start at all.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Guid> Abandoned(
        IReadOnlyList<RaidHistoryEntry> history,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(history);
        return
        [
            .. history
                .Where(raid => raid.EndedUtc is null)
                .Where(raid => !CouldStillBeRunning(raid, nowUtc, LongestRaid + Margin))
                .Select(raid => raid.Id),
        ];
    }

    /// <summary>Whether a raid started recently enough, and not in the future, to still be on.</summary>
    private static bool CouldStillBeRunning(RaidHistoryEntry raid, DateTimeOffset nowUtc, TimeSpan within) =>
        raid.StartedUtc is { } started && started <= nowUtc && nowUtc - started <= within;
}
