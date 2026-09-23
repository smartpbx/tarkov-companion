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
    string Reason)
{
    public IReadOnlyList<string> FloorIds { get; init; } = [];
}

/// <summary>
/// A deterministic straight-line visiting order. It is a personal plan, not a safe or live route.
/// </summary>
public sealed record ObjectiveRouteBundle(
    string StartLabel,
    double TotalDistanceMetres,
    IReadOnlyList<ObjectiveRouteStep> Steps,
    bool ImprovedByTwoOpt);
