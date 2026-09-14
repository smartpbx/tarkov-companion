using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>One squadmate's reading of the extract screen, and why it was accepted.</summary>
/// <param name="From">Whose screenshot it came from, which is said on screen.</param>
/// <param name="Extracts">The exits their scan named.</param>
/// <param name="Transits">The transits their scan named.</param>
/// <param name="RaidClock">What their scan read the clock as, aged to now.</param>
public sealed record SharedExtractReading(
    string From,
    IReadOnlyList<string> Extracts,
    IReadOnlyList<string> Transits,
    TimeSpan? RaidClock);

/// <summary>
/// Whether a squadmate's reading of the extract screen may be used as your own.
/// </summary>
/// <remarks>
/// <para>
/// One player photographs the extract list and gains the offered exits, the transits and the
/// raid clock. The other four see ten possible exits and "counted from the raid's start", which
/// is the difference between knowing where you are leaving from and guessing.
/// </para>
/// <para>
/// This is the riskiest sharing in the application, and it is risky for a reason worth stating:
/// <b>"the offered exits are the same for everybody in a PMC party" is game knowledge, not
/// something the research documents measured.</b> Everything here is built so that being wrong
/// about it costs as little as possible — same map, same side, only when the reader has nothing
/// better, and always labelled with whose screenshot it came from.
/// </para>
/// <para>
/// A scav is excluded at both ends. Their exit list genuinely differs, so a scav publishes none
/// and a scav accepts none.
/// </para>
/// </remarks>
public static class SharedExtracts
{
    /// <summary>
    /// How old a shared reading may be before it is not worth having.
    /// </summary>
    /// <remarks>
    /// Exits do not change during a raid, but a reading from twenty minutes ago sat beside a
    /// clock it was read with — and the clock does change. Rather than accept the exits and
    /// refuse the clock, both go: an exit list somebody photographed at the start of the raid
    /// is still the right list, and a reader who has waited this long has almost certainly
    /// seen the screen themselves.
    /// </remarks>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The best squadmate reading to use, or nothing if the reader should keep their own.
    /// </summary>
    /// <param name="raid">The reader's own raid, including any scan of their own.</param>
    /// <param name="members">Everybody else in the room.</param>
    public static SharedExtractReading? Choose(RaidSnapshot raid, IReadOnlyList<GroupMemberView> members)
    {
        ArgumentNullException.ThrowIfNull(raid);
        ArgumentNullException.ThrowIfNull(members);

        // Their own scan always wins. A reading of their own screen is about their own raid;
        // everything below is an inference from somebody else's.
        if (raid.ActiveExtracts.Count > 0 ||
            raid.State != RaidLifecycleState.InRaid ||
            raid.MapId is not { Length: > 0 } mapId ||
            IsScav(raid.Side))
        {
            return null;
        }

        SharedExtractReading? best = null;
        var freshest = TimeSpan.MaxValue;
        foreach (var member in members)
        {
            if (member.Extracts.Count == 0 ||
                // Same map and same side, checked again here rather than trusted from the
                // sender. A member on Customs telling somebody on Woods where the exits are is
                // the failure this is most likely to produce, and it would look convincing.
                !string.Equals(member.MapId, mapId, StringComparison.OrdinalIgnoreCase) ||
                !SameSide(member.Side, raid.Side) ||
                IsScav(member.Side))
            {
                continue;
            }

            // Without an age there is no way to prefer one reading over another or to say how
            // old the clock is, and an unlabelled clock is the thing this is careful about.
            var age = member.RaidClockAge ?? member.PositionAge;
            if (age is not { } since || since > StaleAfter || since >= freshest)
            {
                continue;
            }

            freshest = since;
            best = new(
                member.Name,
                member.Extracts,
                member.Transits,
                // Aged forward from when they read it. Their clock said thirty minutes four
                // minutes ago, so it says twenty-six now — publishing what they read without
                // ageing it would present a stale number as current.
                member.RaidClock is { } clock
                    ? clock - since > TimeSpan.Zero ? clock - since : TimeSpan.Zero
                    : null);
        }

        return best;
    }

    /// <summary>Whether two sides are the same, treating an unstated one as a mismatch.</summary>
    /// <remarks>
    /// Strict in the direction that costs nothing. Two PMCs match; a PMC and an unknown do not,
    /// because accepting an unknown is accepting a guess about the one thing that decides
    /// whether the list applies at all.
    /// </remarks>
    private static bool SameSide(string? theirs, string? mine) =>
        theirs is { Length: > 0 } &&
        mine is { Length: > 0 } &&
        string.Equals(theirs, mine, StringComparison.OrdinalIgnoreCase);

    private static bool IsScav(string? side) => string.Equals(side, "scav", StringComparison.OrdinalIgnoreCase);
}
