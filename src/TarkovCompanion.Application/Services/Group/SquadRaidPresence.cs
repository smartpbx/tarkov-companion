using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>
/// Who is still in a raid once somebody in the squad has left theirs (#707).
/// </summary>
/// <remarks>
/// A squad does not leave a raid together. The player who extracts or dies first used to keep
/// publishing the last screenshot of a raid that was over, so the squadmates still in it saw a
/// "you" marker standing at an extract nobody was at; and the player who left had nothing on
/// their own screen saying which map to watch for the ones still inside.
///
/// Only Menu and PostRaid count as out. Unknown and LauncherOrGameDetected are how a companion
/// that has not read the log yet describes itself, and reading those as "left" would blank a
/// squadmate who is in fact mid-raid.
/// </remarks>
public static class SquadRaidPresence
{
    /// <summary>Whether this state says the raid is over for whoever is in it.</summary>
    public static bool HasLeftRaid(RaidLifecycleState state) =>
        state is RaidLifecycleState.Menu or RaidLifecycleState.PostRaid;

    /// <summary>Whether a squadmate's shared position still describes where they are.</summary>
    /// <remarks>
    /// An older companion still publishes its last screenshot after the raid ends, so the receiving
    /// end checks as well as the sending one.
    /// </remarks>
    public static bool IsPlaced(GroupMemberView member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return !HasLeftRaid(member.RaidState);
    }

    /// <summary>
    /// The squadmate this player should be watching, when they are out and somebody is still in.
    /// </summary>
    /// <remarks>
    /// Null while the player is in or loading a raid of their own, because then their own raid
    /// decides which map is open. Otherwise the squadmate heard from most recently, so a member
    /// whose companion has gone quiet is not preferred over one who is plainly still playing.
    /// </remarks>
    public static GroupMemberView? StillInRaid(RaidLifecycleState local, IReadOnlyList<GroupMemberView> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (local is RaidLifecycleState.InRaid or RaidLifecycleState.LoadingRaid)
        {
            return null;
        }

        return members
            .Where(member => member.RaidState == RaidLifecycleState.InRaid && !string.IsNullOrWhiteSpace(member.MapId))
            .OrderBy(member => member.Since ?? TimeSpan.Zero)
            .ThenBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
