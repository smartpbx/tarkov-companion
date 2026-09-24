using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

/// <summary>
/// [#802] "On it": the player saying which quest they are doing, so its objectives reach the Raid map.
/// </summary>
/// <remarks>
/// The Raid objectives layer draws a quest only when it is recorded Active or is pinned (see
/// <c>QuestReadService.GetActiveMapObjectivesAsync</c>). With no game log nothing ever records a
/// quest Active, so a quest found through Plan's search stayed "Unknown" and "Open in Raid" opened a
/// map with nothing on it. The only way in was "Start quest" in a row's "More actions" menu.
/// "On it" records both, Active and pinned, so a later log line that disagrees about the state still
/// leaves the quest on the map until the player unpins it or the game says it is finished.
/// </remarks>
public static class PlanQuestOnIt
{
    /// <summary>How many untracked quests "Open in Raid" puts on the map by itself: a search's worth, not a filter's.</summary>
    public const int OpenInRaidLimit = 3;

    /// <summary>Whether the Raid objectives layer draws this quest: the same rule the read service applies.</summary>
    public static bool ShowsInRaid(QuestSummaryReadModel task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.RecordedState is not (RecordedTaskState.Completed or RecordedTaskState.Failed) &&
            (task.RecordedState == RecordedTaskState.Active ||
             task.IsPinned ||
             task.Objectives.Any(objective => objective.IsPinned));
    }

    /// <summary>Anything not already drawn and not handed in; a failed quest can be taken again.</summary>
    public static bool CanPutOnIt(QuestSummaryReadModel task) =>
        !ShowsInRaid(task) && task.RecordedState != RecordedTaskState.Completed;

    /// <summary>
    /// The quests "Open in Raid" puts on the map before it opens: the untracked ones in the group,
    /// but only when a search or filter has narrowed them to a few. Pressing it on "All" with
    /// thirty quests on Customs must not start thirty quests.
    /// </summary>
    public static IReadOnlyList<QuestSummaryReadModel> ForOpenInRaid(IEnumerable<QuestSummaryReadModel> groupQuests)
    {
        ArgumentNullException.ThrowIfNull(groupQuests);
        var untracked = groupQuests
            .DistinctBy(task => task.TaskId, StringComparer.Ordinal)
            .Where(CanPutOnIt)
            .ToArray();
        return untracked.Length <= OpenInRaidLimit ? untracked : [];
    }

    /// <summary>Records the quest Active and pins it, each only where it is not already so.</summary>
    public static async Task ApplyAsync(
        IQuestProgressCommandService commands,
        QuestProfileScope scope,
        QuestSummaryReadModel task,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(task);
        if (task.RecordedState != RecordedTaskState.Active)
        {
            await commands.SetTaskStateAsync(scope, task.TaskId, RecordedTaskState.Active, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!task.IsPinned)
        {
            await commands.SetPinAsync(scope, QuestPinTargetKind.Task, task.TaskId, true, 0, null, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
