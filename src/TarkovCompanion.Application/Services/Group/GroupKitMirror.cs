using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>What one player's game said about another, to be handed back to them.</summary>
public sealed record ObservedKit(string Name, IReadOnlyList<string> Loadout);

/// <summary>
/// Hands each member of a group the one thing their own game will not tell them.
/// </summary>
/// <remarks>
/// The game's notifications about other players carry a full profile with an equipment block.
/// Its notifications about you carry a bare profile id and nothing else. Checked across 325
/// log files on a real installation: every equipment block belongs to somebody else, and the
/// reader's own account id appears in none of them.
///
/// So nobody can see their own kit and everybody can see everybody else's. Published to the
/// group, that asymmetry cancels out: a squadmate running this companion is already reading
/// your weapon, armour, rig and backpack out of their own logs, and can simply say so.
///
/// This adds no new reading of anybody's data. It moves what is already on one player's Squad
/// page onto the screen of the person it is about.
/// </remarks>
public static class GroupKitMirror
{
    /// <summary>A party is five; the bound is what the server will accept.</summary>
    private const int MaximumMembers = 8;

    /// <summary>The slots a player reads, in the order the squad page already uses.</summary>
    private static readonly string[] GearSlots =
    [
        "FirstPrimaryWeapon",
        "SecondPrimaryWeapon",
        "Holster",
        "Headwear",
        "ArmorVest",
        "TacticalVest",
        "Backpack",
    ];

    /// <summary>
    /// What this companion's game has said about everybody else in its party.
    /// </summary>
    /// <param name="squad">The party as the game's own notifications described it.</param>
    /// <param name="name">
    /// Resolves an item id to its name, because the game writes template ids and never names.
    /// An id that resolves to nothing is left out rather than published as a number.
    /// </param>
    public static IReadOnlyList<ObservedKit> Describe(
        SquadSnapshot squad,
        Func<string, string?> name)
    {
        ArgumentNullException.ThrowIfNull(squad);
        ArgumentNullException.ThrowIfNull(name);
        var observed = new List<ObservedKit>(Math.Min(squad.Members.Count, MaximumMembers));
        foreach (var member in squad.Members)
        {
            if (observed.Count == MaximumMembers)
            {
                break;
            }

            if (member.Nickname is not { Length: > 0 } nickname)
            {
                continue;
            }

            var loadout = member.Equipment
                .Where(item => item.SlotId is not null && GearSlots.Contains(item.SlotId, StringComparer.Ordinal))
                .OrderBy(item => Array.IndexOf(GearSlots, item.SlotId!))
                .Select(item => name(item.TemplateId))
                .OfType<string>()
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            if (loadout.Length > 0)
            {
                observed.Add(new(nickname, loadout));
            }
        }

        return observed;
    }

    /// <summary>
    /// The item ids this party's kit refers to that a caller has not named yet.
    /// </summary>
    /// <remarks>
    /// Here rather than in the caller so the slot list is stated once. A resolver that asked
    /// for every id on every publish would be asking a database the same question all evening.
    /// </remarks>
    public static IReadOnlyList<string> UnresolvedIds(SquadSnapshot squad, Func<string, bool> known)
    {
        ArgumentNullException.ThrowIfNull(squad);
        ArgumentNullException.ThrowIfNull(known);
        return squad.Members
            .Take(MaximumMembers)
            .SelectMany(member => member.Equipment)
            .Where(item => item.SlotId is not null && GearSlots.Contains(item.SlotId, StringComparer.Ordinal))
            .Select(item => item.TemplateId)
            .Distinct(StringComparer.Ordinal)
            .Where(templateId => !known(templateId))
            .ToArray();
    }

    /// <summary>
    /// Finds what the group said about one player, by the only key there is.
    /// </summary>
    /// <remarks>
    /// The logs carry the in-game nickname and the relay carries the display name somebody
    /// typed. They are the same string for most people and need not be, and the local player's
    /// own account id is as absent from the logs as their equipment, so there is no better key
    /// to match on. Where the two differ a player will not find themselves, which the Group
    /// page says rather than leaving it a mystery.
    ///
    /// The first answer wins. Two squadmates describing the same person are describing the
    /// same kit, and picking between them would be inventing a disagreement.
    /// </remarks>
    public static IReadOnlyList<string> Find(
        IEnumerable<IReadOnlyList<ObservedKit>> published,
        string? name)
    {
        ArgumentNullException.ThrowIfNull(published);
        if (string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        foreach (var observations in published)
        {
            foreach (var observed in observations)
            {
                if (string.Equals(observed.Name, name.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    observed.Loadout.Count > 0)
                {
                    return observed.Loadout;
                }
            }
        }

        return [];
    }
}
