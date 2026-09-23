using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// [#707] "--raid-left": the raid demo after the player has extracted and the squad has not.
/// </summary>
/// <remarks>
/// The player's raid is over (PostRaid, last screenshot still in the snapshot), Geo and Sam are
/// still in on the same map, and Riley has left too — an older companion still publishing his
/// last position, which must not be drawn. Geo has pinged and Sam has dropped a waypoint, so the
/// render shows the group's marks on the map, not only in the list.
/// </remarks>
internal static class SquadAfterRaidDemo
{
    internal static (RaidSnapshot Raid, GroupSnapshot Group) Apply((RaidSnapshot Raid, GroupSnapshot Group) demo)
    {
        var now = DateTimeOffset.UtcNow;
        var raid = demo.Raid with { State = RaidLifecycleState.PostRaid };
        var members = demo.Group.Members
            .Select(member => member.Name == "Riley" ? member with { RaidState = RaidLifecycleState.PostRaid } : member)
            .ToArray();
        var geo = members.First(member => member.Name == "Geo");
        var sam = members.First(member => member.Name == "Sam");
        var pings = geo.Position is { } at
            ? new[] { new GroupPingView(9001, "Geo", geo.MapId!, at.X + 25, at.Y, at.Z - 20, null, now.AddSeconds(-4)) }
            : [];
        var waypoints = sam.Position is { } there
            ? new[] { new GroupWaypointView(9002, "Sam", sam.MapId!, there.X - 30, there.Y, there.Z + 25, null, null) { CreatedUtc = now.AddMinutes(-1) } }
            : [];
        var group = demo.Group with
        {
            Members = members,
            Pings = pings,
            Waypoints = waypoints,
            Detail = "Sharing as Clay · 2 still in raid",
        };
        return (raid, group);
    }
}
