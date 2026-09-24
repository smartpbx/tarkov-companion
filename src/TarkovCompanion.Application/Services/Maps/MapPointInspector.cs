using TarkovCompanion.Application.Services.Strategy.Prior;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>One thing near an inspected point, and how far it is.</summary>
/// <param name="Metres">Straight-line metres; null when the map has no transform to measure with.</param>
/// <param name="Minutes">A walking estimate for extracts only: the suggested routes' pace over the straight line.</param>
public sealed record MapInspectionHit(
    MapSceneObjectId Id,
    MapSceneObjectKind Kind,
    string Label,
    double? Metres,
    (int Low, int High)? Minutes = null);

/// <summary>What is at one point of the Raid map (#286 Inspect mode).</summary>
/// <param name="AreaName">The catalog's named rectangle containing the point ("dorms"), if any.</param>
/// <param name="NearbyLabel">Failing that, the nearest place label within reach ("Near Crossroads").</param>
/// <param name="HasScale">Whether distances are metres; without a transform only the order is known.</param>
public sealed record MapInspection(
    MapScenePoint Point,
    string? AreaName,
    string? NearbyLabel,
    IReadOnlyList<MapInspectionHit> Extracts,
    IReadOnlyList<MapInspectionHit> SpawnAreas,
    IReadOnlyList<MapInspectionHit> Objectives,
    IReadOnlyList<MapInspectionHit> Loot,
    bool HasScale)
{
    public bool HasAnythingNearby => SpawnAreas.Count + Objectives.Count + Loot.Count > 0;
}

/// <summary>
/// [#286] Inspect mode's lookup: from a clicked plan point to the nearest extracts, the modelled
/// spawn areas, objectives and loot that the map already has on it there. It reads the scene the
/// map is drawing and nothing else, so it can never say something the map does not show.
/// </summary>
/// <remarks>
/// Objectives and loot come from the visible objects only: a player who turned Loot off asked not
/// to see it. Extracts and spawn areas come from the whole scene (an extract is the answer to
/// "how far out am I" whether or not its pin is on), preferring the visible extracts when any are.
/// Spawn areas are modelled places where a raid can start, never somebody seen there.
/// </remarks>
public static class MapPointInspector
{
    public const int MaximumExtracts = 3;

    public const int MaximumNearby = 4;

    /// <summary>How far "here" reaches for objectives and loot: about a building's width.</summary>
    public const double NearbyMetres = 60;

    /// <summary>Spawn areas reach further: a spawn a short run away matters to somebody standing here.</summary>
    public const double SpawnMetres = 90;

    /// <summary>A place label further than this does not name where you are.</summary>
    public const double LabelMetres = 120;

    public static MapInspection Inspect(
        MapScenePoint point,
        IReadOnlyList<MapSceneObject> objects,
        IReadOnlyList<MapSceneObject> visible,
        Func<MapScenePoint, (double X, double Z)?> toWorld,
        string? areaName)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(visible);
        ArgumentNullException.ThrowIfNull(toWorld);
        var here = toWorld(point);
        var hasScale = here is not null;

        double? MetresTo(MapSceneObject item)
        {
            var nearest = NearestPlanPoint(item.Geometry, point);
            if (here is not { } origin || toWorld(nearest) is not { } there)
            {
                return null;
            }

            return Math.Sqrt(Math.Pow(there.X - origin.X, 2) + Math.Pow(there.Z - origin.Z, 2));
        }

        double PlanDistance(MapSceneObject item)
        {
            var nearest = NearestPlanPoint(item.Geometry, point);
            return Math.Sqrt(Math.Pow(nearest.X - point.X, 2) + Math.Pow(nearest.Y - point.Y, 2));
        }

        IReadOnlyList<MapInspectionHit> Nearest(IEnumerable<MapSceneObject> candidates, double reach, int limit, bool minutes)
        {
            var measured = candidates
                .Select(item => (Item: item, Metres: MetresTo(item), Plan: PlanDistance(item)))
                .Where(entry => !double.IsNaN(entry.Plan))
                .Where(entry => reach == double.PositiveInfinity || entry.Metres is { } metres && metres <= reach)
                .OrderBy(entry => entry.Metres ?? entry.Plan)
                .ThenBy(entry => entry.Item.Label, StringComparer.OrdinalIgnoreCase);
            var hits = new List<MapInspectionHit>(limit);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (item, metres, _) in measured)
            {
                // One extract is often several objects (a pin per floor, a zone): name it once.
                if (!seen.Add(item.Label))
                {
                    continue;
                }

                hits.Add(new(
                    item.Id,
                    item.Kind,
                    item.Label,
                    metres,
                    minutes && metres is { } length ? TrafficRoute.MinutesFor(length) : null));
                if (hits.Count == limit)
                {
                    break;
                }
            }

            return hits;
        }

        var visibleExtracts = visible.Where(item => item.Kind == MapSceneObjectKind.Extract).ToArray();
        var extracts = visibleExtracts.Length > 0
            ? visibleExtracts
            : objects.Where(item => item.Kind == MapSceneObjectKind.Extract).ToArray();
        var label = Nearest(objects.Where(item => item.Kind == MapSceneObjectKind.Label), LabelMetres, 1, minutes: false);

        return new(
            point,
            string.IsNullOrWhiteSpace(areaName) ? null : areaName,
            label.Count == 0 ? null : label[0].Label,
            Nearest(extracts, double.PositiveInfinity, MaximumExtracts, minutes: true),
            hasScale ? Nearest(objects.Where(item => item.Kind == MapSceneObjectKind.SpawnArea), SpawnMetres, MaximumNearby, minutes: false) : [],
            hasScale ? Nearest(visible.Where(item => item.Kind == MapSceneObjectKind.QuestObjective && !item.IsCompleted), NearbyMetres, MaximumNearby, minutes: false) : [],
            hasScale ? Nearest(visible.Where(item => item.Kind is MapSceneObjectKind.LootSpawn or MapSceneObjectKind.LootContainer), NearbyMetres, MaximumNearby, minutes: false) : [],
            hasScale);
    }

    /// <summary>
    /// The point of a shape nearest the clicked one: the point itself inside an area's bounds,
    /// the nearest vertex of a line, the point of a pin.
    /// </summary>
    public static MapScenePoint NearestPlanPoint(MapSceneGeometry geometry, MapScenePoint point)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.Kind is MapSceneGeometryKind.Area or MapSceneGeometryKind.Region)
        {
            var bounds = geometry.Bounds;
            return new(
                Math.Clamp(point.X, bounds.MinimumX, bounds.MaximumX),
                Math.Clamp(point.Y, bounds.MinimumY, bounds.MaximumY));
        }

        var best = geometry.Points[0];
        var bestDistance = double.PositiveInfinity;
        foreach (var vertex in geometry.Points)
        {
            var distance = Math.Pow(vertex.X - point.X, 2) + Math.Pow(vertex.Y - point.Y, 2);
            if (distance < bestDistance)
            {
                best = vertex;
                bestDistance = distance;
            }
        }

        return best;
    }
}
