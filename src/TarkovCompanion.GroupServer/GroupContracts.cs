using System.Text.Json.Serialization;

namespace TarkovCompanion.GroupServer;

/// <summary>
/// One member's own state, as they choose to publish it.
/// </summary>
/// <remarks>
/// Every field here describes the sender and nobody else. A member publishes themselves and
/// the server relays it; nothing is ever derived from one member and attributed to another.
///
/// This is a deliberate departure from the desktop application's usual promise that nothing
/// from the game's logs leaves the machine. It is the whole point of the feature, it happens
/// only when somebody turns it on, and what is sent is listed here in full so the promise can
/// be read rather than trusted.
/// </remarks>
/// <param name="Name">The display name the member chose. Not their account name unless they typed it.</param>
/// <param name="MapId">Which map they are on, or null when they are not in a raid.</param>
/// <param name="RaidState">Menu, LoadingRaid, InRaid or PostRaid.</param>
/// <param name="Side">PMC or scav, where the companion could establish it.</param>
/// <param name="X">World position, from their own screenshot, or null when they have taken none.</param>
/// <param name="Z">World position, from their own screenshot, or null when they have taken none.</param>
/// <param name="HeadingDegrees">Which way they were facing in that screenshot.</param>
/// <param name="PositionAgeSeconds">How old that position is, so the others can judge it.</param>
/// <param name="Loadout">What they are carrying, where they chose to share it.</param>
/// <param name="Quests">Quest names they are working on, where they chose to share them.</param>
public sealed record GroupMemberState(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mapId")] string? MapId,
    [property: JsonPropertyName("raidState")] string RaidState,
    [property: JsonPropertyName("side")] string? Side,
    [property: JsonPropertyName("x")] double? X,
    [property: JsonPropertyName("z")] double? Z,
    [property: JsonPropertyName("heading")] double? HeadingDegrees,
    [property: JsonPropertyName("positionAge")] double? PositionAgeSeconds,
    [property: JsonPropertyName("loadout")] IReadOnlyList<string> Loadout,
    [property: JsonPropertyName("quests")] IReadOnlyList<string> Quests);

/// <summary>What the server sends back: everyone in the room except the receiver.</summary>
/// <param name="Room">The room the update belongs to, so a client can ignore a stale one.</param>
/// <param name="Members">Every other member's last published state.</param>
/// <param name="ServerUtc">The server's clock, so a client can spot its own being wrong.</param>
public sealed record GroupRoomState(
    [property: JsonPropertyName("room")] string Room,
    [property: JsonPropertyName("members")] IReadOnlyList<GroupMemberState> Members,
    [property: JsonPropertyName("serverUtc")] DateTimeOffset ServerUtc)
{
    /// <summary>Places the group has marked, which stay until cleared.</summary>
    public IReadOnlyList<GroupWaypoint> Waypoints { get; init; } = [];

    /// <summary>Places somebody is pointing at right now, which fade.</summary>
    public IReadOnlyList<GroupPing> Pings { get; init; } = [];
}
