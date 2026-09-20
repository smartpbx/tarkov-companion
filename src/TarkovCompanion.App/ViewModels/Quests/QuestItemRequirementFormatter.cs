using System.Globalization;
using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.App.ViewModels.Quests;

public static class QuestItemRequirementFormatter
{
    /// <param name="name">
    /// Turns an item id into what the item is called, where the catalog knows. Omitted, the
    /// ids are printed as they are, which is what an objective used to read like.
    /// </param>
    public static string DescribeForQuest(
        QuestObjectiveReadModel objective,
        RecordedTaskState taskState,
        Func<string, string>? name = null)
    {
        ArgumentNullException.ThrowIfNull(objective);
        var prefix = taskState == RecordedTaskState.Active ? "Active need" : "Planning need";
        var remaining = objective.TargetCount is { } target
            ? Math.Max(0, target - (objective.RecordedCount ?? 0)).ToString("0.##", CultureInfo.InvariantCulture)
            : "unknown quantity";
        return Describe(
            objective.ItemTargets,
            objective.FoundInRaidRequired,
            $"{prefix}: {remaining}",
            name);
    }

    public static string DescribeForMap(
        IReadOnlyList<QuestObjectiveItemTarget> targets,
        bool? foundInRaidRequired,
        Func<string, string>? name = null) => Describe(targets, foundInRaidRequired, "Items", name);

    /// <summary>Whether a requirement field names something carried or worn rather than handed over.</summary>
    public static bool IsCarriedIn(string sourceField) => QuestItemTargetFields.IsCarriedIn(sourceField);

    /// <summary>
    /// What to have on you before the raid starts, or empty where nothing is asked.
    /// </summary>
    /// <remarks>
    /// The map arrives on profileStatus about a minute before the raid does, and that minute is
    /// the last one in which any of this can be acted on. Afterwards it is a list of what could
    /// have been brought.
    /// </remarks>
    public static string DescribeBring(
        IReadOnlyList<QuestObjectiveItemTarget> targets,
        Func<string, string>? name = null) => Partition(targets, carriedIn: true, foundInRaidRequired: null, name);

    /// <summary>What the objective wants handed over, or empty where nothing is.</summary>
    public static string DescribeHandIn(
        IReadOnlyList<QuestObjectiveItemTarget> targets,
        bool? foundInRaidRequired,
        Func<string, string>? name = null) => Partition(targets, carriedIn: false, foundInRaidRequired, name);

    /// <summary>
    /// One half of the requirements, said the same way the whole of them is said.
    /// </summary>
    /// <remarks>
    /// Through <see cref="Describe"/> rather than beside it, so the two lines and the existing
    /// one-line form cannot drift apart in how they group alternatives or name a field. Empty
    /// rather than "No item requirement": these are two optional lines on a card, and a card
    /// saying "No item requirement" twice says nothing twice.
    /// </remarks>
    private static string Partition(
        IReadOnlyList<QuestObjectiveItemTarget> targets,
        bool carriedIn,
        bool? foundInRaidRequired,
        Func<string, string>? name)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var wanted = targets
            .Where(target => QuestItemTargetFields.IsCarriedIn(target.SourceField) == carriedIn)
            .ToArray();
        return wanted.Length == 0
            ? string.Empty
            : Describe(wanted, foundInRaidRequired, carriedIn ? "Bring" : "Hand in", name);
    }

    private static string Describe(
        IReadOnlyList<QuestObjectiveItemTarget> targets,
        bool? foundInRaidRequired,
        string itemPrefix,
        Func<string, string>? name)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var describe = name ?? (itemId => itemId);
        if (targets.Count == 0)
        {
            return "No item requirement";
        }

        var itemGroups = targets
            .Where(target => !string.Equals(target.SourceField, "requiredKeys", StringComparison.Ordinal))
            .GroupBy(target => (target.SourceField, target.AlternativeGroup))
            .OrderBy(group => group.Min(target => target.SourceOrdinal))
            .ThenBy(group => group.Key.SourceField, StringComparer.Ordinal)
            .ThenBy(group => group.Key.AlternativeGroup)
            .Select(group =>
                $"{Humanise(group.Key.SourceField)}: {string.Join(" or ", group
                    .OrderBy(target => target.SourceOrdinal)
                    .ThenBy(target => target.ItemId, StringComparer.Ordinal)
                    .Select(target => describe(target.ItemId))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase))}")
            .ToArray();
        var requiredKeys = targets
            .Where(target => string.Equals(target.SourceField, "requiredKeys", StringComparison.Ordinal))
            .GroupBy(target => target.AlternativeGroup)
            .OrderBy(group => group.Min(target => target.SourceOrdinal))
            .ThenBy(group => group.Key)
            .Select(group => string.Join(" or ", group
                .OrderBy(target => target.SourceOrdinal)
                .ThenBy(target => target.ItemId, StringComparer.Ordinal)
                .Select(target => describe(target.ItemId))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)))
            .ToArray();
        var fir = foundInRaidRequired == true ? " · found in raid" : string.Empty;
        var itemNeed = itemGroups.Length == 0
            ? null
            : $"{itemPrefix} · {string.Join("; ", itemGroups)}{fir}";
        var keyNeed = requiredKeys.Length == 0 ? null : $"Keys: {string.Join("; ", requiredKeys)}";
        return string.Join(" · ", new[] { itemNeed, keyNeed }.OfType<string>());
    }

    /// <summary>
    /// Says what a requirement is in the words the game uses, not the feed's field name.
    /// </summary>
    /// <remarks>
    /// These are the property names on the upstream objective, printed straight through, so an
    /// objective read "markerItem: 5991b51486f77447b112d44f" where it meant to say a marker.
    /// An unmapped field is spaced out rather than invented, so a new one reads as a new one
    /// instead of disappearing.
    /// </remarks>
    public static string Humanise(string sourceField) => sourceField switch
    {
        "markerItem" => "Marker",
        "items" => "Items",
        "usingWeapon" => "Weapon",
        "usingWeaponMods" => "Weapon mods",
        "wearing" => "Wearing",
        "notWearing" => "Not wearing",
        "containsAll" => "Contains all",
        "containsOne" => "Contains one",
        "requiredKeys" => "Keys",
        "attributes" => "Attributes",
        _ => Space(sourceField),
    };

    /// <summary>Splits a camel-cased field name into words, capital first.</summary>
    private static string Space(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(value.Length + 4);
        builder.Append(char.ToUpperInvariant(value[0]));
        foreach (var character in value.AsSpan(1))
        {
            if (char.IsUpper(character))
            {
                builder.Append(' ').Append(char.ToLowerInvariant(character));
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
