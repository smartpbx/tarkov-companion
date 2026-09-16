namespace TarkovCompanion.Core.Domain.Maps.Scene;

public enum MapSceneViewChangeKind
{
    SetMode,
    SelectFloor,
    SetLayerVisibility,
    SetCamera,
}

public sealed record MapSceneViewChange(
    Guid ChangeId,
    long ExpectedRevision,
    MapSceneViewChangeKind Kind,
    MapSceneMode? Mode = null,
    string? FloorId = null,
    MapSceneLayerId? LayerId = null,
    bool? IsVisible = null,
    MapSceneCamera? Camera = null);

public enum MapSceneViewChangeStatus
{
    Applied,
    Unchanged,
    StaleRevision,
    Rejected,
}

public sealed record MapSceneViewChangeResult(
    MapSceneViewChangeStatus Status,
    MapSceneSnapshot Scene,
    string? ErrorCode = null);

/// <summary>Applies revision-checked view changes from either desktop or a paired client.</summary>
public static class MapSceneViewReducer
{
    public static MapSceneViewChangeResult Apply(MapSceneSnapshot scene, MapSceneViewChange change)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(change);
        if (change.ChangeId == Guid.Empty)
        {
            return new(MapSceneViewChangeStatus.Rejected, scene, "change_id_required");
        }

        if (change.ExpectedRevision != scene.Revision)
        {
            return new(MapSceneViewChangeStatus.StaleRevision, scene, "scene_revision_changed");
        }

        var nextView = change.Kind switch
        {
            MapSceneViewChangeKind.SetMode => SetMode(scene, change),
            MapSceneViewChangeKind.SelectFloor => SelectFloor(scene, change),
            MapSceneViewChangeKind.SetLayerVisibility => SetLayerVisibility(scene, change),
            MapSceneViewChangeKind.SetCamera => SetCamera(scene, change),
            _ => null,
        };
        if (nextView is null)
        {
            return new(MapSceneViewChangeStatus.Rejected, scene, ErrorCode(scene, change));
        }

        if (nextView == scene.View)
        {
            return new(MapSceneViewChangeStatus.Unchanged, scene);
        }

        if (scene.Revision == long.MaxValue)
        {
            return new(MapSceneViewChangeStatus.Rejected, scene, "scene_revision_exhausted");
        }

        var updated = new MapSceneSnapshot(
            scene.Revision + 1,
            scene.LocationId,
            scene.VariantKey,
            scene.TransformVersion,
            scene.Bounds,
            scene.FloorIds,
            scene.Capabilities,
            nextView,
            scene.Layers,
            scene.Objects,
            scene.Assets);
        return new(MapSceneViewChangeStatus.Applied, updated);
    }

    private static MapSceneViewState? SetMode(MapSceneSnapshot scene, MapSceneViewChange change)
    {
        if (change.Mode is not { } mode || !Enum.IsDefined(mode) || !scene.Capabilities.Supports(mode))
        {
            return null;
        }

        return scene.View with { Mode = mode };
    }

    private static MapSceneViewState? SelectFloor(MapSceneSnapshot scene, MapSceneViewChange change)
    {
        if (change.FloorId is not null && !scene.FloorIds.Contains(change.FloorId, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        return scene.View with { SelectedFloorId = change.FloorId };
    }

    private static MapSceneViewState? SetLayerVisibility(MapSceneSnapshot scene, MapSceneViewChange change)
    {
        if (change.LayerId is not { } layerId || change.IsVisible is not { } isVisible ||
            !scene.Layers.Any(layer => layer.Id == layerId))
        {
            return null;
        }

        var current = scene.View.Layers.FirstOrDefault(state => state.LayerId == layerId)?.IsVisible ??
            scene.Layers.Single(layer => layer.Id == layerId).IsVisibleByDefault;
        if (current == isVisible)
        {
            return scene.View;
        }

        var states = scene.View.Layers
            .Where(state => state.LayerId != layerId)
            .Append(new(layerId, isVisible))
            .OrderBy(state => scene.Layers.FindIndex(layer => layer.Id == state.LayerId))
            .ToArray();
        return scene.View with { Layers = states };
    }

    private static MapSceneViewState? SetCamera(MapSceneSnapshot scene, MapSceneViewChange change) =>
        change.Camera is { } camera ? scene.View with { Camera = camera } : null;

    private static string ErrorCode(MapSceneSnapshot scene, MapSceneViewChange change) => change.Kind switch
    {
        MapSceneViewChangeKind.SetMode when !change.Mode.HasValue || !Enum.IsDefined(change.Mode.Value) => "map_mode_invalid",
        MapSceneViewChangeKind.SetMode => "map_mode_unavailable",
        MapSceneViewChangeKind.SelectFloor => "map_floor_unavailable",
        MapSceneViewChangeKind.SetLayerVisibility => "map_layer_unavailable",
        MapSceneViewChangeKind.SetCamera => "map_camera_required",
        _ => "map_change_invalid",
    };

    private static int FindIndex<T>(this IReadOnlyList<T> values, Func<T, bool> predicate)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (predicate(values[index]))
            {
                return index;
            }
        }

        return int.MaxValue;
    }
}
