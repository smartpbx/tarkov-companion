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
    string Summary)
{
    /// <summary>"PMC" or "scav", where the source could tell them apart.</summary>
    /// <remarks>
    /// Inferred from which profile ran the raid, because the logs carry no word for it.
    /// Optional so evidence from sources that cannot know stays silent rather than guessing.
    /// </remarks>
    public string? Side { get; init; }
}

public sealed record RaidSnapshot(
    Guid? RaidId,
    RaidLifecycleState State,
    string? MapId,
    DateTimeOffset? StartedUtc,
    DateTimeOffset UpdatedUtc,
    Confidence Confidence,
    ScreenshotPosition? LastKnownPosition,
    IReadOnlyList<ActiveExtract> ActiveExtracts,
    bool IsManualMapOverride)
{
    /// <summary>Whether the raid was run as a PMC or a scav, where known.</summary>
    public string? Side { get; init; }
}

public sealed record RaidHistoryEntry(
    Guid Id,
    Guid ProfileId,
    string? MapId,
    string Mode,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? EndedUtc,
    string? Outcome,
    string? Notes);
