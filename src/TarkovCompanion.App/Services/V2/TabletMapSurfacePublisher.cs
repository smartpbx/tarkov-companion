using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.Services.V2;

/// <summary>
/// Carries the desktop's V2 raid map to its paired tablets, and lets a tablet in Control mode move
/// it back.
/// </summary>
/// <remarks>
/// [V2 rough package 24, #407] A tablet without the real map is not usable, and the desktop is the
/// only side that has one: it holds the reviewed artwork, the plan rectangle
/// (<c>MapPlanProjection</c>) and the assembled scene. So this watches the cockpit's own scene
/// rebuilds and publishes exactly what it is drawing — the same rectangle, the same coordinates —
/// rather than a second model of the map that could drift from it.
///
/// It also keeps canonical workspace state in step with the cockpit, which is what makes Follow
/// mean anything: a tablet mirrors the desktop's map, floor, viewport and selection only if those
/// reach canonical state, and before this nothing ever issued the desktop's own
/// <see cref="UpdateDesktopWorkspaceCommand"/>. The reverse direction — a tablet holding a control
/// lease moving the desktop — arrives as <see cref="RelayMarksBridge.DesktopWorkspaceRequested"/>
/// and is applied to the renderer here.
///
/// It controls this application's own window only. Nothing here reaches the game.
/// </remarks>
public sealed class TabletMapSurfacePublisher : IDisposable
{
    /// <summary>A raid ticks several times a second; a second screen does not need every tick.</summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    private readonly RaidCockpitViewModel _cockpit;
    private readonly ITabletMapSurfaceSink _sink;
    private readonly DesktopCompanionAuthority _authority;
    private readonly RelayMarksBridge? _bridge;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private DateTimeOffset _lastPublishedUtc = DateTimeOffset.MinValue;
    private long _publishedRevision = -1;
    private string? _artworkSha;
    private TabletMapArtworkBytes? _artwork;
    private object? _artworkSource;
    private bool _applyingRemoteView;
    private bool _disposed;

    public TabletMapSurfacePublisher(
        RaidCockpitViewModel cockpit,
        ITabletMapSurfaceSink sink,
        DesktopCompanionAuthority authority,
        RelayMarksBridge? bridge = null,
        TimeProvider? timeProvider = null)
    {
        _cockpit = cockpit ?? throw new ArgumentNullException(nameof(cockpit));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _bridge = bridge;
        _clock = timeProvider ?? TimeProvider.System;
        _cockpit.SceneRebuilt += OnSceneRebuilt;
        if (_bridge is not null)
        {
            _bridge.DesktopWorkspaceRequested += OnDesktopWorkspaceRequested;
        }
    }

    /// <summary>Publishes now, regardless of the rebuild throttle. The test seam, and the first publish.</summary>
    public async Task PublishNowAsync(CancellationToken cancellationToken = default)
    {
        if (_cockpit.Renderer is not { } renderer)
        {
            return;
        }

        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scene = renderer.Scene;
            var artwork = ArtworkFor(renderer);
            var name = string.IsNullOrWhiteSpace(_cockpit.SelectedMap?.Name)
                ? scene.LocationId
                : _cockpit.SelectedMap!.Name;
            var surface = TabletMapSurfaceBuilder.Build(
                scene,
                name,
                artwork?.Descriptor,
                _authority.Snapshot.CanonicalState.Workspace.Projection,
                artwork is null ? null : _cockpit.BackgroundStatus(),
                Utc());
            await PushDesktopWorkspaceAsync(scene, cancellationToken).ConfigureAwait(false);
            await _sink.PublishMapSurfaceAsync(surface, artwork?.Bytes, cancellationToken).ConfigureAwait(false);
            _publishedRevision = scene.Revision;
            _lastPublishedUtc = _clock.GetUtcNow();
        }
        finally
        {
            _publishGate.Release();
        }
    }

    private void OnSceneRebuilt(object? sender, EventArgs e)
    {
        if (_disposed || _cockpit.Renderer is not { } renderer)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        if (renderer.Scene.Revision == _publishedRevision || now - _lastPublishedUtc < MinimumInterval)
        {
            return;
        }

        _lastPublishedUtc = now;
        _ = Task.Run(async () =>
        {
            try
            {
                await PublishNowAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A second screen that misses one frame of the map is a stale tablet, never a
                // desktop that stops drawing its own.
            }
        });
    }

    /// <summary>
    /// The desktop's own navigation, as an ordinary revisioned canonical change, so a following
    /// tablet sees it. Skipped when nothing a tablet would follow has actually moved, because a
    /// raid ticks constantly and every command here is a persisted canonical revision.
    /// </summary>
    private async Task PushDesktopWorkspaceAsync(MapSceneSnapshot scene, CancellationToken cancellationToken)
    {
        if (_bridge is null || _applyingRemoteView)
        {
            return;
        }

        var canonical = _authority.Snapshot.CanonicalState;
        var current = canonical.Workspace.Projection;
        var mapId = scene.LocationId;
        var floorId = scene.View.SelectedFloorId;
        WorkspaceViewport viewport;
        try
        {
            viewport = new WorkspaceViewport(
                new MapCoordinate(
                    mapId,
                    floorId,
                    CoordinateSpaceKind.World,
                    scene.TransformVersion,
                    Quantize(scene.View.Camera.CenterX),
                    null,
                    Quantize(scene.View.Camera.CenterY)),
                Math.Clamp(Quantize(scene.View.Camera.Zoom), 0.01, 100));
        }
        catch (ArgumentException)
        {
            // A camera the canonical envelope cannot express (a plan far outside the world
            // coordinate bound) is not worth failing a map rebuild over.
            return;
        }

        if (string.Equals(current.MapId, mapId, StringComparison.Ordinal) &&
            string.Equals(current.FloorId, floorId, StringComparison.Ordinal) &&
            current.Viewport is { } held &&
            held.Zoom == viewport.Zoom &&
            held.Center.X == viewport.Center.X &&
            held.Center.Z == viewport.Center.Z &&
            current.ActiveLayers.SequenceEqual(ActiveLayers(scene)))
        {
            return;
        }

        var projection = new WorkspaceProjection(
            WorkspaceKind.Raid,
            mapId,
            floorId,
            viewport,
            current.Selection,
            current.VisibleObjectiveIds,
            current.PlanIds,
            current.SearchQuery,
            current.ResultIds,
            ActiveLayers(scene),
            current.ActiveFilters,
            current.Dialog);
        var now = Utc();
        await _bridge.ApplyDesktopCommandAsync(
            new UpdateDesktopWorkspaceCommand(
                new CommandId(Guid.NewGuid()),
                new AggregateRevision(canonical.Workspace.Cursor.Revision.Value + 1),
                now,
                now.AddSeconds(30),
                projection),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A paired device holding a control lease has moved the map; the desktop follows it.</summary>
    private void OnDesktopWorkspaceRequested(WorkspaceProjection projection)
    {
        if (_disposed || _cockpit.Renderer is not { } renderer || projection.Viewport is not { } viewport)
        {
            return;
        }

        // Guarded so the change this makes to the cockpit does not come straight back out as a
        // desktop workspace command, which would be the desktop echoing the tablet's own move.
        _applyingRemoteView = true;
        try
        {
            if (!string.Equals(renderer.Scene.View.SelectedFloorId, projection.FloorId, StringComparison.Ordinal))
            {
                renderer.SelectFloor(projection.FloorId);
            }

            renderer.FocusOn(new MapScenePoint(viewport.Center.X, viewport.Center.Z), viewport.Zoom);
            if (projection.Selection is { Kind: WorkspaceSelectionKind.Landmark or WorkspaceSelectionKind.Objective } selection)
            {
                renderer.SelectObject(new MapSceneObjectId(selection.ReferenceId));
            }
            else if (projection.Selection is null)
            {
                renderer.ClearSelection();
            }

            foreach (var layer in renderer.Scene.Layers)
            {
                renderer.SetLayerVisibility(layer.Id, projection.ActiveLayers.Contains(layer.Id.Value));
            }
        }
        finally
        {
            _applyingRemoteView = false;
        }
    }

    private static string[] ActiveLayers(MapSceneSnapshot scene) => scene.View.Layers.Count == 0
        ? scene.Layers.Where(layer => layer.IsVisibleByDefault).Select(layer => layer.Id.Value).ToArray()
        : scene.View.Layers.Where(state => state.IsVisible).Select(state => state.LayerId.Value).ToArray();

    /// <summary>
    /// Encodes the picture the renderer is drawing, once per picture. A scene rebuild happens on
    /// every raid tick and PNG-encoding a multi-megapixel plan each time would be the most
    /// expensive thing this class does; the bitmap instance changing is what says it is a new one.
    /// </summary>
    private (TabletMapArtwork Descriptor, TabletMapArtworkBytes Bytes)? ArtworkFor(MapSceneRendererViewModel renderer)
    {
        if (renderer.BackgroundImage is not Bitmap bitmap)
        {
            return null;
        }

        if (!ReferenceEquals(_artworkSource, bitmap) || _artwork is null || _artworkSha is null)
        {
            using var buffer = new MemoryStream();
            bitmap.Save(buffer, new PngBitmapEncoderOptions());
            var bytes = buffer.ToArray();
            _artworkSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            _artwork = new TabletMapArtworkBytes("image/png", _artworkSha, bytes);
            _artworkSource = bitmap;
        }

        return (
            new TabletMapArtwork(
                "image/png",
                _artworkSha,
                (int)Math.Round(bitmap.Size.Width),
                (int)Math.Round(bitmap.Size.Height)),
            _artwork);
    }

    private static double Quantize(double value) =>
        Math.Round(value, 3, MidpointRounding.AwayFromZero);

    // Every protocol timestamp requires exact millisecond precision, which the system clock's
    // sub-millisecond ticks do not satisfy.
    private DateTimeOffset Utc()
    {
        var utc = _clock.GetUtcNow().ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cockpit.SceneRebuilt -= OnSceneRebuilt;
        if (_bridge is not null)
        {
            _bridge.DesktopWorkspaceRequested -= OnDesktopWorkspaceRequested;
        }

        _publishGate.Dispose();
    }
}

internal static class RaidCockpitBackgroundStatus
{
    /// <summary>
    /// What the cockpit itself says about the artwork (missing tiles, the floor it is showing), so
    /// the tablet repeats the desktop's own words rather than inventing its own.
    /// </summary>
    public static string? BackgroundStatus(this RaidCockpitViewModel cockpit) =>
        string.IsNullOrWhiteSpace(cockpit.Renderer?.BackgroundStatus) ? null : cockpit.Renderer!.BackgroundStatus;
}
