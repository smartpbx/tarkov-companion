using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    [property: JsonPropertyName("completedBy")] string? CompletedBy);

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
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc);

/// <summary>
/// The marks a group has put on its maps.
/// </summary>
/// <remarks>
/// Held per room, like everything else, and bounded so a group cannot fill the server by
/// leaning on a mouse button.
///
/// Waypoints outlive a restart and positions do not, and that distinction is deliberate rather
/// than an inconsistency. A position is a record of where somebody has been, which this server
/// makes a point of never writing down. A waypoint is a thing somebody decided on purpose, and
/// losing the squad's plan because the relay updated itself at the wrong moment would be its
/// own small betrayal. The relay updates twice an hour, so "it only vanishes on a restart"
/// was never a rare event.
///
/// That paragraph was written in the same commit as the in-memory dictionary it sits on, and
/// nothing in this server touched the filesystem, so it described an intention rather than a
/// behaviour: every merge to main took the squad's plan with it. It is true now.
/// </remarks>
public sealed class GroupMarks
{
    /// <summary>How long a ping is shown before it stops meaning "now".</summary>
    private static readonly TimeSpan PingLifetime = TimeSpan.FromSeconds(45);

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
    }

    private sealed class Room
    {
        public List<GroupWaypoint> Waypoints { get; } = [];

        public List<GroupPing> Pings { get; } = [];
    }

    public GroupWaypoint AddWaypoint(string room, string by, string mapId, double x, double y, double z, string? label)
    {
        var entry = _rooms.GetOrAdd(room, _ => new());
        var waypoint = new GroupWaypoint(
            Interlocked.Increment(ref _nextId), by, mapId, x, y, z, Trim(label),
            _timeProvider.GetUtcNow(), null, null);
        lock (entry)
        {
            // Oldest first, so a group that keeps marking loses its stalest plan rather than
            // being told it cannot make a new one.
            if (entry.Waypoints.Count >= MaximumWaypointsPerRoom)
            {
                entry.Waypoints.RemoveAt(0);
            }

            entry.Waypoints.Add(waypoint);
        }

        Save();
        return waypoint;
    }

    public GroupPing AddPing(string room, string by, string mapId, double x, double y, double z, string? label)
    {
        var entry = _rooms.GetOrAdd(room, _ => new());
        var ping = new GroupPing(
            Interlocked.Increment(ref _nextId), by, mapId, x, y, z, Trim(label), _timeProvider.GetUtcNow());
        lock (entry)
        {
            entry.Pings.RemoveAll(Expired);
            if (entry.Pings.Count >= MaximumPingsPerRoom)
            {
                entry.Pings.RemoveAt(0);
            }

            entry.Pings.Add(ping);
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

    public bool Remove(string room, long id)
    {
        if (!_rooms.TryGetValue(room, out var entry))
        {
            return false;
        }

        bool removed;
        lock (entry)
        {
            removed = entry.Waypoints.RemoveAll(waypoint => waypoint.Id == id) > 0;
        }

        if (removed)
        {
            Save();
        }

        return removed;
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
    /// </remarks>
    private void Save()
    {
        if (_storePath is null)
        {
            return;
        }

        try
        {
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

            lock (_saveGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
                var temporary = _storePath + ".writing";
                File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
                File.Move(temporary, _storePath, overwrite: true);
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
    /// one, so completing either would complete the wrong mark.
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
