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
/// <param name="Room">The room the group agreed between themselves.</param>
/// <param name="DisplayName">The name the others see. Whatever the player types.</param>
/// <param name="Secret">The secret the group shares.</param>
/// <param name="SharesLoadout">Whether the kit they are carrying goes too.</param>
/// <param name="SharesQuests">Whether the quests they are working on go too.</param>
public sealed record GroupSharingSettings(
    bool IsEnabled,
    string? ServerUri,
    string? Room,
    string? DisplayName,
    string? Secret,
    bool SharesLoadout,
    bool SharesQuests)
{
    /// <summary>Sharing nothing, which is where every installation starts.</summary>
    public static GroupSharingSettings Off { get; } = new(false, null, null, null, null, false, false);

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
        !string.IsNullOrWhiteSpace(Room) &&
        !string.IsNullOrWhiteSpace(DisplayName) &&
        !string.IsNullOrWhiteSpace(Secret);

    /// <summary>Says what is missing, in the order a person would fill it in.</summary>
    public string? MissingPiece =>
        !IsEnabled ? null
        : string.IsNullOrWhiteSpace(ServerUri) ? "the group's server address"
        : !Uri.TryCreate(ServerUri, UriKind.Absolute, out _) ? "a valid server address"
        : string.IsNullOrWhiteSpace(Room) ? "a room name"
        : string.IsNullOrWhiteSpace(DisplayName) ? "a display name"
        : string.IsNullOrWhiteSpace(Secret) ? "the group's shared secret"
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
    IReadOnlyList<string> Quests);

/// <summary>What the group looks like right now, for the interface to render.</summary>
/// <param name="IsSharing">Whether this companion is publishing anything.</param>
/// <param name="Members">Everyone else who has published recently.</param>
/// <param name="Detail">A sentence saying what is happening, including why nothing is.</param>
/// <param name="UpdatedUtc">When this last changed.</param>
public sealed record GroupSnapshot(
    bool IsSharing,
    IReadOnlyList<GroupMemberView> Members,
    string Detail,
    DateTimeOffset UpdatedUtc)
{
    public static GroupSnapshot Off { get; } = new(
        false,
        [],
        "Not sharing. Nothing about this session leaves the machine.",
        DateTimeOffset.UnixEpoch);
}
