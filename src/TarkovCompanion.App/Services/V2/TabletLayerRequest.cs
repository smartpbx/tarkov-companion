using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.Services.V2;

/// <summary>Which of the desktop's map layers a tablet in Control actually asked to switch.</summary>
/// <remarks>
/// [#933] Every request from a tablet in Control carries a whole list of active layers, and a move
/// of the map (the commonest request by far) carries the list the shared state last held. The
/// desktop does not publish its own changes while a tablet holds Control (#604), so that list was
/// the one from the moment Control was taken. Applying it whole on each move switched back off
/// every layer the player had turned on at the desktop since, and saved each one as off: a layer
/// that was not there yet (You, the squad, at a raid's start) came out off as well. Only a layer
/// whose state in the request differs from the list the request was built on is the tablet's own
/// choice; the rest is the desktop's, and is left alone.
/// </remarks>
internal static class TabletLayerRequest
{
    /// <summary>
    /// The switches to make. With no known previous list, nothing: a stale list must never win,
    /// and a tablet's switch that is missed once is pressed again, while a layer lost is lost for good.
    /// </summary>
    public static IReadOnlyList<(MapSceneLayerId LayerId, bool IsVisible)> Changes(
        MapSceneSnapshot scene,
        IReadOnlyCollection<string>? previous,
        IReadOnlyCollection<string> requested)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(requested);
        if (previous is null)
        {
            return [];
        }

        var changes = new List<(MapSceneLayerId, bool)>();
        foreach (var layer in scene.Layers)
        {
            var wanted = requested.Contains(layer.Id.Value);
            if (wanted == previous.Contains(layer.Id.Value))
            {
                continue;
            }

            var visible = scene.View.Layers.FirstOrDefault(state => state.LayerId == layer.Id)?.IsVisible ?? layer.IsVisibleByDefault;
            if (visible != wanted)
            {
                changes.Add((layer.Id, wanted));
            }
        }

        return changes;
    }
}
