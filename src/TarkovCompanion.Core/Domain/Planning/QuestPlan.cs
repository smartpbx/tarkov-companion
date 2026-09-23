namespace TarkovCompanion.Core.Domain.Planning;

/// <summary>What kind of thing stands between the player and a locked quest.</summary>
public enum QuestUnlockKind
{
    Level,

    Quest,

    Other,
}

/// <summary>One thing to do before a locked quest opens, worded as an instruction.</summary>
/// <param name="TaskId">The quest to finish or start first, where that is what it is.</param>
public sealed record QuestUnlockStep(QuestUnlockKind Kind, string Label, string? TaskId = null);

/// <summary>One map's share of what the player could do in a raid.</summary>
/// <param name="MapKey">The map id, or empty for objectives that can be done on any map.</param>
public sealed record NextRaidCandidate(string MapKey, string MapLabel, int Quests, int Objectives);

/// <summary>The planner's mutually exclusive progression state for one quest.</summary>
public enum QuestPlanState
{
    Current,

    Next,

    Future,

    Blocked,

    Completed,

    Unknown,
}

/// <summary>What kind of fact prevents a quest from moving forward.</summary>
public enum QuestPlanBlockerKind
{
    PlayerLevel,

    TraderLoyalty,

    Prerequisite,

    Other,
}

/// <summary>One known reason a quest cannot move forward yet.</summary>
/// <param name="RelatedId">The trader or prerequisite quest id, where the source names one.</param>
public sealed record QuestPlanBlocker(QuestPlanBlockerKind Kind, string Label, string? RelatedId = null);

/// <summary>A named quest state and the facts that explain it.</summary>
public sealed record QuestStatePlan(
    string TaskId,
    QuestPlanState State,
    IReadOnlyList<QuestPlanBlocker> Blockers);
