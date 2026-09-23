using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Planning;

/// <summary>
/// Names each quest's place in the player's progression. Recorded completion and activity win;
/// eligibility then separates something startable now, delayed until later, definitely blocked,
/// and unknown. An active quest whose catalog objective requires an unrecorded trader loyalty
/// level is blocked rather than silently treated as ready.
/// </summary>
public static class QuestStatePlanner
{
    public static IReadOnlyDictionary<string, QuestStatePlan> Plan(
        IEnumerable<QuestSummaryReadModel> tasks,
        IReadOnlyDictionary<string, int> traderLevels,
        Func<string, string?>? nameOfTask = null)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(traderLevels);
        nameOfTask ??= static _ => null;
        return tasks
            .OrderBy(task => task.TaskId, StringComparer.Ordinal)
            .ToDictionary(
                task => task.TaskId,
                task => Derive(task, traderLevels, nameOfTask),
                StringComparer.Ordinal);
    }

    public static QuestStatePlan Derive(
        QuestSummaryReadModel task,
        IReadOnlyDictionary<string, int> traderLevels,
        Func<string, string?>? nameOfTask = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(traderLevels);
        nameOfTask ??= static _ => null;

        if (task.RecordedState == RecordedTaskState.Completed)
        {
            return new(task.TaskId, QuestPlanState.Completed, []);
        }

        var loyalty = TraderLoyaltyBlockers(task, traderLevels);
        if (task.RecordedState == RecordedTaskState.Active)
        {
            return loyalty.Count == 0
                ? new(task.TaskId, QuestPlanState.Current, [])
                : new(task.TaskId, QuestPlanState.Blocked, loyalty);
        }

        if (task.RecordedState == RecordedTaskState.Failed)
        {
            return new(task.TaskId, QuestPlanState.Blocked,
                [new(QuestPlanBlockerKind.Other, "recorded failed; restart or reset it")]);
        }

        return task.Eligibility.State switch
        {
            QuestEligibilityState.Available => new(task.TaskId, QuestPlanState.Next, []),
            QuestEligibilityState.Delayed => new(task.TaskId, QuestPlanState.Future,
                [new(QuestPlanBlockerKind.Other, "waiting on its availability timer")]),
            QuestEligibilityState.Locked => new(task.TaskId, QuestPlanState.Blocked,
                EligibilityBlockers(task, nameOfTask)),
            _ => new(task.TaskId, QuestPlanState.Unknown,
                EligibilityBlockers(task, nameOfTask)),
        };
    }

    private static IReadOnlyList<QuestPlanBlocker> TraderLoyaltyBlockers(
        QuestSummaryReadModel task,
        IReadOnlyDictionary<string, int> traderLevels) =>
    [
        .. task.Objectives
            .Where(objective => objective.RecordedState != RecordedObjectiveState.Completed &&
                objective.Kind == QuestObjectiveKind.TraderLevel &&
                objective.RequiredTraderId is { Length: > 0 } &&
                objective.RequiredTraderLevel is > 0)
            .Where(objective => traderLevels.GetValueOrDefault(objective.RequiredTraderId!) < objective.RequiredTraderLevel)
            .OrderBy(objective => objective.RequiredTraderId, StringComparer.Ordinal)
            .ThenBy(objective => objective.RequiredTraderLevel)
            .Select(objective => new QuestPlanBlocker(
                QuestPlanBlockerKind.TraderLoyalty,
                traderLevels.TryGetValue(objective.RequiredTraderId!, out var recorded)
                    ? $"{objective.RequiredTraderId} LL{objective.RequiredTraderLevel} (recorded LL{recorded})"
                    : $"{objective.RequiredTraderId} LL{objective.RequiredTraderLevel} (loyalty not recorded)",
                objective.RequiredTraderId)),
    ];

    private static IReadOnlyList<QuestPlanBlocker> EligibilityBlockers(
        QuestSummaryReadModel task,
        Func<string, string?> nameOfTask)
    {
        var steps = QuestUnlockPlanner.Steps(task, nameOfTask);
        return
        [
            .. steps.Select(step => new QuestPlanBlocker(
                step.Kind switch
                {
                    QuestUnlockKind.Level => QuestPlanBlockerKind.PlayerLevel,
                    QuestUnlockKind.Quest => QuestPlanBlockerKind.Prerequisite,
                    _ => QuestPlanBlockerKind.Other,
                },
                step.Label,
                step.TaskId)),
        ];
    }
}
