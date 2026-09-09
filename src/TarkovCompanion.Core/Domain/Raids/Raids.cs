using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Core.Domain.Raids;

public enum RaidLifecycleState
{
    Unknown,
    LauncherOrGameDetected,
    Menu,
    LoadingRaid,
    InRaid,
    PostRaid,
}

public enum RaidEvidenceKind
{
    ProcessDetected,
    LogLine,
    ScreenshotFilename,
    ManualOverride,
    Simulator,
}

public sealed record RaidEvidence(
    RaidEvidenceKind Kind,
    DateTimeOffset ObservedUtc,
    string? MapId,
    RaidLifecycleState? SuggestedState,
    Confidence Confidence,
    string Summary);

public sealed record RaidSnapshot(
    Guid? RaidId,
    RaidLifecycleState State,
    string? MapId,
    DateTimeOffset? StartedUtc,
    DateTimeOffset UpdatedUtc,
    Confidence Confidence,
    ScreenshotPosition? LastKnownPosition,
    IReadOnlyList<ActiveExtract> ActiveExtracts,
    bool IsManualMapOverride);

public sealed record RaidHistoryEntry(
    Guid Id,
    Guid ProfileId,
    string? MapId,
    string Mode,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? EndedUtc,
    string? Outcome,
    string? Notes);
