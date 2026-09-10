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
