namespace TarkovCompanion.Core.Domain.Maps.Scene;

public sealed record MapSceneHit(MapSceneObject Object, double Distance);

/// <summary>Renderer-independent hit testing for pointer, touch, and keyboard focus handoff.</summary>
public static class MapSceneHitTesting
{
    public static IReadOnlyList<MapSceneHit> HitTest(
        MapSceneSnapshot scene,
        MapScenePoint point,
        double tolerance)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!double.IsFinite(tolerance) || tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        }

        var zIndexes = scene.Layers.ToDictionary(layer => layer.Id, layer => layer.ZIndex);
        return scene.VisibleObjects
            .Select(item => new MapSceneHit(item, Distance(item.Geometry, point)))
            .Where(hit => hit.Distance <= tolerance)
            .OrderByDescending(hit => zIndexes[hit.Object.LayerId])
            .ThenBy(hit => hit.Distance)
            .ThenBy(hit => hit.Object.Id.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static double Distance(MapSceneGeometry geometry, MapScenePoint point) => geometry.Kind switch
    {
        MapSceneGeometryKind.Point => Distance(point, geometry.Points[0]),
        MapSceneGeometryKind.Line => DistanceToSegments(point, geometry.Points, close: false),
        MapSceneGeometryKind.Area or MapSceneGeometryKind.Region when Contains(point, geometry.Points) => 0,
        MapSceneGeometryKind.Area or MapSceneGeometryKind.Region => DistanceToSegments(point, geometry.Points, close: true),
        _ => double.PositiveInfinity,
    };

    private static double DistanceToSegments(
        MapScenePoint point,
        IReadOnlyList<MapScenePoint> vertices,
        bool close)
    {
        var segmentCount = close ? vertices.Count : vertices.Count - 1;
        var nearest = double.PositiveInfinity;
        for (var index = 0; index < segmentCount; index++)
        {
            nearest = Math.Min(nearest, DistanceToSegment(point, vertices[index], vertices[(index + 1) % vertices.Count]));
        }

        return nearest;
    }

    private static double DistanceToSegment(MapScenePoint point, MapScenePoint start, MapScenePoint end)
    {
        var deltaX = end.X - start.X;
        var deltaY = end.Y - start.Y;
        var lengthSquared = (deltaX * deltaX) + (deltaY * deltaY);
        if (lengthSquared <= double.Epsilon)
        {
            return Distance(point, start);
        }

        var projection = (((point.X - start.X) * deltaX) + ((point.Y - start.Y) * deltaY)) / lengthSquared;
        var clamped = Math.Clamp(projection, 0, 1);
        return Distance(point, new(start.X + (clamped * deltaX), start.Y + (clamped * deltaY)));
    }

    private static double Distance(MapScenePoint first, MapScenePoint second)
    {
        var deltaX = first.X - second.X;
        var deltaY = first.Y - second.Y;
        return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
    }

    private static bool Contains(MapScenePoint point, IReadOnlyList<MapScenePoint> vertices)
    {
        var inside = false;
        for (var current = 0, previous = vertices.Count - 1; current < vertices.Count; previous = current++)
        {
            var currentPoint = vertices[current];
            var previousPoint = vertices[previous];
            var crosses = (currentPoint.Y > point.Y) != (previousPoint.Y > point.Y) &&
                point.X < ((previousPoint.X - currentPoint.X) * (point.Y - currentPoint.Y) /
                    (previousPoint.Y - currentPoint.Y)) + currentPoint.X;
            if (crosses)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
