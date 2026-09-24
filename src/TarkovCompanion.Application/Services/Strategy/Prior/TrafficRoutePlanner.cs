using System.Globalization;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Strategy;

namespace TarkovCompanion.Application.Services.Strategy.Prior;

/// <summary>One line over the plan, and what the traffic field says about walking it.</summary>
/// <param name="MeanTraffic">Modelled traffic averaged over the route's length, 0 to 1.</param>
public sealed record TrafficRoute(
    IReadOnlyList<MapPoint> Points,
    double Metres,
    double MeanTraffic,
    double PeakTraffic,
    MapPoint PeakAt)
{
    /// <summary>Walls and detours the grid cannot see, as a share of the measured length.</summary>
    public const double ObstacleAllowance = 1.25;

    /// <summary>Metres a second: moving carefully, and moving with purpose. Neither is a sprint.</summary>
    public const double CarefulPace = 1.8;

    public const double BriskPace = 3.2;

    public int MinutesLow => Math.Max(1, (int)Math.Floor(Metres * ObstacleAllowance / BriskPace / 60));

    public int MinutesHigh => Math.Max(MinutesLow + 1, (int)Math.Ceiling(Metres * ObstacleAllowance / CarefulPace / 60));
}

/// <summary>Why the lower-contact route is suggested, as a code the App puts into words (#314).</summary>
public enum TrafficRouteReasonKind
{
    /// <summary>The direct line already has the lowest modelled contact.</summary>
    DirectIsLowest,

    /// <summary>The one route's length and mean contact.</summary>
    LengthAndContact,

    /// <summary>Avoids a named hotspot's convergence (Place set) or the direct line's busiest stretch.</summary>
    AvoidsPeak,

    /// <summary>Mean contact along this route against the direct line's.</summary>
    ContactAgainstDirect,

    /// <summary>How much longer than the direct line.</summary>
    Longer,

    /// <summary>Still crosses raised traffic near a named hotspot (Place set), or at its busiest.</summary>
    StillCrosses,
}

/// <summary>One reason, with the route numbers it was read from.</summary>
/// <param name="Place">A hotspot's name, when the reason is about one.</param>
/// <param name="Share">A traffic share, 0 to 1: this route's mean or peak.</param>
/// <param name="OtherShare">The direct line's matching share, where the reason compares.</param>
public sealed record TrafficRouteReason(
    TrafficRouteReasonKind Kind,
    string? Place = null,
    double Metres = 0,
    double OtherMetres = 0,
    double Share = 0,
    double OtherShare = 0);

/// <summary>The lower-contact route to one destination, the direct line when it differs, and why.</summary>
/// <param name="Cost">What the planner minimised: metres, plus five times metres weighted by traffic.</param>
public sealed record TrafficRoutePlan(
    TrafficRoute LowerContact,
    TrafficRoute? Direct,
    double Cost,
    IReadOnlyList<TrafficRouteReason> Reasons,
    Confidence Confidence);

/// <summary>The traffic field as a graph V1's <see cref="IRoutePlanner"/> can walk.</summary>
public sealed record TrafficRouteGraph(RouteGraph Graph, TrafficField Field, int Stride, int Columns, int Rows, double UnitsPerMetre);

/// <summary>
/// Routes over a modelled traffic field, planned by the route planner V1 shipped.
/// </summary>
/// <remarks>
/// <para>
/// The planner existed with two modes that are exactly this feature — Fastest, and AvoidPvp, which
/// weighs an edge's risk five times — and nothing in the product ever gave it a graph. This gives
/// it one: the field's cells as nodes, neighbours joined, travel cost in metres and risk cost in
/// metres weighted by the traffic under them.
/// </para>
/// <para>
/// The graph is marked incomplete, because it is: the catalog has no walls, water or fences, so
/// an edge exists wherever two cells touch. The planner reports such a route as imprecise at 0.45
/// confidence, and the page says "straight-line guidance" beside it. Every reason given for a
/// route is read back from the two routes' own numbers; none is a sentence about the map.
/// </para>
/// </remarks>
public sealed class TrafficRoutePlanner
{
    /// <summary>Field cells per graph node along each axis: a node every thirty metres is plenty for guidance this coarse.</summary>
    public const int Stride = 2;

    private static readonly IReadOnlySet<RaidPhase> AllPhases = new HashSet<RaidPhase> { RaidPhase.Early, RaidPhase.Mid, RaidPhase.Late };

    private readonly IRoutePlanner _planner;

    public TrafficRoutePlanner(IRoutePlanner planner) => _planner = planner ?? throw new ArgumentNullException(nameof(planner));

    public TrafficRouteGraph BuildGraph(string mapId, TrafficField field, double unitsPerMetre)
    {
        ArgumentNullException.ThrowIfNull(field);
        var columns = (field.Columns + Stride - 1) / Stride;
        var rows = (field.Rows + Stride - 1) / Stride;
        var nodes = new List<RouteNode>(columns * rows);
        var values = new double[columns * rows];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                nodes.Add(new(Id(column, row), Id(column, row), Centre(field, column, row), "cell"));
                // The busiest cell under a node, not the mean: a route should not be let through
                // a hotspot because the ground beside it is quiet.
                for (var y = row * Stride; y < Math.Min(field.Rows, (row + 1) * Stride); y++)
                {
                    for (var x = column * Stride; x < Math.Min(field.Columns, (column + 1) * Stride); x++)
                    {
                        values[(row * columns) + column] = Math.Max(values[(row * columns) + column], field[x, y]);
                    }
                }
            }
        }

        var edges = new List<RouteEdge>(columns * rows * 8);
        var step = field.Cell * Stride / unitsPerMetre;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var (x, y) = (column + dx, row + dy);
                        if ((dx == 0 && dy == 0) || x < 0 || y < 0 || x >= columns || y >= rows)
                        {
                            continue;
                        }

                        var metres = step * (dx != 0 && dy != 0 ? Math.Sqrt(2) : 1);
                        var traffic = (values[(row * columns) + column] + values[(y * columns) + x]) / 2;
                        edges.Add(new(Id(column, row), Id(x, y), metres, metres * traffic, 0, AllPhases));
                    }
                }
            }
        }

        return new(new(mapId, nodes, edges, IsComplete: false), field, Stride, columns, rows, unitsPerMetre);
    }

    /// <summary>Null when either end is off the plan or the planner finds no way between them.</summary>
    public TrafficRoutePlan? Plan(
        TrafficRouteGraph graph,
        MapPoint start,
        MapPoint destination,
        IReadOnlyList<TrafficHotspot> hotspots,
        RaidPhase phase)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(hotspots);
        if (!OnPlan(graph.Field, start) || !OnPlan(graph.Field, destination))
        {
            return null;
        }

        var from = NodeAt(graph, start);
        var to = NodeAt(graph, destination);
        var quiet = _planner.Plan(graph.Graph, from, to, RouteMode.AvoidPvp, phase);
        var fast = _planner.Plan(graph.Graph, from, to, RouteMode.Fastest, phase);
        if (quiet.Nodes.Count == 0 || fast.Nodes.Count == 0 ||
            (from != to && (quiet.Nodes.Count < 2 || fast.Nodes.Count < 2)))
        {
            return null;
        }

        var lower = Measure(graph, start, destination, quiet.Nodes);
        var direct = Measure(graph, start, destination, fast.Nodes);
        // The same cells, or a detour that buys nothing measurable: there is one route, not two.
        var differs = !quiet.Nodes.Select(node => node.Id).SequenceEqual(fast.Nodes.Select(node => node.Id)) &&
            direct.MeanTraffic - lower.MeanTraffic >= 0.02;
        return new(
            lower,
            differs ? direct : null,
            quiet.Cost,
            Reasons(lower, differs ? direct : null, hotspots),
            quiet.Confidence);
    }

    /// <summary>Why the lower-contact route is the one suggested, from the two routes' own numbers.</summary>
    internal static IReadOnlyList<TrafficRouteReason> Reasons(TrafficRoute lower, TrafficRoute? direct, IReadOnlyList<TrafficHotspot> hotspots)
    {
        var reasons = new List<TrafficRouteReason>();
        if (direct is null)
        {
            reasons.Add(new(TrafficRouteReasonKind.DirectIsLowest));
            reasons.Add(new(TrafficRouteReasonKind.LengthAndContact, Metres: lower.Metres, Share: lower.MeanTraffic));
        }
        else
        {
            if (direct.PeakTraffic - lower.PeakTraffic >= 0.15)
            {
                reasons.Add(new(
                    TrafficRouteReasonKind.AvoidsPeak,
                    Nearest(hotspots, direct.PeakAt)?.Name,
                    Share: lower.PeakTraffic,
                    OtherShare: direct.PeakTraffic));
            }

            reasons.Add(new(TrafficRouteReasonKind.ContactAgainstDirect, Share: lower.MeanTraffic, OtherShare: direct.MeanTraffic));
            reasons.Add(new(TrafficRouteReasonKind.Longer, Metres: lower.Metres, OtherMetres: direct.Metres));
        }

        if (lower.PeakTraffic >= 0.5)
        {
            reasons.Add(new(TrafficRouteReasonKind.StillCrosses, Nearest(hotspots, lower.PeakAt)?.Name, Share: lower.PeakTraffic));
        }

        return reasons;
    }

    private static TrafficHotspot? Nearest(IReadOnlyList<TrafficHotspot> hotspots, MapPoint point) => hotspots
        .Select(hotspot => (hotspot, Distance: Distance(hotspot.Position, point)))
        .Where(pair => pair.Distance <= pair.hotspot.RadiusUnits * 2)
        .OrderBy(pair => pair.Distance)
        .Select(pair => pair.hotspot)
        .FirstOrDefault();

    private static TrafficRoute Measure(TrafficRouteGraph graph, MapPoint start, MapPoint destination, IReadOnlyList<RouteNode> nodes)
    {
        // The ends are where the player and the extract are, not the centres of the cells they are in.
        var points = new List<MapPoint> { start };
        points.AddRange(nodes.Skip(1).Take(Math.Max(0, nodes.Count - 2)).Select(node => node.Position));
        points.Add(destination);

        double units = 0, weighted = 0, peak = 0;
        var peakAt = start;
        for (var index = 1; index < points.Count; index++)
        {
            var length = Distance(points[index - 1], points[index]);
            var samples = Math.Max(1, (int)Math.Ceiling(length / (graph.Field.Cell / 2)));
            for (var sample = 0; sample < samples; sample++)
            {
                var t = (sample + 0.5) / samples;
                var at = new MapPoint(
                    points[index - 1].X + ((points[index].X - points[index - 1].X) * t),
                    points[index - 1].Y + ((points[index].Y - points[index - 1].Y) * t));
                var value = graph.Field.ValueAt(at);
                weighted += value * length / samples;
                if (value > peak)
                {
                    (peak, peakAt) = (value, at);
                }
            }

            units += length;
        }

        return new(Smooth(points), units / graph.UnitsPerMetre, units > 0 ? weighted / units : 0, peak, peakAt);
    }

    /// <summary>Two passes of corner cutting: a path through cell centres is a staircase, and nobody walks one.</summary>
    private static IReadOnlyList<MapPoint> Smooth(IReadOnlyList<MapPoint> points)
    {
        var current = points;
        for (var pass = 0; pass < 2 && current.Count > 2; pass++)
        {
            var next = new List<MapPoint>((current.Count * 2) + 2) { current[0] };
            for (var index = 0; index < current.Count - 1; index++)
            {
                var (a, b) = (current[index], current[index + 1]);
                next.Add(new(a.X + ((b.X - a.X) * 0.25), a.Y + ((b.Y - a.Y) * 0.25)));
                next.Add(new(a.X + ((b.X - a.X) * 0.75), a.Y + ((b.Y - a.Y) * 0.75)));
            }

            next.Add(current[^1]);
            current = next;
        }

        return current;
    }

    private static bool OnPlan(TrafficField field, MapPoint point) =>
        point.X >= field.MinimumX && point.Y >= field.MinimumY &&
        point.X <= field.MinimumX + field.Width && point.Y <= field.MinimumY + field.Height;

    private static string NodeAt(TrafficRouteGraph graph, MapPoint point)
    {
        var (column, row) = graph.Field.CellOf(point);
        return Id(column / graph.Stride, row / graph.Stride);
    }

    private static MapPoint Centre(TrafficField field, int column, int row)
    {
        var size = field.Cell * Stride;
        // Held inside the plan: the last node of a row can hang over its edge, and a line that
        // leaves the plan is one the renderer refuses to draw at all.
        return new(
            Math.Min(field.MinimumX + ((column + 0.5) * size), field.MinimumX + field.Width),
            Math.Min(field.MinimumY + ((row + 0.5) * size), field.MinimumY + field.Height));
    }

    private static string Id(int column, int row) => string.Create(CultureInfo.InvariantCulture, $"{column}:{row}");

    private static double Distance(MapPoint a, MapPoint b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
