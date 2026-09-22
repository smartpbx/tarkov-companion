using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

/// <summary>
/// A freshly read quest board with every quest that reads the same as last time replaced by last
/// time's instance.
/// </summary>
/// <remarks>
/// [#453] Every visit to Plan reads the board again, and every read hands back new instances of all
/// 515 quests. The page keeps a row only when its quest and objective are the same instances
/// (<see cref="PlanWorkspaceViewModel.ComposeGroups"/>), so each return to the page built every
/// group and row again and the view built every card again: about 900 ms on the interface thread
/// for a board that had not changed at all. Carrying the old instances over where the values are
/// equal makes an unchanged board compose to the very same groups, and a board where one quest
/// moved rebuild only that quest's rows.
///
/// The records compare their lists by reference, so the comparison here walks them.
/// </remarks>
internal static class QuestBoardReconciler
{
    /// <summary>
    /// <paramref name="next"/>, with each task taken from <paramref name="previous"/> where it is
    /// equal in value. Returns <paramref name="previous"/> itself when nothing at all differs.
    /// </summary>
    public static QuestBoardReadModel Reconcile(QuestBoardReadModel? previous, QuestBoardReadModel next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (previous is null || ReferenceEquals(previous, next))
        {
            return next;
        }

        var earlier = new Dictionary<string, QuestSummaryReadModel>(previous.Tasks.Count, StringComparer.Ordinal);
        foreach (var task in previous.Tasks)
        {
            earlier.TryAdd(task.TaskId, task);
        }

        var tasks = new QuestSummaryReadModel[next.Tasks.Count];
        var allKept = next.Tasks.Count == previous.Tasks.Count;
        for (var index = 0; index < tasks.Length; index++)
        {
            var task = next.Tasks[index];
            if (earlier.TryGetValue(task.TaskId, out var old) && SameTask(old, task))
            {
                tasks[index] = old;
                allKept &= ReferenceEquals(previous.Tasks[index], old);
            }
            else
            {
                tasks[index] = task;
                allKept = false;
            }
        }

        if (allKept
            && Equals(previous.Scope, next.Scope)
            && previous.ProgressRevision == next.ProgressRevision
            && Equals(previous.CatalogProvenance, next.CatalogProvenance)
            && previous.UnavailableReason == next.UnavailableReason
            && previous.OrphanedProgress.SequenceEqual(next.OrphanedProgress))
        {
            return previous;
        }

        return next with { Tasks = tasks };
    }

    internal static bool SameTask(QuestSummaryReadModel left, QuestSummaryReadModel right)
    {
        // Every scalar through the record's own equality, with the lists swapped for the other
        // side's so only they are left to walk.
        if (left != right with
            {
                Eligibility = left.Eligibility,
                FailureConditionNotes = left.FailureConditionNotes,
                Prerequisites = left.Prerequisites,
                Objectives = left.Objectives,
            })
        {
            return false;
        }

        if (left.Eligibility.State != right.Eligibility.State
            || left.Eligibility.AvailableUtc != right.Eligibility.AvailableUtc
            || !left.Eligibility.Reasons.SequenceEqual(right.Eligibility.Reasons)
            || !left.FailureConditionNotes.SequenceEqual(right.FailureConditionNotes, StringComparer.Ordinal)
            || left.Prerequisites.Count != right.Prerequisites.Count
            || left.Objectives.Count != right.Objectives.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Prerequisites.Count; index++)
        {
            var a = left.Prerequisites[index];
            var b = right.Prerequisites[index];
            if (a != b with { RequiredStatuses = a.RequiredStatuses }
                || !a.RequiredStatuses.SequenceEqual(b.RequiredStatuses, StringComparer.Ordinal))
            {
                return false;
            }
        }

        for (var index = 0; index < left.Objectives.Count; index++)
        {
            var a = left.Objectives[index];
            var b = right.Objectives[index];
            if (a != b with { MapIds = a.MapIds, ItemTargets = a.ItemTargets }
                || !a.MapIds.SequenceEqual(b.MapIds, StringComparer.Ordinal)
                || !a.ItemTargets.SequenceEqual(b.ItemTargets))
            {
                return false;
            }
        }

        return true;
    }
}
