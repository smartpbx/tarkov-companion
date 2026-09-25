using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.GroupServer;

/// <summary>A place somebody marked for the group, which stays until it is cleared.</summary>
/// <param name="Id">Assigned by the server, so two members cannot collide on one.</param>
/// <param name="By">The display name of whoever dropped it.</param>
/// <param name="CompletedBy">Who reached it, once somebody has.</param>
public sealed record GroupWaypoint(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("by")] string By,
    [property: JsonPropertyName("mapId")] string MapId,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc,
    [property: JsonPropertyName("completedUtc")] DateTimeOffset? CompletedUtc,
    [property: JsonPropertyName("completedBy")] string? CompletedBy,
    // #290: a palette colour the marker chose; left out of the JSON when there is none, so the
    // shape older clients and older marks.json files know is unchanged.
    [property: JsonPropertyName("color"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Color = null);

/// <summary>A place somebody is pointing at right now, which fades.</summary>
/// <remarks>
/// The difference from a waypoint is the whole point of having both. A waypoint is a plan; a
/// ping is "look here", and a plan that quietly became a pile of forty stale "look here" marks
/// would be worse than either.
/// </remarks>
public sealed record GroupPing(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("by")] string By,
    [property: JsonPropertyName("mapId")] string MapId,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc,
    [property: JsonPropertyName("color"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Color = null);

/// <summary>
/// The marks a group has put on its maps.
/// </summary>
/// <remarks>
/// Held per room, like everything else, and bounded so a group cannot fill the server by
/// leaning on a mouse button.
///
/// Waypoints outlive a restart and positions do not, and that distinction is deliberate rather
/// than an inconsistency. A published position or trail is a record of where somebody has
/// been, which this server holds only in memory. A waypoint is a thing somebody decided on
/// purpose, and losing the squad's plan because the relay updated itself at the wrong moment
/// would be its own small betrayal. The relay updates twice an hour, so "it only vanishes on a
/// restart" was never a rare event. What is written is not free of where people have been,
/// though: a reached waypoint keeps who reached it and when (see the constructor).
///
/// That paragraph was written in the same commit as the in-memory dictionary it sits on, and
/// nothing in this server touched the filesystem, so it described an intention rather than a
/// behaviour: every merge to main took the squad's plan with it. It is true now.
/// </remarks>
public sealed class GroupMarks
{
    /// <summary>How long a ping is shown before it stops meaning "now".</summary>
    /// <remarks>
    /// Issue 584: the one shared constant a desktop right-click, a paired tablet and a squadmate's
    /// ping all read now, rather than each place a ping can come from growing its own number.
    /// </remarks>
    private static readonly TimeSpan PingLifetime = MapMarkPolicy.PingLifetime;

    /// <summary>Enough for a plan, few enough that nobody can drown a map in them.</summary>
    private const int MaximumWaypointsPerRoom = 60;

    private const int MaximumPingsPerRoom = 30;

    /// <summary>How long a waypoint is kept across restarts before it is forgotten.</summary>
    /// <remarks>
    /// A plan is for tonight. Without a bound the file would only ever grow, and a wipe-old
    /// map's marks would outlive the wipe.
    /// </remarks>
    private static readonly TimeSpan WaypointLifetime = TimeSpan.FromDays(7);

    private readonly TimeProvider _timeProvider;
    private readonly string? _storePath;
    private readonly Lock _saveGate = new();
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private long _nextId;

    /// <summary>Raised by every change that should reach the file.</summary>
    private long _changes;

    /// <summary>The change the file on disk already holds; only touched under the save gate.</summary>
    private long _written;

    /// <param name="storePath">
    /// Where the squad's marks are kept, or null to hold them only in memory.
    /// </param>
    /// <remarks>
    /// Marks only. Live positions are never written here: the file holds places somebody chose.
    /// A reached waypoint does record who reached it and when, which is the one thing in it
    /// that says where somebody has been.
    /// </remarks>
    public GroupMarks(TimeProvider timeProvider, string? storePath = null)
    {
        _timeProvider = timeProvider;
        _storePath = storePath;
        Load();

        // #886: ids start at the clock, in milliseconds, rather than at the highest waypoint
        // that survived. Pings are never written and removed waypoints are gone, so the old
        // seed handed a ping's id out again after a restart, and the client removing that
        // expired ping deleted a squadmate's new waypoint. Nothing issues a mark a millisecond,
        // so a later start always numbers above everything an earlier one issued. Well inside
        // a double's exact range, which is what the tablet's JavaScript reads it as.
        _nextId = Math.Max(_nextId, timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
    }

    /// <summary>The most rooms that may hold marks, the same bound the live rooms have.</summary>
    public const int MaximumRooms = GroupRooms.MaximumRooms;

    private sealed class Room
    {
        public List<GroupWaypoint> Waypoints { get; } = [];

        public List<GroupPing> Pings { get; } = [];

        /// <summary>Set, under the room's lock, once the sweep has removed it from the map of rooms.</summary>
        public bool Retired { get; set; }
    }

    /// <summary>The room's marks, or null when it has none and no more rooms may be added.</summary>
    /// <remarks>
    /// #886: rooms were GetOrAdd-only, so against an open relay every new key left a room
    /// behind for good, and each one made every later save longer. Checked before the room is
    /// created, as GroupRooms does, so a refused key allocates nothing. An existing room is
    /// never refused.
    /// </remarks>
    private Room? RoomFor(string room) =>
        _rooms.TryGetValue(room, out var entry) ? entry
        : _rooms.Count >= MaximumRooms ? null
        : _rooms.GetOrAdd(room, _ => new());

    /// <summary>Adds a waypoint, or returns null when the relay already holds marks for as many rooms as it will.</summary>
    public GroupWaypoint? AddWaypoint(string room, string by, string mapId, double x, double y, double z, string? label, string? color = null)
    {
        var waypoint = new GroupWaypoint(
            Interlocked.Increment(ref _nextId), by, mapId, x, y, z, Trim(label),
            _timeProvider.GetUtcNow(), null, null, MarkPalette.Normalize(color));
        while (true)
        {
            if (RoomFor(room) is not { } entry)
            {
                return null;
            }

            lock (entry)
            {
                // The sweep removed this room between the lookup and the lock; the next lookup
                // makes a fresh one rather than adding to a room nothing can reach.
                if (entry.Retired)
                {
                    continue;
                }

                // Oldest first, so a group that keeps marking loses its stalest plan rather than
                // being told it cannot make a new one.
                if (entry.Waypoints.Count >= MaximumWaypointsPerRoom)
                {
                    entry.Waypoints.RemoveAt(0);
                }

                entry.Waypoints.Add(waypoint);
                break;
            }
        }

        Save();
        return waypoint;
    }

    /// <summary>Adds a ping, or returns null when the relay already holds marks for as many rooms as it will.</summary>
    public GroupPing? AddPing(string room, string by, string mapId, double x, double y, double z, string? label, string? color = null)
    {
        var ping = new GroupPing(
            Interlocked.Increment(ref _nextId), by, mapId, x, y, z, Trim(label), _timeProvider.GetUtcNow(), MarkPalette.Normalize(color));
        while (true)
        {
            if (RoomFor(room) is not { } entry)
            {
                return null;
            }

            lock (entry)
            {
                if (entry.Retired)
                {
                    continue;
                }

                entry.Pings.RemoveAll(Expired);
                if (entry.Pings.Count >= MaximumPingsPerRoom)
                {
                    entry.Pings.RemoveAt(0);
                }

                entry.Pings.Add(ping);
                break;
            }
        }

        return ping;
    }

    /// <summary>Marks one reached, recording who got there.</summary>
    public bool Complete(string room, long id, string by)
    {
        if (!_rooms.TryGetValue(room, out var entry))
        {
            return false;
        }

        lock (entry)
        {
            var index = entry.Waypoints.FindIndex(waypoint => waypoint.Id == id);
            if (index < 0 || entry.Waypoints[index].CompletedUtc is not null)
            {
                return false;
            }

            entry.Waypoints[index] = entry.Waypoints[index] with
            {
                CompletedUtc = _timeProvider.GetUtcNow(),
                CompletedBy = by,
            };
        }

        Save();
        return true;
    }

    /// <summary>Removes a waypoint or a ping, whichever the id names — a mark is a mark.</summary>
    /// <remarks>
    /// A ping otherwise only leaves by expiring, which cost the group the ability to say "never
    /// mind" about one it had just sent. Save() persists waypoints only (see its own remark);
    /// removing a ping early needs no extra durability; it was already going to disappear.
    /// </remarks>
    /// <param name="onlyBy">
    /// #886: when given, only a mark this name dropped is removed. A client taking back a mark it
    /// sent itself says so, and a stale id then cannot remove a squadmate's mark, whatever the
    /// relay numbered it. Left out, anybody may remove anybody's, as before.
    /// </param>
    public bool Remove(string room, long id, string? onlyBy = null)
    {
        if (!_rooms.TryGetValue(room, out var entry))
        {
            return false;
        }

        var ownerless = string.IsNullOrWhiteSpace(onlyBy);
        bool Owned(string by) => ownerless || string.Equals(by.Trim(), onlyBy!.Trim(), StringComparison.OrdinalIgnoreCase);

        bool removedWaypoint;
        bool removedPing;
        lock (entry)
        {
            removedWaypoint = entry.Waypoints.RemoveAll(waypoint => waypoint.Id == id && Owned(waypoint.By)) > 0;
            removedPing = !removedWaypoint && entry.Pings.RemoveAll(ping => ping.Id == id && Owned(ping.By)) > 0;
        }

        if (removedWaypoint)
        {
            Save();
        }

        return removedWaypoint || removedPing;
    }

    /// <summary>Clears a map's waypoints, or only the ones already reached.</summary>
    public int Clear(string room, string? mapId, bool reachedOnly)
    {
        if (!_rooms.TryGetValue(room, out var entry))
        {
            return 0;
        }

        int cleared;
        lock (entry)
        {
            cleared = entry.Waypoints.RemoveAll(waypoint =>
                (mapId is null || string.Equals(waypoint.MapId, mapId, StringComparison.OrdinalIgnoreCase))
                && (!reachedOnly || waypoint.CompletedUtc is not null));
        }

        if (cleared > 0)
        {
            Save();
        }

        return cleared;
    }

    /// <summary>Forgets every mark a room has, for a room an operator removed.</summary>
    public void ClearRoom(string room)
    {
        if (_rooms.TryRemove(room, out var entry))
        {
            bool hadWaypoints;
            lock (entry)
            {
                entry.Retired = true;
                hadWaypoints = entry.Waypoints.Count > 0;
            }

            if (hadWaypoints)
            {
                Save();
            }
        }
    }

    /// <summary>
    /// Drops expired pings, waypoints past their lifetime, and rooms left with nothing.
    /// </summary>
    /// <remarks>
    /// #886: the lifetime was applied only when the file was read back, so a waypoint held in
    /// memory never aged out, and nothing ever removed a room. Run by the relay's one-minute
    /// sweeper beside the live rooms' own.
    /// </remarks>
    public void Sweep()
    {
        var now = _timeProvider.GetUtcNow();
        var cutoff = now - WaypointLifetime;
        var dropped = false;
        foreach (var (room, entry) in _rooms)
        {
            lock (entry)
            {
                entry.Pings.RemoveAll(Expired);
                dropped |= entry.Waypoints.RemoveAll(waypoint => waypoint.CreatedUtc <= cutoff) > 0;
                // Retired under the same lock an adder takes, so a mark racing the sweep goes
                // into a fresh room rather than into this one after it has left the map.
                if (entry.Waypoints.Count == 0 && entry.Pings.Count == 0
                    && _rooms.TryRemove(new KeyValuePair<string, Room>(room, entry)))
                {
                    entry.Retired = true;
                }
            }
        }

        if (dropped)
        {
            Save();
        }
    }

    /// <summary>How many rooms hold marks, for tests and the health counts.</summary>
    public int RoomCount => _rooms.Count;

    /// <summary>Everything the group has marked, with expired pings already gone.</summary>
    public (IReadOnlyList<GroupWaypoint> Waypoints, IReadOnlyList<GroupPing> Pings) Read(string room)
    {
        if (!_rooms.TryGetValue(room, out var entry))
        {
            return ([], []);
        }

        lock (entry)
        {
            entry.Pings.RemoveAll(Expired);
            return (entry.Waypoints.ToArray(), entry.Pings.ToArray());
        }
    }

    /// <summary>
    /// Writes the squad's waypoints, and only those, to one file.
    /// </summary>
    /// <remarks>
    /// Pings are not written: they expire in forty-five seconds and mean "now", so one
    /// restored from disk would be a lie. Positions are not written because this server holds
    /// them only in memory, until a few minutes after their member stops publishing.
    ///
    /// Written through a temporary file and moved into place, so a restart during a save
    /// leaves the previous plan rather than a truncated one. A failure is logged nowhere and
    /// swallowed on purpose: losing the file costs the group its marks, and throwing here
    /// would cost them the mark they were making as well.
    ///
    /// #886: the snapshot is taken inside the gate. It used to be taken before it, so two
    /// waypoints dropped together could be written newest first and oldest last, and the
    /// newer one was missing after the next restart. Saves are also coalesced: every change
    /// raises a count, and a caller that reaches the gate after somebody else already wrote
    /// its change writes nothing, so a burst of marks costs one write rather than one each.
    /// </remarks>
    private void Save()
    {
        if (_storePath is null)
        {
            return;
        }

        var change = Interlocked.Increment(ref _changes);
        try
        {
            lock (_saveGate)
            {
                if (_written >= change)
                {
                    return;
                }

                // Everything up to here is in the snapshot below, taken after this read.
                var covers = Volatile.Read(ref _changes);
                var snapshot = new Dictionary<string, IReadOnlyList<GroupWaypoint>>(StringComparer.Ordinal);
                foreach (var (room, entry) in _rooms)
                {
                    lock (entry)
                    {
                        if (entry.Waypoints.Count > 0)
                        {
                            snapshot[room] = entry.Waypoints.ToArray();
                        }
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
                var temporary = _storePath + ".writing";
                File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
                File.Move(temporary, _storePath, overwrite: true);
                _written = covers;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    /// <summary>
    /// Reads back what was saved, dropping anything too old to still be a plan.
    /// </summary>
    /// <remarks>
    /// The next id is seeded above the largest one restored. Without that a restart would
    /// start numbering at one again and the first new waypoint would collide with a restored
    /// one, so completing either would complete the wrong mark. That alone was not enough
    /// (#886): the constructor also seeds it from the clock.
    /// </remarks>
    private void Load()
    {
        if (_storePath is null || !File.Exists(_storePath))
        {
            return;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, List<GroupWaypoint>>>(
                File.ReadAllText(_storePath));
            if (stored is null)
            {
                return;
            }

            var cutoff = _timeProvider.GetUtcNow() - WaypointLifetime;
            var highest = 0L;
            foreach (var (room, waypoints) in stored)
            {
                var kept = waypoints
                    .Where(waypoint => waypoint.CreatedUtc > cutoff)
                    .TakeLast(MaximumWaypointsPerRoom)
                    // #290: a colour edited into the file by hand is held to the palette too.
                    .Select(waypoint => waypoint with { Color = MarkPalette.Normalize(waypoint.Color) })
                    .ToArray();
                if (kept.Length == 0)
                {
                    continue;
                }

                var entry = _rooms.GetOrAdd(room, _ => new());
                entry.Waypoints.AddRange(kept);
                highest = Math.Max(highest, kept.Max(waypoint => waypoint.Id));
            }

            _nextId = highest;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable file is one lost plan, not a server that will not start.
            _rooms.Clear();
        }
    }

    private bool Expired(GroupPing ping) => _timeProvider.GetUtcNow() - ping.CreatedUtc > PingLifetime;

    private static string? Trim(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : label.Trim()[..Math.Min(label.Trim().Length, 64)];
}
