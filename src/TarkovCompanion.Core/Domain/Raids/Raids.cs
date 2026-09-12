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
    /// Optional so evidence from sources that cannot know stays silent rather than guessing.
    /// How it was established is carried separately in <see cref="SideBasis"/>, because the
    /// two ways of knowing are not equally strong and a reader is owed the difference.
    /// </remarks>
    public string? Side { get; init; }

    /// <summary>How <see cref="Side"/> was established, in the player's own words.</summary>
    /// <remarks>
    /// There are two routes and they differ in kind. Which profile ran the raid is an
    /// inference from an asymmetry in the logs; a transfer on the ending notification is
    /// proof, because it has never once appeared on a PMC raid. Presenting the second as the
    /// first would understate it, and the first as the second would be a lie, so the basis
    /// travels with the value rather than being reconstructed by whoever displays it.
    /// </remarks>
    public string? SideBasis { get; init; }
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

    /// <summary>How that was established, carried through so the summary can say.</summary>
    public string? SideBasis { get; init; }

    /// <summary>
    /// Every screenshot position of this raid, oldest first.
    /// </summary>
    /// <remarks>
    /// A single point says where the player was; the sequence says where they have been, which
    /// is the more useful thing on a map. It belongs to one raid and is emptied when the next
    /// one begins, so a trail never crosses from a map the player has left.
    ///
    /// These are the same screenshots the player took themselves, so the trail is a record of
    /// their own evidence rather than any kind of tracking.
    /// </remarks>
    public IReadOnlyList<ScreenshotPosition> PositionTrail { get; init; } = [];
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
