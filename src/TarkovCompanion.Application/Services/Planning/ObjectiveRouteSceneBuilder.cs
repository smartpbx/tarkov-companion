using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.Application.Services.Planning;

public sealed record ObjectiveRouteScene(MapSceneLayer Layer, IReadOnlyList<MapSceneObject> Objects);

/// <summary>Draws a planned objective visit order as one line and numbered waypoint markers.</summary>
public static class ObjectiveRouteSceneBuilder
{
    public static readonly MapSceneLayerId LayerId = new("objective-route");

    public static ObjectiveRouteScene Build(ObjectiveRouteBundle route, MapScenePoint start, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(route);
        var provenance = new DataProvenance("objective-route-planner", nowUtc.ToUniversalTime());
        var objects = new List<MapSceneObject>(route.Steps.Count + 1);
        if (route.Steps.Count > 0)
        {
            objects.Add(new(
                new("objective-route:line"),
                LayerId,
                MapSceneObjectKind.Route,
                MapSceneTruthKind.PersonalPlan,
                "Objective visit order",
                "Straight-line plan; walls, terrain, safety and live conditions are not modelled.",
                new(MapSceneGeometryKind.Line, [start, .. route.Steps.Select(step => step.At)]),
                [],
                provenance));
        }

        foreach (var step in route.Steps)
        {
            objects.Add(new(
                new($"objective-route:step:{step.ObjectiveId}"),
                LayerId,
                MapSceneObjectKind.Waypoint,
                MapSceneTruthKind.PersonalPlan,
                step.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"{step.Label}\n{step.Reason}",
                MapSceneGeometry.At(step.At),
                step.FloorIds,
                provenance));
        }

        return new(new(LayerId, "Objective route", 62, true), objects);
    }
}
