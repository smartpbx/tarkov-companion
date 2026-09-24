using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>
/// How this companion connects to a group, and whether it does at all.
/// </summary>
/// <remarks>
/// Off by default and off after an upgrade, because turning it on changes what this
/// application does with the player's data in a way no default should decide for them.
/// </remarks>
/// <param name="IsEnabled">Whether anything is sent at all.</param>
/// <param name="ServerUri">The group's own server. There is no default and no hosted service.</param>
/// <param name="DisplayName">The name the others see. Whatever the player types.</param>
/// <param name="Key">The one thing the group agrees between themselves.</param>
/// <param name="SharesLoadout">Whether the kit they are carrying goes too.</param>
/// <param name="SharesQuests">Whether the quests they are working on go too.</param>
public sealed record GroupSharingSettings(
    bool IsEnabled,
    string? ServerUri,
    string? DisplayName,
    string? Key,
    bool SharesLoadout,
    bool SharesQuests)
{
    /// <summary>Sharing nothing, which is where every installation starts.</summary>
    /// <remarks>
    /// [#780] Quests are on by default: once somebody joins a group, sharing what they are working
    /// on (quest and objective ids, nothing else) is the point, and Setup &gt; Team &amp; Devices
    /// opts out.
    /// </remarks>
    public static GroupSharingSettings Off { get; } = new(false, null, null, null, false, true);

    /// <summary>
    /// Why these are the defaults rather than what was saved, when that is the reason.
    /// </summary>
    /// <remarks>
    /// An unreadable settings file used to read as "off" and say nothing. Since the file was
    /// also written non-atomically, a kill at the wrong moment left a truncated one — and the
    /// player was silently not sharing, with every field blank and no way to tell that from
    /// never having set it up. Null whenever the file was read properly, which is almost
    /// always.
    /// </remarks>
    public string? ResetReason { get; init; }

    /// <summary>
    /// Whether this is complete enough to try, as opposed to merely switched on.
    /// </summary>
    /// <remarks>
    /// Half-filled settings should report themselves as not ready rather than fail against the
    /// server, so the player is told what is missing instead of what went wrong.
    /// </remarks>
    public bool IsUsable =>
        IsEnabled &&
        Uri.TryCreate(ServerUri, UriKind.Absolute, out var uri) &&
        IsTransportAcceptable(uri) &&
        !string.IsNullOrWhiteSpace(DisplayName) &&
        IsKeyWithinLimits;

    /// <summary>
    /// Whether the key may be sent to this address at all.
    /// </summary>
    /// <remarks>
    /// The group key rides on <c>X-Group-Key</c> on every request, and it is the only thing
    /// between a group and a stranger. Plain http was accepted for any host, and the public
    /// relay answers http today with no redirect, so the key was one mistyped scheme away from
    /// crossing the internet in the clear.
    ///
    /// Http is still fine where there is no internet to cross: loopback, the private ranges,
    /// the carrier-grade range a home network can sit behind, link-local, and a name with no
    /// dot in it or ending .local/.internal — all of which describe a relay on the same LAN,
    /// which is a perfectly ordinary way to run this. Anything else has to be https.
    /// </remarks>
    public static bool IsTransportAcceptable(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        if (uri.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        return IsLocalHost(uri.Host);
    }

    /// <summary>Whether a host is one that cannot be reached from outside the network.</summary>
    internal static bool IsLocalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var trimmed = host.Trim().Trim('[', ']');
        if (System.Net.IPAddress.TryParse(trimmed, out var address))
        {
            if (System.Net.IPAddress.IsLoopback(address))
            {
                return true;
            }

            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var octets = address.GetAddressBytes();
                return octets[0] switch
                {
                    10 => true,
                    127 => true,
                    169 when octets[1] == 254 => true,
                    172 when octets[1] is >= 16 and <= 31 => true,
                    192 when octets[1] == 168 => true,
                    // 100.64/10, which is where a home network behind carrier-grade NAT sits.
                    100 when octets[1] is >= 64 and <= 127 => true,
                    _ => false,
                };
            }

            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
        }

        // A name with no dot in it is a machine on this network; nothing on the public internet
        // resolves without one.
        return !trimmed.Contains('.', StringComparison.Ordinal) ||
            trimmed.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The key is the only thing between a group and a stranger who guesses it.
    /// </summary>
    /// <remarks>
    /// Both ends, matching the server, and checked here as well so somebody is told before they
    /// try rather than by a refusal afterwards. It was called IsKeyLongEnough while testing the
    /// ceiling too, which is the kind of name that survives until somebody trusts it.
    /// </remarks>
    public bool IsKeyWithinLimits =>
        Key is not null &&
        Key.Trim().Length >= GroupKeyLimits.Minimum &&
        Key.Trim().Length <= GroupKeyLimits.Maximum;

    /// <summary>Says what is missing, in the order a person would fill it in.</summary>
    public string? MissingPiece =>
        !IsEnabled ? null
        : string.IsNullOrWhiteSpace(ServerUri) ? "the group's server address"
        : !Uri.TryCreate(ServerUri, UriKind.Absolute, out var address) ? "a valid server address"
        : !IsTransportAcceptable(address) ? "an https address, because the group key travels with every request"
        : string.IsNullOrWhiteSpace(DisplayName) ? "a display name"
        : string.IsNullOrWhiteSpace(Key) ? "the group's key"
        : Key.Trim().Length < GroupKeyLimits.Minimum
            ? $"a group key of at least {GroupKeyLimits.Minimum} characters"
        : !IsKeyWithinLimits ? $"a group key of at most {GroupKeyLimits.Maximum} characters"
        : null;
}

/// <summary>Reads and writes the group settings the player chose.</summary>
public interface IGroupSettingsStore
{
    Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken);
}

/// <summary>One other member of the group, as they last described themselves.</summary>
/// <param name="Name">Their chosen display name.</param>
/// <param name="MapId">The map they are on, or null when they are not in a raid.</param>
/// <param name="RaidState">What they are doing.</param>
/// <param name="Side">PMC or scav, where their companion could establish it.</param>
/// <param name="Position">Where they were when they last took a screenshot.</param>
/// <param name="HeadingDegrees">Which way they were facing then.</param>
/// <param name="PositionAge">How old that is, so it can be presented as evidence rather than truth.</param>
/// <param name="Loadout">What they are carrying, where they chose to share it.</param>
/// <param name="Quests">What they are working on, where they chose to share it.</param>
public sealed record GroupMemberView(
    string Name,
    string? MapId,
    RaidLifecycleState RaidState,
    string? Side,
    Core.Domain.Maps.WorldPosition? Position,
    double? HeadingDegrees,
    TimeSpan? PositionAge,
    IReadOnlyList<string> Loadout,
    IReadOnlyList<string> Quests)
{
    /// <summary>
    /// How long since the relay last heard from this member at all.
    /// </summary>
    /// <remarks>
    /// Not the same question as <see cref="PositionAge"/>, which ages the screenshot a position
    /// came from. A companion that crashed mid-raid keeps republishing nothing, so its last
    /// exchange's position age was frozen and the panel read "12s ago" for the three minutes
    /// until the room forgot them — the marker looked live right up to the moment it vanished.
    ///
    /// Null from a relay too old to send it, which is treated as "not gone quiet" rather than
    /// as silence: an older relay is a reason to know less, not a reason to dim everybody.
    /// </remarks>
    public TimeSpan? Since { get; init; }

    /// <summary>
    /// Whether this member has missed enough ticks to be drawn as a guess.
    /// </summary>
    /// <remarks>
    /// Three, because one missed exchange is a slow request and two is a bad moment; three is a
    /// companion that has stopped. Shorter would dim somebody mid-raid for a hiccup, which is
    /// worse than a marker a few seconds stale — a dimmed squadmate reads as "they are gone".
    /// </remarks>
    public bool HasGoneQuiet => Since is { } since && since > GroupPublishing.QuietAfter;

    /// <summary>
    /// How old this position is here and now, rather than when it was sent.
    /// </summary>
    /// <remarks>
    /// <see cref="PositionAge"/> is the sender's own measurement, taken at the moment they
    /// published. Everything after that — the wait for the relay to be asked, the exchange
    /// itself — happened to the number without changing it, so a marker read younger than it
    /// was by however long the trip took. Adding the relay's own <see cref="Since"/> is the
    /// closest thing to the truth either end can work out, because each half is a duration one
    /// clock measured against itself.
    ///
    /// It is what the interface shows. A number that is a few seconds optimistic is exactly
    /// the kind of wrong that reads as right.
    /// </remarks>
    public TimeSpan? PositionAgeNow =>
        PositionAge is { } age ? age + (Since ?? TimeSpan.Zero) : null;

    /// <summary>
    /// Whether this member's height was published, as opposed to assumed.
    /// </summary>
    /// <remarks>
    /// The position is rebuilt with y = 0 when it was not, and on a map with floors that is a
    /// specific claim rather than a missing one. The map draws such a member quietly and says
    /// the floor is unknown, instead of putting them confidently on the reader's own floor.
    /// </remarks>
    public bool HasKnownHeight { get; init; }

    /// <summary>
    /// The exits this member's own scan of the extract screen read, and when.
    /// </summary>
    /// <remarks>
    /// One player photographs the extract list and gains all of this; the other four see ten
    /// possible exits and a clock counted from the raid's start. In a PMC party the offered
    /// exits are the same for everybody, which is what makes it shareable at all.
    /// </remarks>
    public IReadOnlyList<string> Extracts { get; init; } = [];

    public IReadOnlyList<string> Transits { get; init; } = [];

    /// <summary>Their clock reading, and how old it was when they published it.</summary>
    public TimeSpan? RaidClock { get; init; }

    public TimeSpan? RaidClockAge { get; init; }

    /// <summary>
    /// Which quests they named, by catalog id, where they chose to share them.
    /// </summary>
    /// <remarks>
    /// <see cref="Quests"/> is what a person reads and this is what a companion can act on.
    /// Resolved against this machine's own quest catalog, so an id it does not carry is a
    /// quest added since this copy last synced rather than something to guess at.
    ///
    /// Longer than the names: an id is counted rather than read, and ranking tonight's maps
    /// by where the group's lists overlap wants the whole list rather than the top of it.
    /// </remarks>
    public IReadOnlyList<string> QuestIds { get; init; } = [];

    /// <summary>
    /// [#780] The open objectives of this member's active quests, by id, with a count where kept.
    /// </summary>
    /// <remarks>
    /// Empty from a companion (or a relay) that predates it. The receiver names and places these
    /// from its own catalog; an id its copy does not know is passed over rather than guessed at.
    /// </remarks>
    public IReadOnlyList<GroupObjectiveView> Objectives { get; init; } = [];

    /// <summary>[#289] Ready or not, as they said on their Team workspace; null when unsaid.</summary>
    public bool? Ready { get; init; }

    /// <summary>[#289] The extract they plan to leave by, where they chose one.</summary>
    public string? PlannedExtract { get; init; }

    /// <summary>[#289] A short line they left for the squad.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// Where they have been this raid, oldest first, without their current position.
    /// </summary>
    /// <remarks>
    /// A dot says where somebody is; it does not say which way they came or whether they have
    /// already swept the building you are walking into. Empty for a member whose companion
    /// predates this, which is an ordinary answer rather than a missing one.
    /// </remarks>
    public IReadOnlyList<GroupTrailPointView> Trail { get; init; } = [];

    /// <summary>
    /// [#286] The lines this member drew on their map for the squad; empty from a companion or
    /// relay that predates them.
    /// </summary>
    public IReadOnlyList<GroupDrawingView> Drawings { get; init; } = [];

    /// <summary>
    /// [#269] The game mode this member's companion says they play ("pvp", "pve", "seasonal"), or
    /// null from a companion or relay that predates it. A member on another mode arrives with no
    /// quests: their quest ids would name tasks in a progress line this profile does not share.
    /// </summary>
    public string? GameMode { get; init; }
}

/// <summary>One place a member has been, and how old that reading was when they said so.</summary>
/// <remarks>
/// The age travels with the point because a trail is several stale readings. Drawn by age
/// rather than by position in the list, so a member who took three screenshots in ten seconds
/// and then none for five minutes does not imply they walked the whole line recently.
/// </remarks>
/// <summary>[#780] One open objective a squadmate shared: quest id, objective id, count done.</summary>
public sealed record GroupObjectiveView(string TaskId, string ObjectiveId, decimal? Count);

public sealed record GroupTrailPointView(double X, double Z, TimeSpan Age, double? Y = null);

/// <summary>What the group looks like right now, for the interface to render.</summary>
/// <param name="IsSharing">Whether this companion is publishing anything.</param>
/// <param name="Members">Everyone else who has published recently.</param>
/// <param name="Detail">A sentence saying what is happening, including why nothing is.</param>
/// <param name="UpdatedUtc">When this last changed.</param>
/// <summary>A place the group marked, which stays until somebody clears it.</summary>
/// <param name="Reached">Who got there, once anybody has.</param>
public sealed record GroupWaypointView(
    long Id,
    string By,
    string MapId,
    double X,
    double Y,
    double Z,
    string? Label,
    string? Reached)
{
    /// <summary>
    /// When this waypoint was marked, or the epoch from a relay too old to send it.
    /// </summary>
    /// <remarks>
    /// Appended rather than positional, like <see cref="GroupMemberView.Since"/>: an older relay
    /// simply omits it, which reads as "unknown age" rather than a deserialization failure.
    /// </remarks>
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UnixEpoch;
}

/// <summary>A place somebody is pointing at right now, which fades.</summary>
public sealed record GroupPingView(
    long Id,
    string By,
    string MapId,
    double X,
    double Y,
    double Z,
    string? Label,
    DateTimeOffset CreatedUtc);

public sealed record GroupSnapshot(
    bool IsSharing,
    IReadOnlyList<GroupMemberView> Members,
    string Detail,
    DateTimeOffset UpdatedUtc)
{
    /// <summary>Places the group marked, which stay until cleared.</summary>
    public IReadOnlyList<GroupWaypointView> Waypoints { get; init; } = [];

    /// <summary>Places somebody is pointing at now, which the server expires for us.</summary>
    public IReadOnlyList<GroupPingView> Pings { get; init; } = [];

    /// <summary>
    /// This player's own kit, as the rest of the group described it back to them.
    /// </summary>
    /// <remarks>
    /// The game tells every player what everybody else is wearing and tells them nothing about
    /// themselves, so this is the only route anybody has to their own kit. It stays empty until
    /// somebody else in the same party is also running this companion.
    /// </remarks>
    public IReadOnlyList<string> MyLoadout { get; init; } = [];

    /// <summary>
    /// This player's own level, faction and scav timer, as their squadmates' games saw them.
    /// </summary>
    /// <remarks>
    /// The same asymmetry as the kit, and the same route back. The Quests level is typed by
    /// hand because nothing could read it; the profile's faction is never set; and a player's
    /// own scav cooldown appears nowhere in this application — while every squadmate's game
    /// has written all three down about them.
    ///
    /// Null until somebody else in the same party is also running this companion, which is the
    /// same condition MyLoadout has always carried.
    /// </remarks>
    public int? MyLevel { get; init; }

    public string? MySide { get; init; }

    public DateTimeOffset? MyScavLockedUntil { get; init; }

    /// <summary>
    /// [#269] This player's game mode as sent ("pvp", "pve", "seasonal"), or null where the
    /// active profile could not be read. What each member's <see cref="GroupMemberView.GameMode"/>
    /// is compared against.
    /// </summary>
    public string? MyGameMode { get; init; }

    /// <summary>When contact with the relay was lost, if what is here is the last good read.</summary>
    /// <remarks>
    /// Null while the exchange is working. Set on the first failed exchange and kept across
    /// later ones, so the interface can say how long ago the group was really heard from
    /// rather than presenting a three-minute-old picture as current.
    ///
    /// It exists because the alternative was worse: one failed exchange used to publish
    /// <see cref="Off"/>, which empties Members, Waypoints and Pings, and the map cleared
    /// every squadmate and every mark for five seconds until the next tick put them back.
    /// A stale marker that says it is stale beats a marker that vanishes and returns.
    /// </remarks>
    public DateTimeOffset? StaleSince { get; init; }

    /// <summary>
    /// How long squadmate positions have actually been taking to arrive.
    /// </summary>
    /// <remarks>
    /// Measured, not asserted. The whole point of publishing on change rather than on a tick is
    /// a number, and a number nobody can read is a claim. It is empty until a squadmate takes a
    /// screenshot while this companion is watching.
    /// </remarks>
    public GroupPositionLatencySnapshot PositionLatency { get; init; } = GroupPositionLatencySnapshot.None;

    public static GroupSnapshot Off { get; } = new(
        false,
        [],
        "Not sharing",
        DateTimeOffset.UnixEpoch);
}

/// <summary>
/// What the relay will accept as a key, stated once so both ends agree.
/// </summary>
/// <remarks>
/// The ceiling was only ever checked on the server, so a key longer than 128 characters passed
/// every check here and came back 401 — and the message said "wrong group key", which sent
/// people to compare keys that were identical and fine.
/// </remarks>
/// <summary>
/// How often a member publishes, and how long a silence has to be to mean something.
/// </summary>
/// <remarks>
/// Here rather than inside the session service because the interval and the rule that depends
/// on it are one decision: "three missed ticks" is only a number if the tick is defined beside
/// it, and two files each holding half of that is how they drift apart.
/// </remarks>
public static class GroupPublishing
{
    /// <summary>How often each member publishes itself and reads the room.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>How many exchanges a member may miss before being drawn as a guess.</summary>
    public const int MissedTicks = 3;

    /// <summary>The silence that means a companion has stopped rather than stumbled.</summary>
    public static readonly TimeSpan QuietAfter = Interval * MissedTicks;
}

public static class GroupKeyLimits
{
    public const int Minimum = 8;

    public const int Maximum = 128;
}
