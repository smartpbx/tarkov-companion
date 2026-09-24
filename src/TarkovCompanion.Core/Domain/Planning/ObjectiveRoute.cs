using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.Core.Domain.Planning;

/// <summary>One exactly placed objective offered to the route planner.</summary>
public sealed record ObjectiveRouteStop(string ObjectiveId, string Label, MapScenePoint At)
{
    public IReadOnlyList<string> FloorIds { get; init; } = [];
}

/// <summary>One stop in a planned visiting order.</summary>
public sealed record ObjectiveRouteStep(
    int Number,
    string ObjectiveId,
    string Label,
    MapScenePoint At,
    double LegDistanceMetres,
    ObjectiveRouteReason Reason)
{
    public IReadOnlyList<string> FloorIds { get; init; } = [];
}

/// <summary>Why a stop has its place in the order; the App says it (#314).</summary>
public enum ObjectiveRouteReasonKind
{
    /// <summary>The nearest unvisited objective from <see cref="ObjectiveRouteReason.Previous"/>.</summary>
    NearestFrom,

    /// <summary>2-opt moved it here from step <see cref="ObjectiveRouteReason.Step"/> to shorten the route.</summary>
    MovedByTwoOpt,

    /// <summary>Still step <see cref="ObjectiveRouteReason.Step"/>; 2-opt reordered the stops before it.</summary>
    KeptByTwoOpt,
}

/// <param name="Previous">The stop or start it is nearest from, for <see cref="ObjectiveRouteReasonKind.NearestFrom"/>.</param>
/// <param name="Step">The step number the reason names.</param>
public sealed record ObjectiveRouteReason(ObjectiveRouteReasonKind Kind, string Previous = "", int Step = 0);

/// <summary>
/// A deterministic straight-line visiting order. It is a personal plan, not a safe or live route.
/// </summary>
public sealed record ObjectiveRouteBundle(
    string StartLabel,
    double TotalDistanceMetres,
    IReadOnlyList<ObjectiveRouteStep> Steps,
    bool ImprovedByTwoOpt)
{
    /// <summary>The rules version of the planner that ordered it (#307); empty where none stamped it.</summary>
    public string PlannerVersion { get; init; } = string.Empty;
}
