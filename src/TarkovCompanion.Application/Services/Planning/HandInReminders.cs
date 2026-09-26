using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Planning;

/// <summary>Why a quest is worth a trip to its trader.</summary>
public enum HandInReason
{
    /// <summary>Every required objective is recorded done; only the trader's button is left.</summary>
    Ready,

    /// <summary>Everything else is done and the recorded stash holds what the trader wants handed over.</summary>
    ItemsHeld,
}

/// <summary>"Golden Swag is ready to hand in to Skier."</summary>
public sealed record HandInReminder(string TaskId, string TaskName, string? TraderId, string TraderLabel, HandInReason Reason);

/// <summary>
/// [#712 T4, 2-3] Quests the player's own progress says are finished but not handed in.
/// </summary>
/// <remarks>
/// Only the local player's recorded progress and stash counts are read. A quest is <see
/// cref="HandInReason.Ready"/> when the board already says its required objectives are satisfied.
/// It is <see cref="HandInReason.ItemsHeld"/> when everything but its hand-over objectives is done
/// and the recorded holding meets each of them; an item nobody recorded is not held
/// (<see cref="HeldCount"/>), so an unknown stash never produces a reminder. Found-in-raid status is
/// not in the holdings, which is why that reason reads "in your stash" and not "ready".
/// </remarks>
public static class HandInReminders
{
    public static IReadOnlyList<HandInReminder> Find(
        IReadOnlyList<QuestSummaryReadModel> tasks,
        IReadOnlyDictionary<string, int> held)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(held);
        var reminders = new List<HandInReminder>();
        foreach (var task in tasks)
        {
            if (task.RecordedState != RecordedTaskState.Active)
            {
                continue;
            }

            HandInReason? reason = task.RecordedObjectivesSatisfied == RecordedObjectivesSatisfaction.Satisfied
                ? HandInReason.Ready
                : ItemsInHand(task, held) ? HandInReason.ItemsHeld : null;
            if (reason is { } found)
            {
                var trader = string.IsNullOrWhiteSpace(task.TraderName) ? task.TraderId ?? string.Empty : task.TraderName;
                reminders.Add(new(task.TaskId, task.Name, task.TraderId, trader, found));
            }
        }

        return
        [
            .. reminders
                .OrderBy(reminder => reminder.Reason)
                .ThenBy(reminder => reminder.TraderLabel, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(reminder => reminder.TaskName, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    private static bool ItemsInHand(QuestSummaryReadModel task, IReadOnlyDictionary<string, int> held)
    {
        var required = task.Objectives.Where(objective => objective.IsOptional != true).ToArray();
        var handOvers = required
            .Where(objective => objective.Kind == QuestObjectiveKind.GiveItem
                && objective.RecordedState != RecordedObjectiveState.Completed)
            .ToArray();
        if (handOvers.Length == 0)
        {
            return false;
        }

        if (required.Except(handOvers).Any(objective => objective.RecordedState != RecordedObjectiveState.Completed))
        {
            return false;
        }

        foreach (var objective in handOvers)
        {
            var need = (int)Math.Ceiling(Math.Max(0m, (objective.TargetCount ?? 1m) - (objective.RecordedCount ?? 0m)));
            var itemIds = objective.ItemTargets.Select(target => target.ItemId).Distinct(StringComparer.Ordinal).ToArray();
            if (itemIds.Length == 0 || !HeldCount.Meets(need, HeldCount.OfAny(held, itemIds)))
            {
                return false;
            }
        }

        return true;
    }
}
