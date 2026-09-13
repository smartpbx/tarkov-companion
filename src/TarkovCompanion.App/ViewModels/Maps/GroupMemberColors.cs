namespace TarkovCompanion.App.ViewModels.Maps;

/// <summary>
/// Gives everybody in the group a colour of their own, on the map and in the panel beside it.
/// </summary>
/// <remarks>
/// Every member used to be drawn in the same ochre, so a map with three of them on it said
/// where three people were without saying which was which. The panel names them and the map
/// marks them, and the colour is the only thing that can join the two together at a glance.
///
/// The colour comes from the name rather than from a position in a list, so it survives
/// somebody joining, leaving or reconnecting. Where two names land on the same colour the
/// later one steps to the next free slot, which is why the whole group is assigned at once:
/// two people sharing a colour defeats the point, and both machines have to reach the same
/// answer from the same member list.
///
/// Cyan is not in the palette. That is the player's own marker and their trail, and a
/// squadmate the same colour as yourself is the one confusion worth designing out.
/// </remarks>
public static class GroupMemberColors
{
    /// <summary>Eight hues that hold up on dark artwork and read apart from each other.</summary>
    public static IReadOnlyList<string> Palette { get; } =
    [
        "E0B45C",
        "DF6A62",
        "B98BD9",
        "8FD14F",
        "F09A3E",
        "6FA8F5",
        "E06AA8",
        "C97B4E",
    ];

    /// <summary>The colour used before a group has been read, and for anybody unnamed.</summary>
    public const string Fallback = "E0B45C";

    /// <summary>Assigns one colour per name, the same way on every machine.</summary>
    public static IReadOnlyDictionary<string, string> Assign(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var ordered = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var taken = new bool[Palette.Count];
        var assigned = new Dictionary<string, string>(ordered.Length, StringComparer.Ordinal);
        foreach (var name in ordered)
        {
            var start = (int)(Hash(name) % (uint)Palette.Count);
            var slot = start;
            for (var step = 0; step < Palette.Count; step++)
            {
                var candidate = (start + step) % Palette.Count;
                if (!taken[candidate])
                {
                    slot = candidate;
                    break;
                }
            }

            taken[slot] = true;
            assigned[name] = Palette[slot];
        }

        return assigned;
    }

    /// <summary>Prefixes an alpha, because the markers fade rather than change hue when stale.</summary>
    public static string WithAlpha(string rgb, string alpha) => "#" + alpha + rgb;

    /// <summary>
    /// FNV-1a, written out rather than taken from <c>string.GetHashCode</c>.
    /// </summary>
    /// <remarks>
    /// The framework's string hash is randomised per process, so the same name would be a
    /// different colour on every launch and a different colour on each machine. Both of those
    /// break the one thing the colour is for.
    /// </remarks>
    private static uint Hash(string value)
    {
        var hash = 2166136261u;
        foreach (var character in value)
        {
            hash = (hash ^ character) * 16777619u;
        }

        return hash;
    }
}
