using System.Globalization;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Gear;
using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.Application.Services.Intel;

/// <summary>One item's facts for a side-by-side comparison: its Intel card, plus gear and weight.</summary>
/// <param name="Gear">Armor class, durability, zones and penalties, when the item is gear.</param>
/// <param name="WeightKg">Null when the catalog has no weight for it, never zero.</param>
public sealed record ItemComparisonFacts(V2ItemIntelResult Intel, GearFacts? Gear, double? WeightKg);

/// <summary>Which attribute set a comparison uses. Mixed kinds fall back to <see cref="Item"/>.</summary>
public enum ItemComparisonKind
{
    Item,
    Ammo,
    Armor,
    Key,
}

/// <summary>Which way a row's winner points; <see cref="None"/> rows are facts with no better or worse.</summary>
public enum ComparisonDirection
{
    None,
    Higher,
    Lower,
}

/// <summary>One item's value in one row. Unknown is drawn as a dash and can never be best.</summary>
public sealed record ItemComparisonCell(string Text, double? Value, bool IsBest);

public sealed record ItemComparisonRow(string Label, ComparisonDirection Direction, IReadOnlyList<ItemComparisonCell> Cells);

public sealed record ItemComparisonColumn(string ItemId, string Name, string ShortName);

public sealed record ItemComparisonTable(
    ItemComparisonKind Kind,
    IReadOnlyList<ItemComparisonColumn> Columns,
    IReadOnlyList<ItemComparisonRow> Rows);

/// <summary>
/// Lays two or three items out as rows of the attributes that decide between them (#287), and
/// marks the best value in each row.
/// </summary>
/// <remarks>
/// Price points both ways on purpose. Ammo and armor are bought, so the cheaper flea price wins;
/// keys and loot are sold or kept for their value, so the higher one wins. A row where every
/// known value is the same has no best, because a highlight on all of them reads as a
/// difference that is not there. An unknown value is never best and never zero: it is a dash.
/// </remarks>
public static class ItemComparisonBuilder
{
    public const int MaximumItems = 3;

    private const string Unknown = "—";

    public static ItemComparisonKind KindOf(IReadOnlyList<ItemComparisonFacts> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return ItemComparisonKind.Item;
        }

        if (items.All(item => item.Intel.Kind == V2IntelKind.Ammo))
        {
            return ItemComparisonKind.Ammo;
        }

        if (items.All(item => item.Intel.Kind == V2IntelKind.Key))
        {
            return ItemComparisonKind.Key;
        }

        return items.All(IsArmor) ? ItemComparisonKind.Armor : ItemComparisonKind.Item;
    }

    public static ItemComparisonTable Build(IReadOnlyList<ItemComparisonFacts> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var kind = KindOf(items);
        var columns = items
            .Select(item => new ItemComparisonColumn(item.Intel.ItemId, item.Intel.Name, item.Intel.ShortName))
            .ToArray();
        var rows = kind switch
        {
            ItemComparisonKind.Ammo => AmmoRows(items),
            ItemComparisonKind.Armor => ArmorRows(items),
            ItemComparisonKind.Key => KeyRows(items),
            _ => ItemRows(items),
        };
        return new(kind, columns, [.. rows.Select(Mark)]);
    }

    private static bool IsArmor(ItemComparisonFacts item) =>
        item.Intel.Category is ItemCategory.Armor or ItemCategory.Helmet or ItemCategory.Plate ||
        item.Gear?.ArmorClass is > 0;

    private static IEnumerable<ItemComparisonRow> AmmoRows(IReadOnlyList<ItemComparisonFacts> items)
    {
        yield return Numbers("Damage", ComparisonDirection.Higher, items, item => item.Intel.Ammo?.Damage, Whole);
        yield return Numbers("Penetration", ComparisonDirection.Higher, items, item => item.Intel.Ammo?.Penetration, Whole);
        yield return Numbers("Armor damage", ComparisonDirection.Higher, items, item => item.Intel.Ammo?.ArmorDamagePercent,
            value => $"{Whole(value)}%");
        yield return Numbers("Speed", ComparisonDirection.Higher, items, item => item.Intel.Ammo?.VelocityMetresPerSecond,
            value => $"{Whole(value)} m/s");
        yield return Numbers("Fragmentation", ComparisonDirection.Higher, items,
            item => item.Intel.Ammo?.FragmentationChance * 100, value => $"{Whole(value)}%");
        yield return Numbers("Beats armor", ComparisonDirection.Higher, items, item => BeatsClass(item.Intel.Ammo),
            value => value > 0 ? $"Class {Whole(value)}" : "None");
        yield return Texts("Tier", items, item => item.Intel.Ammo?.Tier);
        yield return Numbers("Flea price", ComparisonDirection.Lower, items, item => item.Intel.Prices?.FleaRoubles, Roubles);
        yield return SellsFor(items);
    }

    private static IEnumerable<ItemComparisonRow> ArmorRows(IReadOnlyList<ItemComparisonFacts> items)
    {
        yield return Numbers("Armor class", ComparisonDirection.Higher, items, item => item.Gear?.ArmorClass is > 0 and var c ? c : null,
            value => $"Class {Whole(value)}");
        yield return Numbers("Durability", ComparisonDirection.Higher, items, item => item.Gear?.Durability, Whole);
        yield return Numbers("Zones", ComparisonDirection.Higher, items,
            item => item.Gear is { Zones.Count: > 0 } gear ? gear.Zones.Count : null,
            value => $"{Whole(value)} zones");
        yield return Texts("Covers", items, item => item.Gear is { Zones.Count: > 0 } gear ? ZoneSummary(gear.Zones) : null);
        yield return Texts("Material", items, item => item.Gear?.Material);
        yield return Numbers("Weight", ComparisonDirection.Lower, items, item => item.WeightKg, Kilograms);
        yield return Numbers("Move speed", ComparisonDirection.Higher, items, item => item.Gear?.SpeedPenalty * 100, Percent);
        yield return Numbers("Ergonomics", ComparisonDirection.Higher, items, item => item.Gear?.ErgoPenalty * 100, Percent);
        yield return Numbers("Flea price", ComparisonDirection.Lower, items, item => item.Intel.Prices?.FleaRoubles, Roubles);
        yield return SellsFor(items);
    }

    private static IEnumerable<ItemComparisonRow> KeyRows(IReadOnlyList<ItemComparisonFacts> items)
    {
        yield return Texts("Map", items, item => item.Intel.Key?.MapName ?? item.Intel.Key?.MapId);
        yield return Texts("Opens", items, item => item.Intel.Key is { } key ? LocksText(key.Locks) : null);
        yield return Numbers("Uses", ComparisonDirection.Higher, items, item => item.Intel.Key?.MaximumUses, Whole);
        yield return Numbers("Value", ComparisonDirection.Higher, items, item => item.Intel.Value?.ValueRoubles, Roubles);
        yield return Numbers("Cost to buy", ComparisonDirection.Lower, items, item => item.Intel.Key?.AcquisitionCostRoubles, Roubles);
        yield return Numbers("Needed", ComparisonDirection.None, items, Needed, NeededText);
    }

    private static IEnumerable<ItemComparisonRow> ItemRows(IReadOnlyList<ItemComparisonFacts> items)
    {
        yield return Texts("Category", items, item => item.Intel.Category.ToString());
        yield return Numbers("Value", ComparisonDirection.Higher, items, item => item.Intel.Value?.ValueRoubles, Roubles);
        yield return Numbers("Per slot", ComparisonDirection.Higher, items, PerSlot, Roubles);
        yield return Texts("Size", items, item => item.Intel.Width > 0 && item.Intel.Height > 0
            ? $"{item.Intel.Width}×{item.Intel.Height}"
            : null);
        yield return Numbers("Weight", ComparisonDirection.Lower, items, item => item.WeightKg, Kilograms);
        yield return Numbers("Flea fee", ComparisonDirection.Lower, items, item => item.Intel.Prices?.FeeRoubles, Roubles);
        yield return Numbers("Needed", ComparisonDirection.None, items, Needed, NeededText);
    }

    /// <summary>
    /// What it sells back for, unmarked: for something bought, a higher resale is not a better
    /// buy. It stands beside the flea price because much ammo is banned from the flea, and a
    /// row of dashes alone said nothing about what it is worth.
    /// </summary>
    private static ItemComparisonRow SellsFor(IReadOnlyList<ItemComparisonFacts> items) =>
        Numbers("Sells for", ComparisonDirection.None, items, item => item.Intel.Value?.ValueRoubles, Roubles);

    /// <summary>The highest class the round rates good or better against, 0 when none, null when unrated.</summary>
    internal static int? BeatsClass(V2IntelAmmoFacts? ammo)
    {
        if (ammo is null || ammo.ArmorClassRatings.Count == 0)
        {
            return null;
        }

        return ammo.ArmorClassRatings
            .Where(rating => rating.Value is ArmorEffectiveness.Good or ArmorEffectiveness.Excellent)
            .Select(rating => rating.Key)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static double? PerSlot(ItemComparisonFacts item) =>
        item.Intel.Value?.ValueRoubles is { } value && item.Intel.Width * item.Intel.Height is > 0 and var slots
            ? Math.Round((double)value / slots)
            : null;

    /// <summary>
    /// Quests read from Keep, hideout from Value: the same sum the Intel headline shows, because
    /// Value's quest count over-reports (a dorm key read "225 wanted" beside "0 needed").
    /// </summary>
    private static double? Needed(ItemComparisonFacts item) => item.Intel.Keep is null && item.Intel.Value is null
        ? null
        : (item.Intel.Keep?.Quests.Sum(row => row.Remaining ?? 0) ?? 0) + (item.Intel.Value?.HideoutCount ?? 0);

    /// <summary>
    /// The lock names, or how many locks when the catalog only has their ids: a column of
    /// 24-character hex strings is not a place anybody can find.
    /// </summary>
    internal static string? LocksText(IReadOnlyList<string> locks)
    {
        if (locks.Count == 0)
        {
            return null;
        }

        var named = locks.Where(name => !LooksLikeId(name)).ToArray();
        return named.Length > 0
            ? string.Join(", ", named)
            : locks.Count == 1 ? "1 lock" : $"{locks.Count} locks";
    }

    private static bool LooksLikeId(string text)
    {
        var head = text.Split(':')[0];
        return head.Length == 24 && head.All(char.IsAsciiHexDigit);
    }

    private static string NeededText(double value) => value > 0 ? $"{Whole(value)} wanted" : "Not needed";

    private static string ZoneSummary(IReadOnlyList<string> zones)
    {
        // The catalog lists "Thorax, Upper back" style pairs; the first word is the body part.
        var parts = zones
            .Select(zone => zone.Split(',')[0].Trim())
            .Where(part => part.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return string.Join(", ", parts);
    }

    private static ItemComparisonRow Numbers(
        string label,
        ComparisonDirection direction,
        IReadOnlyList<ItemComparisonFacts> items,
        Func<ItemComparisonFacts, double?> read,
        Func<double, string> format) =>
        new(label, direction, [.. items.Select(item => read(item) is { } value
            ? new ItemComparisonCell(format(value), value, false)
            : new ItemComparisonCell(Unknown, null, false))]);

    private static ItemComparisonRow Texts(string label, IReadOnlyList<ItemComparisonFacts> items, Func<ItemComparisonFacts, string?> read) =>
        new(label, ComparisonDirection.None, [.. items.Select(item =>
            new ItemComparisonCell(read(item) is { Length: > 0 } text ? text : Unknown, null, false))]);

    /// <summary>Marks the best known value; nothing when fewer than two are known or they are all equal.</summary>
    internal static ItemComparisonRow Mark(ItemComparisonRow row)
    {
        if (row.Direction == ComparisonDirection.None)
        {
            return row;
        }

        var known = row.Cells.Where(cell => cell.Value.HasValue).Select(cell => cell.Value!.Value).ToArray();
        if (known.Length < 2 || known.Distinct().Count() == 1)
        {
            return row;
        }

        var best = row.Direction == ComparisonDirection.Higher ? known.Max() : known.Min();
        return row with { Cells = [.. row.Cells.Select(cell => cell with { IsBest = cell.Value == best })] };
    }

    private static string Whole(double value) => Math.Round(value).ToString("N0", CultureInfo.CurrentCulture);

    private static string Roubles(double value) => "₽" + Whole(value);

    private static string Kilograms(double value) => value.ToString("0.##", CultureInfo.CurrentCulture) + " kg";

    private static string Percent(double value) => value == 0
        ? "0%"
        : Math.Round(value, 1).ToString("+0.#;-0.#", CultureInfo.CurrentCulture) + "%";
}
