using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.Application.Services.Planning;

/// <summary>
/// The route's line and pins, plus the step numbers that ride on existing objective pins instead
/// of a pin of their own (<see cref="Badges"/>: objective pin id to step number).
/// </summary>
public sealed record ObjectiveRouteScene(
    MapSceneLayer Layer,
    IReadOnlyList<MapSceneObject> Objects,
    IReadOnlyDictionary<MapSceneObjectId, string> Badges);

/// <summary>An objective's own pin already on the map, which a route stop can number instead of adding one.</summary>
public sealed record ObjectiveRoutePin(string ObjectiveId, MapSceneObjectId PinId, MapScenePoint At);

/// <summary>The words the route is drawn with, from the App's string table (#314).</summary>
/// <param name="LayerName">The layer's name in the layer list.</param>
/// <param name="LineTitle">The line's label.</param>
/// <param name="LineDetail">What the line is not: it says walls, terrain, safety and live conditions are not modelled.</param>
/// <param name="Reason">A step's reason, as a sentence.</param>
public sealed record ObjectiveRouteSceneWords(
    string LayerName,
    string LineTitle,
    string LineDetail,
    Func<ObjectiveRouteReason, string> Reason);

/// <summary>Draws a planned objective visit order as one line and numbered waypoint markers.</summary>
public static class ObjectiveRouteSceneBuilder
{
    public static readonly MapSceneLayerId LayerId = new("objective-route");

    /// <summary>How far, in map units, a stop may sit from its objective's pin and still be that pin.</summary>
    public const double PinTolerance = 0.5;

    /// <param name="pins">
    /// The objective pins the map already draws. A stop whose objective has a pin on (or within
    /// <see cref="PinTolerance"/> of) the stop is numbered on that pin: a second pin on top of the
    /// lettered one hid the letter and read as two different things at one spot.
    /// </param>
    public static ObjectiveRouteScene Build(
        ObjectiveRouteBundle route,
        MapScenePoint start,
        DateTimeOffset nowUtc,
        ObjectiveRouteSceneWords words,
        IReadOnlyList<ObjectiveRoutePin>? pins = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(words);
        var provenance = new DataProvenance("objective-route-planner", nowUtc.ToUniversalTime());
        var objects = new List<MapSceneObject>(route.Steps.Count + 1);
        var badges = new Dictionary<MapSceneObjectId, string>();
        if (route.Steps.Count > 0)
        {
            objects.Add(new(
                new("objective-route:line"),
                LayerId,
                MapSceneObjectKind.Route,
                MapSceneTruthKind.PersonalPlan,
                words.LineTitle,
                words.LineDetail,
                new(MapSceneGeometryKind.Line, [start, .. route.Steps.Select(step => step.At)]),
                [],
                provenance));
        }

        foreach (var step in route.Steps)
        {
            var number = step.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (PinFor(step, pins, badges) is { } pin)
            {
                badges[pin] = number;
                continue;
            }

            objects.Add(new(
                new($"objective-route:step:{step.ObjectiveId}"),
                LayerId,
                MapSceneObjectKind.Waypoint,
                MapSceneTruthKind.PersonalPlan,
                number,
                $"{step.Label}\n{words.Reason(step.Reason)}",
                MapSceneGeometry.At(step.At),
                step.FloorIds,
                provenance));
        }

        return new(new(LayerId, words.LayerName, 62, true), objects, badges);
    }

    private static MapSceneObjectId? PinFor(
        ObjectiveRouteStep step,
        IReadOnlyList<ObjectiveRoutePin>? pins,
        IReadOnlyDictionary<MapSceneObjectId, string> taken)
    {
        if (pins is null)
        {
            return null;
        }

        MapSceneObjectId? best = null;
        var bestDistance = double.MaxValue;
        foreach (var pin in pins)
        {
            if (!string.Equals(pin.ObjectiveId, step.ObjectiveId, StringComparison.Ordinal) || taken.ContainsKey(pin.PinId))
            {
                continue;
            }

            var distance = Math.Sqrt(Math.Pow(pin.At.X - step.At.X, 2) + Math.Pow(pin.At.Y - step.At.Y, 2));
            if (distance <= PinTolerance && distance < bestDistance)
            {
                best = pin.PinId;
                bestDistance = distance;
            }
        }

        return best;
    }
}
