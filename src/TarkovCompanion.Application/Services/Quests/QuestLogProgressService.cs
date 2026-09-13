using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Quests;

/// <summary>
/// Records what the game says about the player's quests.
/// </summary>
/// <remarks>
/// Every quest on the page read Unknown because the stored progress was created empty and
/// nothing ever wrote to it again. The game has been announcing each quest starting, failing
/// and being handed in the whole time.
///
/// Two rules, and both exist because a log is replayed rather than streamed.
///
/// A message is acted on once. The game restates a notification when it redelivers one, and
/// the same files are re-read from the start whenever observation restarts.
///
/// A quest never goes backwards. An old "started" arriving after a hand-in would undo the
/// hand-in, and somebody watching their completed list empty itself would rightly stop
/// trusting the page. Finished and failed are where a quest stops.
/// </remarks>
public sealed class QuestLogProgressService(
    IPlayerProfileService profiles,
    IQuestProgressCommandService commands,
    ILogger<QuestLogProgressService> logger)
{
    private readonly HashSet<string> _applied = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecordedTaskState> _recorded = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>How many quests this session has recorded, for the page to report.</summary>
    public int Recorded { get; private set; }

    public async Task ApplyAsync(QuestStatusObservation observation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_applied.Add(observation.EventId) || !Advances(observation))
            {
                return;
            }

            var profile = await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
            var result = await commands
                .SetTaskStateAsync(scope, observation.TaskId, observation.State, cancellationToken)
                .ConfigureAwait(false);
            _recorded[observation.TaskId] = observation.State;
            if (!result.Changed)
            {
                // Already at that state, which is the ordinary outcome of re-reading a log
                // folder the companion has read before.
                return;
            }

            Recorded++;
            logger.LogInformation(
                "The game reported quest {Task} as {State}.",
                observation.TaskId,
                observation.State);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One unrecordable quest is one quest, not the end of observing the logs.
            logger.LogWarning(exception, "Could not record what the game said about a quest.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Whether this is news, rather than an older message arriving again.
    /// </summary>
    /// <remarks>
    /// Only within the session. What is already stored belongs to the player, who may have
    /// recorded it by hand or imported it, and the game's own word about their own quest is
    /// the better authority when the two disagree.
    /// </remarks>
    private bool Advances(QuestStatusObservation observation) =>
        !_recorded.TryGetValue(observation.TaskId, out var seen) ||
        seen is not (RecordedTaskState.Completed or RecordedTaskState.Failed);
}
