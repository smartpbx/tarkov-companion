using System.Collections.Concurrent;
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
/// makes a point of never keeping. A waypoint is a thing somebody decided on purpose, and
/// losing the squad's plan because the relay updated itself at the wrong moment would be its
/// own small betrayal. The relay now updates twice an hour, so "it only vanishes on a restart"
/// stopped being a rare event.
/// </remarks>
public sealed class GroupMarks(TimeProvider timeProvider)
{
    /// <summary>How long a ping is shown before it stops meaning "now".</summary>
    private static readonly TimeSpan PingLifetime = TimeSpan.FromSeconds(45);

    /// <summary>Enough for a plan, few enough that nobody can drown a map in them.</summary>
    private const int MaximumWaypointsPerRoom = 60;

    private const int MaximumPingsPerRoom = 30;

    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private long _nextId;

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
            timeProvider.GetUtcNow(), null, null);
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

        return waypoint;
    }

    public GroupPing AddPing(string room, string by, string mapId, double x, double y, double z, string? label)
    {
        var entry = _rooms.GetOrAdd(room, _ => new());
        var ping = new GroupPing(
            Interlocked.Increment(ref _nextId), by, mapId, x, y, z, Trim(label), timeProvider.GetUtcNow());
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
                CompletedUtc = timeProvider.GetUtcNow(),
                CompletedBy = by,
            };
            return true;
        }
    }

    public bool Remove(string room, long id)
    {
        if (!_rooms.TryGetValue(room, out var entry))
        {
            return false;
        }

        lock (entry)
        {
            return entry.Waypoints.RemoveAll(waypoint => waypoint.Id == id) > 0;
        }
    }

    /// <summary>Clears a map's waypoints, or only the ones already reached.</summary>
    public int Clear(string room, string? mapId, bool reachedOnly)
    {
        if (!_rooms.TryGetValue(room, out var entry))
        {
            return 0;
        }

        lock (entry)
        {
            return entry.Waypoints.RemoveAll(waypoint =>
                (mapId is null || string.Equals(waypoint.MapId, mapId, StringComparison.OrdinalIgnoreCase))
                && (!reachedOnly || waypoint.CompletedUtc is not null));
        }
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

    private bool Expired(GroupPing ping) => timeProvider.GetUtcNow() - ping.CreatedUtc > PingLifetime;

    private static string? Trim(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : label.Trim()[..Math.Min(label.Trim().Length, 64)];
}
