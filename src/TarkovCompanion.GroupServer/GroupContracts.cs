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
    [property: JsonPropertyName("quests")] IReadOnlyList<string> Quests)
{
    /// <summary>
    /// What this member's game told them about everybody else in their in-game party.
    /// </summary>
    /// <remarks>
    /// The one place where a member publishes something that is not about themselves, and it
    /// exists because of an asymmetry in what the game says. Its notifications about other
    /// players carry a full profile with an equipment block; its notifications about you carry
    /// a bare profile id and nothing else. Checked across 325 log files: every equipment block
    /// belongs to somebody else and the reader's own account id appears in none of them.
    ///
    /// So nobody can see their own kit and everybody can see everybody else's. Published here,
    /// the group can hand each member back the one thing they cannot read.
    ///
    /// Only the party the game has already told them about, and only the slots the Squad page
    /// already shows. This adds no new reading of anybody's data; it moves what is already on
    /// one screen onto the screen of the person it is about.
    /// </remarks>
    [JsonPropertyName("observed")]
    public IReadOnlyList<GroupObservedMember> Observed { get; init; } = [];

    /// <summary>
    /// Where this member has been this raid, oldest first.
    /// </summary>
    /// <remarks>
    /// One dot says where somebody is. It does not say which way they came, whether they are
    /// moving, or whether they have already swept the building you are about to walk into.
    ///
    /// Published rather than each client remembering what it has seen, because somebody who
    /// opens the map mid-raid or restarts the application is exactly the person a squadmate's
    /// path is most worth having, and remembering locally gives them nothing.
    ///
    /// Bounded, like everything else a member publishes. These are screenshots, not a stream.
    /// </remarks>
    [JsonPropertyName("trail")]
    public IReadOnlyList<GroupTrailPoint> Trail { get; init; } = [];
}

/// <summary>One place a member has been, and how long ago they were there.</summary>
/// <remarks>
/// The age travels with the point because a trail is several stale readings and the oldest may
/// be minutes old. A member who took three screenshots in ten seconds and then none for five
/// minutes must not draw a line implying they walked it recently.
/// </remarks>
/// <param name="X">World position, from their own screenshot.</param>
/// <param name="Z">World position, from their own screenshot.</param>
/// <param name="AgeSeconds">How old the reading was when it was published.</param>
public sealed record GroupTrailPoint(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("age")] double AgeSeconds);

/// <summary>What one member's game said about another player, to be handed back to them.</summary>
/// <param name="Name">The other player's in-game nickname, which is the only key there is.</param>
/// <param name="Loadout">The gear slots the game named, in the order a player reads them.</param>
public sealed record GroupObservedMember(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("loadout")] IReadOnlyList<string> Loadout);

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
