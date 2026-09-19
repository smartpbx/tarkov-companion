using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Core.Domain.Quests;

public enum RecordedTaskState
{
    Unknown,
    NotStarted,
    Active,
    Completed,
    Failed,
}

public enum RecordedObjectiveState
{
    Unknown,
    InProgress,
    Completed,
}

public enum QuestProgressActor
{
    User,
    Import,
    SystemMigration,

    /// <summary>
    /// The game's own announcement, read out of its logs.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="User"/> because a quest state the game reported and one the
    /// player typed are different claims, and the page has to be able to say which it is
    /// showing. Stored as its own name, so rows written before this existed still read back as
    /// the actor they were written with.
    /// </remarks>
    GameLog,
}

public enum QuestPinTargetKind
{
    Task,
    Objective,
}

public enum QuestProgressEntityKind
{
    Task,
    Objective,
    ItemHolding,
    Pin,
}

/// <summary>
/// What established a recorded value, as it is stored and as the page reports it.
/// </summary>
/// <remarks>
/// A free string in the mutation, because the store keeps whatever it is given. These are the
/// two the application writes, named once so the write side, the validation and the page cannot
/// drift apart on the spelling.
/// </remarks>
/// <summary>
/// A recorded value was refused because the catalog has no such quest, objective or item.
/// </summary>
/// <remarks>
/// Its own type, and a subclass so existing callers that catch <see cref="InvalidOperationException"/>
/// are unaffected. The game naming a quest the loaded catalog does not have is an ordinary
/// consequence of a catalog that is older than the patch, and it is a completely different
/// problem from storage failing. Told apart, the page can say "the game reported four quests,
/// one of which is not in the catalog" instead of silently recording three.
/// </remarks>
public sealed class QuestCatalogEntryUnknownException(string message, string entryId)
    : InvalidOperationException(message)
{
    /// <summary>The id the catalog did not have.</summary>
    public string EntryId { get; } = entryId;
}

public static class QuestProgressSources
{
    /// <summary>The player said so, on the page.</summary>
    public const string Manual = "Manual";

    /// <summary>The game said so, in its own logs.</summary>
    public const string GameLog = "GameLog";
}

public sealed record QuestProfileScope(Guid ProfileId, GameMode GameMode, string Generation);

public sealed record RecordedTaskProgress(
    string TaskId,
    RecordedTaskState State,
    string Source,
    long Revision,
    DateTimeOffset ModifiedUtc);

public sealed record RecordedObjectiveProgress(
    string ObjectiveId,
    RecordedObjectiveState State,
    decimal? Count,
    string Source,
    long Revision,
    DateTimeOffset ModifiedUtc);

public sealed record RecordedItemHolding(
    string ItemId,
    bool FoundInRaid,
    int Count,
    string Source,
    long Revision,
    DateTimeOffset ModifiedUtc);

public sealed record RecordedQuestPin(
    QuestPinTargetKind TargetKind,
    string TargetId,
    int SortOrder,
    string? Note,
    string Source,
    long Revision,
    DateTimeOffset ModifiedUtc);

public sealed record QuestProgressSnapshot(
    QuestProfileScope Scope,
    long Revision,
    IReadOnlyDictionary<string, RecordedTaskProgress> Tasks,
    IReadOnlyDictionary<string, RecordedObjectiveProgress> Objectives,
    IReadOnlyList<RecordedItemHolding> ItemHoldings,
    IReadOnlyList<RecordedQuestPin> Pins);

public sealed record QuestProgressChange(
    long Id,
    QuestProfileScope Scope,
    Guid CorrelationId,
    QuestProgressEntityKind EntityKind,
    string EntityId,
    string FieldName,
    string PreviousValueJson,
    string NewValueJson,
    string InverseValueJson,
    QuestProgressActor Actor,
    string Source,
    long Revision,
    DateTimeOffset RecordedUtc);

public abstract record QuestProgressMutation(
    QuestProfileScope Scope,
    string ProfileName,
    QuestProgressActor Actor,
    string Source,
    Guid CorrelationId,
    DateTimeOffset RecordedUtc);

public sealed record SetTaskStateMutation(
    QuestProfileScope Scope,
    string ProfileName,
    string TaskId,
    RecordedTaskState State,
    QuestProgressActor Actor,
    string Source,
    Guid CorrelationId,
    DateTimeOffset RecordedUtc)
    : QuestProgressMutation(Scope, ProfileName, Actor, Source, CorrelationId, RecordedUtc);

public sealed record SetObjectiveProgressMutation(
    QuestProfileScope Scope,
    string ProfileName,
    string ObjectiveId,
    RecordedObjectiveState State,
    decimal? Count,
    QuestProgressActor Actor,
    string Source,
    Guid CorrelationId,
    DateTimeOffset RecordedUtc)
    : QuestProgressMutation(Scope, ProfileName, Actor, Source, CorrelationId, RecordedUtc);

public sealed record SetItemHoldingMutation(
    QuestProfileScope Scope,
    string ProfileName,
    string ItemId,
    bool FoundInRaid,
    int? Count,
    QuestProgressActor Actor,
    string Source,
    Guid CorrelationId,
    DateTimeOffset RecordedUtc)
    : QuestProgressMutation(Scope, ProfileName, Actor, Source, CorrelationId, RecordedUtc);

public sealed record SetQuestPinMutation(
    QuestProfileScope Scope,
    string ProfileName,
    QuestPinTargetKind TargetKind,
    string TargetId,
    bool IsPinned,
    int SortOrder,
    string? Note,
    QuestProgressActor Actor,
    string Source,
    Guid CorrelationId,
    DateTimeOffset RecordedUtc)
    : QuestProgressMutation(Scope, ProfileName, Actor, Source, CorrelationId, RecordedUtc);

public sealed record QuestProgressCommandResult(Guid CorrelationId, long Revision, bool Changed);
