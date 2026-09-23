using System.Windows.Input;
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
    private bool _objectiveRouteHidden;
    private ICommand? _toggleObjectiveRouteCommand;
    private IReadOnlyDictionary<MapSceneObjectId, MapSceneObjectStyle> _objectiveRouteStyles =
        new Dictionary<MapSceneObjectId, MapSceneObjectStyle>();

    /// <summary>Whether a route from Plan has been opened on this map, so there is one to hide or show.</summary>
    public bool HasObjectiveRoute => _objectiveRouteOpened;

    /// <summary>"Hide route": the objective route leaves the map and stops recomputing until shown again.</summary>
    public bool ObjectiveRouteHidden
    {
        get => _objectiveRouteHidden;
        set
        {
            if (!SetProperty(ref _objectiveRouteHidden, value))
            {
                return;
            }

            Follower.SetEnabled(_objectiveRouteOpened && !value);
            _rebuildRequest.Request();
        }
    }

    public ICommand ToggleObjectiveRouteCommand =>
        _toggleObjectiveRouteCommand ??= new DelegateCommand(() => ObjectiveRouteHidden = !ObjectiveRouteHidden);

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
        // origin after "from", so it keeps only the noun.
        var label = _map.PlayerPosition is not null && start.Label.Contains("screenshot", StringComparison.Ordinal)
            ? "your last screenshot"
            : "the selected spawn";
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
        if (route is null || _map.RenderModel is not { } model || ObjectiveRouteOrigin() is not { } origin)
        {
            _objectiveRoute = null;
            _rebuildRequest.Request();
            return;
        }

        _objectiveRoute = new(model.Location.Id, origin, route);
        _objectiveRouteOpened = true;
        OnPropertyChanged(nameof(HasObjectiveRoute));
        RememberPlanRouteStops(model.Location.Id, [.. route.Steps.Select(step =>
            new ObjectiveRouteStop(step.ObjectiveId, step.Label, step.At) { FloorIds = step.FloorIds })]);
        Follower.SetOrigin(origin);
        ObjectiveRouteHidden = false;
        Follower.SetEnabled(true);
        _rebuildRequest.Request();
    }

    /// <summary>Plan's exactly placed objectives for the map it previews, whenever it orders them.</summary>
    internal void UpdateObjectiveRouteStops(string locationId, IReadOnlyList<ObjectiveRouteStop> stops)
    {
        if (_objectiveRouteOpened)
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
        _rebuildRequest.Request();
    }

    /// <summary>The route drawn on this rebuild, its stops merged into the objective pins already drawn.</summary>
    private ObjectiveRouteScene? ObjectiveRouteFor(MapRenderModel model)
    {
        if (_objectiveRouteOpened)
        {
            Follower.SetOrigin(ObjectiveRouteOrigin());
        }

        if (_objectiveRouteHidden || _objectiveRoute is not { } shown ||
            !string.Equals(model.Location.Id, shown.LocationId, StringComparison.OrdinalIgnoreCase))
        {
            _objectiveRouteStyles = new Dictionary<MapSceneObjectId, MapSceneObjectStyle>();
            return null;
        }

        var scene = ObjectiveRouteSceneBuilder.Build(
            shown.Route,
            shown.Origin.At,
            _timeProvider.GetUtcNow(),
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

        foreach (var (pinId, number) in scene.Badges)
        {
            styles[pinId] = new(Badge: number);
        }

        _objectiveRouteStyles = styles;
        return scene;
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
