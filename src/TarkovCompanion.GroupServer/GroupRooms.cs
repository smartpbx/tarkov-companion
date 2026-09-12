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

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Entry>> _rooms =
        new(StringComparer.Ordinal);

    private sealed record Entry(GroupMemberState State, DateTimeOffset PublishedUtc);

    /// <summary>Records what one member says about themselves, replacing what they said before.</summary>
    public void Publish(string room, string memberKey, GroupMemberState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberKey);
        ArgumentNullException.ThrowIfNull(state);
        var members = _rooms.GetOrAdd(room, _ => new(StringComparer.Ordinal));
        members[memberKey] = new(state, timeProvider.GetUtcNow());
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

        return new(room, live.OrderBy(member => member.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), now);
    }

    /// <summary>Forgets a member immediately, for when they say they are leaving.</summary>
    public void Remove(string room, string memberKey)
    {
        if (_rooms.TryGetValue(room, out var members))
        {
            members.TryRemove(memberKey, out _);
        }
    }
}
