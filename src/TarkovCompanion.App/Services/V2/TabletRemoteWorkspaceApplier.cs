using System;
using System.Threading.Tasks;
using TarkovCompanion.CompanionProtocol;

namespace TarkovCompanion.App.Services.V2;

/// <summary>The desktop map a tablet in Control drives: which map it shows, and how to move it.</summary>
public interface ITabletRemoteMap
{
    /// <summary>The map the desktop is drawing now, or null before it draws one.</summary>
    string? CurrentMapId { get; }

    /// <summary>Switches the desktop to another map; returns once the switch has been made.</summary>
    Task SelectMapAsync(string mapId);

    /// <summary>Applies the floor, selection and layers, and the camera when one is given.</summary>
    void ApplyView(WorkspaceProjection projection, WorkspaceViewport? camera);
}

/// <summary>
/// Applies what a tablet in Control asks of the desktop's map, one request at a time, newest first.
/// </summary>
/// <remarks>
/// [#800] "When I change maps on the tablet the desktop changes too, but not to the correct map,
/// and the zoom does not sync up." Three things on this side, each measured in
/// TabletControlMapSyncBrowserTests:
/// <list type="bullet">
/// <item>Requests were applied concurrently. A switch awaits the scene rebuild, and every move
/// that arrived meanwhile started its own apply against the map being left, so which map won
/// depended on timing. Now one runs at a time and only the newest waiting request follows it.</item>
/// <item>After a switch the request's camera was applied: the tablet cannot know the new map's
/// plan, so it sends (0, 0) at zoom 1, and the desk opened the new map on a corner of the world.
/// A switch now leaves the map as the desk opens it, and the tablet takes that view from the
/// surface the desk publishes.</item>
/// <item>A move that arrives during a switch and names the map being left is dropped. The tablet
/// learns of the new map only when the desk publishes it, so until then its moves carry the old
/// map's id, and applied after the switch each one sent the desk straight back.</item>
/// <item>A camera is only ever applied to the map it was measured on: a camera for a map the desk
/// could not switch to is another plan's coordinates.</item>
/// </list>
/// UI thread only: every call and every continuation runs there, so nothing here is locked.
/// </remarks>
public sealed class TabletRemoteWorkspaceApplier(ITabletRemoteMap map)
{
    private readonly ITabletRemoteMap _map = map ?? throw new ArgumentNullException(nameof(map));
    private WorkspaceProjection? _next;
    private bool _running;
    private string? _leaving;

    /// <summary>True while a request is being applied, so the change is not echoed back out.</summary>
    public bool IsApplying { get; private set; }

    /// <summary>
    /// Applies a Raid workspace a tablet asked for, after any apply already under way. A request
    /// still waiting when a newer one arrives is replaced by it: the newest says where to be.
    /// </summary>
    public async Task SubmitAsync(WorkspaceProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (_leaving is not null && TargetMapOf(projection) is { } target && SameMap(target, _leaving))
        {
            return;
        }

        _next = projection;
        if (_running)
        {
            return;
        }

        _running = true;
        IsApplying = true;
        try
        {
            while (_next is { } item)
            {
                _next = null;
                await ApplyOneAsync(item).ConfigureAwait(true);
            }
        }
        finally
        {
            _running = false;
            IsApplying = false;
        }
    }

    /// <summary>The map a request names: its own map id, else the map its camera was measured on.</summary>
    public static string? TargetMapOf(WorkspaceProjection projection) =>
        string.IsNullOrWhiteSpace(projection.MapId) ? projection.Viewport?.Center.MapId : projection.MapId;

    private async Task ApplyOneAsync(WorkspaceProjection projection)
    {
        if (_map.CurrentMapId is not { } current)
        {
            return;
        }

        if (TargetMapOf(projection) is not { } target)
        {
            return;
        }

        if (!SameMap(current, target))
        {
            _leaving = current;
            try
            {
                await _map.SelectMapAsync(target).ConfigureAwait(true);
            }
            finally
            {
                _leaving = null;
            }

            return;
        }

        var camera = projection.Viewport is { } viewport && SameMap(viewport.Center.MapId, current)
            ? viewport
            : null;
        _map.ApplyView(projection, camera);
    }

    // Ids are the catalog's normalized names; #724 was the same map under two casings.
    private static bool SameMap(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
