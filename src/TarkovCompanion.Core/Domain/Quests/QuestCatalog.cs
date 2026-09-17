using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Core.Domain.Quests;

public enum QuestObjectiveKind
{
    Unsupported,
    BuildWeapon,
    Dialogue,
    Experience,
    Extract,
    FindItem,
    FindQuestItem,
    GiveItem,
    GiveQuestItem,
    GlobalVariable,
    Mark,
    PlantItem,
    PlantQuestItem,
    SellItem,
    Shoot,
    Skill,
    TaskStatus,
    TraderLevel,
    TraderStanding,
    UseItem,
    Visit,
}

public enum QuestMapAssociationKind
{
    Declared,
    Zone,
    PossibleLocation,
}

public sealed record QuestCatalogProvenance(
    string Source,
    string SourceUri,
    GameMode GameMode,
    string SourceMode,
    string Language,
    string PayloadSha256,
    string TranslatedPayloadSha256,
    string? ETag,
    DateTimeOffset? LastModifiedUtc,
    DateTimeOffset FetchedUtc,
    DateTimeOffset ValidatedUtc);

public sealed record QuestTaskRequirement(
    int SourceOrdinal,
    string RequiredTaskId,
    IReadOnlyList<string> RequiredStatuses,
    string RawSourceJson);

public sealed record QuestMapAssociation(
    QuestMapAssociationKind Kind,
    int SourceOrdinal,
    string MapId);

public sealed record QuestObjectiveItemTarget(
    string ItemId,
    string SourceField,
    int AlternativeGroup,
    int SourceOrdinal,
    decimal? TargetCount,
    bool? FoundInRaidRequired);

public readonly record struct QuestZoneSize(double? X, double? Y, double? Z);

public sealed record QuestObjectiveZone(
    int SourceOrdinal,
    string? SourceZoneId,
    string? MapId,
    WorldPosition? Position,
    IReadOnlyList<WorldPosition> Outline,
    double? BottomElevation,
    double? TopElevation,
    double? TerrainElevation,
    QuestZoneSize? Size,
    string? Name,
    string RawSourceJson);

public sealed record QuestObjectiveDefinition(
    string Id,
    string TaskId,
    string SourceType,
    QuestObjectiveKind Kind,
    bool IsFailureCondition,
    int SourceOrdinal,
    string Description,
    decimal? TargetCount,
    bool? Optional,
    bool? FoundInRaidRequired,
    string? TargetTaskId,
    IReadOnlyList<string> TargetStatuses,
    IReadOnlyList<QuestObjectiveItemTarget> ItemTargets,
    IReadOnlyList<QuestMapAssociation> MapAssociations,
    IReadOnlyList<QuestObjectiveZone> Zones,
    string SubtypeJson,
    string RawSourceJson)
{
    public bool IsUnsupported => Kind == QuestObjectiveKind.Unsupported;
}

public sealed record QuestTaskDefinition(
    string Id,
    string Name,
    string? NormalizedName,
    string? TraderId,
    int? MinimumPlayerLevel,
    string? FactionName,
    string? PrimaryMapId,
    bool? Restartable,
    bool? KappaRequired,
    bool? LightkeeperRequired,
    string? RequiredPrestigeId,
    int? AvailableDelaySecondsMinimum,
    int? AvailableDelaySecondsMaximum,
    IReadOnlyList<string> SourceGameModes,
    IReadOnlyList<QuestTaskRequirement> Requirements,
    IReadOnlyList<QuestObjectiveDefinition> Objectives,
    IReadOnlyList<QuestObjectiveDefinition> FailureConditions,
    string RawSourceJson)
{
    /// <summary>The wiki page for this task, where the last sync had one.</summary>
    public string? WikiUri { get; init; }
}

public sealed record QuestCatalogSnapshot(
    QuestCatalogProvenance Provenance,
    IReadOnlyList<QuestTaskDefinition> Tasks,
    string RawSourceJson,
    string TranslatedSourceJson);
