using TarkovCompanion.Application.Services.Planning;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>[#712 T7] One line of a Loadout check, as the squad sees it.</summary>
/// <param name="Kind">One of <see cref="LoadoutCheckKinds"/>.</param>
/// <param name="Ok">True all there, false something is missing, null not known (never scanned).</param>
/// <param name="Missing">The first missing item's name, where <paramref name="Ok"/> is false.</param>
public sealed record LoadoutCheckItem(string Kind, bool? Ok, string? Missing = null);

/// <summary>[#712 T7] The kinds a Loadout check line can be, in the order a kit is packed.</summary>
public static class LoadoutCheckKinds
{
    public const string Keys = "keys";
    public const string Items = "items";
    public const string Weapon = "weapon";
    public const string Gear = "gear";

    /// <summary>The kind a loadout suggestion is checked under; null for one that names nothing to own.</summary>
    public static string? Of(LoadoutSuggestionKind kind) => kind switch
    {
        LoadoutSuggestionKind.Key => Keys,
        LoadoutSuggestionKind.Carry => Items,
        LoadoutSuggestionKind.Weapon or LoadoutSuggestionKind.WeaponMod => Weapon,
        LoadoutSuggestionKind.Wear => Gear,
        // Leave-behind, range and room are about how, not what: nothing to own.
        _ => null,
    };

    internal static int Order(string kind) => kind switch
    {
        Keys => 0,
        Items => 1,
        Weapon => 2,
        Gear => 3,
        _ => 4,
    };
}

/// <summary>
/// [#712 T7] This player's Loadout check for one map, made on this companion from this player's own
/// quests and stash, for the squad's ready check.
/// </summary>
/// <remarks>
/// Built from the Plan › Loadout suggestions (#815), which read only conditions the catalog states
/// on the player's own active objectives, and the owned counts their own stash scans recorded. So a
/// line says "keys" or "items" and never "class 4 armour": nothing this app reads says what armour a
/// map wants, and a guess sent to the squad would be read as a fact.
///
/// Unknown is not missing (#509): an item no scan has counted makes its line "not known", and the
/// squad sees nothing claimed about it.
/// </remarks>
/// <param name="MapId">The map checked, by quest catalog id.</param>
/// <param name="Items">One line per kind, packed order.</param>
/// <param name="CheckedUtc">When this companion made the check.</param>
public sealed record SharedLoadoutCheck(string? MapId, IReadOnlyList<LoadoutCheckItem> Items, DateTimeOffset CheckedUtc)
{
    /// <summary>The relay's bound on how many lines one check carries.</summary>
    public const int MaximumItems = 8;

    /// <summary>The relay's bound on a missing item's name.</summary>
    public const int MissingLimit = 64;

    /// <summary>The check from Plan › Loadout's rows for the chosen map.</summary>
    public static SharedLoadoutCheck From(LoadoutSuggestionPlan plan, DateTimeOffset checkedUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var items = plan.Rows
            .Select(row => (Kind: LoadoutCheckKinds.Of(row.Suggestion.Kind), Row: row))
            .Where(pair => pair.Kind is not null && pair.Row.Suggestion.ItemIds.Count > 0)
            .GroupBy(pair => pair.Kind!, StringComparer.Ordinal)
            .OrderBy(group => LoadoutCheckKinds.Order(group.Key))
            .Select(group => Check(group.Key, [.. group.Select(pair => pair.Row)]))
            .Take(MaximumItems)
            .ToArray();
        return new(plan.MapId, items, checkedUtc);
    }

    /// <summary>How many lines say something is missing.</summary>
    public int MissingCount => Items.Count(item => item.Ok == false);

    private static LoadoutCheckItem Check(string kind, IReadOnlyList<LoadoutSuggestionRow> rows)
    {
        // "None owned" is a scan that counted zero; "not scanned" is no scan at all.
        var missing = rows.FirstOrDefault(row => !row.IsOwned && IsCounted(row));
        if (missing is not null)
        {
            return new(kind, false, Clip(ItemName(missing.Suggestion.Title)));
        }

        return rows.All(row => row.IsOwned) ? new(kind, true) : new(kind, null);
    }

    /// <summary>The planner says "Owned: not scanned" only where no scan counted any of the items.</summary>
    private static bool IsCounted(LoadoutSuggestionRow row) =>
        !string.Equals(row.Have, "Owned: not scanned", StringComparison.Ordinal) && row.Have.Length > 0;

    /// <summary>"Bring MS2000 Marker ×2" is the suggestion; the squad reads "MS2000 Marker".</summary>
    internal static string ItemName(string title)
    {
        var name = title.StartsWith("Bring one of: ", StringComparison.Ordinal)
            ? title["Bring one of: ".Length..]
            : title.StartsWith("Bring ", StringComparison.Ordinal) ? title["Bring ".Length..] : title;
        var times = name.LastIndexOf(" ×", StringComparison.Ordinal);
        return (times > 0 ? name[..times] : name).Trim();
    }

    private static string Clip(string value) => value.Length > MissingLimit ? value[..MissingLimit] : value;
}

/// <summary>[#712 T7] A squadmate's Loadout check, as their companion shared it.</summary>
/// <param name="Age">How old the check was when it was last published.</param>
public sealed record GroupLoadoutCheckView(string? MapId, IReadOnlyList<LoadoutCheckItem> Items, TimeSpan? Age)
{
    public int MissingCount => Items.Count(item => item.Ok == false);

    /// <summary>Equal by what it says, so an unchanged check does not rebuild a row.</summary>
    public bool Equals(GroupLoadoutCheckView? other) =>
        other is not null &&
        string.Equals(MapId, other.MapId, StringComparison.Ordinal) &&
        Age == other.Age &&
        Items.SequenceEqual(other.Items);

    public override int GetHashCode() => HashCode.Combine(MapId, Items.Count, Age);
}
