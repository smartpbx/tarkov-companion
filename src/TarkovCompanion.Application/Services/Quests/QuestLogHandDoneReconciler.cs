using Microsoft.Extensions.Logging;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Quests;

/// <summary>
/// Clears the player's hand "Done" marks on a quest the game reports failed or started again.
/// </summary>
/// <remarks>
/// [Issue 571] A hand mark says "I already did this objective". A failed quest has to be done
/// again from the start, and a restarted one is a fresh attempt, so an old mark would hide an
/// objective the player now still needs. Nothing is inferred from position or time: only the
/// game's own quest notification, as recorded by <see cref="QuestLogProgressService"/>, clears.
/// A completed quest needs nothing here: it is no longer active, so its objectives leave the
/// map and the list on the next quest-layer refresh anyway.
/// </remarks>
public static class QuestLogHandDoneReconciler
{
    public static void Attach(
        QuestLogProgressService questLog,
        IHandDoneObjectiveStore handDone,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(questLog);
        ArgumentNullException.ThrowIfNull(handDone);
        ArgumentNullException.ThrowIfNull(logger);
        questLog.TaskRecorded += (_, recorded) => _ = ClearAsync(handDone, recorded, logger);
    }

    /// <summary>Whether a recorded state voids the player's earlier hand marks on that quest.</summary>
    public static bool Voids(RecordedTaskState state) =>
        state is RecordedTaskState.Failed or RecordedTaskState.Active;

    private static async Task ClearAsync(IHandDoneObjectiveStore handDone, QuestLogTaskRecorded recorded, ILogger logger)
    {
        if (!Voids(recorded.State))
        {
            return;
        }

        try
        {
            // Loaded first: the startup replay reports quests before any page has loaded the
            // marks, and an unloaded store would look empty and clear nothing.
            await handDone.LoadAsync().ConfigureAwait(false);
            if (!handDone.Entries.Any(mark =>
                    mark.ProfileId == recorded.ProfileId &&
                    string.Equals(mark.TaskId, recorded.TaskId, StringComparison.Ordinal)))
            {
                return;
            }

            await handDone.ClearForTaskAsync(recorded.ProfileId, recorded.TaskId).ConfigureAwait(false);
            logger.LogInformation(
                "Cleared hand Done marks on quest {Task}: the game reported it {State}.",
                recorded.TaskId,
                recorded.State);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not clear hand Done marks on quest {Task}.", recorded.TaskId);
        }
    }
}
