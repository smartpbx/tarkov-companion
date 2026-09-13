namespace TarkovCompanion.Core.Domain.Quests;

public enum QuestEligibilityState
{
    Locked,
    Available,
    Delayed,
    Indeterminate,
}

public enum RecordedObjectivesSatisfaction
{
    Indeterminate,
    NotSatisfied,
    Satisfied,
}

public sealed record QuestEligibilityReason(string Code, string Detail, string? RelatedTaskId = null);

public sealed record QuestEligibility(
    QuestEligibilityState State,
    IReadOnlyList<QuestEligibilityReason> Reasons,
    DateTimeOffset? AvailableUtc = null);

public sealed record QuestObjectiveReadModel(
    string ObjectiveId,
    string Description,
    QuestObjectiveKind Kind,
    bool? IsOptional,
    bool IsUnsupported,
    RecordedObjectiveState RecordedState,
    decimal? RecordedCount,
    decimal? TargetCount,
    bool? FoundInRaidRequired,
    string ProgressSource,
    DateTimeOffset? ProgressModifiedUtc,
    bool IsPinned,
    IReadOnlyList<string> MapIds,
    IReadOnlyList<QuestObjectiveItemTarget> ItemTargets);

public sealed record QuestPrerequisiteReadModel(
    string RequiredTaskId,
    IReadOnlyList<string> RequiredStatuses,
    RecordedTaskState RecordedState);

public sealed record QuestSummaryReadModel(
    string TaskId,
    string Name,
    string? TraderId,
    string? PrimaryMapId,
    RecordedTaskState RecordedState,
    string ProgressSource,
    DateTimeOffset? ProgressModifiedUtc,
    QuestEligibility Eligibility,
    RecordedObjectivesSatisfaction RecordedObjectivesSatisfied,
    bool IsPinned,
    bool? Restartable,
    bool HasFailureConditions,
    IReadOnlyList<string> FailureConditionNotes,
    IReadOnlyList<QuestPrerequisiteReadModel> Prerequisites,
    IReadOnlyList<QuestObjectiveReadModel> Objectives)
{
    /// <summary>
    /// What the trader is called, where the last sync stored a name for the id.
    /// </summary>
    /// <remarks>
    /// An init property rather than another positional parameter: this record already takes
    /// fifteen, and a sixteenth would be sixteen call sites changed to carry one optional word.
    ///
    /// Null where the traders table has nothing for the id, and the id is then what gets
    /// printed. Wrong is worse than ugly, and a trader the catalog does not know about is
    /// something to notice rather than something to hide behind a blank.
    /// </remarks>
    public string? TraderName { get; init; }
}


public sealed record OrphanedQuestProgress(
    QuestProgressEntityKind EntityKind,
    string ExternalId,
    string RecordedValue);

public sealed record QuestBoardReadModel(
    QuestProfileScope Scope,
    long ProgressRevision,
    QuestCatalogProvenance? CatalogProvenance,
    IReadOnlyList<QuestSummaryReadModel> Tasks,
    IReadOnlyList<OrphanedQuestProgress> OrphanedProgress,
    string? UnavailableReason = null);

public sealed record QuestItemRequirementReadModel(
    string TaskId,
    string TaskName,
    string ObjectiveId,
    IReadOnlyList<string> AcceptableItemIds,
    string SourceField,
    int AlternativeGroup,
    bool? FoundInRaidRequired,
    decimal? TargetCount,
    decimal? RecordedCount,
    decimal? RemainingCount);

public sealed record QuestItemNeedsReadModel(
    QuestProfileScope Scope,
    string ItemId,
    int? FoundInRaidHeldCount,
    int? NonFoundInRaidHeldCount,
    IReadOnlyList<QuestItemRequirementReadModel> Requirements,
    IReadOnlyList<OrphanedQuestProgress> OrphanedProgress,
    string? UnavailableReason = null);

public sealed record QuestMapObjectiveReadModel(
    string TaskId,
    string TaskName,
    string? TraderId,
    string ObjectiveId,
    int SourceOrdinal,
    string Description,
    QuestObjectiveKind Kind,
    bool IsUnsupported,
    bool? IsOptional,
    RecordedTaskState TaskState,
    RecordedObjectiveState ObjectiveState,
    bool IsTaskPinned,
    bool IsObjectivePinned,
    int? PinSortOrder,
    string ProgressSource,
    DateTimeOffset? ProgressModifiedUtc,
    bool? FoundInRaidRequired,
    IReadOnlyList<string> MapIds,
    IReadOnlyList<QuestObjectiveZone> Zones,
    IReadOnlyList<QuestObjectiveItemTarget> ItemTargets)
{
    /// <summary>What the trader is called, where the last sync stored a name for the id.</summary>
    /// <remarks>
    /// The same reasoning as on <see cref="QuestSummaryReadModel"/>, and the same source: this
    /// carried an id into the map projection, where anything drawing a quest objective had no
    /// way to say whose quest it was.
    /// </remarks>
    public string? TraderName { get; init; }
}

public sealed record QuestMapObjectivesReadModel(
    QuestProfileScope Scope,
    long ProgressRevision,
    QuestCatalogProvenance? CatalogProvenance,
    IReadOnlyList<string> RequestedMapIds,
    IReadOnlyList<QuestMapObjectiveReadModel> Objectives,
    IReadOnlyList<OrphanedQuestProgress> OrphanedProgress,
    string? UnavailableReason = null);
