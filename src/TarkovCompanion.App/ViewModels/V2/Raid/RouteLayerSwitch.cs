using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>
/// [#902] One saved switch per route: its Layers-menu row. Every other control for a route (the
/// card's "Show route", the route card's "Show on map", Plan's "Route" chip) reads and flips this.
/// </summary>
/// <remarks>
/// The objective route had two controls that disagreed: a "Hide route" button held in memory,
/// which every "Open in Raid" turned back off, and the saved Layers switch, which, once off, hid
/// every later route with nothing in sight to say so. Plan's preview obeyed neither. Now the
/// layer is the state, the layer is always declared so its row stays, and the rest are shortcuts.
/// </remarks>
internal static class RouteLayerSwitch
{
    /// <summary>The gold path sent from Plan, the owner's "proposed path".</summary>
    public static readonly MapSceneLayerId Objective = ObjectiveRouteSceneBuilder.LayerId;

    /// <summary>The cyan lower-contact path to an extract. The id predates the name.</summary>
    public static readonly MapSceneLayerId Suggested = new("traffic-routes");

    /// <summary>The grey straight line to the same extract, which used to ride on the suggested route's switch.</summary>
    public static readonly MapSceneLayerId Direct = new("traffic-route-direct");

    public static bool IsRoute(MapSceneLayerId id) => id == Objective || id == Suggested || id == Direct;

    /// <summary>What the map shows now: the scene's state, else the saved choice, else on.</summary>
    public static bool IsShown(MapSceneRendererViewModel? renderer, MapLayerVisibilitySetting setting, MapSceneLayerId id)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return renderer?.Scene.View.Layers.FirstOrDefault(state => state.LayerId == id) is { } state
            ? state.IsVisible
            : setting.Get(id) ?? true;
    }

    /// <summary>
    /// Flips the layer the way its Layers row does, so the change is drawn and saved by the one path
    /// that saves every layer. With no scene to flip yet, the choice is saved straight away.
    /// </summary>
    /// <returns>True when the choice was saved directly, so the caller must redraw itself.</returns>
    public static bool Set(MapSceneRendererViewModel? renderer, MapLayerVisibilitySetting setting, MapSceneLayerId id, bool shown)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (IsShown(renderer, setting, id) == shown)
        {
            return false;
        }

        if (renderer is not null && renderer.Scene.Layers.Any(layer => layer.Id == id))
        {
            renderer.SetLayerVisibility(id, shown);
            return false;
        }

        setting.Set(id, shown);
        return true;
    }
}
