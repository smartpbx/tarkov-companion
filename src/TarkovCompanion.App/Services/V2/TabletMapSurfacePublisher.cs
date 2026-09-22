using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Events;
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
    private readonly IIntelEventStateCatalog? _eventStates;
    private readonly TimeProvider _clock;
    private TabletSearch? _search;
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private long _seenSceneRevision = -1;
    private int _scheduled;
    private byte[]? _publishedContent;
    private string? _artworkSha;
    private TabletMapArtworkBytes? _artwork;
    private object? _artworkSource;
    private int _artworkWidth;
    private int _artworkHeight;
    private bool _applyingRemoteView;
    private bool _disposed;
    private WorkspaceProjection? _remoteTarget;
    private int _remotePosted;
    private DesktopViewportEase? _ease;
    private DateTimeOffset? _sentToTabletUtc;
    private TabletLootResult? _loot;
    private TabletWorkspaceNavigation? _workspaceNavigation;

    public TabletMapSurfacePublisher(
        RaidCockpitViewModel cockpit,
        ITabletMapSurfaceSink sink,
        DesktopCompanionAuthority authority,
        RelayMarksBridge? bridge = null,
        IItemSearchService? items = null,
        IItemRepository? prices = null,
        TimeProvider? timeProvider = null,
        IIntelEventStateCatalog? eventStates = null)
    {
        _cockpit = cockpit ?? throw new ArgumentNullException(nameof(cockpit));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _bridge = bridge;
        _items = items;
        _prices = prices;
        _eventStates = eventStates;
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

    /// <summary>Connects authorised tablet workspace requests to the already-built V2 shell.</summary>
    public void AttachDesktopWorkspaceNavigation(Func<V2RouteId> currentRoute, Action<V2RouteId> navigate) =>
        _workspaceNavigation = new TabletWorkspaceNavigation(currentRoute, navigate);

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
            if (!TryArtworkFor(renderer, out var artwork))
            {
                // The picture was replaced between the renderer naming it and this reading it.
                // The rebuild that replaced it has a newer scene and publishes that; a surface
                // sent now would tell the tablet the map has no picture.
                return;
            }

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
                Utc(),
                _sentToTabletUtc,
                Volatile.Read(ref _loot));
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

    /// <summary>
    /// "Send to tablet": publishes the desktop's current map view with a new stamp, and a paired
    /// tablet in Independent moves its own view there (one in Follow already shows it).
    /// </summary>
    /// <returns>Whether the relay took the surface carrying this send.</returns>
    public async Task<bool> SendToTabletAsync(CancellationToken cancellationToken = default)
    {
        var stamp = Utc();
        _sentToTabletUtc = stamp;
        await PublishNowAsync(cancellationToken).ConfigureAwait(false);
        return LastSurface?.SentToTabletUtc == stamp;
    }

    /// <summary>#572: puts a Loot Scan result on the paired tablets with the next publish, now.</summary>
    public void ShowLootResult(TabletLootResult loot)
    {
        ArgumentNullException.ThrowIfNull(loot);
        Volatile.Write(ref _loot, loot);
        _ = Task.Run(async () =>
        {
            try
            {
                await PublishNowAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A tablet that misses a loot result still has the desktop's Loot page.
            }
        });
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
        if (_bridge is null || _applyingRemoteView || _ease?.IsMoving == true)
        {
            return;
        }

        var canonical = _authority.Snapshot.CanonicalState;
        // [#604] A tablet holding Control is driving this map. The desk pushing its own camera
        // back as a new revision would race every move the tablet streams (each names the
        // revision after the last) and throw the tablet's drag back to wherever the desk was.
        if (canonical.DeviceModes.ControlLease is not null)
        {
            return;
        }

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
            // Once per query, like the prices below: the same reasoning IntelEventStateCatalog's
            // own remarks give for reading it once rather than once a row.
            var eventStates = _eventStates is null
                ? null
                : await _eventStates.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            var results = new List<TabletSearchResult>(hits.Count);
            foreach (var hit in hits)
            {
                var price = _prices is null
                    ? null
                    : await _prices.GetPriceAsync(hit.Item.Id, cancellationToken).ConfigureAwait(false);
                var isAllergic = eventStates is not null &&
                    eventStates.TryGetValue(hit.Item.Id, out var state) &&
                    state == EventItemState.Allergic;
                results.Add(TabletSearchResultBuilder.From(hit, price, isAllergic));
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
    /// <remarks>
    /// [#604] Raised on the relay reader's thread. Only the newest move is kept, and at most one
    /// apply is queued on the UI thread at a time, so a burst of moves costs one apply there.
    /// </remarks>
    private void OnDesktopWorkspaceRequested(WorkspaceProjection projection)
    {
        if (_disposed)
        {
            return;
        }

        Volatile.Write(ref _remoteTarget, projection);
        if (Interlocked.Exchange(ref _remotePosted, 1) == 0)
        {
            Dispatcher.UIThread.Post(ApplyLatestRemoteWorkspace, DispatcherPriority.Input);
        }
    }

    private void ApplyLatestRemoteWorkspace()
    {
        Interlocked.Exchange(ref _remotePosted, 0);
        if (Interlocked.Exchange(ref _remoteTarget, null) is { } projection)
        {
            // The wire enum is validated during deserialization, and this second allowlist keeps
            // the desktop fail-closed if a future protocol value reaches this older app build.
            if (!TabletWorkspaceNavigation.TryRoute(projection.Workspace, out _))
            {
                return;
            }

            _workspaceNavigation?.TryNavigate(projection.Workspace);
            if (projection.Workspace != WorkspaceKind.Raid)
            {
                return;
            }

            ApplyRemoteWorkspace(projection);
        }
    }

    private async void ApplyRemoteWorkspace(WorkspaceProjection projection)
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
            // [#407] A tablet in Control always sent workspace: "Raid" and its own current map,
            // and this read only the floor/viewport/selection fields below — a request to switch
            // maps went nowhere. Switching rebuilds the renderer (RaidCockpitViewModel.
            // SelectMapAsync -> V1's own FollowRaidAsync), so everything after this applies to
            // whichever renderer instance comes out the other side, not the one this started with.
            if (!string.Equals(renderer.Scene.LocationId, viewport.Center.MapId, StringComparison.Ordinal))
            {
                await _cockpit.SelectMapAsync(viewport.Center.MapId).ConfigureAwait(true);
                if (_disposed || _cockpit.Renderer is not { } switched)
                {
                    return;
                }

                renderer = switched;
            }

            if (!string.Equals(renderer.Scene.View.SelectedFloorId, projection.FloorId, StringComparison.Ordinal))
            {
                renderer.SelectFloor(projection.FloorId);
            }

            // [#604] Eased, and exactly: FocusOn only ever zoomed in.
            _ease ??= new DesktopViewportEase(_clock);
            _ease.MoveTo(renderer, new EasedCamera(viewport.Center.X, viewport.Center.Z, viewport.Zoom));
            if (projection.Selection is { Kind: WorkspaceSelectionKind.Landmark or WorkspaceSelectionKind.Objective } selection)
            {
                renderer.SelectObject(new MapSceneObjectId(selection.ReferenceId));
            }
            else if (projection.Selection is null)
            {
                renderer.ClearSelection();
            }

            // Only the layers that differ: every call is a view change of its own, and a streamed
            // drag would otherwise re-send every layer's switch with every move.
            var visible = ActiveLayers(renderer.Scene).ToHashSet(StringComparer.Ordinal);
            foreach (var layer in renderer.Scene.Layers)
            {
                var wanted = projection.ActiveLayers.Contains(layer.Id.Value);
                if (wanted != visible.Contains(layer.Id.Value))
                {
                    renderer.SetLayerVisibility(layer.Id, wanted);
                }
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
    /// <remarks>
    /// This runs on a pool thread and the encode takes a few hundred milliseconds, during which
    /// the cockpit may replace the picture and free the old one. It did, on every launch of
    /// 2.0.1278, and the process died inside Skia's PNG encoder with an access violation. The
    /// bitmap is therefore touched only under a lease from the cockpit that made it, and its size
    /// is remembered with its bytes so a picture already encoded is never touched again. False
    /// when the picture has been replaced already and there is nothing safe to read.
    /// </remarks>
    private bool TryArtworkFor(
        MapSceneRendererViewModel renderer,
        out (TabletMapArtwork Descriptor, TabletMapArtworkBytes Bytes)? artwork)
    {
        artwork = null;
        if (renderer.BackgroundImage is not Bitmap bitmap)
        {
            return true;
        }

        if (!ReferenceEquals(_artworkSource, bitmap) || _artwork is null || _artworkSha is null)
        {
            using var lease = _cockpit.TryReadPicture(bitmap);
            if (lease is null)
            {
                return false;
            }

            using var buffer = new MemoryStream();
            bitmap.Save(buffer, new PngBitmapEncoderOptions());
            var bytes = buffer.ToArray();
            _artworkSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            _artwork = new TabletMapArtworkBytes("image/png", _artworkSha, bytes);
            _artworkWidth = (int)Math.Round(bitmap.Size.Width);
            _artworkHeight = (int)Math.Round(bitmap.Size.Height);
            _artworkSource = bitmap;
        }

        artwork = (new TabletMapArtwork("image/png", _artworkSha, _artworkWidth, _artworkHeight), _artwork);
        return true;
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
        _ease?.Dispose();
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
