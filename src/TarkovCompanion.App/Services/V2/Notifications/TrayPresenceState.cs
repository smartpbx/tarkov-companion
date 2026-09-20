using System.Globalization;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.Services.V2.Notifications;

/// <summary>What the tray icon is saying, at a glance.</summary>
public enum TrayStatus
{
    /// <summary>The game is not running, or nothing has been observed yet.</summary>
    Idle,

    /// <summary>The game is up and the player is out of a raid.</summary>
    Menu,

    /// <summary>A raid is loading.</summary>
    Loading,

    /// <summary>A raid is running. The one state worth a colour of its own.</summary>
    InRaid,

    /// <summary>A raid has just ended and its debrief is waiting.</summary>
    PostRaid,

    /// <summary>Something is wrong that the player would want to know about.</summary>
    Attention,
}

/// <summary>
/// The words and the colour behind the tray icon, as a rule with no Avalonia in it.
/// </summary>
/// <remarks>
/// [V2 rough package 43] Separate from <see cref="TrayPresence"/> so what the tray says can be
/// tested by handing it a snapshot, rather than by standing up a windowing system that CI does not
/// have. The tray is then a thin thing that draws whatever this returns.
/// </remarks>
public static class TrayPresenceState
{
    /// <summary>Windows truncates a tray tooltip at 127 characters; this stays well inside it.</summary>
    public const int MaximumTooltipLength = 120;

    public static TrayPresenceText Describe(ApplicationRuntimeSnapshot snapshot, int unread)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var raid = snapshot.Raid;
        var group = snapshot.Group;
        var relayIsDown = group.IsSharing && group.StaleSince is not null;
        var status = relayIsDown
            ? TrayStatus.Attention
            : raid.State switch
            {
                RaidLifecycleState.InRaid => TrayStatus.InRaid,
                RaidLifecycleState.LoadingRaid => TrayStatus.Loading,
                RaidLifecycleState.PostRaid => TrayStatus.PostRaid,
                RaidLifecycleState.Menu or RaidLifecycleState.LauncherOrGameDetected => TrayStatus.Menu,
                _ => TrayStatus.Idle,
            };

        var raidLine = raid.State switch
        {
            RaidLifecycleState.InRaid when !string.IsNullOrWhiteSpace(raid.MapId) =>
                string.Create(CultureInfo.CurrentCulture, $"In raid · {raid.MapId}"),
            RaidLifecycleState.InRaid => "In raid",
            RaidLifecycleState.LoadingRaid => "Loading a raid",
            RaidLifecycleState.PostRaid => "Raid over · debrief ready",
            RaidLifecycleState.Menu or RaidLifecycleState.LauncherOrGameDetected => "In the menu",
            _ => "Not in a raid",
        };

        var groupLine = !group.IsSharing
            ? "Not sharing"
            : relayIsDown
                ? "Relay unreachable"
                : group.Members.Count switch
                {
                    0 => "Sharing · nobody else yet",
                    1 => "Sharing with 1",
                    var count => string.Create(CultureInfo.CurrentCulture, $"Sharing with {count}"),
                };

        var unreadLine = unread <= 0
            ? string.Empty
            : unread == 1
                ? "1 new"
                : string.Create(CultureInfo.CurrentCulture, $"{unread} new");

        var tooltip = string.Join(
            " · ",
            new[] { "Tarkov Companion", raidLine, groupLine, unreadLine }.Where(part => part.Length > 0));
        return new(
            raidLine,
            groupLine,
            unreadLine,
            tooltip.Length <= MaximumTooltipLength ? tooltip : tooltip[..MaximumTooltipLength],
            status);
    }
}

/// <param name="RaidLine">Where the player is, in three or four words.</param>
/// <param name="GroupLine">What the relay is doing.</param>
/// <param name="UnreadLine">How many notifications have not been looked at; empty for none.</param>
/// <param name="Tooltip">The three joined, bounded to what a tray tooltip will show.</param>
/// <param name="Status">Which colour the icon carries.</param>
public sealed record TrayPresenceText(
    string RaidLine,
    string GroupLine,
    string UnreadLine,
    string Tooltip,
    TrayStatus Status);
