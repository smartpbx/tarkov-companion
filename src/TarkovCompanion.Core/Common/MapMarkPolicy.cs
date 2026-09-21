namespace TarkovCompanion.Core.Common;

/// <summary>
/// How long a ping means "now" before it stops mattering — one number, read by both the places
/// that place a ping: the relay's own group pings (<c>GroupMarks.PingLifetime</c>, in
/// <c>TarkovCompanion.GroupServer</c>) and the desktop's locally-stored marks
/// (<c>JsonFileRaidMarkStore</c>, in <c>TarkovCompanion.Infrastructure</c>), which is where a
/// tablet's ping lands too — <c>RelayMarksBridge</c> applies it the same way a desktop right-click
/// does.
/// </summary>
/// <remarks>
/// Issue 584: a ping placed from a paired tablet never disappeared. The local mark store's
/// <c>AddAsync</c> never stamped an expiry on anything it created, so a ping was kept exactly like
/// a waypoint — including surviving a restart, because the store is a file. A desktop right-click
/// ping had the identical bug; it went unnoticed because it is only visible over a couple of
/// minutes, not a whole raid. Both are fixed from this one constant now, rather than each having
/// grown its own "45 seconds" that happened to agree.
/// </remarks>
public static class MapMarkPolicy
{
    public static readonly TimeSpan PingLifetime = TimeSpan.FromSeconds(45);
}
