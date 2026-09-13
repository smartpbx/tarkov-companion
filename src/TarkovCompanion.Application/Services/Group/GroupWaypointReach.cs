using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>
/// Decides whether somebody has got to a place the group marked.
/// </summary>
/// <remarks>
/// A reached waypoint is drawn quiet rather than removed, because "we went there" is worth
/// keeping on screen. Nothing ever decided that anybody had, so the group's plan was a list
/// that only grew.
///
/// Decided on the client. The server is told where a member is only as the two coordinates
/// they publish, and it has no business measuring distances between people and places; the
/// client knows its own screenshot position and says so once.
/// </remarks>
public static class GroupWaypointReach
{
    /// <summary>
    /// How close counts as having got there.
    /// </summary>
    /// <remarks>
    /// A waypoint is a place rather than a point, and the only position the companion has is
    /// wherever the player last took a screenshot. Fifty metres is close enough that somebody
    /// standing on the spot is not told they have not arrived, and tight enough that arriving
    /// at one waypoint on a route does not tick off the next one along.
    /// </remarks>
    public const double WithinMetres = 50;

    /// <summary>
    /// Whether the player is standing at the marked place.
    /// </summary>
    /// <remarks>
    /// Measured in three dimensions rather than two. A waypoint fifty metres above you is on
    /// another floor of the same building and is not somewhere you have been.
    /// </remarks>
    public static bool IsReached(WorldPosition player, double x, double y, double z)
    {
        var dx = player.X - x;
        var dy = player.Y - y;
        var dz = player.Z - z;
        return (dx * dx) + (dy * dy) + (dz * dz) <= WithinMetres * WithinMetres;
    }
}
