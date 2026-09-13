using System.Globalization;
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
