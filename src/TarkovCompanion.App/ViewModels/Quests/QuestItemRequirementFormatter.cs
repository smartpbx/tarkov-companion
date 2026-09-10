using System.Globalization;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.App.ViewModels.Quests;

public static class QuestItemRequirementFormatter
{
    public static string DescribeForQuest(
        QuestObjectiveReadModel objective,
        RecordedTaskState taskState)
    {
        ArgumentNullException.ThrowIfNull(objective);
        var prefix = taskState == RecordedTaskState.Active ? "Active need" : "Planning need";
        var remaining = objective.TargetCount is { } target
            ? Math.Max(0, target - (objective.RecordedCount ?? 0)).ToString("0.##", CultureInfo.InvariantCulture)
            : "unknown quantity";
        return Describe(
            objective.ItemTargets,
            objective.FoundInRaidRequired,
            $"{prefix}: {remaining}");
    }

    public static string DescribeForMap(
        IReadOnlyList<QuestObjectiveItemTarget> targets,
        bool? foundInRaidRequired) => Describe(targets, foundInRaidRequired, "Items");

    private static string Describe(
        IReadOnlyList<QuestObjectiveItemTarget> targets,
        bool? foundInRaidRequired,
        string itemPrefix)
    {
        ArgumentNullException.ThrowIfNull(targets);
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
                $"{group.Key.SourceField}: {string.Join(" or ", group
                    .OrderBy(target => target.SourceOrdinal)
                    .ThenBy(target => target.ItemId, StringComparer.Ordinal)
                    .Select(target => target.ItemId)
                    .Distinct(StringComparer.Ordinal))}")
            .ToArray();
        var requiredKeys = targets
            .Where(target => string.Equals(target.SourceField, "requiredKeys", StringComparison.Ordinal))
            .GroupBy(target => target.AlternativeGroup)
            .OrderBy(group => group.Min(target => target.SourceOrdinal))
            .ThenBy(group => group.Key)
            .Select(group => string.Join(" or ", group
                .OrderBy(target => target.SourceOrdinal)
                .ThenBy(target => target.ItemId, StringComparer.Ordinal)
                .Select(target => target.ItemId)
                .Distinct(StringComparer.Ordinal)))
            .ToArray();
        var fir = foundInRaidRequired == true ? " · found in raid required" : string.Empty;
        var itemNeed = itemGroups.Length == 0
            ? null
            : $"{itemPrefix} · {string.Join("; ", itemGroups)}{fir}";
        var keyNeed = requiredKeys.Length == 0 ? null : $"Required keys: {string.Join("; ", requiredKeys)}";
        return string.Join(" · ", new[] { itemNeed, keyNeed }.OfType<string>());
    }
}
