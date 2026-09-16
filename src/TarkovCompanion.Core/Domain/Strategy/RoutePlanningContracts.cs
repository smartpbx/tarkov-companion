using System.Collections.ObjectModel;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Maps;
using TrafficRaidPhase = TarkovCompanion.Core.Abstractions.V2.RaidPhase;

namespace TarkovCompanion.Core.Domain.Strategy;

public static class RoutePlanningBounds
{
    public const int MaximumWaypoints = 2_048;
    public const int MaximumCorridors = 8_192;
    public const int MaximumPressureInputs = 8_192;
    public const int MaximumWarnings = 32;
    public const int MaximumIdentifierLength = 128;
    public const int MaximumNameLength = 256;
    public const int MaximumExplanationLength = 1_024;
    public const double MaximumCoordinateMagnitude = 10_000_000;
    public const double MaximumCorridorTravelSeconds = 86_400;
    public const double MaximumRouteTravelSeconds = 604_800;
}

public enum RoutePlanningMode
{
    Fastest = 1,
    LowerExpectedContact,
    Quest,
    Loot,
    TeamRegroup,
}

/// <summary>What a renderer may honestly draw from one route alternative.</summary>
public enum RoutePlanShape
{
    PrecisePath = 1,
    WaypointsOnly,
    StrategyOnly,
}

public sealed record RoutePlanningWaypoint
{
    public RoutePlanningWaypoint(
        string waypointId,
        string name,
        MapPoint position,
        double questOpportunity = 0,
        double lootOpportunity = 0,
        double teamRegroupOpportunity = 0)
    {
        WaypointId = RoutePlanningGuard.Identifier(waypointId, nameof(waypointId));
        Name = RoutePlanningGuard.Text(name, nameof(name), RoutePlanningBounds.MaximumNameLength);
        RoutePlanningGuard.Position(position, nameof(position));
        Position = position;
        QuestOpportunity = RoutePlanningGuard.Unit(questOpportunity, nameof(questOpportunity));
        LootOpportunity = RoutePlanningGuard.Unit(lootOpportunity, nameof(lootOpportunity));
        TeamRegroupOpportunity = RoutePlanningGuard.Unit(teamRegroupOpportunity, nameof(teamRegroupOpportunity));
    }

    public string WaypointId { get; }

    public string Name { get; }

    public MapPoint Position { get; }

    public double QuestOpportunity { get; }

    public double LootOpportunity { get; }

    public double TeamRegroupOpportunity { get; }
}

/// <summary>A directed static-map connection. It has no current-player or entity input.</summary>
public sealed record RoutePlanningCorridor
{
    public RoutePlanningCorridor(
        string corridorId,
        string fromWaypointId,
        string toWaypointId,
        double estimatedTravelSeconds,
        IReadOnlyList<TrafficRaidPhase>? availablePhases = null)
    {
        CorridorId = RoutePlanningGuard.Identifier(corridorId, nameof(corridorId));
        FromWaypointId = RoutePlanningGuard.Identifier(fromWaypointId, nameof(fromWaypointId));
        ToWaypointId = RoutePlanningGuard.Identifier(toWaypointId, nameof(toWaypointId));
        if (string.Equals(FromWaypointId, ToWaypointId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A route corridor must connect two different waypoints.", nameof(toWaypointId));
        }

        if (!double.IsFinite(estimatedTravelSeconds) ||
            estimatedTravelSeconds is <= 0 or > RoutePlanningBounds.MaximumCorridorTravelSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(estimatedTravelSeconds),
                $"Corridor travel time must be above zero and at most {RoutePlanningBounds.MaximumCorridorTravelSeconds} seconds.");
        }

        EstimatedTravelSeconds = estimatedTravelSeconds;
        var phases = RoutePlanningGuard.ReadOnly(
            availablePhases ?? Enum.GetValues<TrafficRaidPhase>(),
            nameof(availablePhases),
            Enum.GetValues<TrafficRaidPhase>().Length,
            requireNonEmpty: true);
        if (phases.Any(phase => !Enum.IsDefined(phase)) || phases.Distinct().Count() != phases.Count)
        {
            throw new ArgumentException("Corridor phases must be defined and distinct.", nameof(availablePhases));
        }

        AvailablePhases = phases;
    }

    public string CorridorId { get; }

    public string FromWaypointId { get; }

    public string ToWaypointId { get; }

    public double EstimatedTravelSeconds { get; }

    public IReadOnlyList<TrafficRaidPhase> AvailablePhases { get; }
}

/// <summary>
/// Renderer-neutral route topology. A complete topology is the only input permitted to produce a
/// connected path; a partial topology can produce waypoint advice, but never a line that claims a
/// missing connection exists.
/// </summary>
public sealed record RoutePlanningTopology
{
    public RoutePlanningTopology(
        string mapId,
        IReadOnlyList<RoutePlanningWaypoint> waypoints,
        IReadOnlyList<RoutePlanningCorridor> corridors,
        bool isComplete,
        double? coverageFraction,
        EvidenceProvenance provenance)
    {
        MapId = RoutePlanningGuard.Identifier(mapId, nameof(mapId));
        Waypoints = RoutePlanningGuard.ReadOnly(
            waypoints,
            nameof(waypoints),
            RoutePlanningBounds.MaximumWaypoints,
            requireNonEmpty: true);
        Corridors = RoutePlanningGuard.ReadOnly(
            corridors,
            nameof(corridors),
            RoutePlanningBounds.MaximumCorridors,
            requireNonEmpty: false);
        IsComplete = isComplete;
        CoverageFraction = coverageFraction is { } coverage
            ? RoutePlanningGuard.Unit(coverage, nameof(coverageFraction))
            : null;
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));

        if (provenance.SourceClass is not EvidenceSourceClass.PublicStructuredData and
            not EvidenceSourceClass.CuratedData and
            not EvidenceSourceClass.UserEntered)
        {
            throw new ArgumentException(
                "Route topology must come from public, curated, or explicitly entered static map knowledge.",
                nameof(provenance));
        }

        if (isComplete && CoverageFraction != 1)
        {
            throw new ArgumentException("Complete topology must declare full coverage.", nameof(coverageFraction));
        }

        if (!isComplete && CoverageFraction == 1)
        {
            throw new ArgumentException("Incomplete topology cannot declare full coverage.", nameof(coverageFraction));
        }

        if (Waypoints.Select(waypoint => waypoint.WaypointId).Distinct(StringComparer.Ordinal).Count() != Waypoints.Count)
        {
            throw new ArgumentException("Route waypoint ids must be distinct.", nameof(waypoints));
        }

        if (Corridors.Select(corridor => corridor.CorridorId).Distinct(StringComparer.Ordinal).Count() != Corridors.Count)
        {
            throw new ArgumentException("Route corridor ids must be distinct.", nameof(corridors));
        }

        var waypointIds = Waypoints.Select(waypoint => waypoint.WaypointId).ToHashSet(StringComparer.Ordinal);
        if (Corridors.Any(corridor =>
                !waypointIds.Contains(corridor.FromWaypointId) ||
                !waypointIds.Contains(corridor.ToWaypointId)))
        {
            throw new ArgumentException("Every route corridor endpoint must name a topology waypoint.", nameof(corridors));
        }
    }

    public string MapId { get; }

    public IReadOnlyList<RoutePlanningWaypoint> Waypoints { get; }

    public IReadOnlyList<RoutePlanningCorridor> Corridors { get; }

    public bool IsComplete { get; }

    /// <summary>Measured topology coverage, or null when the source did not publish it.</summary>
    public double? CoverageFraction { get; }

    public EvidenceProvenance Provenance { get; }
}

public sealed record RoutePlanningRequest
{
    public RoutePlanningRequest(
        RoutePlanningTopology topology,
        string startWaypointId,
        string destinationWaypointId,
        TrafficRaidPhase phase,
        IReadOnlyList<ModelledIntelligence<RouteCorridorPressure>> corridorPressures,
        DateTimeOffset plannedUtc)
    {
        Topology = topology ?? throw new ArgumentNullException(nameof(topology));
        StartWaypointId = RoutePlanningGuard.Identifier(startWaypointId, nameof(startWaypointId));
        DestinationWaypointId = RoutePlanningGuard.Identifier(destinationWaypointId, nameof(destinationWaypointId));
        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        Phase = phase;
        CorridorPressures = RoutePlanningGuard.ReadOnly(
            corridorPressures,
            nameof(corridorPressures),
            RoutePlanningBounds.MaximumPressureInputs,
            requireNonEmpty: false);
        PlannedUtc = RoutePlanningGuard.Utc(plannedUtc, nameof(plannedUtc));

        var corridorIds = topology.Corridors
            .Select(corridor => corridor.CorridorId)
            .ToHashSet(StringComparer.Ordinal);
        var pressureKeys = new HashSet<(string CorridorId, TrafficRaidPhase Phase)>();
        foreach (var intelligence in CorridorPressures)
        {
            var pressure = intelligence.Estimate.Value;
            if (pressure is null)
            {
                continue;
            }

            if (!string.Equals(pressure.MapId, topology.MapId, StringComparison.Ordinal) ||
                !corridorIds.Contains(pressure.CorridorId))
            {
                throw new ArgumentException(
                    "Corridor pressure must belong to the requested topology and one of its corridors.",
                    nameof(corridorPressures));
            }

            if (!pressureKeys.Add((pressure.CorridorId, pressure.Phase)))
            {
                throw new ArgumentException(
                    "A route request may contain only one selected pressure estimate per corridor and phase.",
                    nameof(corridorPressures));
            }
        }
    }

    public RoutePlanningTopology Topology { get; }

    public string StartWaypointId { get; }

    public string DestinationWaypointId { get; }

    public TrafficRaidPhase Phase { get; }

    public IReadOnlyList<ModelledIntelligence<RouteCorridorPressure>> CorridorPressures { get; }

    public DateTimeOffset PlannedUtc { get; }
}

public sealed record RouteTradeoffs
{
    public RouteTradeoffs(
        double? estimatedTravelSeconds,
        double? expectedContactPressure,
        int evaluatedCorridorCount,
        int pressureCoveredCorridorCount,
        double corridorPressureCoverageFraction,
        double? minimumModelCoverageFraction,
        double questOpportunity,
        double lootOpportunity,
        double teamRegroupOpportunity,
        bool topologyComplete,
        double? topologyCoverageFraction,
        IReadOnlyList<string> missingPressureCorridorIds)
    {
        if (estimatedTravelSeconds is { } travel &&
            (!double.IsFinite(travel) || travel is < 0 or > RoutePlanningBounds.MaximumRouteTravelSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedTravelSeconds));
        }

        EstimatedTravelSeconds = estimatedTravelSeconds;
        ExpectedContactPressure = expectedContactPressure is { } pressure
            ? RoutePlanningGuard.Unit(pressure, nameof(expectedContactPressure))
            : null;
        if (evaluatedCorridorCount is < 0 or > RoutePlanningBounds.MaximumCorridors ||
            pressureCoveredCorridorCount < 0 ||
            pressureCoveredCorridorCount > evaluatedCorridorCount)
        {
            throw new ArgumentOutOfRangeException(nameof(evaluatedCorridorCount));
        }

        EvaluatedCorridorCount = evaluatedCorridorCount;
        PressureCoveredCorridorCount = pressureCoveredCorridorCount;
        CorridorPressureCoverageFraction = RoutePlanningGuard.Unit(
            corridorPressureCoverageFraction,
            nameof(corridorPressureCoverageFraction));
        MinimumModelCoverageFraction = minimumModelCoverageFraction is { } modelCoverage
            ? RoutePlanningGuard.Unit(modelCoverage, nameof(minimumModelCoverageFraction))
            : null;
        QuestOpportunity = RoutePlanningGuard.Unit(questOpportunity, nameof(questOpportunity));
        LootOpportunity = RoutePlanningGuard.Unit(lootOpportunity, nameof(lootOpportunity));
        TeamRegroupOpportunity = RoutePlanningGuard.Unit(teamRegroupOpportunity, nameof(teamRegroupOpportunity));
        TopologyComplete = topologyComplete;
        TopologyCoverageFraction = topologyCoverageFraction is { } topologyCoverage
            ? RoutePlanningGuard.Unit(topologyCoverage, nameof(topologyCoverageFraction))
            : null;
        MissingPressureCorridorIds = RoutePlanningGuard.ReadOnly(
            missingPressureCorridorIds,
            nameof(missingPressureCorridorIds),
            RoutePlanningBounds.MaximumCorridors,
            requireNonEmpty: false,
            RoutePlanningGuard.Identifier);

        var expectedCoverage = evaluatedCorridorCount == 0
            ? 0
            : pressureCoveredCorridorCount / (double)evaluatedCorridorCount;
        if (Math.Abs(expectedCoverage - CorridorPressureCoverageFraction) > 1e-12)
        {
            throw new ArgumentException(
                "Corridor pressure coverage must reconcile with the evaluated and covered counts.",
                nameof(corridorPressureCoverageFraction));
        }

        if (MissingPressureCorridorIds.Count != evaluatedCorridorCount - pressureCoveredCorridorCount ||
            MissingPressureCorridorIds.Distinct(StringComparer.Ordinal).Count() != MissingPressureCorridorIds.Count)
        {
            throw new ArgumentException(
                "Missing pressure corridor ids must be distinct and reconcile with route coverage.",
                nameof(missingPressureCorridorIds));
        }

        if (ExpectedContactPressure is not null && pressureCoveredCorridorCount != evaluatedCorridorCount)
        {
            throw new ArgumentException(
                "Expected contact pressure is reportable only when every evaluated corridor has pressure evidence.",
                nameof(expectedContactPressure));
        }

        if (ExpectedContactPressure is not null && evaluatedCorridorCount == 0)
        {
            throw new ArgumentException("A route with no corridors has no contact-pressure estimate.", nameof(expectedContactPressure));
        }

        if (MinimumModelCoverageFraction is not null && pressureCoveredCorridorCount == 0)
        {
            throw new ArgumentException("Model coverage requires at least one pressure-backed corridor.", nameof(minimumModelCoverageFraction));
        }

        if (topologyComplete && TopologyCoverageFraction != 1)
        {
            throw new ArgumentException("Complete route topology must retain full coverage.", nameof(topologyCoverageFraction));
        }

        if (!topologyComplete && TopologyCoverageFraction == 1)
        {
            throw new ArgumentException("Incomplete route topology cannot retain full coverage.", nameof(topologyCoverageFraction));
        }
    }

    public double? EstimatedTravelSeconds { get; }

    /// <summary>Travel-time-weighted relative pressure, or null unless every corridor is covered.</summary>
    public double? ExpectedContactPressure { get; }

    public int EvaluatedCorridorCount { get; }

    public int PressureCoveredCorridorCount { get; }

    public double CorridorPressureCoverageFraction { get; }

    /// <summary>The least source coverage reported by used models, or null if any source left it unknown.</summary>
    public double? MinimumModelCoverageFraction { get; }

    public double QuestOpportunity { get; }

    public double LootOpportunity { get; }

    public double TeamRegroupOpportunity { get; }

    public bool TopologyComplete { get; }

    public double? TopologyCoverageFraction { get; }

    public IReadOnlyList<string> MissingPressureCorridorIds { get; }
}

public sealed record RouteAlternative
{
    public RouteAlternative(
        RoutePlanningMode mode,
        RoutePlanShape shape,
        IReadOnlyList<RoutePlanningWaypoint> waypoints,
        IReadOnlyList<string> corridorIds,
        RouteTradeoffs tradeoffs,
        IReadOnlyList<ModelledIntelligence<RouteCorridorPressure>> pressureEvidence,
        string tradeoffSummary,
        string whyDifferent,
        string guidance)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (!Enum.IsDefined(shape))
        {
            throw new ArgumentOutOfRangeException(nameof(shape));
        }

        Mode = mode;
        Shape = shape;
        Waypoints = RoutePlanningGuard.ReadOnly(
            waypoints,
            nameof(waypoints),
            RoutePlanningBounds.MaximumWaypoints,
            requireNonEmpty: false);
        CorridorIds = RoutePlanningGuard.ReadOnly(
            corridorIds,
            nameof(corridorIds),
            RoutePlanningBounds.MaximumCorridors,
            requireNonEmpty: false,
            RoutePlanningGuard.Identifier);
        Tradeoffs = tradeoffs ?? throw new ArgumentNullException(nameof(tradeoffs));
        PressureEvidence = RoutePlanningGuard.ReadOnly(
            pressureEvidence,
            nameof(pressureEvidence),
            RoutePlanningBounds.MaximumPressureInputs,
            requireNonEmpty: false);
        TradeoffSummary = RoutePlanningGuard.Text(
            tradeoffSummary,
            nameof(tradeoffSummary),
            RoutePlanningBounds.MaximumExplanationLength);
        WhyDifferent = RoutePlanningGuard.Text(
            whyDifferent,
            nameof(whyDifferent),
            RoutePlanningBounds.MaximumExplanationLength);
        Guidance = RoutePlanningGuard.Text(
            guidance,
            nameof(guidance),
            RoutePlanningBounds.MaximumExplanationLength);

        if (shape != RoutePlanShape.PrecisePath && CorridorIds.Count != 0)
        {
            throw new ArgumentException(
                "Only a precise route may expose connected corridor ids to a renderer.",
                nameof(corridorIds));
        }

        if (shape == RoutePlanShape.PrecisePath &&
            (Waypoints.Count == 0 || CorridorIds.Count != Math.Max(0, Waypoints.Count - 1)))
        {
            throw new ArgumentException("A precise route must reconcile its ordered waypoints and corridors.", nameof(corridorIds));
        }

        if (Waypoints.Select(waypoint => waypoint.WaypointId).Distinct(StringComparer.Ordinal).Count() != Waypoints.Count ||
            CorridorIds.Distinct(StringComparer.Ordinal).Count() != CorridorIds.Count)
        {
            throw new ArgumentException("A route alternative cannot repeat waypoints or corridors.", nameof(waypoints));
        }

        if (PressureEvidence.Count != Tradeoffs.PressureCoveredCorridorCount ||
            PressureEvidence.Any(intelligence => intelligence.Estimate.Value is null) ||
            PressureEvidence.Select(intelligence => intelligence.Estimate.Value!.CorridorId)
                .Distinct(StringComparer.Ordinal).Count() != PressureEvidence.Count)
        {
            throw new ArgumentException(
                "Pressure evidence must identify every covered route corridor exactly once.",
                nameof(pressureEvidence));
        }

        if (shape == RoutePlanShape.PrecisePath &&
            (Tradeoffs.EvaluatedCorridorCount != CorridorIds.Count ||
             PressureEvidence.Any(intelligence => !CorridorIds.Contains(
                 intelligence.Estimate.Value!.CorridorId,
                 StringComparer.Ordinal)) ||
             Tradeoffs.MissingPressureCorridorIds.Any(corridorId => !CorridorIds.Contains(
                 corridorId,
                 StringComparer.Ordinal))))
        {
            throw new ArgumentException(
                "A precise route's tradeoffs and pressure evidence must belong to its rendered corridors.",
                nameof(tradeoffs));
        }
    }

    public RoutePlanningMode Mode { get; }

    public RoutePlanShape Shape { get; }

    /// <summary>Ordered path nodes for a precise path; otherwise independent planning anchors.</summary>
    public IReadOnlyList<RoutePlanningWaypoint> Waypoints { get; }

    /// <summary>Renderable only for <see cref="RoutePlanShape.PrecisePath"/>.</summary>
    public IReadOnlyList<string> CorridorIds { get; }

    public RouteTradeoffs Tradeoffs { get; }

    /// <summary>Exact evidence used for route pressure; source, time, coverage, confidence, and model version are retained.</summary>
    public IReadOnlyList<ModelledIntelligence<RouteCorridorPressure>> PressureEvidence { get; }

    public string TradeoffSummary { get; }

    public string WhyDifferent { get; }

    public string Guidance { get; }
}

public sealed record RoutePlanBundle
{
    private static readonly RoutePlanningMode[] ModeOrder =
    [
        RoutePlanningMode.Fastest,
        RoutePlanningMode.LowerExpectedContact,
        RoutePlanningMode.Quest,
        RoutePlanningMode.Loot,
        RoutePlanningMode.TeamRegroup,
    ];

    public RoutePlanBundle(
        string mapId,
        string startWaypointId,
        string destinationWaypointId,
        TrafficRaidPhase phase,
        DateTimeOffset plannedUtc,
        string planningPolicyVersion,
        EvidenceProvenance topologyProvenance,
        IReadOnlyList<RouteAlternative> alternatives,
        IReadOnlyList<string> warnings)
    {
        MapId = RoutePlanningGuard.Identifier(mapId, nameof(mapId));
        StartWaypointId = RoutePlanningGuard.Identifier(startWaypointId, nameof(startWaypointId));
        DestinationWaypointId = RoutePlanningGuard.Identifier(destinationWaypointId, nameof(destinationWaypointId));
        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        Phase = phase;
        PlannedUtc = RoutePlanningGuard.Utc(plannedUtc, nameof(plannedUtc));
        PlanningPolicyVersion = RoutePlanningGuard.Identifier(planningPolicyVersion, nameof(planningPolicyVersion));
        TopologyProvenance = topologyProvenance ?? throw new ArgumentNullException(nameof(topologyProvenance));
        Alternatives = RoutePlanningGuard.ReadOnly(
            alternatives,
            nameof(alternatives),
            ModeOrder.Length,
            requireNonEmpty: true);
        Warnings = RoutePlanningGuard.ReadOnly(
            warnings,
            nameof(warnings),
            RoutePlanningBounds.MaximumWarnings,
            requireNonEmpty: false,
            (value, parameterName) => RoutePlanningGuard.Text(
                value,
                parameterName,
                RoutePlanningBounds.MaximumExplanationLength));

        if (Alternatives.Count != ModeOrder.Length ||
            !Alternatives.Select(alternative => alternative.Mode).SequenceEqual(ModeOrder))
        {
            throw new ArgumentException(
                "A route bundle must contain all five alternatives in canonical order.",
                nameof(alternatives));
        }


        if (Alternatives.SelectMany(alternative => alternative.PressureEvidence)
            .Any(intelligence =>
                !string.Equals(intelligence.Estimate.Value!.MapId, MapId, StringComparison.Ordinal) ||
                intelligence.Estimate.Value!.Phase != Phase))
        {
            throw new ArgumentException(
                "Every retained pressure estimate must match the route bundle's map and raid phase.",
                nameof(alternatives));
        }
    }

    public string MapId { get; }

    public string StartWaypointId { get; }

    public string DestinationWaypointId { get; }

    public TrafficRaidPhase Phase { get; }

    public DateTimeOffset PlannedUtc { get; }

    public string PlanningPolicyVersion { get; }

    public EvidenceProvenance TopologyProvenance { get; }

    public IReadOnlyList<RouteAlternative> Alternatives { get; }

    public IReadOnlyList<string> Warnings { get; }
}

internal static class RoutePlanningGuard
{
    public static string Identifier(string value, string parameterName) =>
        Text(value, parameterName, RoutePlanningBounds.MaximumIdentifierLength);

    public static string Text(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException($"{parameterName} is outside its text bounds.", parameterName);
        }

        return normalized;
    }

    public static double Unit(double value, string parameterName) =>
        double.IsFinite(value) && value is >= 0 and <= 1
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, "Expected a finite value between zero and one.");

    public static void Position(MapPoint position, string parameterName)
    {
        if (!double.IsFinite(position.X) ||
            !double.IsFinite(position.Y) ||
            Math.Abs(position.X) > RoutePlanningBounds.MaximumCoordinateMagnitude ||
            Math.Abs(position.Y) > RoutePlanningBounds.MaximumCoordinateMagnitude)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Waypoint position is outside the route-planning bounds.");
        }
    }

    public static DateTimeOffset Utc(DateTimeOffset value, string parameterName)
    {
        if (value == default)
        {
            throw new ArgumentException("A planning timestamp is required.", parameterName);
        }

        return value.ToUniversalTime();
    }

    public static ReadOnlyCollection<T> ReadOnly<T>(
        IEnumerable<T>? values,
        string parameterName,
        int maximumCount,
        bool requireNonEmpty,
        Func<T, string, T>? normalize = null)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var copy = values.ToArray();
        if (copy.Length > maximumCount || (requireNonEmpty && copy.Length == 0))
        {
            throw new ArgumentException($"{parameterName} is outside its collection bounds.", parameterName);
        }

        if (copy.Any(value => value is null))
        {
            throw new ArgumentException($"{parameterName} cannot contain null entries.", parameterName);
        }

        if (normalize is not null)
        {
            for (var index = 0; index < copy.Length; index++)
            {
                copy[index] = normalize(copy[index], parameterName);
            }
        }

        return Array.AsReadOnly(copy);
    }
}
