using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>One heading of the Inspect popover and its lines.</summary>
public sealed record RaidInspectSection(string Title, IReadOnlyList<string> Lines, string? Caveat = null)
{
    public bool HasCaveat => Caveat is { Length: > 0 };
}

/// <summary>
/// [#286] The Raid map's Navigate / Inspect / Route / Draw switch, and the Inspect and Route modes.
/// </summary>
/// <remarks>
/// Inspect answers "what is here?" from what the map already carries: the catalog's named area,
/// the nearest extracts with a walking estimate over the straight line, modelled spawn areas, and
/// the objectives and loot on the layers that are on. It never places anything.
/// Route builds the player's own planned route, click by click, as waypoints that carry one route
/// id and a step: the same marks a tablet's "Draw route" makes (#787), so they take the Marks
/// card's Just me / Squad switch, go to the squad through the same forwarder, and are joined by
/// the same dashed line. In every mode a drag still pans and Escape goes back to Navigate.
/// </remarks>
public sealed partial class RaidCockpitViewModel
{
    private readonly PlannedRoute _plannedRoute = new();
    private readonly Dictionary<(Guid Route, int Step), Task<RaidMark>> _routePlacements = [];
    private string? _plannedRouteMapId;
    private RaidMarkLifetime _newRouteLifetime = RaidMarkLifetime.ThisRaid;
    private MapInspection? _inspection;
    private string? _inspectionMapId;
    private IReadOnlyList<RaidInspectSection> _inspectSections = [];
    private ICommand? _navigateModeCommand;
    private ICommand? _inspectModeCommand;
    private ICommand? _routeModeCommand;
    private ICommand? _undoRouteStopCommand;
    private ICommand? _clearRouteCommand;
    private ICommand? _closeInspectCommand;

    public bool IsNavigateMode => _interactionMode == MapInteractionMode.Navigate;

    public bool IsInspectMode => _interactionMode == MapInteractionMode.Inspect;

    public bool IsRouteMode => _interactionMode == MapInteractionMode.Route;

    /// <summary>A plain click belongs to the mode (Inspect, Route), not to selection.</summary>
    public bool IsClickMode => IsInspectMode || IsRouteMode;

    /// <summary>Any mode but Navigate: Escape has somewhere to go back from.</summary>
    public bool LeavesModeOnEscape => !IsNavigateMode;

    public ICommand NavigateModeCommand => _navigateModeCommand ??= new DelegateCommand(() => SetInteractionMode(MapInteractionMode.Navigate));

    /// <summary>Pressing a lit mode again goes back to Navigate, the way the Draw pencil always has.</summary>
    public ICommand InspectModeCommand => _inspectModeCommand ??= new DelegateCommand(() =>
        SetInteractionMode(IsInspectMode ? MapInteractionMode.Navigate : MapInteractionMode.Inspect));

    public ICommand RouteModeCommand => _routeModeCommand ??= new DelegateCommand(() =>
        SetInteractionMode(IsRouteMode ? MapInteractionMode.Navigate : MapInteractionMode.Route));

    public ICommand UndoRouteStopCommand => _undoRouteStopCommand ??= new DelegateCommand(UndoRouteStop);

    public ICommand ClearRouteCommand => _clearRouteCommand ??= new DelegateCommand(ClearRoute);

    public ICommand CloseInspectCommand => _closeInspectCommand ??= new DelegateCommand(() => SetInspection(null));

    // ---- Inspect ----

    public MapInspection? Inspection => _inspection;

    public bool HasInspection => _inspection is not null;

    /// <summary>Inspect mode with nothing clicked yet: the bar says what a click does.</summary>
    public bool ShowsInspectHint => IsInspectMode && _inspection is null;

    public string InspectTitle => _inspection switch
    {
        null => string.Empty,
        { AreaName: { } area } => Capitalise(area),
        { NearbyLabel: { } near } => RaidText.InspectNear(near),
        _ => RaidText.InspectHere,
    };

    public IReadOnlyList<RaidInspectSection> InspectSections => _inspectSections;

    /// <summary>Shows what is at a plan point, from the scene the map is drawing now.</summary>
    public MapInspection? InspectAt(MapScenePoint point)
    {
        if (Renderer is not { } renderer || _map.RenderModel is not { } model)
        {
            return null;
        }

        var floorId = renderer.Scene.View.SelectedFloorId ?? model.SelectedFloor?.Id;
        var floor = model.Floors.FirstOrDefault(candidate => string.Equals(candidate.Id, floorId, StringComparison.OrdinalIgnoreCase))
            ?? model.SelectedFloor;
        var height = FloorHeight(model, floorId) ?? _stateStore.Current.Raid.LastKnownPosition?.Position.Y ?? 0;
        var toWorld = WorldConverter(model, height);
        var area = toWorld(new MapPoint(point.X, point.Y)) is { } world
            ? MapAreaName.Describe(floor, new WorldPosition(world.X, height, world.Z))
            : null;
        var inspection = MapPointInspector.Inspect(
            point,
            renderer.Scene.Objects,
            renderer.Scene.VisibleObjects,
            scenePoint => toWorld(new MapPoint(scenePoint.X, scenePoint.Y)),
            area);
        SetInspection(inspection);
        return inspection;
    }

    /// <summary>The popover's sections, in the order a player reads them: out, then here.</summary>
    internal static IReadOnlyList<RaidInspectSection> SectionsFor(MapInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        var sections = new List<RaidInspectSection>();
        if (inspection.Extracts.Count > 0)
        {
            sections.Add(new(
                RaidText.InspectExtracts,
                [.. inspection.Extracts.Select(hit => RaidText.InspectExtract(hit.Label, hit.Metres, hit.Minutes))],
                inspection.HasScale ? RaidText.InspectExtractsCaveat : null));
        }

        void Add(string title, IReadOnlyList<MapInspectionHit> hits)
        {
            if (hits.Count > 0)
            {
                sections.Add(new(title, [.. hits.Select(hit => RaidText.InspectDistance(hit.Label, hit.Metres))]));
            }
        }

        Add(RaidText.InspectObjectives, inspection.Objectives);
        Add(RaidText.InspectLoot, inspection.Loot);
        Add(RaidText.InspectSpawns, inspection.SpawnAreas);
        if (!inspection.HasAnythingNearby && inspection.HasScale)
        {
            sections.Add(new(RaidText.InspectNothing, []));
        }

        return sections;
    }

    private void RenderModelChangedForModes()
    {
        var mapId = _map.RenderModel?.Location.Id;
        if (_inspection is not null && !string.Equals(mapId, _inspectionMapId, StringComparison.OrdinalIgnoreCase))
        {
            SetInspection(null);
        }

        PlannedRouteChanged();
    }

    private void SetInspection(MapInspection? inspection)
    {
        _inspectionMapId = inspection is null ? null : _map.RenderModel?.Location.Id;
        _inspection = inspection;
        _inspectSections = inspection is null ? [] : SectionsFor(inspection);
        OnPropertyChanged(nameof(Inspection));
        OnPropertyChanged(nameof(HasInspection));
        OnPropertyChanged(nameof(ShowsInspectHint));
        OnPropertyChanged(nameof(InspectTitle));
        OnPropertyChanged(nameof(InspectSections));
    }

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];

    // ---- Route ----

    public int PlannedRouteStopCount => _plannedRoute.Stops.Count;

    public bool HasPlannedRouteStops => _plannedRoute.Stops.Count > 0;

    public bool IsPlannedRouteFull => _plannedRoute.IsFull;

    public RaidMarkLifetime NewRouteLifetime => _newRouteLifetime;

    /// <summary>"Route · 4 stops · ~3–5 min", or what a click does while there are none.</summary>
    public string PlannedRouteSummary
    {
        get
        {
            if (_plannedRoute.Stops.Count == 0)
            {
                return RaidText.PlannedRouteHint;
            }

            var stops = RaidText.PlannedRouteStops(_plannedRoute.Stops.Count);
            if (_plannedRoute.IsFull)
            {
                stops = RaidText.PlannedRouteFull(stops);
            }

            var minutes = PlannedRouteMinutes();
            return minutes is { } range
                ? $"{stops} · {RaidText.RouteMinutes(range.Low, range.High)}"
                : stops;
        }
    }

    /// <summary>"Squad · This raid": what the next stop will be.</summary>
    public string RouteSettingsLabel =>
        $"{RaidText.MarkScope(NewMarkScope)} · {RaidText.MarkLifetime(_newRouteLifetime)}";

    public void ChooseRouteLifetime(RaidMarkLifetime lifetime)
    {
        _newRouteLifetime = lifetime;
        OnPropertyChanged(nameof(NewRouteLifetime));
        OnPropertyChanged(nameof(RouteSettingsLabel));
    }

    /// <summary>
    /// Adds a stop where the player clicked, as a waypoint on this route; null when the route
    /// already has its twelve or there is no map.
    /// </summary>
    public PlannedRouteStop? AddRouteStop(MapScenePoint point)
    {
        if (_map.RenderModel is not { } model)
        {
            return null;
        }

        if (!string.Equals(_plannedRouteMapId, model.Location.Id, StringComparison.OrdinalIgnoreCase))
        {
            // A route is one map's: a stop on another map starts a new one. The old route's
            // stops stay as marks on their own map.
            _plannedRoute.Clear();
            _routePlacements.Clear();
            _plannedRouteMapId = model.Location.Id;
        }

        if (_plannedRoute.Add(new MapPoint(point.X, point.Y)) is not { } stop)
        {
            return null;
        }

        var floorId = Renderer?.Scene.View.SelectedFloorId ?? model.SelectedFloor?.Id;
        var routeId = _plannedRoute.RouteId;
        var placement = _marks.PlaceAsync(
            model.Location.Id,
            floorId,
            point.X,
            point.Y,
            null,
            NewMarkScope,
            _newRouteLifetime,
            route: new RaidMarkRoute(routeId, stop.Step),
            colour: NewMarkColour);
        _routePlacements[(routeId, stop.Step)] = placement;
        _ = BindWhenPlacedAsync(routeId, stop.Step, placement);
        PlannedRouteChanged();
        return stop;
    }

    private void UndoRouteStop()
    {
        var routeId = _plannedRoute.RouteId;
        if (_plannedRoute.Undo() is { } stop)
        {
            RemovePlacement(routeId, stop);
            PlannedRouteChanged();
        }
    }

    private void ClearRoute()
    {
        var routeId = _plannedRoute.RouteId;
        foreach (var stop in _plannedRoute.Clear())
        {
            RemovePlacement(routeId, stop);
        }

        _routePlacements.Clear();
        PlannedRouteChanged();
    }

    /// <summary>Removes a stop's mark, waiting for the store's answer if the click was only just made.</summary>
    private void RemovePlacement(Guid routeId, PlannedRouteStop stop)
    {
        if (_routePlacements.Remove((routeId, stop.Step), out var placement))
        {
            _ = RemoveWhenPlacedAsync(placement);
        }
        else if (stop.MarkId is { } markId)
        {
            _ = _marks.RemoveAsync(markId);
        }
    }

    private async Task RemoveWhenPlacedAsync(Task<RaidMark> placement)
    {
        try
        {
            var mark = await placement.ConfigureAwait(false);
            await _marks.RemoveAsync(mark.Id).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The mark never arrived, or the store could not write: there is nothing to take off.
        }
    }

    private async Task BindWhenPlacedAsync(Guid routeId, int step, Task<RaidMark> placement)
    {
        RaidMark mark;
        try
        {
            mark = await placement.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return;
        }

        Dispatch(() =>
        {
            if (_plannedRoute.RouteId == routeId && TakePlacement(_routePlacements, (routeId, step), placement))
            {
                _plannedRoute.Bind(step, mark.Id);
            }
        });
    }

    /// <summary>
    /// Removes a stop's pending placement only if it is still this one, and says whether it was.
    /// </summary>
    /// <remarks>
    /// #938: Undo frees a step number and the next click reuses it, under the same route id. A
    /// bind that removed whatever sat under (route, step) took the new click's entry, bound the
    /// undone mark (deleted a moment later, so the stop left the route), and the new mark was never
    /// bound: a waypoint on the map, shared with the squad, that Undo and Clear no longer knew.
    /// </remarks>
    internal static bool TakePlacement<T>(IDictionary<(Guid Route, int Step), T> placements, (Guid Route, int Step) key, T placement)
        where T : class
    {
        if (!placements.TryGetValue(key, out var stored) || !ReferenceEquals(stored, placement))
        {
            return false;
        }

        placements.Remove(key);
        return true;
    }

    /// <summary>A stop removed some other way (the Marks card, "This raid" ending) leaves the route.</summary>
    private void PlannedRouteMarksChanged() => Dispatch(() =>
    {
        var present = _marks.Marks.Select(mark => mark.Id).ToHashSet();
        if (_plannedRoute.Forget(present.Contains))
        {
            PlannedRouteChanged();
        }
    });

    private (int Low, int High)? PlannedRouteMinutes()
    {
        if (_map.RenderModel is not { } model)
        {
            return null;
        }

        var floorId = Renderer?.Scene.View.SelectedFloorId ?? model.SelectedFloor?.Id;
        var height = FloorHeight(model, floorId) ?? _stateStore.Current.Raid.LastKnownPosition?.Position.Y ?? 0;
        return _plannedRoute.Minutes(WorldConverter(model, height));
    }

    private void PlannedRouteChanged()
    {
        OnPropertyChanged(nameof(PlannedRouteStopCount));
        OnPropertyChanged(nameof(HasPlannedRouteStops));
        OnPropertyChanged(nameof(IsPlannedRouteFull));
        OnPropertyChanged(nameof(PlannedRouteSummary));
    }

    // ---- Shared ----

    /// <summary>Plan to world metres (x, z) on this map at one height; null for every point without a transform.</summary>
    private static Func<MapPoint, (double X, double Z)?> WorldConverter(MapRenderModel model, double height) =>
        model.TransformAvailability == MapTransformAvailability.Valid && model.Variant.Transform is { } transform
            ? point => transform.TryUnproject(point, height, out var world) ? (world.X, world.Z) : null
            : _ => null;

    /// <summary>A click on the map in Inspect or Route mode.</summary>
    public void ModeClicked(MapScenePoint point)
    {
        switch (_interactionMode)
        {
            case MapInteractionMode.Inspect:
                InspectAt(point);
                break;
            case MapInteractionMode.Route:
                AddRouteStop(point);
                break;
        }
    }

    private void InteractionModeChanged(MapInteractionMode previous)
    {
        if (previous == MapInteractionMode.Inspect && _inspection is not null)
        {
            SetInspection(null);
        }

        OnPropertyChanged(nameof(IsNavigateMode));
        OnPropertyChanged(nameof(IsInspectMode));
        OnPropertyChanged(nameof(IsRouteMode));
        OnPropertyChanged(nameof(IsClickMode));
        OnPropertyChanged(nameof(LeavesModeOnEscape));
        OnPropertyChanged(nameof(ShowsInspectHint));
        OnPropertyChanged(nameof(PlannedRouteSummary));
    }

    /// <summary>The Marks card's scope switch also names what the next route stop will be.</summary>
    private void RouteScopeChanged() => OnPropertyChanged(nameof(RouteSettingsLabel));
}
