using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>
/// What the player is working on, in the two forms a group needs it in.
/// </summary>
/// <remarks>
/// The names are read by a person and the ids are read by a companion. A squadmate's panel
/// holds about five lines, so that is how many names there is any point sending; the ids are
/// counted rather than read, and ranking tonight's maps by where the group overlaps wants the
/// whole active list rather than the top of it.
/// </remarks>
/// <param name="Names">The first few, pinned first, for a squadmate to read.</param>
/// <param name="TaskIds">All of them, by catalog id, for a squadmate's companion to place.</param>
public sealed record SharedQuests(IReadOnlyList<string> Names, IReadOnlyList<string> TaskIds)
{
    public static SharedQuests None { get; } = new([], []);
}

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
    /// <summary>How many names are worth sending, which is what fits in a squadmate's panel.</summary>
    private const int Maximum = 5;

    /// <summary>
    /// How many ids are worth sending.
    /// </summary>
    /// <remarks>
    /// More than the names, because an id is not read. Forty is more quests than anybody has
    /// open at once, and it is the bound the wire contract enforces on the other end.
    /// </remarks>
    private const int MaximumIds = 40;

    private static readonly TimeSpan RereadAfter = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SharedQuests _shared = SharedQuests.None;
    private DateTimeOffset _readUtc = DateTimeOffset.MinValue;

    public async Task<SharedQuests> GetAsync(CancellationToken cancellationToken)
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

    /// <summary>Pinned first, then active, in one order that both lists are cut from.</summary>
    /// <remarks>
    /// One ordering rather than two, so the names a squadmate reads are the head of the ids
    /// their companion counts. Two orderings would let the panel say one thing and the map
    /// rank another, off the same exchange.
    /// </remarks>
    private static SharedQuests Choose(IReadOnlyList<QuestSummaryReadModel> tasks)
    {
        var chosen = tasks
            .Where(task => task.IsPinned || task.RecordedState == RecordedTaskState.Active)
            .OrderByDescending(task => task.IsPinned)
            .ThenBy(task => task.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaximumIds)
            .ToArray();
        return new(
            [.. chosen.Take(Maximum).Select(task => task.Name)],
            [.. chosen.Select(task => task.TaskId)]);
    }
}
