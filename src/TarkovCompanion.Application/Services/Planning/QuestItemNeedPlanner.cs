using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Planning;

/// <summary>What the quest board says each open quest still asks the player to have, item by item.</summary>
/// <param name="Requirements">One row per task, objective and item. Alternatives are one row each.</param>
/// <param name="InterchangeableCounts">
/// For a row that is one of several items which would each do, how many would: "any of 5". A row
/// not listed here is the only item that satisfies its objective.
/// </param>
/// <param name="RecordedProgress">What the board has recorded against each objective, by objective id.</param>
public sealed record QuestItemNeeds(
    IReadOnlyList<QuestItemRequirement> Requirements,
    IReadOnlyDictionary<(string TaskId, string ItemId), int> InterchangeableCounts,
    IReadOnlyDictionary<string, int> RecordedProgress)
{
    /// <summary>The rows that are carried in and back out (a key): one serves every quest that asks.</summary>
    public IReadOnlySet<(string TaskId, string ItemId)> Reusable { get; init; } = new HashSet<(string, string)>();
}

/// <summary>
/// Reads quest item needs off the quest board, which is where the Plan page reads them too.
/// </summary>
/// <remarks>
/// The Keep list used to read <c>task_objective_items</c> through the requirement catalog, and on
/// a database that had been migrated rather than freshly synced it showed no quest rows at all.
/// Migration 0013 drops <c>task_objectives</c> before it copies the child table, and with foreign
/// keys on (they always are) that drop cascades: measured on the 2026-09-14 seed, 15,177 rows go
/// in and 0 come out. A sync rewrites the table, so a player is without quest needs from the
/// upgrade until the next successful sync. The board's <c>quest_catalog_*</c> tables are untouched
/// by that migration, are scoped to the profile's game mode, and carry keys and markers the old
/// table never held, so this reads them instead. The migration itself is reported, not fixed here.
///
/// Three rules, each from counting the real catalog rather than from guessing:
///
/// A find objective whose item the same task also hands over asks for nothing new. 115 of 317
/// single-item needs are such a pair ("Shortage": find 3 Salewa in raid, hand over 3 Salewa), and
/// adding them told the player to keep 6. The hand-over governs, because a player who has found
/// them but not handed them in is holding them and must still not sell them.
///
/// A requirement met by more than <see cref="MaximumNamedAlternatives"/> items is a category, not
/// an item. 33 of 573 requirements are, and they hold 14,449 of the 15,180 targets: "Sell any
/// items to Ragman" alone is 3,535. One row each would bury the list, so they name nothing here.
/// The sizes run ...15, 17, 18, 19, 22, 23... with no gap to cut at; twenty keeps figurines,
/// branded gear and boss-guard helmets, and drops "any eyewear" (40) and everything above it.
///
/// Selling is not keeping, and an optional objective is not a need. A quest for the other
/// faction is not a need either: the board locks it with the reason "faction", this profile can
/// never take it, and its BEAR and USEC copies share a name, so it showed as the same quest twice.
///
/// A key is one key. It is asked for once per quest however many of the quest's objectives are
/// behind its door, and not at all by a quest that also hands it over, which already counts it.
/// </remarks>
public static class QuestItemNeedPlanner
{
    public const int MaximumNamedAlternatives = 20;

    public static QuestItemNeeds FromBoard(IEnumerable<QuestSummaryReadModel> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var requirements = new List<QuestItemRequirement>();
        var interchangeable = new Dictionary<(string, string), int>();
        var progress = new Dictionary<string, int>(StringComparer.Ordinal);
        var reusable = new HashSet<(string, string)>();
        foreach (var task in tasks.Where(CanStillBeDone))
        {
            var handedOver = HandedOverItemIds(task);
            foreach (var objective in task.Objectives.Where(Asks))
            {
                if (objective.RecordedCount is > 0)
                {
                    progress[objective.ObjectiveId] = Math.Max(
                        progress.GetValueOrDefault(objective.ObjectiveId),
                        (int)Math.Floor(objective.RecordedCount.Value));
                }

                foreach (var alternatives in NamedAlternatives(objective))
                {
                    var carried = QuestItemTargetFields.IsCarriedIn(alternatives.Key.SourceField);
                    var carriedBackOut = QuestItemTargetFields.IsReusable(alternatives.Key.SourceField);
                    var ids = alternatives
                        .Select(target => target.ItemId)
                        .Distinct(StringComparer.Ordinal)
                        .Where(id => objective.Kind != QuestObjectiveKind.FindItem || !handedOver.Contains(id))
                        // One key per quest, and none where the quest hands the same key over.
                        .Where(id => !carriedBackOut || (!handedOver.Contains(id) && !reusable.Contains((task.TaskId, id))))
                        .ToArray();
                    var required = carried
                        ? 1
                        : (int)Math.Ceiling(Math.Max(1m, alternatives.Max(target => target.TargetCount) ?? objective.TargetCount ?? 1m));
                    var foundInRaid = !carried &&
                        (alternatives.Any(target => target.FoundInRaidRequired == true) || objective.FoundInRaidRequired == true);
                    foreach (var id in ids)
                    {
                        requirements.Add(new(task.TaskId, objective.ObjectiveId, id, required, foundInRaid));
                        if (carriedBackOut)
                        {
                            reusable.Add((task.TaskId, id));
                        }

                        if (ids.Length > 1)
                        {
                            interchangeable[(task.TaskId, id)] = ids.Length;
                        }
                    }
                }
            }
        }

        return new QuestItemNeeds(requirements, interchangeable, progress) { Reusable = reusable };
    }

    /// <summary>
    /// The lifetime quantity every catalog quest asks for, including tasks and objectives already
    /// completed. All other need rules stay the same, so faction-only and category objectives do
    /// not reappear merely because this is a denominator rather than today's keep list.
    /// </summary>
    public static IReadOnlyDictionary<string, int> TotalsByItem(IEnumerable<QuestSummaryReadModel> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var lifetimeTasks = tasks.Select(task => task with
        {
            RecordedState = RecordedTaskState.NotStarted,
            Objectives = [.. task.Objectives.Select(objective => objective with
            {
                RecordedState = RecordedObjectiveState.InProgress,
                RecordedCount = null,
            })],
        });
        return FromBoard(lifetimeTasks).Requirements
            .GroupBy(requirement => requirement.ItemId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(requirement => requirement.Required), StringComparer.Ordinal);
    }

    /// <summary>Not finished, and not a quest only the other faction is ever offered.</summary>
    private static bool CanStillBeDone(QuestSummaryReadModel task) =>
        task.RecordedState != RecordedTaskState.Completed &&
        !task.Eligibility.Reasons.Any(reason => string.Equals(reason.Code, "faction", StringComparison.Ordinal));

    /// <summary>
    /// The groups of targets on an objective that name particular items: not a condition, not a
    /// quest-only item, and not a whole category.
    /// </summary>
    public static IEnumerable<IGrouping<(string SourceField, int AlternativeGroup), QuestObjectiveItemTarget>> NamedAlternatives(
        QuestObjectiveReadModel objective)
    {
        ArgumentNullException.ThrowIfNull(objective);
        return objective.ItemTargets
            .Where(target =>
                !QuestItemTargetFields.IsCondition(target.SourceField) &&
                !QuestItemTargetFields.IsQuestOnlyItem(target.SourceField))
            .GroupBy(target => (target.SourceField, target.AlternativeGroup))
            .Where(group => group.Select(target => target.ItemId).Distinct(StringComparer.Ordinal).Count() <= MaximumNamedAlternatives);
    }

    /// <summary>Every item some hand-over objective of the task names, finished or not.</summary>
    /// <remarks>
    /// A finished hand-over still counts: the items are gone, so the find objective beside it has
    /// nothing left to ask for either.
    /// </remarks>
    public static IReadOnlySet<string> HandedOverItemIds(QuestSummaryReadModel task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.Objectives
            .Where(objective => objective.Kind == QuestObjectiveKind.GiveItem)
            .SelectMany(objective => objective.ItemTargets)
            .Select(target => target.ItemId)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool Asks(QuestObjectiveReadModel objective) =>
        objective.RecordedState != RecordedObjectiveState.Completed &&
        // Only a step the catalog says is optional is left out. One it says nothing about is kept:
        // on a Keep list the costly mistake is selling what a quest turns out to need.
        objective.IsOptional != true &&
        objective.Kind != QuestObjectiveKind.SellItem;
}
