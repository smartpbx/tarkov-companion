using System.Collections.Concurrent;

namespace TarkovCompanion.GroupServer;

/// <summary>
/// [#920] What the relay owner has taken out of a room and has to keep out: members they removed,
/// and lines they cleared.
/// </summary>
/// <remarks>
/// Needed because a member's client republishes its whole state every few seconds. Forgetting a
/// member, or blanking their lines, lasted exactly until their next exchange, so both have to be
/// remembered here and applied to every publish that follows.
///
/// A cleared line is kept off by its id. The drawer's client goes on sending that id until the
/// line expires on their machine — every client does, including ones from before this existed —
/// and the relay drops it on the way in, so the squad stops seeing it on their next exchange.
/// A new line has a new id and shows as normal. The ids held for a member shrink to the ones they
/// are still sending, so the set never outgrows one member's twenty lines.
///
/// A removal is a kick, not a ban. It holds until the member leaves (<c>DELETE /state/{name}</c>,
/// which is what turning sharing off does) or <see cref="RemovalLifetime"/> passes, whichever is
/// first; after that the same name may join again. Everything is in memory, like the rooms.
/// </remarks>
public sealed class GroupRoomModeration(TimeProvider timeProvider)
{
    /// <summary>How long a removal holds when the member never leaves.</summary>
    public static readonly TimeSpan RemovalLifetime = TimeSpan.FromMinutes(30);

    /// <summary>How long cleared ids are kept for a member who stopped publishing.</summary>
    private static readonly TimeSpan SuppressionIdle = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);

    private sealed class Room
    {
        public Dictionary<string, DateTimeOffset> Removed { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Suppressed> Cleared { get; } = new(StringComparer.Ordinal);

        /// <summary>Set under the lock when the sweep takes it off the map, so a writer racing it retries.</summary>
        public bool Retired { get; set; }
    }

    private sealed record Suppressed(HashSet<string> Ids, DateTimeOffset TouchedUtc);

    /// <summary>Removes a member until they leave or the removal lapses.</summary>
    /// <returns>False when the room already holds as many removals as it can hold members.</returns>
    public bool Remove(string room, string member)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        if (!_rooms.ContainsKey(room) && _rooms.Count >= GroupRooms.MaximumRooms)
        {
            return false;
        }

        var entry = _rooms.GetOrAdd(room, _ => new Room());
        lock (entry)
        {
            if (entry.Retired)
            {
                return Remove(room, member);
            }

            var now = timeProvider.GetUtcNow();
            Prune(entry, now);
            if (!entry.Removed.ContainsKey(member) && entry.Removed.Count >= GroupRooms.MaximumMembersPerRoom)
            {
                return false;
            }

            entry.Removed[member] = now + RemovalLifetime;
            entry.Cleared.Remove(member);
            return true;
        }
    }

    /// <summary>Whether a member is removed and their publishes are refused.</summary>
    public bool IsRemoved(string room, string member)
    {
        if (!_rooms.TryGetValue(room, out var entry))
        {
            return false;
        }

        lock (entry)
        {
            return entry.Removed.TryGetValue(member, out var until) && until > timeProvider.GetUtcNow();
        }
    }

    /// <summary>A member left the room themselves, which is how a removed member rejoins.</summary>
    public void Left(string room, string member)
    {
        if (!_rooms.TryGetValue(room, out var entry))
        {
            return;
        }

        lock (entry)
        {
            entry.Removed.Remove(member);
            entry.Cleared.Remove(member);
        }
    }

    /// <summary>Names removed from a room right now.</summary>
    public IReadOnlyList<string> RemovedFrom(string room)
    {
        if (!_rooms.TryGetValue(room, out var entry))
        {
            return [];
        }

        lock (entry)
        {
            Prune(entry, timeProvider.GetUtcNow());
            return [.. entry.Removed.Keys.Order(StringComparer.CurrentCultureIgnoreCase)];
        }
    }

    /// <summary>Keeps these line ids of one member's out of the room from now on.</summary>
    public void Clear(string room, string member, IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (!_rooms.ContainsKey(room) && _rooms.Count >= GroupRooms.MaximumRooms)
        {
            return;
        }

        var entry = _rooms.GetOrAdd(room, _ => new Room());
        lock (entry)
        {
            if (entry.Retired)
            {
                Clear(room, member, ids);
                return;
            }

            var now = timeProvider.GetUtcNow();
            if (!entry.Cleared.TryGetValue(member, out var held))
            {
                if (entry.Cleared.Count >= GroupRooms.MaximumMembersPerRoom)
                {
                    return;
                }

                held = new Suppressed(new HashSet<string>(StringComparer.Ordinal), now);
            }

            foreach (var id in ids.Take(GroupMemberState.MaximumDrawings))
            {
                if (held.Ids.Count < GroupMemberState.MaximumDrawings * 2)
                {
                    held.Ids.Add(id);
                }
            }

            entry.Cleared[member] = held with { TouchedUtc = now };
        }
    }

    /// <summary>A member's state with the lines the owner cleared taken out.</summary>
    public GroupMemberState Filter(string room, GroupMemberState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Drawings is not { Count: > 0 } drawings || !_rooms.TryGetValue(room, out var entry))
        {
            return state;
        }

        lock (entry)
        {
            if (!entry.Cleared.TryGetValue(state.Name, out var held))
            {
                return state;
            }

            // Only the ids still being sent are worth remembering; the rest have expired on the
            // drawer's own machine and will not come back.
            held.Ids.IntersectWith(drawings.Select(drawing => drawing.Id));
            if (held.Ids.Count == 0)
            {
                entry.Cleared.Remove(state.Name);
                return state;
            }

            entry.Cleared[state.Name] = held with { TouchedUtc = timeProvider.GetUtcNow() };
            var kept = drawings.Where(drawing => !held.Ids.Contains(drawing.Id)).ToArray();
            return state with { Drawings = kept.Length == 0 ? null : kept };
        }
    }

    /// <summary>Forgets everything held for a room: a reset starts it clean.</summary>
    public void Forget(string room)
    {
        if (_rooms.TryRemove(room, out var entry))
        {
            lock (entry)
            {
                entry.Retired = true;
            }
        }
    }

    /// <summary>Drops lapsed removals, idle cleared ids, and rooms left with neither.</summary>
    public void Sweep()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (room, entry) in _rooms)
        {
            lock (entry)
            {
                Prune(entry, now);
                if (entry.Removed.Count == 0 && entry.Cleared.Count == 0 &&
                    _rooms.TryRemove(new KeyValuePair<string, Room>(room, entry)))
                {
                    entry.Retired = true;
                }
            }
        }
    }

    private static void Prune(Room entry, DateTimeOffset now)
    {
        foreach (var (member, until) in entry.Removed.ToArray())
        {
            if (until <= now)
            {
                entry.Removed.Remove(member);
            }
        }

        foreach (var (member, held) in entry.Cleared.ToArray())
        {
            if (now - held.TouchedUtc > SuppressionIdle)
            {
                entry.Cleared.Remove(member);
            }
        }
    }
}
