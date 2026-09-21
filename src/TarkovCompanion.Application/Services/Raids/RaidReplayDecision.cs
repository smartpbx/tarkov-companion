using System.Globalization;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>One piece of raid evidence read back from a file that was already written.</summary>
/// <param name="Evidence">What the parser made of the line.</param>
/// <param name="WrittenUtc">When the game wrote it, from the line's own stamp, where it had one.</param>
/// <param name="Order">Where it came in the replay, which breaks ties between equal stamps.</param>
public sealed record ReplayedRaidLine(RaidEvidence Evidence, DateTimeOffset? WrittenUtc, long Order);

/// <summary>The raid the newest game session left open, and whether it can still be running.</summary>
/// <param name="Start">The line that began it, or null when the session left no raid open.</param>
/// <param name="StartedUtc">When the game wrote that line.</param>
/// <param name="LastSeenUtc">The last thing the session wrote at all, which is as late as the raid can have been alive.</param>
/// <param name="IsLive">Whether the companion should start inside this raid.</param>
/// <param name="Reason">Why, in words that go into the log.</param>
public sealed record RaidReplayVerdict(
    RaidEvidence? Start,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? LastSeenUtc,
    bool IsLive,
    string Reason)
{
    public static RaidReplayVerdict NoRaid { get; } = new(null, null, null, false, "The newest game session left no raid open.");
}

/// <summary>
/// Decides, when the companion starts, whether the player is in a raid right now.
/// </summary>
/// <remarks>
/// <para>
/// This used to be answered by replaying the newest log folder, one file after another, into a
/// private state machine and asking where it came to rest. Two things were wrong with that. The
/// files are not in time order with each other, so the rest state depended on which file was read
/// last; and nothing asked whether the raid could still be going. On 2026-09-20 a companion started
/// at 19:52 announced "a raid on lighthouse was already running" for a raid the same folder showed
/// confirmed at 19:37 and over at 19:48, and one started with the game not running at all
/// resurrected a raid whose process had died (#568).
/// </para>
/// <para>
/// The game writes no shutdown marker, so a log that stops proves nothing. What is trusted:
/// the raid's short id has a start and no end; no other raid began after it; the game is running;
/// and the raid started recently enough that it could still be on.
/// </para>
/// </remarks>
public static class RaidReplayDecision
{
    /// <summary>How far ahead of the companion's clock a line may be stamped and still be believed.</summary>
    private static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(5);

    public static RaidReplayVerdict Decide(
        IReadOnlyList<ReplayedRaidLine> newestSession,
        bool gameIsRunning,
        DateTimeOffset nowUtc,
        TimeSpan? longestRaid = null)
    {
        ArgumentNullException.ThrowIfNull(newestSession);
        ReplayedRaidLine? open = null;
        DateTimeOffset? lastSeen = null;
        foreach (var line in newestSession
                     .OrderBy(line => line.WrittenUtc ?? DateTimeOffset.MinValue)
                     .ThenBy(line => line.Order))
        {
            if (line.WrittenUtc is { } written && (lastSeen is null || written > lastSeen))
            {
                lastSeen = written;
            }

            var evidence = line.Evidence;
            switch (evidence.SuggestedState)
            {
                // A bare GameStarted names no raid and no map, so it continues a raid and never
                // begins one: whatever wrote it after the last raid ended is not a raid to resume.
                case RaidLifecycleState.InRaid
                    when open is not null || evidence.MapId is not null
                        || evidence.RaidKey is not null || evidence.StartsNewRaid:
                    open = Begin(open, line);
                    break;
                case RaidLifecycleState.PostRaid or RaidLifecycleState.Menu:
                    // The end of another raid is not the end of this one.
                    if (open is not null && !RaidIdentity.DifferentRaid(open.Evidence.RaidKey, evidence.RaidKey))
                    {
                        open = null;
                    }

                    break;
            }
        }

        if (open is null)
        {
            return RaidReplayVerdict.NoRaid;
        }

        var where = open.Evidence.MapId is null ? "A raid" : $"A raid on {open.Evidence.MapId}";
        if (!gameIsRunning)
        {
            return new(open.Evidence, open.WrittenUtc, lastSeen, false,
                $"{where} was never reported over, but the game is not running, so it has ended.");
        }

        var bound = (longestRaid ?? RaidResume.LongestRaid) + RaidResume.Margin;
        if (open.WrittenUtc is { } started && (nowUtc - started > bound || started - nowUtc > ClockSlack))
        {
            return new(open.Evidence, started, lastSeen, false,
                $"{where} was never reported over, but it began longer ago than any raid lasts.");
        }

        return new(open.Evidence, open.WrittenUtc, lastSeen, true, $"{where} is still running.");
    }

    private static ReplayedRaidLine Begin(ReplayedRaidLine? open, ReplayedRaidLine line)
    {
        if (open is null)
        {
            return line;
        }

        var known = open.Evidence;
        var next = line.Evidence;
        if (RaidIdentity.DifferentRaid(known.RaidKey, next.RaidKey))
        {
            return line;
        }

        if (RaidIdentity.SameRaid(known.RaidKey, next.RaidKey))
        {
            return Merge(open, next);
        }

        // At least one side has no id. A line that names another map, or that is the game's own
        // confirmation, is another raid; anything else (a bare GameStarted) is this one going on.
        var namesAnotherMap = next.MapId is not null && known.MapId is not null
            && !string.Equals(next.MapId, known.MapId, StringComparison.OrdinalIgnoreCase);
        var confirmsAnother = next.StartsNewRaid && next.EventId is { Length: > 0 }
            && !string.Equals(next.EventId, known.EventId, StringComparison.Ordinal);
        return namesAnotherMap || confirmsAnother ? line : Merge(open, next);
    }

    /// <summary>Keeps the start, and learns from a later line what the first one did not say.</summary>
    private static ReplayedRaidLine Merge(ReplayedRaidLine open, RaidEvidence later) => open with
    {
        Evidence = open.Evidence with
        {
            MapId = open.Evidence.MapId ?? later.MapId,
            RaidKey = open.Evidence.RaidKey ?? later.RaidKey,
            Side = open.Evidence.Side ?? later.Side,
            SideBasis = open.Evidence.Side is null ? later.SideBasis : open.Evidence.SideBasis,
        },
    };

    /// <summary>Reads the stamp the game puts at the start of every log entry.</summary>
    /// <remarks>
    /// <c>2026-09-20 13:57:21.416|1.1.5.1.47510|…</c>, in the machine's local time with no offset.
    /// Older builds wrote an offset after the time; both are read. A time that does not exist in
    /// the zone (the hour a clock skips in spring) reads as unknown rather than throwing.
    /// </remarks>
    public static DateTimeOffset? WrittenUtc(string? line, TimeZoneInfo localZone)
    {
        ArgumentNullException.ThrowIfNull(localZone);
        if (line is null || line.Length < 19)
        {
            return null;
        }

        var bar = line.IndexOf('|', StringComparison.Ordinal);
        if (bar is < 19 or > 40)
        {
            return null;
        }

        var stamp = line.AsSpan(0, bar).Trim();
        if (stamp.Length > 24 && DateTimeOffset.TryParse(
                stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
        {
            return withOffset.ToUniversalTime();
        }

        if (!DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return localZone.IsInvalidTime(local)
            ? null
            : new DateTimeOffset(local, localZone.GetUtcOffset(local)).ToUniversalTime();
    }
}
