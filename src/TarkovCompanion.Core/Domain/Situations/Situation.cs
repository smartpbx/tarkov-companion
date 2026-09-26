using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Core.Domain.Situations;

/// <summary>What the player is doing, as far as the evidence says (ADR 0022).</summary>
/// <remarks>
/// <see cref="Dead"/> and <see cref="Extracted"/> are reachable only from an outcome somebody or
/// something reported: the game's logs never say how a raid ended (EFT_LOG_FACTS.md), so the
/// log alone can only ever reach <see cref="PostRaid"/>.
/// </remarks>
public enum SituationPhase
{
    Unknown,
    Menu,

    /// <summary>Between raids, looking at a screen the last screenshot showed (flea, TASKS, an item).</summary>
    Screen,
    Matching,
    Loading,
    InRaid,
    Dead,
    Extracted,
    PostRaid,
}

/// <summary>Where a fact came from. Shown behind "Why", never as the fact itself.</summary>
public enum SituationSource
{
    None,
    GameLog,
    Screenshot,

    /// <summary>Counted by the companion from a start time and a length: a model, not a reading.</summary>
    Count,
    Relay,
    Plan,
    Profile,
    Player,
}

public enum SituationSide
{
    Unknown,
    Pmc,
    Scav,
}

/// <summary>How a raid ended, where anybody said.</summary>
public enum SituationOutcome
{
    Unknown,
    Survived,
    Died,
    RunThrough,
    Transit,
}

/// <summary>One derived fact with its confidence, source, time and a one-line reason.</summary>
/// <param name="Because">The "because" line: why the companion believes this, in plain words.</param>
/// <param name="IsInferred">True when this was worked out rather than read; never shown as live.</param>
public sealed record SituationFact<T>(
    T Value,
    Confidence Confidence,
    SituationSource Source,
    DateTimeOffset ObservedUtc,
    string Because,
    bool IsInferred = false)
{
    /// <summary>How old the evidence is. Never negative: a clock that stepped back makes it zero, not minus four hours.</summary>
    public TimeSpan AgeAt(DateTimeOffset nowUtc) => nowUtc > ObservedUtc ? nowUtc - ObservedUtc : TimeSpan.Zero;
}

public enum SituationClockBasis
{
    Unknown,

    /// <summary>Read off the extract screen in a screenshot: the game's own number.</summary>
    Observed,

    /// <summary>Counted from the game's raid confirmation against the map's length.</summary>
    Counted,

    /// <summary>Only the start is known, so the clock counts up.</summary>
    Elapsed,
}

/// <summary>The raid clock as an anchor, so it can tick every second without a new situation.</summary>
/// <remarks>
/// Time left changes every second and the situation must not: a version per second would make the
/// change stream useless for "something happened". The clock is therefore stored as the reading
/// it was worked out from (time left at <see cref="AnchorUtc"/>) and asked for the time now.
/// </remarks>
public sealed record SituationClock(
    SituationClockBasis Basis,
    TimeSpan? RemainingAtAnchor,
    DateTimeOffset? AnchorUtc,
    DateTimeOffset? StartedUtc,
    string Because)
{
    /// <summary>A count or a stepped-back clock is a model, not the game's number.</summary>
    public bool IsInferred => Basis != SituationClockBasis.Observed;

    public TimeSpan? RemainingAt(DateTimeOffset nowUtc)
    {
        if (RemainingAtAnchor is not { } left || AnchorUtc is not { } anchor)
        {
            return null;
        }

        var since = nowUtc > anchor ? nowUtc - anchor : TimeSpan.Zero;
        return left > since ? left - since : TimeSpan.Zero;
    }

    public TimeSpan? ElapsedAt(DateTimeOffset nowUtc) =>
        StartedUtc is { } started ? (nowUtc > started ? nowUtc - started : TimeSpan.Zero) : null;
}

/// <summary>YOU: where the player's last screenshot put them, in words.</summary>
/// <param name="Facing">A compass point ("NE") from the screenshot's heading.</param>
public sealed record SituationYou(
    string? AreaName,
    string? FloorName,
    string? Facing,
    double HeadingDegrees,
    WorldPosition Position,
    DateTimeOffset TakenUtc,
    string Because)
{
    public TimeSpan AgeAt(DateTimeOffset nowUtc) => nowUtc > TakenUtc ? nowUtc - TakenUtc : TimeSpan.Zero;
}

public enum SquadMemberState
{
    Unknown,
    OutOfRaid,
    Loading,
    InRaid,
    OnAnotherMap,

    /// <summary>Their companion stopped sending; the last position is kept, marked old.</summary>
    Quiet,
}

/// <summary>One squad row: only what that member's own companion shared over the relay.</summary>
public sealed record SituationSquadMember(
    string Name,
    SquadMemberState State,
    string? MapId,
    string? AreaName,
    double? DistanceMetres,
    string? Compass,
    DateTimeOffset? PositionTakenUtc,
    string Because)
{
    public TimeSpan? AgeAt(DateTimeOffset nowUtc) =>
        PositionTakenUtc is { } taken ? (nowUtc > taken ? nowUtc - taken : TimeSpan.Zero) : null;
}

/// <summary>NEXT: a stop of the objective route the player opened on this map.</summary>
public sealed record SituationObjective(
    string ObjectiveId,
    string Label,
    double? DistanceMetres,
    string MapId,
    DateTimeOffset PlannedUtc,
    string Because);

/// <summary>LAST SCAN: what the newest screenshot scan found.</summary>
/// <param name="Headline">The item named, where one was.</param>
/// <param name="Action">The recommendation, where one was given ("Take", "Sell").</param>
public sealed record SituationScan(
    ScanContext Kind,
    string? Headline,
    int ItemCount,
    string? Action,
    DateTimeOffset ObservedUtc,
    string Because);

/// <summary>One immutable picture of what is going on, with a reason for each part (ADR 0022).</summary>
/// <remarks>
/// Every V3 surface (the Now panel, sound, the tablet, toasts) is a projection of this and does
/// not ask the engines underneath directly. <see cref="Version"/> rises by one each time anything
/// in it changes, and never otherwise.
/// </remarks>
public sealed record Situation(
    long Version,
    DateTimeOffset ComputedUtc,
    SituationFact<SituationPhase> Phase)
{
    public static Situation Initial { get; } = new(
        0,
        DateTimeOffset.UnixEpoch,
        new(SituationPhase.Unknown, Confidence.Unknown, SituationSource.None, DateTimeOffset.UnixEpoch, "Nothing from the game yet."));

    /// <summary>The companion's own id for the raid, while there is one.</summary>
    public Guid? RaidId { get; init; }

    /// <summary>The map slug ("customs"), null out of raid.</summary>
    public SituationFact<string>? Map { get; init; }

    public SituationFact<SituationSide>? Side { get; init; }

    public SituationFact<SituationOutcome>? Outcome { get; init; }

    /// <summary>The companion profile in use and its mode ("Main · Regular").</summary>
    public SituationFact<string>? Profile { get; init; }

    public SituationClock? Clock { get; init; }

    public SituationYou? You { get; init; }

    public IReadOnlyList<SituationSquadMember> Squad { get; init; } = [];

    public SituationObjective? Next { get; init; }

    public SituationObjective? Then { get; init; }

    public SituationScan? LastScan { get; init; }

    /// <summary>[#712 0-3] Whether the game's logs and screenshot names are still in shapes the companion reads.</summary>
    /// <remarks>Degraded means every other fact here may be missing or wrong; its "because" says which source and why.</remarks>
    public SituationFact<FormatHealthStatus>? FormatHealth { get; init; }
}

/// <summary>A phase change and the evidence for it, as the transition log keeps it.</summary>
public sealed record SituationTransition(
    long Version,
    DateTimeOffset AtUtc,
    SituationPhase From,
    SituationPhase To,
    string Because);
