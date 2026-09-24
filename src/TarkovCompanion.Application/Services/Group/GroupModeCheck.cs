using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>
/// [#269] A group whose members play different game modes: said once, and quests kept apart.
/// </summary>
/// <remarks>
/// PvP and PvE progress are separate in the game, so a PvE squadmate's "Delivery from the Past"
/// is not the task this PvP player is on even though it has the same id. Showing it as shared, or
/// ranking tonight's maps by it, would be a claim about a squad that is not playing together.
/// Position, raid state and pings are still shown: those are true whatever the mode.
/// A member who sends no mode (an older companion or relay) is not assumed to differ.
/// </remarks>
public static class GroupModeCheck
{
    public const string Pvp = "pvp";
    public const string Pve = "pve";
    public const string Seasonal = "seasonal";

    /// <summary>The wire word for a profile's mode.</summary>
    public static string Wire(GameMode mode) => mode switch
    {
        GameMode.Pve => Pve,
        GameMode.PvpSeason => Seasonal,
        _ => Pvp,
    };

    /// <summary>The wire word, or null for absent or unknown text.</summary>
    public static string? Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        Pvp or "regular" => Pvp,
        Pve => Pve,
        Seasonal or "pvpseason" => Seasonal,
        _ => null,
    };

    public static string Label(string? mode) => Normalize(mode) switch
    {
        Pve => "PvE",
        Seasonal => "Seasonal",
        Pvp => "PvP",
        _ => "unknown",
    };

    /// <summary>True only when both modes are known and differ.</summary>
    public static bool Differs(string? own, string? theirs) =>
        Normalize(own) is { } a && Normalize(theirs) is { } b && a != b;

    /// <summary>Members as received, with the quests of anyone on another mode taken off.</summary>
    public static IReadOnlyList<GroupMemberView> Separate(string? own, IReadOnlyList<GroupMemberView> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return
        [
            .. members.Select(member => Differs(own, member.GameMode)
                ? member with { Quests = [], QuestIds = [], Objectives = [] }
                : member),
        ];
    }

    /// <summary>The members on another mode than <paramref name="own"/>.</summary>
    public static IReadOnlyList<GroupMemberView> OtherMode(string? own, IEnumerable<GroupMemberView> members) =>
        [.. members.Where(member => Differs(own, member.GameMode))];

    /// <summary>
    /// One line, such as "Sam is on PvE; this profile is PvP. Quests are not shared across modes.",
    /// or null when everybody whose mode is known plays this one.
    /// </summary>
    public static string? Warning(string? own, IEnumerable<GroupMemberView> members)
    {
        var others = OtherMode(own, members);
        if (others.Count == 0)
        {
            return null;
        }

        var modes = others.Select(member => Normalize(member.GameMode)).Distinct().ToArray();
        var names = others.Count switch
        {
            1 => others[0].Name,
            2 => $"{others[0].Name} and {others[1].Name}",
            _ => $"{others[0].Name} and {others.Count - 1} others",
        };
        var verb = others.Count == 1 ? "is" : "are";
        var where = modes.Length == 1 ? Label(modes[0]) : "another mode";
        return $"{names} {verb} on {where}; this profile is {Label(own)}. Quests are not shared across modes.";
    }
}

/// <summary>
/// [#269] Says a mode difference once. Dismissed, it stays quiet for those members on those modes
/// until the app restarts; a new member on another mode, or a switch of profile, is said again.
/// </summary>
public sealed class GroupModeWarnings
{
    private readonly HashSet<string> _dismissed = new(StringComparer.Ordinal);

    /// <summary>The line to show now, or null when there is nothing new to say.</summary>
    public string? Current(string? own, IEnumerable<GroupMemberView> members) =>
        GroupModeCheck.Warning(own, GroupModeCheck.OtherMode(own, members).Where(member => !_dismissed.Contains(Key(own, member))));

    public void Dismiss(string? own, IEnumerable<GroupMemberView> members)
    {
        foreach (var member in GroupModeCheck.OtherMode(own, members))
        {
            _dismissed.Add(Key(own, member));
        }
    }

    private static string Key(string? own, GroupMemberView member) =>
        $"{GroupModeCheck.Normalize(own)}|{member.Name}|{GroupModeCheck.Normalize(member.GameMode)}";
}
