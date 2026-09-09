using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Core.Domain.Strategy;

public enum RaidPhase
{
    Early,
    Mid,
    Late,
}

public sealed record StrategyZone(
    string Id,
    string Name,
    MapPoint Position,
    double Radius,
    double SpawnWeight,
    double PoiWeight,
    double ChokepointWeight,
    double QuestWeight,
    double ExtractWeight,
    DataProvenance Provenance);

public sealed record TrafficSample(MapPoint Position, double Score, IReadOnlyList<string> Factors);

public sealed record TrafficPrediction(
    RaidPhase Phase,
    IReadOnlyList<TrafficSample> Samples,
    string CurrentAreaRisk,
    IReadOnlyList<string> StrategyNotes,
    DateTimeOffset CalculatedUtc,
    string Disclaimer = "Predicted traffic based on map and game knowledge—not live player data.");

public enum RouteMode
{
    Fastest,
    Safest,
    Quest,
    Loot,
    AvoidPvp,
}

public sealed record RouteNode(string Id, string Name, MapPoint Position, string Kind);

public sealed record RouteEdge(
    string FromNodeId,
    string ToNodeId,
    double TravelCost,
    double RiskCost,
    double LootUtility,
    IReadOnlySet<RaidPhase> Phases);

public sealed record RouteGraph(string MapId, IReadOnlyList<RouteNode> Nodes, IReadOnlyList<RouteEdge> Edges, bool IsComplete);

public sealed record PlannedRoute(
    RouteMode Mode,
    IReadOnlyList<RouteNode> Nodes,
    double Cost,
    bool IsPrecise,
    string Guidance,
    Confidence Confidence);
