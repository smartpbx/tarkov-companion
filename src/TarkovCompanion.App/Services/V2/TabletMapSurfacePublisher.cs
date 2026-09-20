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
using TarkovCompanion.Core.Abstractions;
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
    /// <summary>
    /// How long one publish waits for the rest of a burst to arrive.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 34] This was a fixed one-second throttle, which meant the second screen
    /// was up to a second behind the desk before it had even asked for the change, on top of its
    /// own poll. A floor rather than a period: a change is published as soon as this much time has
    /// passed, and a tick that changes nothing never starts the clock at all. Small enough to be
    /// invisible beside a screenshot's own cost, large enough that the several rebuilds one
    /// screenshot causes become one publish.
    /// </remarks>
    private static readonly TimeSpan CoalesceFloor = TimeSpan.FromMilliseconds(40);

    private readonly RaidCockpitViewModel _cockpit;
    private readonly ITabletMapSurfaceSink _sink;
    private readonly DesktopCompanionAuthority _authority;
    private readonly RelayMarksBridge? _bridge;
    private readonly IItemSearchService? _items;
    private readonly IItemRepository? _prices;
    private readonly TimeProvider _clock;
    private TabletSearch? _search;
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private long _seenSceneRevision = -1;
    private int _scheduled;
    private byte[]? _publishedContent;
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
        IItemSearchService? items = null,
        IItemRepository? prices = null,
        TimeProvider? timeProvider = null)
    {
        _cockpit = cockpit ?? throw new ArgumentNullException(nameof(cockpit));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _bridge = bridge;
        _items = items;
        _prices = prices;
        _clock = timeProvider ?? TimeProvider.System;
        _cockpit.SceneRebuilt += OnSceneRebuilt;
        if (_bridge is not null)
        {
            _bridge.DesktopWorkspaceRequested += OnDesktopWorkspaceRequested;
        }
    }

    /// <summary>
    /// When this desktop last put a scene on the relay, and which scene it was.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 41] Read by Setup's self-test, which has to answer "is the desktop
    /// publishing anything" without publishing to find out. Null until the first publish, which
    /// is itself the answer when a tablet is paired and its map is empty.
    /// </remarks>
    public DateTimeOffset? LastPublishedUtc { get; private set; }

    public TabletMapSurface? LastSurface { get; private set; }

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
                await SearchResultsAsync(cancellationToken).ConfigureAwait(false),
                artwork is null ? null : _cockpit.BackgroundStatus(),
                Utc());
            await PushDesktopWorkspaceAsync(scene, cancellationToken).ConfigureAwait(false);

            // The scene is rebuilt on every runtime tick and most ticks change nothing a tablet
            // would draw, so what is published is compared rather than the revision that carries
            // it. Serialized with the timestamp blanked, because the timestamp is the one field
            // that differs on every publish and comparing it would make every tick a change.
            var content = TabletMapSurfaceJson.Serialize(surface with { PublishedUtc = default });
            if (_publishedContent is { } previous && previous.AsSpan().SequenceEqual(content))
            {
                return;
            }

            var carried = await _sink.PublishMapSurfaceAsync(
                TabletMapSurfaceJson.Serialize(surface),
                artwork?.Bytes,
                cancellationToken).ConfigureAwait(false);
            if (!carried)
            {
                // [#407] Nothing took it: no relay, or the relay is not claimed yet. Recording it
                // as published made every later identical scene a "no change", so the map that was
                // open when the relay was finally claimed never reached a tablet.
                return;
            }

            _publishedContent = content;
            // Package 41's self-test asks when this desktop last put a scene on the relay, and
            // which one, so these follow the publish that actually happened. A tick that
            // deduplicated above published nothing, and leaving them where they were is the
            // honest answer to that question.
            LastPublishedUtc = _clock.GetUtcNow();
            LastSurface = surface;
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

        // A rebuild that produced the same scene revision is the same scene: nothing is
        // scheduled, so a quiet desktop wakes nothing at all.
        if (Interlocked.Exchange(ref _seenSceneRevision, renderer.Scene.Revision) == renderer.Scene.Revision)
        {
            return;
        }

        // One publish per burst. A screenshot lands as several rebuilds in quick succession (the
        // position, then the marks, then the loot layer), and the tablet wants the last of them.
        if (Interlocked.Exchange(ref _scheduled, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(CoalesceFloor, _clock).ConfigureAwait(false);
                Interlocked.Exchange(ref _scheduled, 0);
                await PublishNowAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Interlocked.Exchange(ref _scheduled, 0);
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

    /// <summary>
    /// The answers to the lookup the desktop is currently showing, for the tablet that asked for
    /// it.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 34] Run here rather than on the relay because this is the side that has
    /// the catalogue and the prices; #417 could only set the desktop's search box and leave the
    /// person holding the tablet looking at the wrong screen. Resolved once per query rather than
    /// per publish: the query changes when somebody types, and the scene republishes constantly.
    /// </remarks>
    private async Task<TabletSearch?> SearchResultsAsync(CancellationToken cancellationToken)
    {
        var query = _authority.Snapshot.CanonicalState.Workspace.Projection.SearchQuery;
        if (string.IsNullOrWhiteSpace(query) || _items is null)
        {
            return string.IsNullOrWhiteSpace(query) ? null : _search;
        }

        if (_search is { } cached && string.Equals(cached.Query, query, StringComparison.Ordinal))
        {
            return cached;
        }

        try
        {
            var hits = await _items.SearchAsync(query, MaximumSearchResults, cancellationToken).ConfigureAwait(false);
            var results = new List<TabletSearchResult>(hits.Count);
            foreach (var hit in hits)
            {
                var price = _prices is null
                    ? null
                    : await _prices.GetPriceAsync(hit.Item.Id, cancellationToken).ConfigureAwait(false);
                results.Add(new(
                    hit.Item.Id,
                    hit.Item.Name,
                    hit.Item.ShortName,
                    price?.FleaPriceRoubles,
                    price?.BestTrader?.ValueRoubles));
            }

            _search = new(query, results);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // A catalogue that is not loaded yet is a tablet told nothing, never a map that stops
            // publishing.
            _search = new(query, []);
        }

        return _search;
    }

    /// <summary>What fits on a tablet beside the map, and the same twelve the relay's own search returned.</summary>
    private const int MaximumSearchResults = 12;

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
