using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Planning;

/// <summary>
/// What a set of quest objectives asks the player to have on them, with how much of it they hold.
/// </summary>
/// <remarks>
/// Keys, weapons, worn gear and markers are carried in, one of each; everything else is handed
/// over, as many as the objective still needs. Alternatives ("this or that") are one requirement
/// satisfied by any of them. The same item asked for by two objectives is one requirement,
/// because the player holds one pile of it. "Not wearing" and container-content conditions name
/// nothing to have, and a quest-only item cannot be had before the raid, so they are left out. Bring, Hand in and Find in raid stay three separate
/// requirements even for the same item: they are needed at different moments.
///
/// This lived in the Plan view model until #307 moved it here, so the same answer can feed the
/// Keep list, a pre-raid brief or the tablet without each recomputing it. The result is ordered by
/// item id, not by name, because naming an item is a catalog read this planner has no business making.
/// </remarks>
public static class QuestRequirementPlanner
{
    /// <param name="handedOverByItsTask">
    /// For an objective, the items its own quest also has a hand-over objective for. A find
    /// objective asks for nothing new of those: "find 3 Salewa in raid" beside "hand over 3 Salewa"
    /// is three, and adding them read as "0 / 6". Null where the caller cannot say, which keeps both.
    /// </param>
    public static IReadOnlyList<PlannedRequirement> Build(
        IEnumerable<QuestObjectiveReadModel> objectives,
        IReadOnlyDictionary<string, int> owned,
        Func<QuestObjectiveReadModel, IReadOnlySet<string>>? handedOverByItsTask = null)
    {
        ArgumentNullException.ThrowIfNull(objectives);
        ArgumentNullException.ThrowIfNull(owned);

        var rows = new Dictionary<(string ItemId, RequirementHandling Handling), PlannedRequirement>();
        foreach (var objective in objectives.Where(objective => objective.RecordedState != RecordedObjectiveState.Completed))
        {
            var targets = objective.ItemTargets.Where(target =>
                !QuestItemTargetFields.IsCondition(target.SourceField) &&
                !QuestItemTargetFields.IsQuestOnlyItem(target.SourceField));
            foreach (var alternatives in targets.GroupBy(target => (target.SourceField, target.AlternativeGroup)))
            {
                var ids = alternatives
                    .OrderBy(target => target.SourceOrdinal)
                    .ThenBy(target => target.ItemId, StringComparer.Ordinal)
                    .Select(target => target.ItemId)
                    .Distinct(StringComparer.Ordinal)
                    .Where(id => objective.Kind != QuestObjectiveKind.FindItem ||
                                 handedOverByItsTask is null ||
                                 !handedOverByItsTask(objective).Contains(id))
                    .ToArray();
                if (ids.Length == 0)
                {
                    continue;
                }

                var carried = QuestItemTargetFields.IsCarriedIn(alternatives.Key.SourceField);
                var handling = carried
                    ? RequirementHandling.Bring
                    : objective.FoundInRaidRequired == true ? RequirementHandling.FindInRaid : RequirementHandling.HandIn;
                var need = carried
                    ? 1
                    : (int)Math.Ceiling(Math.Max(
                        1m,
                        (alternatives.Max(target => target.TargetCount) ?? objective.TargetCount ?? 1m) - (objective.RecordedCount ?? 0m)));
                var have = ids.Sum(id => owned.GetValueOrDefault(id));
                var key = (ids[0], handling);
                rows[key] = rows.TryGetValue(key, out var existing)
                    ? existing with { Need = carried ? existing.Need : existing.Need + need }
                    : new PlannedRequirement(ids, handling, need, have);
            }
        }

        return
        [
            .. rows.Values
                .OrderBy(row => row.IsSatisfied)
                .ThenBy(row => row.PrimaryItemId, StringComparer.Ordinal)
                .ThenBy(row => row.Handling),
        ];
    }
}
