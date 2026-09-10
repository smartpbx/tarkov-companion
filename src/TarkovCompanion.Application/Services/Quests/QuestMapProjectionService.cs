using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Quests;

public enum QuestMapGeometryKind
{
    AssociationOnly,
    Point,
    Region,
}

public sealed record QuestMapObjectiveProjection(
    string TaskId,
    string TaskName,
    string ObjectiveId,
    string Description,
    QuestObjectiveKind ObjectiveKind,
    bool IsUnsupported,
    bool IsPinned,
    string? ZoneId,
    QuestMapGeometryKind GeometryKind,
    IReadOnlyList<MapPoint> Points,
    bool IsFloorFiltered,
    string Availability,
    string? FloorHint,
    bool? FoundInRaidRequired,
    IReadOnlyList<QuestObjectiveItemTarget> ItemTargets,
    QuestCatalogProvenance QuestCatalogProvenance,
    MapCatalogProvenance MapCatalogProvenance,
    string Attribution)
{
    public bool HasExactGeometry =>
        !IsFloorFiltered && GeometryKind is QuestMapGeometryKind.Point or QuestMapGeometryKind.Region;
}

public sealed record QuestMapProjectionReadModel(
    QuestProfileScope Scope,
    long ProgressRevision,
    string LocationId,
    string VariantKey,
    IReadOnlyList<QuestMapObjectiveProjection> Objectives,
    IReadOnlyList<OrphanedQuestProgress> OrphanedProgress,
    string? UnavailableReason = null);

public sealed class QuestMapProjectionService
{
    public QuestMapProjectionReadModel Project(
        QuestMapObjectivesReadModel query,
        MapLocation location,
        MapVariant variant,
        MapFloorDefinition? selectedFloor,
        MapCatalogProvenance mapCatalogProvenance)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(mapCatalogProvenance);

        if (query.CatalogProvenance is null)
        {
            return Unavailable(query, location, variant, query.UnavailableReason ?? "Quest catalog provenance is unavailable.");
        }

        var commonFailure = ValidateProjectionBoundary(
            query,
            location,
            variant,
            mapCatalogProvenance);
        var results = query.Objectives
            .SelectMany(objective => ProjectObjective(
                objective,
                query.CatalogProvenance,
                location,
                variant,
                selectedFloor,
                mapCatalogProvenance,
                commonFailure))
            .ToArray();
        return new(
            query.Scope,
            query.ProgressRevision,
            location.Id,
            variant.Key,
            results,
            query.OrphanedProgress,
            query.UnavailableReason);
    }

    private static IReadOnlyList<QuestMapObjectiveProjection> ProjectObjective(
        QuestMapObjectiveReadModel objective,
        QuestCatalogProvenance questCatalogProvenance,
        MapLocation location,
        MapVariant variant,
        MapFloorDefinition? selectedFloor,
        MapCatalogProvenance mapCatalogProvenance,
        string? commonFailure)
    {
        if (objective.IsUnsupported)
        {
            return [Association(objective, null, questCatalogProvenance, variant, mapCatalogProvenance,
                "Unsupported objective details; map association only.")];
        }

        if (commonFailure is not null)
        {
            return [Association(objective, null, questCatalogProvenance, variant, mapCatalogProvenance, commonFailure)];
        }

        if (objective.Zones.Count == 0)
        {
            return [Association(objective, null, questCatalogProvenance, variant, mapCatalogProvenance,
                "Map association only—source objective geometry is unavailable.")];
        }

        var compatibleMapIds = CompatibleMapIds(location, variant);
        var matchingZones = objective.Zones
            .Where(zone => zone.MapId is not null && compatibleMapIds.Contains(zone.MapId))
            .OrderBy(zone => zone.SourceOrdinal)
            .ToArray();
        if (matchingZones.Length == 0)
        {
            return [Association(
                objective,
                objective.Zones.OrderBy(zone => zone.SourceOrdinal).First(),
                questCatalogProvenance,
                variant,
                mapCatalogProvenance,
                "Exact geometry is unavailable because its source map does not match the selected map.")];
        }

        return matchingZones
            .Select(zone => ProjectZone(
                objective,
                zone,
                questCatalogProvenance,
                variant,
                selectedFloor,
                mapCatalogProvenance))
            .ToArray();
    }

    private static QuestMapObjectiveProjection ProjectZone(
        QuestMapObjectiveReadModel objective,
        QuestObjectiveZone zone,
        QuestCatalogProvenance questCatalogProvenance,
        MapVariant variant,
        MapFloorDefinition? selectedFloor,
        MapCatalogProvenance mapCatalogProvenance)
    {
        if (!TryValidateElevation(zone, out var elevationFailure))
        {
            return Association(objective, zone, questCatalogProvenance, variant, mapCatalogProvenance, elevationFailure!);
        }

        if (!IsVisibleOnFloor(zone, selectedFloor))
        {
            return Association(
                objective,
                zone,
                questCatalogProvenance,
                variant,
                mapCatalogProvenance,
                $"Exact geometry is hidden by the selected upstream floor '{selectedFloor!.Name}'.",
                isFloorFiltered: true);
        }

        var sourcePoints = zone.Outline.Count switch
        {
            0 when zone.Position is { } position => [position],
            >= 3 => zone.Outline,
            0 => [],
            _ => zone.Outline,
        };
        if (sourcePoints.Count == 0)
        {
            return Association(
                objective,
                zone,
                questCatalogProvenance,
                variant,
                mapCatalogProvenance,
                "Map association only—source objective geometry is unavailable.");
        }

        if (zone.Outline.Count is 1 or 2)
        {
            return Association(
                objective,
                zone,
                questCatalogProvenance,
                variant,
                mapCatalogProvenance,
                "Source region geometry is incomplete; exact geometry is hidden.");
        }

        if (sourcePoints.Any(point => !IsFinite(point) || variant.Bounds?.Contains(point.X, point.Z) != true))
        {
            return Association(
                objective,
                zone,
                questCatalogProvenance,
                variant,
                mapCatalogProvenance,
                "Source geometry is invalid or outside the selected variant bounds; exact geometry is hidden.");
        }

        var projected = new List<MapPoint>(sourcePoints.Count);
        foreach (var sourcePoint in sourcePoints)
        {
            if (variant.Transform?.TryProject(sourcePoint, out var point) != true || !IsFinite(point))
            {
                return Association(
                    objective,
                    zone,
                    questCatalogProvenance,
                    variant,
                    mapCatalogProvenance,
                    "The validated transform could not project the source geometry; exact geometry is hidden.");
            }

            projected.Add(point);
        }

        if (projected.Count >= 3 && Math.Abs(SignedArea(projected)) < 1e-9)
        {
            return Association(
                objective,
                zone,
                questCatalogProvenance,
                variant,
                mapCatalogProvenance,
                "Source region geometry is degenerate; exact geometry is hidden.");
        }

        var geometryKind = projected.Count == 1 ? QuestMapGeometryKind.Point : QuestMapGeometryKind.Region;
        return new(
            objective.TaskId,
            objective.TaskName,
            objective.ObjectiveId,
            objective.Description,
            objective.Kind,
            objective.IsUnsupported,
            objective.IsTaskPinned || objective.IsObjectivePinned,
            zone.SourceZoneId,
            geometryKind,
            projected,
            false,
            geometryKind == QuestMapGeometryKind.Point
                ? "Exact source-authored point projected through the selected tarkov.dev variant."
                : "Exact source-authored region projected through the selected tarkov.dev variant.",
            FloorHint(zone),
            objective.FoundInRaidRequired,
            objective.ItemTargets,
            questCatalogProvenance,
            mapCatalogProvenance,
            Attribution(variant));
    }

    private static string? ValidateProjectionBoundary(
        QuestMapObjectivesReadModel query,
        MapLocation location,
        MapVariant variant,
        MapCatalogProvenance mapCatalogProvenance)
    {
        var questCatalogProvenance = query.CatalogProvenance!;
        if (questCatalogProvenance.GameMode != query.Scope.GameMode ||
            !string.Equals(questCatalogProvenance.SourceMode, SourceMode(query.Scope.GameMode), StringComparison.Ordinal))
        {
            return "Quest catalog mode does not match the exact profile scope; exact geometry is hidden.";
        }

        var compatibleMapIds = CompatibleMapIds(location, variant);
        if (!query.RequestedMapIds.Any(compatibleMapIds.Contains))
        {
            return "The objective query belongs to a different map; exact geometry is hidden.";
        }

        if (!string.Equals(location.Id, variant.LocationId, StringComparison.OrdinalIgnoreCase))
        {
            return "The selected map variant belongs to a different map; exact geometry is hidden.";
        }

        if (!variant.HasRuntimeAsset)
        {
            return "The selected map variant has no approved upstream artwork; exact geometry is hidden.";
        }

        if (variant.Transform?.IsValid != true || variant.Bounds?.IsValid != true)
        {
            return "The selected map variant has no validated transform and bounds; exact geometry is hidden.";
        }

        if (string.IsNullOrWhiteSpace(variant.Author) ||
            variant.AuthorLink is null ||
            variant.AuthorLink.Scheme != Uri.UriSchemeHttps)
        {
            return "The selected map variant has incomplete source attribution; exact geometry is hidden.";
        }

        if (!Uri.TryCreate(questCatalogProvenance.SourceUri, UriKind.Absolute, out var questSourceUri) ||
            questSourceUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(questSourceUri.Host, "json.tarkov.dev", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(questCatalogProvenance.Source) ||
            string.IsNullOrWhiteSpace(questCatalogProvenance.PayloadSha256) ||
            questCatalogProvenance.ValidatedUtc == default)
        {
            return "Quest catalog provenance is incomplete; exact geometry is hidden.";
        }

        if (mapCatalogProvenance.SourceUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(mapCatalogProvenance.ContentSha256) ||
            mapCatalogProvenance.RetrievedUtc == default)
        {
            return "Map catalog provenance is incomplete; exact geometry is hidden.";
        }

        return null;
    }

    private static bool TryValidateElevation(QuestObjectiveZone zone, out string? failure)
    {
        failure = null;
        if ((zone.BottomElevation is { } bottom && !double.IsFinite(bottom)) ||
            (zone.TopElevation is { } top && !double.IsFinite(top)) ||
            (zone.BottomElevation is { } lower && zone.TopElevation is { } upper && upper <= lower))
        {
            failure = "Source elevation geometry is invalid; exact geometry is hidden.";
            return false;
        }

        return true;
    }

    private static bool IsVisibleOnFloor(QuestObjectiveZone zone, MapFloorDefinition? selectedFloor)
    {
        if (selectedFloor is null || selectedFloor.Extents.Count == 0)
        {
            return true;
        }

        var boundedExtents = selectedFloor.Extents
            .Where(extent => extent.MinimumHeight is not null || extent.MaximumHeight is not null)
            .ToArray();
        if (boundedExtents.Length == 0)
        {
            return true;
        }

        if (zone.BottomElevation is { } bottom && zone.TopElevation is { } top)
        {
            return boundedExtents.Any(extent =>
                (extent.MinimumHeight is null || top > extent.MinimumHeight) &&
                (extent.MaximumHeight is null || bottom < extent.MaximumHeight));
        }

        if (zone.Position is { } position)
        {
            return boundedExtents.Any(extent => extent.Contains(position));
        }

        if (zone.Outline.Count > 0)
        {
            return zone.Outline.Any(point => boundedExtents.Any(extent => extent.Contains(point)));
        }

        return true;
    }

    private static QuestMapObjectiveProjection Association(
        QuestMapObjectiveReadModel objective,
        QuestObjectiveZone? zone,
        QuestCatalogProvenance questCatalogProvenance,
        MapVariant variant,
        MapCatalogProvenance mapCatalogProvenance,
        string availability,
        bool isFloorFiltered = false) => new(
        objective.TaskId,
        objective.TaskName,
        objective.ObjectiveId,
        objective.Description,
        objective.Kind,
        objective.IsUnsupported,
        objective.IsTaskPinned || objective.IsObjectivePinned,
        zone?.SourceZoneId,
        QuestMapGeometryKind.AssociationOnly,
        [],
        isFloorFiltered,
        availability,
        zone is null ? null : FloorHint(zone),
        objective.FoundInRaidRequired,
        objective.ItemTargets,
        questCatalogProvenance,
        mapCatalogProvenance,
        Attribution(variant));

    private static QuestMapProjectionReadModel Unavailable(
        QuestMapObjectivesReadModel query,
        MapLocation location,
        MapVariant variant,
        string reason) => new(
        query.Scope,
        query.ProgressRevision,
        location.Id,
        variant.Key,
        [],
        query.OrphanedProgress,
        reason);

    public static IReadOnlySet<string> CompatibleMapIds(MapLocation location, MapVariant variant)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(variant);
        return new HashSet<string>(
            new[] { location.Id, location.SourceId }
                .Concat(variant.AlternateLocationIds)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string Attribution(MapVariant variant) =>
        $"Map by {variant.Author ?? "unknown author"} via tarkov.dev · quest catalog via json.tarkov.dev";

    private static string SourceMode(GameMode gameMode) => gameMode switch
    {
        GameMode.Regular => "regular",
        GameMode.Pve => "pve",
        GameMode.PvpSeason => "pvp-season",
        _ => throw new ArgumentOutOfRangeException(nameof(gameMode)),
    };

    private static string? FloorHint(QuestObjectiveZone zone) =>
        zone.BottomElevation is { } bottom && zone.TopElevation is { } top
            ? $"Elevation {bottom:0.##} to {top:0.##}"
            : zone.Position is { } position
                ? $"Elevation {position.Y:0.##}"
                : null;

    private static bool IsFinite(WorldPosition point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);

    private static bool IsFinite(MapPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y);

    private static double SignedArea(IReadOnlyList<MapPoint> points)
    {
        var twiceArea = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            var next = (index + 1) % points.Count;
            twiceArea += (points[index].X * points[next].Y) - (points[next].X * points[index].Y);
        }

        return twiceArea / 2;
    }
}
