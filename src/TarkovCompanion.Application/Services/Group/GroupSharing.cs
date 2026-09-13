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
    public static GroupSharingSettings Off { get; } = new(false, null, null, null, false, false);

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
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
        !string.IsNullOrWhiteSpace(DisplayName) &&
        IsKeyLongEnough;

    /// <summary>
    /// The key is the only thing between a group and a stranger who guesses it.
    /// </summary>
    /// <remarks>
    /// Eight characters, matching the server, and checked here as well so somebody is told
    /// before they try rather than by a refusal afterwards.
    /// </remarks>
    public bool IsKeyLongEnough => Key is not null && Key.Trim().Length >= 8;

    /// <summary>Says what is missing, in the order a person would fill it in.</summary>
    public string? MissingPiece =>
        !IsEnabled ? null
        : string.IsNullOrWhiteSpace(ServerUri) ? "the group's server address"
        : !Uri.TryCreate(ServerUri, UriKind.Absolute, out _) ? "a valid server address"
        : string.IsNullOrWhiteSpace(DisplayName) ? "a display name"
        : string.IsNullOrWhiteSpace(Key) ? "the group's key"
        : !IsKeyLongEnough ? "a group key of at least eight characters"
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
    /// Whether this member's height was published, as opposed to assumed.
    /// </summary>
    /// <remarks>
    /// The position is rebuilt with y = 0 when it was not, and on a map with floors that is a
    /// specific claim rather than a missing one. The map draws such a member quietly and says
    /// the floor is unknown, instead of putting them confidently on the reader's own floor.
    /// </remarks>
    public bool HasKnownHeight { get; init; }

    /// <summary>
    /// Where they have been this raid, oldest first, without their current position.
    /// </summary>
    /// <remarks>
    /// A dot says where somebody is; it does not say which way they came or whether they have
    /// already swept the building you are walking into. Empty for a member whose companion
    /// predates this, which is an ordinary answer rather than a missing one.
    /// </remarks>
    public IReadOnlyList<GroupTrailPointView> Trail { get; init; } = [];
}

/// <summary>One place a member has been, and how old that reading was when they said so.</summary>
/// <remarks>
/// The age travels with the point because a trail is several stale readings. Drawn by age
/// rather than by position in the list, so a member who took three screenshots in ten seconds
/// and then none for five minutes does not imply they walked the whole line recently.
/// </remarks>
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
    string? Reached);

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

    public static GroupSnapshot Off { get; } = new(
        false,
        [],
        "Not sharing",
        DateTimeOffset.UnixEpoch);
}
