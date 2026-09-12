using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// Receives group notifications as the log watcher reads them.
/// </summary>
/// <remarks>
/// Separate from the raid evidence stream because these describe the party rather than the
/// raid, and because the volume is completely different: a readiness toggle republishes every
/// member with their whole inventory, so these arrive in bursts of hundreds while raid
/// evidence arrives a handful of times a raid.
/// </remarks>
public interface IGroupObservationSink
{
    void Observe(GroupObservation observation);
}

/// <summary>
/// Keeps the player's current party, collapsed from the stream of restatements.
/// </summary>
/// <remarks>
/// <see cref="GroupNotificationParser"/> states plainly that its output has to be debounced
/// into one entry per member, and this is where that happens. Everything is keyed by
/// <see cref="GroupMember.Key"/>, so a member who toggles ready two hundred times is one row
/// that changes rather than two hundred rows.
///
/// A notification usually restates only part of a member, so a later one that omits a field
/// must not erase what an earlier, fuller one established. Fields are therefore merged
/// forward: a stated value replaces, and an unstated one leaves the previous value alone.
/// Without that, a compact notification would blank out the party's levels and sides.
/// </remarks>
public sealed class SquadStateService : IGroupObservationSink
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, GroupMember> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DogtagObservation> _dogtags = new(StringComparer.Ordinal);
    private DateTimeOffset? _matchStartedUtc;
    private TimeSpan? _queueEstimate;
    private DateTimeOffset _updatedUtc = DateTimeOffset.UnixEpoch;

    public SquadSnapshot Current { get; private set; } = SquadSnapshot.Empty;

    public void Observe(GroupObservation observation) => Apply(observation);

    public SquadSnapshot Apply(GroupObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_gate)
        {
            switch (observation.Kind)
            {
                case GroupObservationKind.MemberUpdated when observation.Member is { } updated:
                    _members[updated.Key] = _members.TryGetValue(updated.Key, out var known)
                        ? Merge(known, updated)
                        : updated;
                    break;
                case GroupObservationKind.MemberLeft when observation.Member is { } departed:
                    _members.Remove(departed.Key);
                    break;
                case GroupObservationKind.MatchStarting:
                    _matchStartedUtc = observation.ObservedUtc;
                    _queueEstimate = observation.QueueEstimate;
                    break;
                default:
                    return Current;
            }

            _updatedUtc = observation.ObservedUtc;
            return Rebuild();
        }
    }

    /// <summary>
    /// Records the dogtags the player is carrying.
    /// </summary>
    /// <remarks>
    /// Only ever called with the player's own inventory. A squadmate's dogtags describe people
    /// the player never met, which docs/SAFETY.md places out of bounds, and the parser keeps
    /// dogtag reading on a separate entry point precisely so this stays a deliberate act.
    /// </remarks>
    public SquadSnapshot ApplyDogtags(IReadOnlyList<DogtagObservation> dogtags, DateTimeOffset observedUtc)
    {
        ArgumentNullException.ThrowIfNull(dogtags);
        lock (_gate)
        {
            foreach (var dogtag in dogtags)
            {
                _dogtags[DogtagKey(dogtag)] = dogtag;
            }

            _updatedUtc = observedUtc.ToUniversalTime();
            return Rebuild();
        }
    }

    /// <summary>
    /// Forgets the party, for when the companion stops knowing who is in it.
    /// </summary>
    /// <remarks>
    /// Not called at the end of a raid: a party in this game survives the raid it was formed
    /// for, and clearing it there would empty the list of people who are still standing in the
    /// lobby. It is called when observation restarts on a new game session, where the previous
    /// party is genuinely unknown rather than merely between raids.
    /// </remarks>
    public SquadSnapshot Clear()
    {
        lock (_gate)
        {
            _members.Clear();
            _dogtags.Clear();
            _matchStartedUtc = null;
            _queueEstimate = null;
            _updatedUtc = DateTimeOffset.UnixEpoch;
            Current = SquadSnapshot.Empty;
            return Current;
        }
    }

    /// <summary>
    /// Keys a dogtag so the same tag restated is one row.
    /// </summary>
    /// <remarks>
    /// The inventory item id is the natural key and is preferred. When the game did not state
    /// one, the victim and the moment they died identify the tag well enough: two tags for the
    /// same player killed at the same instant are the same tag.
    /// </remarks>
    private static string DogtagKey(DogtagObservation dogtag) => dogtag.SourceItemId is { } itemId
        ? "item:" + itemId
        : $"kill:{dogtag.VictimNickname}@{dogtag.KilledAt?.ToUnixTimeSeconds()}";

    /// <summary>Keeps what an earlier, fuller notification established.</summary>
    private static GroupMember Merge(GroupMember known, GroupMember latest) => latest with
    {
        MemberId = latest.MemberId ?? known.MemberId,
        AccountId = latest.AccountId ?? known.AccountId,
        Nickname = latest.Nickname ?? known.Nickname,
        Side = latest.Side ?? known.Side,
        Level = latest.Level ?? known.Level,
        IsLeader = latest.IsLeader ?? known.IsLeader,
        IsReady = latest.IsReady ?? known.IsReady,
        ScavLockedUntil = latest.ScavLockedUntil ?? known.ScavLockedUntil,
        Equipment = latest.Equipment.Count > 0 ? latest.Equipment : known.Equipment,
    };

    private SquadSnapshot Rebuild()
    {
        Current = new(
            _members.Values
                .OrderByDescending(member => member.IsLeader == true)
                .ThenBy(member => member.Nickname ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            _dogtags.Values
                .OrderByDescending(dogtag => dogtag.KilledAt ?? DateTimeOffset.MinValue)
                .ToArray(),
            _matchStartedUtc,
            _queueEstimate,
            _updatedUtc);
        return Current;
    }
}
