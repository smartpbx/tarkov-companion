using TarkovCompanion.Core.Common;
using TarkovCompanion.Application.Services.Quests;
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

    /// <summary>
    /// [#780] The objectives still open on the active quests, by id, with the count where one is
    /// recorded. Ids and counts only: the receiver names them and places them from its own catalog.
    /// </summary>
    public IReadOnlyList<SharedObjective> Objectives { get; init; } = [];
}

/// <summary>[#780] One open objective of an active quest, as a squadmate's companion needs it.</summary>
/// <param name="TaskId">The quest, by catalog id.</param>
/// <param name="ObjectiveId">The objective, by catalog id.</param>
/// <param name="Count">How many of the target are done, where a count is recorded.</param>
public sealed record SharedObjective(string TaskId, string ObjectiveId, decimal? Count);

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
/// Read at most every <see cref="RereadAfter"/>. The publish loop runs every few seconds and quest
/// progress changes a handful of times a raid, so asking the database on every exchange would be
/// hundreds of queries for an answer that almost never differs.
///
/// [#780] Change-driven as well: the game's log recording a quest, or the player marking an
/// objective done, calls <see cref="Invalidate"/>, and the group session sends at once rather
/// than on the next re-read. Edits made on the Plan page have no event yet and ride the re-read.
/// </remarks>
public sealed class GroupQuestShare
{
    private readonly IPlayerProfileService profiles;
    private readonly IQuestReadService quests;
    private readonly IHandDoneObjectiveStore? _handDone;

    public GroupQuestShare(
        IPlayerProfileService profiles,
        IQuestReadService quests,
        TimeProvider? timeProvider = null,
        // Optional so the tests and any composition without them still build; with them the
        // share re-reads the moment the game or the player changes a quest.
        IHandDoneObjectiveStore? handDone = null,
        QuestLogProgressService? questLog = null)
    {
        this.profiles = profiles;
        this.quests = quests;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _handDone = handDone;
        if (handDone is not null)
        {
            handDone.Changed += Invalidate;
        }

        if (questLog is not null)
        {
            questLog.Changed += (_, _) => Invalidate();
        }
    }

    /// <summary>
    /// [#780] Raised when what this shares may have changed, so the group session can send now.
    /// </summary>
    public event Action? Changed;

    /// <summary>Forgets the cached answer, so the next exchange reads the board again.</summary>
    public void Invalidate()
    {
        _readUtc = DateTimeOffset.MinValue;
        _modeReadUtc = DateTimeOffset.MinValue;
        Changed?.Invoke();
    }

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

    /// <summary>
    /// [#780] How many open objectives are worth sending: the relay refuses more than sixty, and
    /// sixty at about seventy bytes each keeps a member's publish well inside its 32 KB bound.
    /// </summary>
    internal const int MaximumObjectives = 60;

    private static readonly TimeSpan RereadAfter = TimeSpan.FromSeconds(20);

    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SharedQuests _shared = SharedQuests.None;
    private DateTimeOffset _readUtc = DateTimeOffset.MinValue;

    public async Task<SharedQuests> GetAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (WallClockAge.IsWithin(now, _readUtc, RereadAfter))
        {
            return _shared;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (WallClockAge.IsWithin(_timeProvider.GetUtcNow(), _readUtc, RereadAfter))
            {
                return _shared;
            }

            var profile = await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
            var board = await quests.GetQuestBoardAsync(scope, cancellationToken).ConfigureAwait(false);
            var handDone = (_handDone?.Entries ?? [])
                .Where(mark => mark.ProfileId == profile.Id)
                .Select(mark => mark.ObjectiveId)
                .ToHashSet(StringComparer.Ordinal);
            _shared = Choose(board.Tasks, handDone);
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

    private string? _mode;
    private DateTimeOffset _modeReadUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// [#269] The active profile's game mode as the group sends it, re-read at most every
    /// <see cref="RereadAfter"/>; null when the profile cannot be read, which no receiver treats
    /// as a difference.
    /// </summary>
    public async Task<string?> GameModeAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (WallClockAge.IsWithin(now, _modeReadUtc, RereadAfter))
        {
            return _mode;
        }

        try
        {
            var profile = await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            _mode = GroupModeCheck.Wire(profile.GameMode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Said as "unknown", never guessed: a wrong mode would hide a squadmate's quests.
            _mode = null;
        }

        _modeReadUtc = _timeProvider.GetUtcNow();
        return _mode;
    }

    /// <summary>Pinned first, then active, in one order that both lists are cut from.</summary>
    /// <remarks>
    /// One ordering rather than two, so the names a squadmate reads are the head of the ids
    /// their companion counts. Two orderings would let the panel say one thing and the map
    /// rank another, off the same exchange.
    /// </remarks>
    public static SharedQuests Choose(IReadOnlyList<QuestSummaryReadModel> tasks, IReadOnlySet<string>? handDone = null)
    {
        var chosen = tasks
            .Where(task => task.IsPinned || task.RecordedState == RecordedTaskState.Active)
            .OrderByDescending(task => task.IsPinned)
            .ThenBy(task => task.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaximumIds)
            .ToArray();
        return new(
            [.. chosen.Take(Maximum).Select(task => task.Name)],
            [.. chosen.Select(task => task.TaskId)])
        {
            // [#780] Only an active quest's open objectives: a pinned quest the player has not
            // started has nothing to help with yet. Done means recorded complete or marked done
            // by hand, the same two things that take an objective off the player's own map.
            Objectives =
            [
                .. chosen
                    .Where(task => task.RecordedState == RecordedTaskState.Active)
                    .SelectMany(task => task.Objectives
                        .Where(objective => objective.RecordedState != RecordedObjectiveState.Completed &&
                            handDone?.Contains(objective.ObjectiveId) != true)
                        .Select(objective => new SharedObjective(task.TaskId, objective.ObjectiveId, objective.RecordedCount)))
                    .Take(MaximumObjectives),
            ],
        };
    }
}
