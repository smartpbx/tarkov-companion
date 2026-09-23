using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

internal readonly record struct ObjectiveRouteOrigin(MapScenePoint At, string Label, double UnitsPerMetre);

public sealed partial class RaidCockpitViewModel
{
    private ObjectiveRouteScene? _objectiveRouteScene;
    private string? _objectiveRouteLocationId;

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
    internal void SetObjectiveRoute(string mapId, ObjectiveRouteBundle? route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        if (route is null || _map.RenderModel is not { } model || ObjectiveRouteOrigin() is not { } origin)
        {
            _objectiveRouteScene = null;
            _objectiveRouteLocationId = null;
            _rebuildRequest.Request();
            return;
        }

        _objectiveRouteLocationId = model.Location.Id;
        _objectiveRouteScene = ObjectiveRouteSceneBuilder.Build(route, origin.At, _timeProvider.GetUtcNow());
        _rebuildRequest.Request();
    }

    private ObjectiveRouteScene? ObjectiveRouteFor(MapRenderModel model) =>
        string.Equals(model.Location.Id, _objectiveRouteLocationId, StringComparison.OrdinalIgnoreCase)
            ? _objectiveRouteScene
            : null;
}
