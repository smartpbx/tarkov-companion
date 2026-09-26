using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

internal readonly record struct ObjectiveRouteOrigin(MapScenePoint At, string Label, double UnitsPerMetre);

public sealed partial class RaidCockpitViewModel
{
    /// <summary>
    /// [#307] The plan gold: Debrief's planned route (#755) and the objective route are both a
    /// plan, so both are drawn in it, and neither can be read as a cyan extract route.
    /// </summary>
    internal const string PlanRouteColor = "#FFF1C75B";

    private static readonly TimeSpan ObjectiveRouteDebounce = TimeSpan.FromMilliseconds(250);

    private ObjectiveRouteFollower? _objectiveRouteFollower;
    private ObjectiveRouteFollowerResult? _objectiveRoute;
    private bool _objectiveRouteOpened;
    private ObjectiveRouteMaps? _objectiveRouteMaps;
    private ICommand? _toggleObjectiveRouteCommand;

    /// <summary>[#902] Plan's stops per map, as Plan last handed them over this session.</summary>
    private readonly Dictionary<string, IReadOnlyList<ObjectiveRouteStop>> _planStopsByMap =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<MapSceneObjectId, MapSceneObjectStyle> _objectiveRouteStyles =
        new Dictionary<MapSceneObjectId, MapSceneObjectStyle>();

    /// <summary>Whether a route from Plan has been opened on this map, so there is one to hide or show.</summary>
    public bool HasObjectiveRoute => _objectiveRouteOpened;

    /// <summary>
    /// [#902] "Show route": the Objective route layer itself, saved, and read by Plan's preview.
    /// Opening a route again never turns it back on; only the player does.
    /// </summary>
    public bool ObjectiveRouteShown
    {
        get => RouteLayerSwitch.IsShown(Renderer, _layerVisibility, RouteLayerSwitch.Objective);
        set => SetRouteLayer(RouteLayerSwitch.Objective, value);
    }

    public ICommand ToggleObjectiveRouteCommand =>
        _toggleObjectiveRouteCommand ??= new DelegateCommand(() => ObjectiveRouteShown = !ObjectiveRouteShown);

    private ObjectiveRouteMaps RouteMaps => _objectiveRouteMaps ??= new(_layout);

    private ObjectiveRouteFollower Follower => _objectiveRouteFollower ??= new(
        _timeProvider,
        ObjectiveRouteDebounce,
        PublishObjectiveRoute,
        SynchronizationContext.Current);

    /// <summary>The player's last screenshot, else the spawn selected on the Raid map.</summary>
    internal ObjectiveRouteOrigin? ObjectiveRouteOrigin()
    {
        if (_map.RenderModel is not { } model)
        {
            return null;
        }

        // RouteStart remembers the spawn it used so the extract routes notice a new one; Plan asking
        // for the origin must not make a spawn change look already handled.
        var rememberedSpawn = _routeStartSpawnId;
        var routeStart = RouteStart(model);
        _routeStartSpawnId = rememberedSpawn;
        if (routeStart is not { } start)
        {
            return null;
        }

        var unitsPerMetre = UnitsPerMetre(model);
        if (!double.IsFinite(unitsPerMetre) || unitsPerMetre <= 0)
        {
            return null;
        }

        // The extract routes label reads "From the selected spawn"; the objective route puts its
        // origin after "from", so it keeps only the noun. [#314] Matched against the same translated
        // words RouteStart gave it, not against English.
        var label = _map.PlayerPosition is not null && string.Equals(start.Label, RaidText.FromYourLastScreenshot, StringComparison.Ordinal)
            ? RaidText.YourLastScreenshot
            : RaidText.TheSelectedSpawn;
        return new(new(start.At.X, start.At.Y), label, unitsPerMetre);
    }

    /// <summary>Hands Plan's chosen-map visit order to the Raid map as numbered waypoints.</summary>
    /// <remarks>
    /// [#307] From here on the route follows the plan: its stops are fed back by Plan
    /// (<see cref="UpdateObjectiveRouteStops"/>) and its origin by every scene rebuild, and it is
    /// recomputed when either changes rather than standing still until the next "Open in Raid".
    /// </remarks>
    internal void SetObjectiveRoute(string mapId, ObjectiveRouteBundle? route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        if (route is null || _map.RenderModel is not { } model)
        {
            _objectiveRoute = null;
            PublishSituationPlan(null);
            _rebuildRequest.Request();
            return;
        }

        // [#902] Opened even with nowhere to start from yet: the follower draws it once a screenshot
        // or a selected spawn gives it an origin, instead of the press being lost.
        var origin = ObjectiveRouteOrigin();
        _objectiveRoute = origin is { } start ? new(model.Location.Id, start, route) : null;
        RouteMaps.Add(model.Location.Id);
        SetObjectiveRouteOpened(true);
        _planStopsByMap[model.Location.Id] = [.. route.Steps.Select(step =>
            new ObjectiveRouteStop(step.ObjectiveId, step.Label, step.At) { FloorIds = step.FloorIds })];
        RememberPlanRouteStops(model.Location.Id, _planStopsByMap[model.Location.Id]);
        Follower.SetOrigin(origin);
        Follower.SetEnabled(true);
        _rebuildRequest.Request();
    }

    /// <summary>Plan's exactly placed objectives for the map it previews, whenever it orders them.</summary>
    internal void UpdateObjectiveRouteStops(string locationId, IReadOnlyList<ObjectiveRouteStop> stops)
    {
        if (RouteMaps.Contains(locationId))
        {
            _planStopsByMap[locationId] = stops;
        }

        if (_objectiveRouteOpened && string.Equals(_map.RenderModel?.Location.Id, locationId, StringComparison.OrdinalIgnoreCase))
        {
            // [#780] Through the squad's stops, which "Route squad" may add to Plan's.
            RememberPlanRouteStops(locationId, stops);
        }
    }

    private void PublishObjectiveRoute(ObjectiveRouteFollowerResult? result)
    {
        if (_disposed)
        {
            return;
        }

        _objectiveRoute = result;
        PublishSituationPlan(result);
        _rebuildRequest.Request();
    }

    /// <summary>The route drawn on this rebuild, its stops merged into the objective pins already drawn.</summary>
    /// <remarks>
    /// [#902] Always a layer, empty or not, so the Layers row stays and reads off when it is off.
    /// Its objects are handed over while hidden too, so the row counts them; only the step numbers
    /// riding on objective pins need leaving out, because those pins belong to another layer.
    /// </remarks>
    private ObjectiveRouteScene ObjectiveRouteFor(MapRenderModel model)
    {
        FollowObjectiveRouteMap(model);
        if (_objectiveRouteOpened)
        {
            Follower.SetOrigin(ObjectiveRouteOrigin());
        }

        if (_objectiveRoute is not { } shown ||
            !string.Equals(model.Location.Id, shown.LocationId, StringComparison.OrdinalIgnoreCase))
        {
            _objectiveRouteStyles = new Dictionary<MapSceneObjectId, MapSceneObjectStyle>();
            return new(ObjectiveRouteLayer(), [], new Dictionary<MapSceneObjectId, string>());
        }

        var scene = ObjectiveRouteSceneBuilder.Build(
            shown.Route,
            shown.Origin.At,
            _timeProvider.GetUtcNow(),
            PlanText.ObjectiveRouteWords(),
            // With the objectives layer switched off there are no pins to number, so every stop
            // gets its own gold pin again.
            QuestPinsShown() ? [.. ObjectiveRoutePins(_questScene), .. SquadRoutePins()] : null);
        var styles = new Dictionary<MapSceneObjectId, MapSceneObjectStyle>();
        foreach (var item in scene.Objects)
        {
            if (ObjectiveRouteStyle(item) is { } style)
            {
                styles[item.Id] = style;
            }
        }

        if (ObjectiveRouteShown)
        {
            foreach (var (pinId, number) in scene.Badges)
            {
                styles[pinId] = new(Badge: number);
            }
        }

        _objectiveRouteStyles = styles;
        return scene;
    }

    private static MapSceneLayer ObjectiveRouteLayer() =>
        new(RouteLayerSwitch.Objective, PlanText.ObjectiveRouteWords().LayerName, 62, true);

    /// <summary>
    /// [#902] Whether this map has a route opened on it, remembered across a restart, and the stops
    /// it follows: Plan's, once Plan has handed them over this session, else this map's own
    /// single-spot objective pins, the rule Plan orders its stops by.
    /// </summary>
    private void FollowObjectiveRouteMap(MapRenderModel model)
    {
        var opened = RouteMaps.Contains(model.Location.Id);
        SetObjectiveRouteOpened(opened);
        if (!opened)
        {
            return;
        }

        Follower.SetEnabled(true);
        RememberPlanRouteStops(
            model.Location.Id,
            _planStopsByMap.TryGetValue(model.Location.Id, out var planned) ? planned : StopsFromObjectivePins(_questScene));
    }

    private void SetObjectiveRouteOpened(bool opened)
    {
        if (_objectiveRouteOpened != opened)
        {
            _objectiveRouteOpened = opened;
            OnPropertyChanged(nameof(HasObjectiveRoute));
        }
    }

    /// <summary>Each placed objective with exactly one spot, once: several candidates name no one place to walk to.</summary>
    internal static IReadOnlyList<ObjectiveRouteStop> StopsFromObjectivePins(QuestObjectiveScene scene)
    {
        var objects = scene.Objects.ToDictionary(item => item.Id);
        var stops = new List<ObjectiveRouteStop>();
        foreach (var entry in scene.Entries.Where(entry => entry.IsPlaced).DistinctBy(entry => entry.ObjectiveId, StringComparer.Ordinal))
        {
            var points = entry.ObjectIds
                .Where(objects.ContainsKey)
                .Select(id => objects[id].Geometry)
                .Where(geometry => geometry.Kind == MapSceneGeometryKind.Point)
                .Select(geometry => geometry.Points[0])
                .Distinct()
                .ToArray();
            if (points.Length == 1)
            {
                stops.Add(new(entry.ObjectiveId, entry.Objective.Description, points[0]) { FloorIds = entry.FloorIds });
            }
        }

        return stops;
    }

    private bool QuestPinsShown()
    {
        var questsLayer = Application.Services.Maps.Scene.MapSceneAssembler.IdFor(MapOverlayKind.QuestObjectives);
        return Renderer?.Scene.View.Layers.FirstOrDefault(state => state.LayerId == questsLayer)?.IsVisible ?? true;
    }

    /// <summary>Every objective pin the scene draws at a single point, by the objective it belongs to.</summary>
    internal static IReadOnlyList<ObjectiveRoutePin> ObjectiveRoutePins(QuestObjectiveScene scene)
    {
        var objects = scene.Objects
            .Where(item => item.Kind == MapSceneObjectKind.QuestObjective && item.Geometry.Kind == MapSceneGeometryKind.Point)
            .ToDictionary(item => item.Id);
        return
        [
            .. scene.Entries.SelectMany(entry => entry.ObjectIds
                .Where(objects.ContainsKey)
                .Select(id => new ObjectiveRoutePin(entry.ObjectiveId, id, objects[id].Geometry.Points[0]))),
        ];
    }

    /// <summary>The plan gold, dashed line and gold numbered pins; also used by Plan's own preview.</summary>
    internal static MapSceneObjectStyle? ObjectiveRouteStyle(MapSceneObject item) =>
        item.LayerId != ObjectiveRouteSceneBuilder.LayerId ? null
        : item.Kind == MapSceneObjectKind.Route ? new MapSceneObjectStyle(PlanRouteColor, LineThickness: 3, Opacity: 0.9, Dashed: true)
        // [#307] A stop's number is the same gold badge an objective pin carries, not the pin's own small label.
        : new MapSceneObjectStyle(PlanRouteColor, Badge: item.Kind == MapSceneObjectKind.Waypoint ? item.Label : null);
}
