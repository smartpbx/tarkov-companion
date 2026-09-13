using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>
/// What the player is working on, for the group to see.
/// </summary>
/// <remarks>
/// The quest switch on the Group page has sent an empty list since it was built, because there
/// was no quest progress to send: the store was created empty and nothing wrote to it. Now
/// that the game's own notifications record every quest starting and being handed in, there
/// is.
///
/// Pinned first and then active, because a pin is the player saying which one they are
/// actually doing and a squad reads the first two lines of this. Completed and failed quests
/// are not shared: the question a group asks is "what are you on", not "what have you done".
///
/// Read at most once a minute. The publish loop runs every few seconds and quest progress
/// changes a handful of times a raid, so asking the database on every exchange would be
/// hundreds of queries for an answer that almost never differs.
/// </remarks>
public sealed class GroupQuestShare(
    IPlayerProfileService profiles,
    IQuestReadService quests,
    TimeProvider? timeProvider = null)
{
    /// <summary>How many are worth sending, which is what fits in a squadmate's panel.</summary>
    private const int Maximum = 5;

    private static readonly TimeSpan RereadAfter = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<string> _shared = [];
    private DateTimeOffset _readUtc = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<string>> GetAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (now - _readUtc < RereadAfter)
        {
            return _shared;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_timeProvider.GetUtcNow() - _readUtc < RereadAfter)
            {
                return _shared;
            }

            var profile = await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
            var board = await quests.GetQuestBoardAsync(scope, cancellationToken).ConfigureAwait(false);
            _shared = Choose(board.Tasks);
            _readUtc = _timeProvider.GetUtcNow();
            return _shared;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A group exchange must not fail because the quest board could not be read. The
            // last answer stands and the next minute tries again.
            _readUtc = _timeProvider.GetUtcNow();
            return _shared;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Pinned first, then active, and never more than fits in a panel.</summary>
    private static IReadOnlyList<string> Choose(IReadOnlyList<QuestSummaryReadModel> tasks) => tasks
        .Where(task => task.IsPinned || task.RecordedState == RecordedTaskState.Active)
        .OrderByDescending(task => task.IsPinned)
        .ThenBy(task => task.Name, StringComparer.CurrentCultureIgnoreCase)
        .Select(task => task.Name)
        .Take(Maximum)
        .ToArray();
}
