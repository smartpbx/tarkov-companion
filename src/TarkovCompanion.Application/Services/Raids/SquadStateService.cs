using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// Receives the notifications that are not about the raid, as the watcher reads them.
/// </summary>
/// <remarks>
/// Separate from the raid evidence stream because these describe other things entirely, and
/// because the volume is completely different: a readiness toggle republishes every party
/// member with their whole inventory, so those arrive in bursts of hundreds while raid
/// evidence arrives a handful of times a raid.
///
/// One interface with an overload per observation rather than one interface each, so that the
/// watcher keeps a single collaborator as more notification types are recognised.
/// </remarks>
public interface IEftLogObserver
{
    void Observe(GroupObservation observation);

    void Observe(FleaSaleObservation sale);

    /// <summary>A quest the game says has started, failed or been handed in.</summary>
    void Observe(QuestStatusObservation quest);

    /// <summary>How long matchmaking took, ahead of the raid it belongs to.</summary>
    void Observe(LoadTimeObservation loadTime);

    /// <summary>[#712 0-2] Matching, matched, map loaded or game started, for the situation (ADR 0022).</summary>
    void Observe(RaidPhaseMarker marker)
    {
    }

    /// <summary>[#712 decision 4] The game's <c>Session mode:</c> line, which the active profile follows.</summary>
    void Observe(Core.Domain.Profiles.GameSessionMode mode)
    {
    }
}

/// <summary>Routes each kind of observation to the service that keeps it.</summary>
/// <remarks>
/// Also the one place that knows a quest hand-in and a flea sale happened at all, which is why
/// the raid record is written from here. RaidActivityCoordinator wrote position, scan, state and
/// extracts and nothing else, so a raid's own record was thinner than what the game had said
/// during it — the quests were applied to progress and the sales were kept for the session, and
/// neither was ever tied to the raid they happened in.
/// </remarks>
public sealed class EftLogObservers(
    SquadStateService squad,
    FleaSaleStateService flea,
    // Optional so a composition without quest storage is still a valid composition, which is
    // what the tests that build this by hand rely on.
    QuestLogProgressService? quests = null,
    // Optional for the same reason. Without it the observations still reach the services that
    // keep them; what is lost is the raid they belonged to.
    IRaidActivityRecorder? raid = null,
    // [#712 0-2] Optional like the rest; without it nothing tells matching from loading.
    Situations.SituationService? situation = null,
    // [#712 decision 4] Optional like the rest; without it the profile stays where the player put it.
    Profiles.ProfileModeFollower? profileMode = null) : IEftLogObserver
{
    public void Observe(RaidPhaseMarker marker) => situation?.Observe(marker);

    /// <summary>Not awaited, like the quest and sale writes: the watcher is reading a file and must not stop.</summary>
    public void Observe(Core.Domain.Profiles.GameSessionMode mode) =>
        _ = profileMode?.ObserveAsync(mode, CancellationToken.None);

    public void Observe(GroupObservation observation) => squad.Apply(observation);

    public void Observe(FleaSaleObservation sale)
    {
        // Every sale is written to backend and to output, so each arrives twice. Only the first
        // reaches the raid record, whose Debrief rows add counts up and would show it twice.
        if (!flea.TryApply(sale))
        {
            return;
        }

        // Only while a raid is open, which the recorder decides. A sale made in the menu
        // belongs to no raid, and attaching it to the last one would put it in a record of
        // something that had already finished.
        _ = raid?.RecordSaleAsync(sale, CancellationToken.None);
    }

    /// <summary>
    /// Recorded without waiting, because the watcher is reading a file and must not stop.
    /// </summary>
    /// <remarks>
    /// The service takes a gate of its own, so two lines arriving together are applied one
    /// after the other rather than racing each other into the database.
    /// </remarks>
    public void Observe(QuestStatusObservation quest)
    {
        _ = quests?.ApplyAsync(quest, CancellationToken.None);
        _ = raid?.RecordQuestAsync(quest, CancellationToken.None);
    }

    /// <summary>
    /// Held rather than applied immediately: matchmaking finishes before the raid it timed has
    /// an id, so the coordinator keeps this until that raid starts.
    /// </summary>
    public void Observe(LoadTimeObservation loadTime) =>
        _ = raid?.RecordLoadTimeAsync(loadTime, CancellationToken.None);
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
public sealed class SquadStateService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, GroupMember> _members = new(StringComparer.Ordinal);
    private DateTimeOffset? _matchStartedUtc;
    private TimeSpan? _queueEstimate;
    private DateTimeOffset _updatedUtc = DateTimeOffset.UnixEpoch;

    public SquadSnapshot Current { get; private set; } = SquadSnapshot.Empty;

    public SquadSnapshot Apply(GroupObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_gate)
        {
            switch (observation.Kind)
            {
                case GroupObservationKind.MemberUpdated when observation.Member is { } updated:
                    if (_members.TryGetValue(updated.Key, out var known))
                    {
                        _members[updated.Key] = Merge(known, updated);
                    }
                    else if (updated.Nickname is null)
                    {
                        // A not-ready notification names only an account id. For somebody not yet
                        // seen it would add a nameless row; and it could only be the player's own
                        // toggle or a member whose ready line was missed, neither worth a row.
                        return Current;
                    }
                    else
                    {
                        _members[updated.Key] = updated;
                    }

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
            _matchStartedUtc = null;
            _queueEstimate = null;
            _updatedUtc = DateTimeOffset.UnixEpoch;
            Current = SquadSnapshot.Empty;
            return Current;
        }
    }

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
            _matchStartedUtc,
            _queueEstimate,
            _updatedUtc);
        return Current;
    }
}
