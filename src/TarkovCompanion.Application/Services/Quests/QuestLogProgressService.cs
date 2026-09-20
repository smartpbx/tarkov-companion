using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Quests;

/// <summary>What this session has learned about quests from the game's own logs.</summary>
/// <param name="Observed">Quest notifications read, whatever came of them.</param>
/// <param name="Recorded">Quests whose recorded state this session actually changed.</param>
/// <param name="Unmatched">Quests the game named that the loaded catalog does not have.</param>
/// <param name="Failed">Quests that could not be written down at all.</param>
/// <param name="LastObservedUtc">When the game last said anything about a quest.</param>
/// <param name="LastRecordedUtc">When a quest's state last actually changed because of it.</param>
public sealed record QuestLogProgressReading(
    int Observed,
    int Recorded,
    int Unmatched,
    int Failed,
    DateTimeOffset? LastObservedUtc,
    DateTimeOffset? LastRecordedUtc)
{
    public static readonly QuestLogProgressReading Nothing = new(0, 0, 0, 0, null, null);

    /// <summary>Whether the game has said anything at all about a quest this session.</summary>
    public bool HeardAnything => Observed > 0;
}

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
///
/// Everything is counted, including what came to nothing. A player finished quests in a raid,
/// the companion recorded none of them, and nothing anywhere said so: zero recorded and zero
/// read look identical from the outside, which is how it went a day unnoticed. Four counts and
/// two times are kept so the page can state which of those happened, and <see cref="Changed"/>
/// exists so a board already on screen does not have to be navigated away from and back to
/// before it shows a hand-in.
/// </remarks>
public sealed class QuestLogProgressService(
    IPlayerProfileService profiles,
    IQuestProgressCommandService commands,
    ILogger<QuestLogProgressService> logger)
{
    private readonly HashSet<string> _applied = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecordedTaskState> _recorded = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Raised after a quest's recorded state changed because the game said so.
    /// </summary>
    /// <remarks>
    /// The board refreshes itself after every mutation it makes, which covers the player
    /// editing a quest on the page and covers nothing that arrives from a log while they are
    /// looking at it. Raised on the reading thread, so a handler marshals to the UI itself.
    /// </remarks>
    public event EventHandler<QuestLogProgressReading>? Changed;

    /// <summary>How many quests this session has recorded, for the page to report.</summary>
    public int Recorded { get; private set; }

    /// <summary>Everything this session has heard and made of it.</summary>
    public QuestLogProgressReading Reading { get; private set; } = QuestLogProgressReading.Nothing;

    public async Task ApplyAsync(QuestStatusObservation observation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var changed = false;
        try
        {
            // Counted before anything can reject it. "The game told me about eleven quests and
            // I recorded none of them" is the sentence that was impossible to say before, and
            // it is the one that names the problem.
            Reading = Reading with
            {
                Observed = Reading.Observed + 1,
                LastObservedUtc = Later(Reading.LastObservedUtc, observation.ObservedUtc),
            };

            if (!_applied.Add(observation.EventId) || !Advances(observation))
            {
                return;
            }

            var profile = await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
            var result = await commands
                .SetTaskStateAsync(
                    scope,
                    observation.TaskId,
                    observation.State,
                    QuestProgressActor.GameLog,
                    QuestProgressSources.GameLog,
                    cancellationToken)
                .ConfigureAwait(false);
            _recorded[observation.TaskId] = observation.State;
            if (!result.Changed)
            {
                // Already at that state, which is the ordinary outcome of re-reading a log
                // folder the companion has read before.
                return;
            }

            Recorded++;
            Reading = Reading with
            {
                Recorded = Reading.Recorded + 1,
                LastRecordedUtc = Later(Reading.LastRecordedUtc, observation.ObservedUtc),
            };
            changed = true;
            logger.LogInformation(
                "The game reported quest {Task} as {State}.",
                observation.TaskId,
                observation.State);
        }
        catch (QuestCatalogEntryUnknownException unknown)
        {
            // A catalog older than the patch the player is on. Not retried, because re-reading
            // the same line will not add the quest to the catalog, and counted on its own so
            // the page can say the catalog is behind rather than say nothing.
            Reading = Reading with { Unmatched = Reading.Unmatched + 1 };
            logger.LogWarning(
                "The game reported quest {Task}, which the loaded catalog does not have.",
                unknown.EntryId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One unrecordable quest is one quest, not the end of observing the logs. Forgotten
            // rather than remembered as applied, so a redelivery of the same notification gets
            // another attempt at whatever went wrong.
            _applied.Remove(observation.EventId);
            Reading = Reading with { Failed = Reading.Failed + 1 };
            logger.LogWarning(exception, "Could not record what the game said about a quest.");
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            // Outside the gate: a handler that refreshes a board must not be holding the lock
            // the next line off the log needs.
            Changed?.Invoke(this, Reading);
        }
    }

    private static DateTimeOffset Later(DateTimeOffset? known, DateTimeOffset candidate) =>
        known is { } seen && seen > candidate ? seen : candidate;

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
