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

        // A map has ten or so exits and a handful of transits. Anything past that is a client
        // sending a list rather than a screen.
        if (Extracts is { Count: > 16 } || Transits is { Count: > 16 })
        {
            return "An extract or transit list may carry at most sixteen entries.";
        }

        if (Extracts is { } offered && offered.Any(name => name is null || name.Length > 64))
        {
            return "An extract name must be 64 characters or fewer.";
        }

        // Ids rather than names, so they are bounded on their own terms: a name is read and
        // five of them fill a panel, an id is counted and the whole active list is worth
        // having. Forty is more quests than anybody has open at once.
        if (QuestIds is { } ids && (ids.Count > 40 || ids.Any(id => id is null || id.Length > 64)))
        {
            return "A quest id list may carry at most forty ids of 64 characters or fewer.";
        }

        // A trail is screenshots, not a stream: a raid produces a handful.
        return Trail is { Count: > 12 }
            ? "A trail may carry at most twelve points."
            : null;
    }

    /// <summary>
    /// Which quests those are, by catalog id, so a receiver can place them on a map.
    /// </summary>
    /// <remarks>
    /// <see cref="Quests"/> is what a squadmate reads and this is what their companion can
    /// act on. A name is a string that happens to match; an id is the key the local quest
    /// catalog is indexed by, so the receiver can ask its own catalog which maps the quest
    /// wants and rank tonight's options by where the group overlaps.
    ///
    /// Resolved against the receiver's catalog rather than sent with maps attached. Both ends
    /// have the same catalog, and an id that the receiver's copy does not know is a quest
    /// added since they last synced — which is an answer, where a map id from a stranger's
    /// catalog would be a claim this cannot check.
    ///
    /// An init property, so a client that predates it neither sends nor trips over it.
    /// </remarks>
    [JsonPropertyName("questIds")]
    public IReadOnlyList<string> QuestIds { get; init; } = [];

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
    /// How long ago this member last published anything, in seconds.
    /// </summary>
    /// <remarks>
    /// Different from <see cref="PositionAgeSeconds"/>, and the difference is the point. That
    /// one ages the screenshot a member's position came from; this ages the member. A companion
    /// that crashed mid-raid keeps its last exchange's position age for ever, so the panel read
    /// "12s ago" for the three minutes until the room forgot them — the marker looked live right
    /// up to the moment it vanished.
    ///
    /// Filled in by the server on the way out, because only the server knows when it last heard
    /// from somebody. A publisher has no idea how long ago its own last message arrived.
    /// </remarks>
    [JsonPropertyName("sinceSeconds")]
    public double? SinceSeconds { get; init; }

    /// <summary>
    /// The exits this member's game offered them, as their own scan read them.
    /// </summary>
    /// <remarks>
    /// One player photographs the extract list and gains ActiveExtracts, Transits and the raid
    /// clock. The other four see ten possible exits and "counted from the raid's start", which
    /// is the difference between knowing where you are leaving from and guessing.
    ///
    /// In a PMC party the offered exits are the same for everybody, which is what makes this
    /// shareable at all — and it is game knowledge rather than something the research documents
    /// measured, which is why a receiver checks the map and side before applying any of it.
    /// </remarks>
    [JsonPropertyName("extracts")]
    public IReadOnlyList<string> Extracts { get; init; } = [];

    [JsonPropertyName("transits")]
    public IReadOnlyList<string> Transits { get; init; } = [];

    /// <summary>
    /// How long the sender's own scan said was left, and how old that reading is.
    /// </summary>
    /// <remarks>
    /// A reading with its age, exactly as the raid snapshot keeps it. Without the age a clock
    /// read four minutes ago is a stale claim presented as current, and a receiver would have
    /// no way to prefer its own fresher one.
    /// </remarks>
    [JsonPropertyName("raidClockSeconds")]
    public double? RaidClockSeconds { get; init; }

    [JsonPropertyName("raidClockAge")]
    public double? RaidClockAgeSeconds { get; init; }

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

    /// <summary>What this server speaks, so a client can tell skew from breakage.</summary>
    [JsonPropertyName("protocol")]
    public int Protocol { get; init; } = GroupProtocol.Version;
}

/// <summary>
/// The number that says whether two builds understand each other.
/// </summary>
/// <remarks>
/// Everything on this wire is additive — new fields are optional and an older reader ignores
/// them — so a mismatch is almost never fatal. That is exactly why it needs saying out loud:
/// a client quietly missing a field it was never sent looks identical to a feature that does
/// not work, and the day the group key replaced a room name and a server secret, a client that
/// had updated could not talk to a server that had not, and nothing anywhere said so.
///
/// Raise this when a change is not additive. Do not raise it for a new optional field.
/// </remarks>
public static class GroupProtocol
{
    public const int Version = 1;
}

/// <summary>One room as the admin panel shows it.</summary>
/// <param name="Room">The hash the relay buckets members by, which is all it holds.</param>
/// <param name="Label">What the operator called it, or null for one that is not registered.</param>
/// <param name="Members">How many members published in the last few minutes.</param>
public sealed record AdminRoom(string Room, string? Label, DateTimeOffset? RegisteredUtc, int Members);

/// <summary>
/// What the relay is serving and what it was meant to be serving.
/// </summary>
/// <param name="Closed">Whether a room has to be registered to be usable.</param>
/// <param name="Unregistered">
/// Rooms holding members that are not on the list. Empty is the state an operator wants; a row
/// here is either a friend whose room predates the list, or somebody who is not a friend.
/// </param>
public sealed record AdminRoomsView(
    bool Closed,
    string Version,
    string? Commit,
    DateTimeOffset StartedUtc,
    IReadOnlyList<AdminRoom> Registered,
    IReadOnlyList<AdminRoom> Unregistered);

/// <summary>
/// Registering a room: by generated key, by an existing key, or by a room already being held.
/// </summary>
/// <param name="Key">A key the group already uses, or null to have one generated.</param>
/// <param name="Room">A room hash to adopt, which takes precedence and needs no key at all.</param>
public sealed record AdminRoomRequest(string Label, string? Key = null, string? Room = null);

/// <summary>
/// A registered room, and the key if this call generated one.
/// </summary>
/// <remarks>
/// The only time a generated key exists outside the group. The relay keeps its hash and nothing
/// else, so there is no second request that can be made to see it again.
/// </remarks>
public sealed record AdminRoomCreated(string Room, string Label, string? Key);
