using System.Collections.Concurrent;

namespace TarkovCompanion.GroupServer;

/// <summary>
/// Holds what each room's members last said about themselves.
/// </summary>
/// <remarks>
/// Entirely in memory and deliberately so. This exists to let a handful of friends see each
/// other during an evening, not to build a history of where any of them have been. Nothing is
/// written to disk, a member's state is discarded when they stop publishing, and restarting
/// the server forgets everyone. Keeping a record would be easy and is the thing worth not
/// doing.
///
/// A room is identified by a name and a secret the group agrees between themselves. That is
/// the whole access model: it suits a group of friends and it is not an account system, which
/// is stated plainly rather than implied so nobody mistakes it for one.
/// </remarks>
public sealed class GroupRooms(TimeProvider timeProvider)
{
    /// <summary>How long a member is shown after they last published.</summary>
    /// <remarks>
    /// Long enough to survive a slow raid load or a brief disconnection, short enough that
    /// somebody who has closed their companion stops appearing to be in a raid. A stale marker
    /// on a map is worse than a missing one, because it looks current.
    /// </remarks>
    private static readonly TimeSpan MemberLifetime = TimeSpan.FromMinutes(3);

    /// <summary>How many members one room may hold.</summary>
    /// <remarks>
    /// A party is five. Sixteen leaves room for a group that reshuffles mid-evening without
    /// leaving a room able to grow without limit on a 1 GB box.
    /// </remarks>
    public const int MaximumMembersPerRoom = 16;

    /// <summary>How many rooms this relay will hold at once.</summary>
    /// <remarks>
    /// Rooms were GetOrAdd-only and never removed, so every key anybody ever sent — including
    /// every key a guesser tried — left an entry behind for the life of the process. This is
    /// far above any real use of a relay shared by a handful of friends and far below what it
    /// takes to exhaust the machine.
    /// </remarks>
    public const int MaximumRooms = 512;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Entry>> _rooms =
        new(StringComparer.Ordinal);

    private sealed record Entry(GroupMemberState State, DateTimeOffset PublishedUtc);

    /// <summary>Records what one member says about themselves, replacing what they said before.</summary>
    public void Publish(string room, string memberKey, GroupMemberState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberKey);
        ArgumentNullException.ThrowIfNull(state);
        if (!_rooms.TryGetValue(room, out var members))
        {
            // Checked before the room is created, so a guesser walking the key space cannot
            // add an entry per attempt. An existing room is never refused.
            if (_rooms.Count >= MaximumRooms)
            {
                return;
            }

            members = _rooms.GetOrAdd(room, _ => new(StringComparer.Ordinal));
        }

        // A member replacing their own entry is always accepted; only a new one is capped, so
        // a full room keeps working for the people already in it.
        if (members.Count >= MaximumMembersPerRoom && !members.ContainsKey(memberKey))
        {
            return;
        }

        // Observations are pruned on the way in as well as on the way out. A five-man filled
        // from LFG describes the random's nickname and kit to the client, and nothing stopped
        // that reaching the room, where anyone with the key could read it. SAFETY.md says
        // other players' log data is never transmitted; the exception it records covers the
        // people who are in the room, and nobody else.
        members[memberKey] = new(PruneObserved(state, members.Keys, memberKey), timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Keeps only the observations that describe somebody in this room.
    /// </summary>
    /// <remarks>
    /// Matched on the name, case-insensitively, which is the same rule the client already uses
    /// to hand a player their own kit back: the logs carry an in-game nickname and the relay
    /// carries a typed display name, they are the same string for most people, and there is no
    /// better key on either side.
    ///
    /// Done on the server because the server half is the one that holds. The friend's WPF
    /// client publishes to this relay too, and a rule enforced only in our client would not
    /// apply to it.
    /// </remarks>
    private static GroupMemberState PruneObserved(
        GroupMemberState state,
        IEnumerable<string> memberKeys,
        string publisherKey)
    {
        if (state.Observed.Count == 0)
        {
            return state;
        }

        var present = new HashSet<string>(memberKeys, StringComparer.OrdinalIgnoreCase) { publisherKey };
        var kept = state.Observed.Where(observed => present.Contains(observed.Name.Trim())).ToArray();
        return kept.Length == state.Observed.Count ? state : state with { Observed = kept };
    }

    /// <summary>Everyone else in the room who has published recently.</summary>
    /// <remarks>
    /// The asker is left out because they already know where they are, and including them
    /// would put two markers on their own position.
    /// </remarks>
    public GroupRoomState Read(string room, string exceptMemberKey)
    {
        var now = timeProvider.GetUtcNow();
        if (!_rooms.TryGetValue(room, out var members))
        {
            return new(room, [], now);
        }

        var live = new List<GroupMemberState>();
        foreach (var (key, entry) in members)
        {
            if (now - entry.PublishedUtc > MemberLifetime)
            {
                members.TryRemove(key, out _);
                continue;
            }

            if (!string.Equals(key, exceptMemberKey, StringComparison.Ordinal))
            {
                live.Add(entry.State);
            }
        }

        // Pruned again on the way out, because somebody described at publish time may have
        // left the room since, and the entry that described them is kept until its own
        // lifetime expires.
        var present = members.Keys.ToArray();
        return new(
            room,
            live
                .Select(member => PruneObserved(member, present, member.Name))
                .OrderBy(member => member.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            now);
    }

    /// <summary>
    /// Drops members that have stopped publishing, and rooms that are then empty.
    /// </summary>
    /// <remarks>
    /// Expiry used to happen only when a room was read, so a room nobody reads is a room that
    /// never forgets anything — and an empty room was never removed at all. Called on a timer
    /// so the cost of a room somebody abandoned is bounded by time rather than by whether
    /// anybody happens to look at it.
    ///
    /// Returns how many rooms it removed, which is the only number worth logging.
    /// </remarks>
    public int Sweep()
    {
        var now = timeProvider.GetUtcNow();
        var removed = 0;
        foreach (var (room, members) in _rooms)
        {
            foreach (var (key, entry) in members)
            {
                if (now - entry.PublishedUtc > MemberLifetime)
                {
                    members.TryRemove(key, out _);
                }
            }

            // Only when it is still empty at the moment of removal, so a member joining
            // between the two checks is not thrown away with the room.
            if (members.IsEmpty && _rooms.TryRemove(new(room, members)))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>How many rooms are held right now, for /health to report.</summary>
    public int RoomCount => _rooms.Count;

    /// <summary>How many members are held across every room.</summary>
    public int MemberCount => _rooms.Values.Sum(members => members.Count);

    /// <summary>Which rooms hold members right now, and how many each holds.</summary>
    /// <remarks>
    /// Counts against room hashes: no names, no positions, nothing about who. It exists so an
    /// operator can see that a room is in use, which is the only way to tell a group that was
    /// meant to be here from one that invented a key — and the only way to adopt the first kind
    /// without asking everybody for their key.
    /// </remarks>
    public IReadOnlyDictionary<string, int> Occupancy()
    {
        var now = timeProvider.GetUtcNow();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (room, members) in _rooms)
        {
            var live = members.Values.Count(entry => now - entry.PublishedUtc <= MemberLifetime);
            if (live > 0)
            {
                counts[room] = live;
            }
        }

        return counts;
    }

    /// <summary>Forgets every member of a room, for when it stops being allowed.</summary>
    public void Clear(string room) => _rooms.TryRemove(room, out _);

    /// <summary>Forgets a member immediately, for when they say they are leaving.</summary>
    public void Remove(string room, string memberKey)
    {
        if (_rooms.TryGetValue(room, out var members))
        {
            members.TryRemove(memberKey, out _);
        }
    }
}
