using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Quests;

public enum QuestHistoryInferenceReason
{
    ConfirmedActive,
    RequiredCompleted,
}

public sealed record QuestHistoryInferenceChange(
    string TaskId,
    string QuestName,
    RecordedTaskState PreviousState,
    RecordedTaskState NewState,
    QuestHistoryInferenceReason Reason);

public sealed record QuestHistoryInferencePreview(
    IReadOnlyList<QuestHistoryInferenceChange> Changes,
    IReadOnlyList<string> UnknownTaskIds)
{
    public int ActiveChanges => Changes.Count(change =>
        change.Reason == QuestHistoryInferenceReason.ConfirmedActive);

    public int EarlierQuestChanges => Changes.Count(change =>
        change.Reason == QuestHistoryInferenceReason.RequiredCompleted);
}

/// <summary>Previews the quest history implied by a confirmed list of active quests.</summary>
/// <remarks>
/// A TASKS screenshot is direct evidence only for the active rows it shows. Earlier quests are
/// inferred solely across catalog prerequisite edges that explicitly require completion. The
/// preview is deliberately inert: its caller must show these changes before writing any of them.
/// </remarks>
public sealed class QuestHistoryInference
{
    public QuestHistoryInferencePreview Preview(
        IEnumerable<string> confirmedActiveTaskIds,
        QuestCatalogSnapshot catalog,
        QuestProgressSnapshot progress)
    {
        ArgumentNullException.ThrowIfNull(confirmedActiveTaskIds);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(progress);

        var tasks = catalog.Tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var activeIds = confirmedActiveTaskIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var activeSet = activeIds.ToHashSet(StringComparer.Ordinal);
        var changes = new List<QuestHistoryInferenceChange>();
        var proposed = new HashSet<string>(StringComparer.Ordinal);
        var unknown = new List<string>();

        foreach (var taskId in activeIds)
        {
            if (!tasks.TryGetValue(taskId, out var task))
            {
                unknown.Add(taskId);
                continue;
            }

            AddChangeIfAllowed(
                task,
                RecordedTaskState.Active,
                QuestHistoryInferenceReason.ConfirmedActive,
                progress,
                changes,
                proposed);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var taskId in activeIds)
        {
            VisitCompletedPrerequisites(taskId);
        }

        return new(changes, unknown);

        void VisitCompletedPrerequisites(string taskId)
        {
            if (!visited.Add(taskId) || !tasks.TryGetValue(taskId, out var task))
            {
                return;
            }

            foreach (var requirement in task.Requirements.OrderBy(value => value.SourceOrdinal))
            {
                if (!RequiresCompletion(requirement) ||
                    !tasks.TryGetValue(requirement.RequiredTaskId, out var prerequisite))
                {
                    continue;
                }

                // A row the player just confirmed as active is stronger evidence than the
                // prerequisite graph. Keep it active and surface no contradictory mutation.
                if (!activeSet.Contains(prerequisite.Id))
                {
                    AddChangeIfAllowed(
                        prerequisite,
                        RecordedTaskState.Completed,
                        QuestHistoryInferenceReason.RequiredCompleted,
                        progress,
                        changes,
                        proposed);
                }

                VisitCompletedPrerequisites(prerequisite.Id);
            }
        }
    }

    private static bool RequiresCompletion(QuestTaskRequirement requirement) =>
        requirement.RequiredStatuses.Any(status =>
            status.Equals("complete", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("completed", StringComparison.OrdinalIgnoreCase));

    private static void AddChangeIfAllowed(
        QuestTaskDefinition task,
        RecordedTaskState target,
        QuestHistoryInferenceReason reason,
        QuestProgressSnapshot progress,
        ICollection<QuestHistoryInferenceChange> changes,
        ISet<string> proposed)
    {
        var previous = progress.Tasks.TryGetValue(task.Id, out var recorded)
            ? recorded.State
            : RecordedTaskState.Unknown;
        if (previous == target ||
            previous is RecordedTaskState.Completed or RecordedTaskState.Failed ||
            !proposed.Add(task.Id))
        {
            return;
        }

        changes.Add(new(task.Id, task.Name, previous, target, reason));
    }
}
