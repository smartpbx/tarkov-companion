using System.Globalization;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Strategy;
using TarkovCompanion.Application.Services.Strategy.Prior;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>
/// [Issue 286] Suggested routes over the modelled traffic field: from where the player last was
/// (or a spawn they select) to each extract their side can take.
/// </summary>
/// <remarks>
/// V1's <see cref="RoutePlanner"/> does the planning; see <see cref="TrafficRoutePlanner"/> for
/// why its answer is called straight-line guidance. One extract's pair of routes is drawn — the
/// cheapest by the planner's own cost until the player picks another row — and every extract
/// that was planned says how long its route is in "Extract options".
/// </remarks>
public sealed partial class RaidCockpitViewModel
{
    private static readonly MapSceneLayerId RouteLayerId = new("traffic-routes");

    /// <summary>Planning is per extract; the nearest few are the ones a player is choosing between.</summary>
    private const int MaximumRoutedExtracts = 16;

    private const string RouteColor = "#FF5CF0FF";
    private const string AlternativeRouteColor = "#FFD7DEE6";

    private readonly TrafficRoutePlanner _routePlanner = new(new RoutePlanner());
    private TrafficRouteGraph? _routeGraph;
    private MapPriorTraffic? _routeGraphPrior;
    private string? _routesSignature;
    private IReadOnlyList<ExtractRoute> _extractRoutes = [];
    private string? _chosenRouteExtract;
    private string _routeStartLabel = string.Empty;
    private bool _routeNeedsStart;
    private IReadOnlyDictionary<MapSceneObjectId, MapSceneObjectStyle> _routeStyles =
        new Dictionary<MapSceneObjectId, MapSceneObjectStyle>();

    private ExtractRoute? ShownRoute =>
        _extractRoutes.FirstOrDefault(route => route.Extract == _chosenRouteExtract) ?? _extractRoutes.FirstOrDefault();

    public bool HasSuggestedRoute => ShownRoute is not null;

    public string RouteTitle => ShownRoute is { } route ? $"To {route.Extract}" : string.Empty;

    public string RouteStartLabel => _routeStartLabel;

    public string PrimaryRouteEstimate => ShownRoute?.Plan.LowerContact.MinutesLabel ?? string.Empty;

    public bool HasAlternativeRoute => ShownRoute?.Plan.Direct is not null;

    public string AlternativeRouteEstimate => ShownRoute?.Plan.Direct?.MinutesLabel ?? string.Empty;

    public IReadOnlyList<string> RouteReasons => ShownRoute?.Plan.Reasons ?? [];

    public string RouteCaveat => "Straight-line guidance · walls, water and terrain are not modelled";

    /// <summary>There is a field to route over and nowhere to route from.</summary>
    public bool ShowsRouteHint => _routeNeedsStart && !HasSuggestedRoute;

    public string RouteHint => "Take a screenshot in raid, or select a spawn on the map, to route from there";

    private (IReadOnlyList<MapSceneLayer> Layers, IReadOnlyList<MapSceneObject> Objects) BuildRouteLayers(
        MapRenderModel model,
        string transformVersion,
        RaidSnapshot raid)
    {
        if (_prior is not { Field: { } field } prior)
        {
            SetRoutes([], string.Empty, needsStart: false, signature: null);
            return ([], []);
        }

        if (RouteStart(model) is not { } start)
        {
            SetRoutes([], string.Empty, needsStart: true, signature: null);
            return ([], []);
        }

        var scav = string.Equals(raid.Side, "scav", StringComparison.OrdinalIgnoreCase);
        // [Issue 573] A co-op extract is not a suggested-route target either, unless the player
        // asked to see co-op extracts normally.
        var extracts = model.OverlayElements
            .Where(element => element.Layer == MapOverlayKind.Extracts && !element.Label.EndsWith('→') &&
                element.Faction != (scav ? MapFeatureFaction.Pmc : MapFeatureFaction.Scav) &&
                CoOpExtracts.IsOffered(element.Label, _coOpExtractVisibility))
            .GroupBy(element => element.Label, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        // Once the game has said which extracts this raid has, those are the only choices.
        var offered = extracts.Where(element => MapViewModel.IsOfferedMarker(element.Label, raid.ActiveExtracts)).ToArray();
        var pool = (offered.Length > 0 ? offered : extracts)
            .OrderBy(element => Math.Pow(element.Position.X - start.At.X, 2) + Math.Pow(element.Position.Y - start.At.Y, 2))
            .Take(MaximumRoutedExtracts)
            .ToArray();

        var signature = string.Create(
            CultureInfo.InvariantCulture,
            $"{_priorSignature}|{field.CellOf(start.At)}|{string.Join(',', pool.Select(element => element.Label))}");
        if (signature != _routesSignature)
        {
            if (!ReferenceEquals(_routeGraphPrior, prior))
            {
                _routeGraph = _routePlanner.BuildGraph(prior.MapId, field, UnitsPerMetre(model));
                _routeGraphPrior = prior;
            }

            var planned = pool
                .Select(element => (element.Label, Plan: _routePlanner.Plan(_routeGraph!, start.At, element.Position, prior.Hotspots, prior.Phase)))
                .Where(route => route.Plan is not null)
                .OrderBy(route => route.Plan!.Cost)
                .Select(route => new ExtractRoute(route.Label, route.Plan!))
                .ToArray();
            SetRoutes(planned, start.Label, needsStart: false, signature);
        }

        if (ShownRoute is not { } shown)
        {
            return ([], []);
        }

        var (estimate, provenance) = PriorEstimate(prior, transformVersion);
        var styles = new Dictionary<MapSceneObjectId, MapSceneObjectStyle>();
        var objects = new List<MapSceneObject>(2);
        void Add(string id, string label, string detail, TrafficRoute route, MapSceneObjectStyle style)
        {
            objects.Add(new(
                new(id),
                RouteLayerId,
                MapSceneObjectKind.Route,
                MapSceneTruthKind.HistoricalEstimate,
                label,
                detail,
                new(MapSceneGeometryKind.Line, [.. route.Points.Select(point => new MapScenePoint(point.X, point.Y))]),
                [],
                provenance,
                estimate));
            styles[new(id)] = style;
        }

        if (shown.Plan.Direct is { } direct)
        {
            Add(
                "traffic-route:direct",
                $"Direct line to {shown.Extract} · {direct.MinutesLabel}",
                $"Higher modelled contact. {RouteCaveat}.",
                direct,
                new(AlternativeRouteColor, LineThickness: 3, Opacity: 0.85));
        }

        Add(
            "traffic-route:lower-contact",
            $"Lower-contact route to {shown.Extract} · {shown.Plan.LowerContact.MinutesLabel}",
            $"{string.Join(". ", shown.Plan.Reasons)}. {RouteCaveat}.",
            shown.Plan.LowerContact,
            new(RouteColor, LineThickness: 4));
        _routeStyles = styles;
        return ([new(RouteLayerId, "Suggested routes", 65, true)], objects);
    }

    private MapSceneObjectId? _routeStartSpawnId;

    private MapSceneObjectId? SelectedSpawnId() =>
        Renderer?.SelectedObject?.SceneObject is { Kind: MapSceneObjectKind.SpawnArea } spawn ? spawn.Id : null;

    /// <summary>The player's last screenshot, else a player spawn they have selected on the map.</summary>
    private (MapPoint At, string Label)? RouteStart(MapRenderModel model)
    {
        _routeStartSpawnId = SelectedSpawnId();
        if (_map.PlayerPosition is { } position && TryPlan(model, position.Position, out var here))
        {
            return (new(here.X, here.Y), "From your last screenshot");
        }

        return Renderer?.SelectedObject?.SceneObject is { Kind: MapSceneObjectKind.SpawnArea } spawn &&
            spawn.Geometry.Points is [var at, ..]
            ? (new(at.X, at.Y), "From the selected spawn")
            : null;
    }

    private void SetRoutes(
        IReadOnlyList<ExtractRoute> routes,
        string startLabel,
        bool needsStart,
        string? signature)
    {
        var changed = signature != _routesSignature || needsStart != _routeNeedsStart || routes.Count != _extractRoutes.Count;
        _routesSignature = signature;
        _extractRoutes = routes;
        _routeStartLabel = startLabel;
        _routeNeedsStart = needsStart;
        if (routes.Count == 0)
        {
            _routeStyles = new Dictionary<MapSceneObjectId, MapSceneObjectStyle>();
        }

        if (changed)
        {
            RaiseRouteChanged();
        }
    }

    private void ChooseRouteExtract(string extract)
    {
        _chosenRouteExtract = extract;
        RaiseRouteChanged();
        _rebuildRequest.Request();
    }

    /// <summary>The extract rows with their routes' estimates, planned ones first and cheapest first.</summary>
    private IReadOnlyList<RaidExtractRowViewModel> WithRouteEstimates(IReadOnlyList<RaidExtractRowViewModel> rows)
    {
        if (_extractRoutes.Count == 0)
        {
            return rows;
        }

        var shown = ShownRoute?.Extract;
        var order = _extractRoutes
            .Select((route, index) => (route, index))
            .ToDictionary(pair => pair.route.Extract, pair => pair, StringComparer.OrdinalIgnoreCase);
        return [.. rows
            .Select((row, index) => order.TryGetValue(row.Name, out var planned)
                ? (Row: row.WithRoute(
                    planned.route.Plan.LowerContact.MinutesLabel,
                    string.Equals(row.Name, shown, StringComparison.OrdinalIgnoreCase),
                    new DelegateCommand(() => ChooseRouteExtract(planned.route.Extract))), Rank: planned.index)
                : (Row: row, Rank: _extractRoutes.Count + index))
            .OrderBy(pair => pair.Rank)
            .Select(pair => pair.Row)];
    }

    private sealed record ExtractRoute(string Extract, TrafficRoutePlan Plan);

    private void RaiseRouteChanged()
    {
        OnPropertyChanged(nameof(HasSuggestedRoute));
        OnPropertyChanged(nameof(RouteTitle));
        OnPropertyChanged(nameof(RouteStartLabel));
        OnPropertyChanged(nameof(PrimaryRouteEstimate));
        OnPropertyChanged(nameof(HasAlternativeRoute));
        OnPropertyChanged(nameof(AlternativeRouteEstimate));
        OnPropertyChanged(nameof(RouteReasons));
        OnPropertyChanged(nameof(ShowsRouteHint));
    }
}
