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
    /// Why this state cannot be accepted, or null if it can.
    /// </summary>
    /// <remarks>
    /// Null-tolerant throughout. A payload with `"observed": null` reached a Count on a null
    /// list and threw, which the framework turned into a 500: a malformed request answered as
    /// a server fault, and an unhandled exception per attempt for anybody who cared to send
    /// them. The collections are non-nullable in the record and a JSON null still lands.
    /// </remarks>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 48)
        {
            return "A display name is required and must be 48 characters or fewer.";
        }

        if (MapId is { Length: > 64 })
        {
            return "A map id must be 64 characters or fewer.";
        }

        if (RaidState is null || RaidState.Length > 32)
        {
            return "A raid state is required and must be 32 characters or fewer.";
        }

        if (Loadout is { Count: > 24 } || Quests is { Count: > 24 })
        {
            return "A loadout and a quest list may each carry at most twenty-four entries.";
        }

        // The one field carrying something about other people. A client that published four
        // hundred of them would be filling the room rather than helping it; a party is five.
        if (Observed is { } observed &&
            (observed.Count > 8 || observed.Any(entry =>
                entry is null ||
                string.IsNullOrWhiteSpace(entry.Name) ||
                entry.Name.Length > 48 ||
                entry.Loadout is { Count: > 12 })))
        {
            return "Observations must name at most eight players with at most twelve items each.";
        }

        // Guarded the same way the check above it is, and for the reason the remark on this
        // method already gives: a payload with "observed": null lands as a null list, and Any
        // on one throws — which the framework turns into a 500, a malformed request answered
        // as a server fault. I wrote these two without the guard and the test written for that
        // exact bug caught it.
        if (Observed is { } stated)
        {
            // The game's own range. A level outside it is a client that has miscounted or is
            // making something up, and either way it would be handed straight back to the
            // person it claims to describe and used to gate their quest list.
            if (stated.Any(entry => entry.Level is { } level && level is < 1 or > 79))
            {
                return "An observed level must be between 1 and 79.";
            }

            if (stated.Any(entry => entry.Side is { Length: > 16 }))
            {
                return "An observed side must be 16 characters or fewer.";
            }
        }

        // A trail is screenshots, not a stream: a raid produces a handful.
        return Trail is { Count: > 12 }
            ? "A trail may carry at most twelve points."
            : null;
    }

    /// <summary>How high this member is standing, where their screenshot said.</summary>
    /// <remarks>
    /// Optional so a client that predates this still parses, and because a member whose
    /// position came from a source without a height has none to give.
    ///
    /// Waypoints have carried a height since they were added, on the grounds that it "matters
    /// for a map with floors"; members did not, and the client rebuilt them at y = 0. Since the
    /// floor stack is baselined at the selected floor, that put a squadmate standing in
    /// Reserve's bunkers on whichever floor the reader happened to be looking at, with nothing
    /// on screen to say otherwise.
    /// </remarks>
    [JsonPropertyName("y")]
    public double? Y { get; init; }

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
    [property: JsonPropertyName("age")] double AgeSeconds)
{
    /// <summary>How high this step was, where the screenshot said.</summary>
    [JsonPropertyName("y")]
    public double? Y { get; init; }
}

/// <summary>What one member's game said about another player, to be handed back to them.</summary>
/// <param name="Name">The other player's in-game nickname, which is the only key there is.</param>
/// <param name="Loadout">The gear slots the game named, in the order a player reads them.</param>
public sealed record GroupObservedMember(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("loadout")] IReadOnlyList<string> Loadout)
{
    /// <summary>
    /// What somebody else's game says about this player, which their own does not.
    /// </summary>
    /// <remarks>
    /// The same asymmetry the loadout exploits, and the same rule: this is only ever handed
    /// back to the person it is about, by the nickname match the kit already uses. Nothing here
    /// reaches anybody who was not already looking at it on their own screen.
    ///
    /// Init properties so a client that predates them still parses, and so a client that does
    /// not send them is not refused.
    /// </remarks>
    [JsonPropertyName("level")]
    public int? Level { get; init; }

    [JsonPropertyName("side")]
    public string? Side { get; init; }

    /// <summary>When this player's scav is available again, as a Unix second.</summary>
    /// <remarks>
    /// Seconds rather than a formatted time, because the receiver renders it in their own
    /// locale and a string would have arrived in the sender's.
    /// </remarks>
    [JsonPropertyName("scavLockedUntil")]
    public long? ScavLockedUntilUnix { get; init; }
}

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
