using System.Globalization;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>One piece of raid evidence read back from a file that was already written.</summary>
/// <param name="Evidence">What the parser made of the line.</param>
/// <param name="WrittenUtc">When the game wrote it, from the line's own stamp, where it had one.</param>
/// <param name="Order">Where it came in the replay: its place within its file, and the tie-break between files.</param>
public sealed record ReplayedRaidLine(RaidEvidence Evidence, DateTimeOffset? WrittenUtc, long Order)
{
    /// <summary>The file it was read from. Lines of one file are taken in the order written.</summary>
    public string? Source { get; init; }

    /// <summary>
    /// Which stretch of its file between backward clock steps this line is in, counted back from the
    /// file's end over every line of the file: 0 is the last stretch, -1 the one before it (see
    /// <see cref="RaidReplayDecision.IsClockStep"/> and <see cref="RaidReplayDecision.CountFromEnd"/>).
    /// Null leaves it to be counted over the replayed lines alone, which are sparse enough to miss a step.
    /// </summary>
    public int? Stretch { get; init; }
}

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

    /// <summary>How far a file's stamps may run backwards before the clock is taken to have stepped.</summary>
    private static readonly TimeSpan ClockStep = TimeSpan.FromMinutes(5);

    public static RaidReplayVerdict Decide(
        IReadOnlyList<ReplayedRaidLine> newestSession,
        bool gameIsRunning,
        DateTimeOffset nowUtc,
        TimeSpan? longestRaid = null)
    {
        ArgumentNullException.ThrowIfNull(newestSession);
        ReplayedRaidLine? open = null;
        // The last scene the game loaded, which names the map of a raid that has no id (#892).
        ReplayedRaidLine? loading = null;
        DateTimeOffset? lastSeen = null;
        var lastEpoch = int.MinValue;
        foreach (var (line, epoch) in InWrittenOrder(newestSession))
        {
            if (line.WrittenUtc is { } written
                && (epoch > lastEpoch || lastSeen is null || written > lastSeen))
            {
                lastSeen = written;
                lastEpoch = epoch;
            }

            var evidence = line.Evidence;
            switch (evidence.SuggestedState)
            {
                // A new scene with another map is another raid, whatever the last one left behind.
                case RaidLifecycleState.LoadingRaid when evidence.MapId is not null:
                    loading = line;
                    if (open is not null && open.Evidence.MapId is { } openMap
                        && !string.Equals(openMap, evidence.MapId, StringComparison.OrdinalIgnoreCase))
                    {
                        open = null;
                    }

                    break;
                // A bare GameStarted names no raid and no map, so it continues a raid and never
                // begins one: whatever wrote it after the last raid ended is not a raid to resume.
                // Unless a scene was loaded for it, which is all an offline raid ever writes.
                case RaidLifecycleState.InRaid
                    when open is not null || evidence.MapId is not null
                        || evidence.RaidKey is not null || evidence.StartsNewRaid || loading is not null:
                    open = open is null && loading is not null && evidence.MapId is null
                        ? line with { Evidence = evidence with { MapId = loading.Evidence.MapId } }
                        : Begin(open, line);
                    loading = null;
                    break;
                case RaidLifecycleState.PostRaid or RaidLifecycleState.Menu:
                    loading = null;
                    // The end of another raid is not the end of this one, and a profile reload
                    // ends only a raid with no id.
                    if (open is not null && !RaidIdentity.DifferentRaid(open.Evidence.RaidKey, evidence.RaidKey)
                        && !(evidence.EndsOnlyARaidWithoutId && open.Evidence.RaidKey is not null))
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
        if (open.WrittenUtc is { } started && nowUtc - started > bound)
        {
            return new(open.Evidence, started, lastSeen, false,
                $"{where} was never reported over, but it began longer ago than any raid lasts.");
        }

        if (open.WrittenUtc is { } ahead && ahead - nowUtc > ClockSlack)
        {
            return new(open.Evidence, ahead, lastSeen, false,
                $"{where} was never reported over, but it is stamped later than now, so the clock has moved since.");
        }

        return new(open.Evidence, open.WrittenUtc, lastSeen, true, $"{where} is still running.");
    }

    /// <summary>
    /// Puts the files back together in the order the game wrote them, with the clock step each line came after.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to sort every line by its stamp. The stamp is the PC's clock, and on 2026-09-22 that
    /// clock stepped back four hours in the middle of a game session (Windows resynchronising). Sorted
    /// by stamp, the Lighthouse raid that began after the step came before the Shoreline raid that had
    /// ended before it, so the Shoreline end closed it, and a companion restarted in that Lighthouse
    /// raid spent its last 23 minutes on no map (#892).
    /// </para>
    /// <para>
    /// A file is appended in real time, so its own order is right whatever the clock did. Stamps are
    /// used only to interleave the files, and only within one stretch between steps: every file of a
    /// session steps at the same moment (measured: application, backend and output all stepped once,
    /// together, in both sessions that stepped), so each file's k-th stretch from the end is the same
    /// stretch of time.
    /// </para>
    /// </remarks>
    private static IEnumerable<(ReplayedRaidLine Line, int Epoch)> InWrittenOrder(IReadOnlyList<ReplayedRaidLine> lines)
    {
        var files = lines
            .GroupBy(line => line.Source ?? string.Empty, StringComparer.Ordinal)
            .Select(file => Stretches(file.OrderBy(line => line.Order)).ToArray())
            .ToArray();
        var next = new int[files.Length];
        while (true)
        {
            var pick = -1;
            for (var index = 0; index < files.Length; index++)
            {
                if (next[index] < files[index].Length
                    && (pick < 0 || Earlier(files[index][next[index]], files[pick][next[pick]])))
                {
                    pick = index;
                }
            }

            if (pick < 0)
            {
                yield break;
            }

            var chosen = files[pick][next[pick]++];
            yield return (chosen.Line, chosen.Epoch);
        }

        static bool Earlier(
            (ReplayedRaidLine Line, int Epoch, DateTimeOffset At) candidate,
            (ReplayedRaidLine Line, int Epoch, DateTimeOffset At) best) =>
            candidate.Epoch != best.Epoch
                ? candidate.Epoch < best.Epoch
                : candidate.At != best.At ? candidate.At < best.At : candidate.Line.Order < best.Line.Order;
    }

    /// <summary>
    /// Renumbers the stretches of the lines one file added, <paramref name="firstOfFile"/> onwards,
    /// from the stretch count the whole file reached, so that 0 is the file's last stretch.
    /// </summary>
    /// <remarks>
    /// Rebased on the file's final count, not on its last replayed line (#937): when a file's last
    /// evidence line comes before a step and only keepalives follow it, rebasing on that line moved
    /// all of the file's lines one stretch too late, and a Shoreline end stamped four hours fast then
    /// sorted after, and closed, a Lighthouse raid that began after the step.
    /// </remarks>
    public static void CountFromEnd(IList<ReplayedRaidLine> replayed, int firstOfFile, int stretchesInFile)
    {
        for (var index = firstOfFile; index < replayed.Count; index++)
        {
            replayed[index] = replayed[index] with { Stretch = (replayed[index].Stretch ?? 0) - stretchesInFile };
        }
    }

    /// <summary>Whether a file's stamps going from <paramref name="before"/> to <paramref name="after"/> means its clock stepped back.</summary>
    public static bool IsClockStep(DateTimeOffset before, DateTimeOffset after) => before - after > ClockStep;

    /// <summary>
    /// Numbers the stretches of one file between backward clock steps, counting back from its end;
    /// an unstamped line sits with the line before it.
    /// </summary>
    /// <remarks>
    /// Counted from the end because every file runs up to the moment the companion reads it, while
    /// their beginnings differ: a replay reads only the tail of a large file, and that tail can begin
    /// after the step.
    /// </remarks>
    private static IEnumerable<(ReplayedRaidLine Line, int Epoch, DateTimeOffset At)> Stretches(IEnumerable<ReplayedRaidLine> file)
    {
        var counted = new List<(ReplayedRaidLine Line, int Epoch, DateTimeOffset At)>();
        var epoch = 0;
        DateTimeOffset? previous = null;
        foreach (var line in file)
        {
            if (line.WrittenUtc is { } written)
            {
                if (previous is { } before && IsClockStep(before, written))
                {
                    epoch++;
                }

                previous = written;
            }

            counted.Add((line, epoch, previous ?? DateTimeOffset.MinValue));
        }

        // A supplied stretch is already counted back from the file's end; only the ones counted
        // here, over the replayed lines alone, are rebased on the last of them.
        var last = counted.Count > 0 ? counted[^1].Epoch : 0;
        return counted.Select(entry => entry with { Epoch = entry.Line.Stretch ?? entry.Epoch - last });
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
