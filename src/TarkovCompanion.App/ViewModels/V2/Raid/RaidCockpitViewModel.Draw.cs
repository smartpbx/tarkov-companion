using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>What a left-drag on the Raid map does (#286).</summary>
public enum MapInteractionMode
{
    /// <summary>A left-drag pans. The default, and where Escape always returns.</summary>
    Navigate,

    /// <summary>A left-drag draws a line; a middle-drag, or Space held with a left-drag, still pans.</summary>
    Draw,

    /// <summary>A click shows what is at that spot, in a popover; a drag still pans.</summary>
    Inspect,

    /// <summary>A click adds a stop to the player's planned route; a drag still pans.</summary>
    Route,
}

/// <summary>
/// [#286] Draw mode: freehand lines on the Raid map, for the player and, when they choose, the squad.
/// </summary>
/// <remarks>
/// A line is kept in plan units on the floor it was drawn on, so pan, zoom, a turn or a floor
/// change never moves it. It takes the same "Just me" / "Squad" switch the marks do, and a
/// lifetime from the marks' own list ("This raid" by default: a line is a plan for this raid).
/// "Squad" lines go to the group inside this player's own published state, bounded to twenty
/// lines of two hundred points; a squadmate's lines are drawn in their colour and only they can
/// take them off. Off by default, so nobody drags the map and finds they have scribbled on it.
/// These are the player's own annotations on the companion's map and nothing more.
/// </remarks>
public sealed partial class RaidCockpitViewModel
{
    private static readonly MapSceneLayerId DrawingsLayerId = new("drawings");
    private const string DrawingPrefix = "drawing:";
    private const string SquadDrawingPrefix = "squad-drawing:";

    /// <summary>
    /// Our own lines are white, not the player's cyan: the player's trail and a tablet's route are
    /// already cyan lines, and a drawing that looked like where you had walked would be a lie.
    /// </summary>
    private const string InkColor = "#FFF4F4F4";

    private RaidDrawingStore? _drawingStore;
    /// <summary>Each line's world points, taken when it was drawn, while its map's transform was at hand.</summary>
    private readonly Dictionary<Guid, GroupDrawingView> _drawingWorld = [];
    private MapInteractionMode _interactionMode = MapInteractionMode.Navigate;
    private RaidMarkLifetime _newDrawingLifetime = RaidMarkLifetime.ThisRaid;
    private ITimer? _drawingClock;
    private ICommand? _toggleDrawCommand;
    private ICommand? _clearMyDrawingsCommand;

    internal RaidDrawingStore DrawingStore => _drawingStore ??= new(_timeProvider);

    public MapInteractionMode InteractionMode => _interactionMode;

    public bool IsDrawMode => _interactionMode == MapInteractionMode.Draw;

    /// <summary>What the Draw switch says it does, for its tooltip and a screen reader.</summary>
    public string DrawModeLabel => IsDrawMode
        ? RaidText.DrawingFor(RaidText.MarkScope(NewMarkScope))
        : RaidText.DrawOnTheMap;

    /// <summary>"Squad · This raid": what a line drawn now will be.</summary>
    public string DrawSettingsLabel =>
        $"{RaidText.MarkScope(NewMarkScope)} · {RaidText.MarkLifetime(_newDrawingLifetime)}";

    public RaidMarkLifetime NewDrawingLifetime => _newDrawingLifetime;

    /// <summary>[#919] How thick the next line is, in pixels; remembered.</summary>
    public int NewDrawingWidth => _drawWidth.Value;

    /// <summary>[#919] Thin, medium, thick: the Draw bar's width picker.</summary>
    public IReadOnlyList<DrawWidthChoiceViewModel> DrawWidthChoices =>
    [
        .. RaidDrawingWidths.Choices.Select(width => new DrawWidthChoiceViewModel(
            width,
            width == _drawWidth.Value,
            new DelegateCommand(() => ChooseDrawingWidth(width)))),
    ];

    /// <summary>[#919] Picks the width the next line gets, and remembers it. Lines already drawn keep theirs.</summary>
    public void ChooseDrawingWidth(int width)
    {
        if (_drawWidth.Value == width)
        {
            return;
        }

        _drawWidth.Set(width);
        RaiseDrawingWidth();
    }

    private void RaiseDrawingWidth()
    {
        OnPropertyChanged(nameof(NewDrawingWidth));
        OnPropertyChanged(nameof(DrawWidthChoices));
    }

    public ICommand ToggleDrawCommand => _toggleDrawCommand ??= new DelegateCommand(() =>
        SetInteractionMode(IsDrawMode ? MapInteractionMode.Navigate : MapInteractionMode.Draw));

    public ICommand ClearMyDrawingsCommand => _clearMyDrawingsCommand ??= new DelegateCommand(ClearMyDrawings);

    /// <summary>Whether this map has a line of ours on it, so "Clear my drawings" has something to do.</summary>
    public bool HasOwnDrawings => _map.RenderModel is { } model &&
        DrawingStore.Drawings.Any(drawing => string.Equals(drawing.MapId, model.Location.Id, StringComparison.OrdinalIgnoreCase));

    public void SetInteractionMode(MapInteractionMode mode)
    {
        if (_interactionMode == mode || mode == MapInteractionMode.Draw && !IsDrawAvailable)
        {
            return;
        }

        var previous = _interactionMode;
        _interactionMode = mode;
        OnPropertyChanged(nameof(InteractionMode));
        OnPropertyChanged(nameof(IsDrawMode));
        OnPropertyChanged(nameof(DrawModeLabel));
        // [#286] Inspect and Route, in RaidCockpitViewModel.Modes.cs.
        InteractionModeChanged(previous);
    }

    /// <summary>Picks the lifetime the next line gets; the scope is the Marks card's switch.</summary>
    public void ChooseDrawingLifetime(RaidMarkLifetime lifetime)
    {
        _newDrawingLifetime = lifetime;
        OnPropertyChanged(nameof(NewDrawingLifetime));
        OnPropertyChanged(nameof(DrawSettingsLabel));
    }

    /// <summary>Keeps a line the player just drew, in plan units, on the floor they are looking at.</summary>
    public RaidDrawing? AddDrawing(IReadOnlyList<MapScenePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (_map.RenderModel is not { } model)
        {
            return null;
        }

        var floorId = Renderer?.Scene.View.SelectedFloorId ?? model.SelectedFloor?.Id;
        var drawing = DrawingStore.Add(
            model.Location.Id,
            floorId,
            [.. points.Select(point => new MapPoint(point.X, point.Y))],
            NewMarkScope,
            _newDrawingLifetime,
            _drawWidth.Value);
        if (drawing is not null &&
            model.TransformAvailability == MapTransformAvailability.Valid &&
            model.Variant.Transform is { } transform)
        {
            var height = FloorHeight(model, floorId) ?? _stateStore.Current.Raid.LastKnownPosition?.Position.Y ?? 0;
            if (ToWorld(transform, height, drawing) is { } world)
            {
                lock (_drawingWorld)
                {
                    _drawingWorld[drawing.Id] = world;
                }

                PublishSharedDrawings();
            }
        }

        return drawing;
    }

    /// <summary>One of our own lines, for the map's right-click menu; null for anything else.</summary>
    public RaidDrawing? OwnDrawing(MapSceneObjectId objectId) =>
        objectId.Value.StartsWith(DrawingPrefix, StringComparison.Ordinal) &&
        Guid.TryParse(objectId.Value.AsSpan(DrawingPrefix.Length), out var id)
            ? DrawingStore.Drawings.FirstOrDefault(drawing => drawing.Id == id)
            : null;

    public void RemoveDrawing(Guid id) => DrawingStore.Remove(id);

    public void SetDrawingOptions(Guid id, RaidMarkScope scope, RaidMarkLifetime lifetime) =>
        DrawingStore.SetOptions(id, scope, lifetime);

    private void ClearMyDrawings()
    {
        if (_map.RenderModel is { } model)
        {
            DrawingStore.Clear(model.Location.Id);
        }
    }

    private void AttachDrawings() => DrawingStore.Changed += DrawingsChanged;

    private void DetachDrawings()
    {
        DrawingStore.Changed -= DrawingsChanged;
        _drawingClock?.Dispose();
        _groupSession?.Drawings.Set([]);
    }

    /// <summary>A raid ended: its "This raid" lines go with it, like its marks.</summary>
    private void EndRaidDrawings() => DrawingStore.EndRaid();

    private void DrawingsChanged()
    {
        var present = DrawingStore.Drawings.Select(drawing => drawing.Id).ToHashSet();
        lock (_drawingWorld)
        {
            foreach (var gone in _drawingWorld.Keys.Where(id => !present.Contains(id)).ToArray())
            {
                _drawingWorld.Remove(gone);
            }
        }

        PublishSharedDrawings();
        ScheduleDrawingClock();
        Dispatch(() =>
        {
            OnPropertyChanged(nameof(HasOwnDrawings));
            _rebuildRequest.Request();
        });
    }

    /// <summary>Hands the group session this player's "Squad" lines, newest first within the relay's bounds.</summary>
    private void PublishSharedDrawings()
    {
        if (_groupSession is not { } session)
        {
            return;
        }

        IReadOnlyList<GroupDrawingView> shared;
        lock (_drawingWorld)
        {
            shared = RaidDrawingStore.SelectShared(
                DrawingStore.Drawings,
                drawing => _drawingWorld.GetValueOrDefault(drawing.Id),
                view => view.Points.Count);
        }

        session.Drawings.Set(shared);
    }

    /// <summary>Wakes when the soonest line expires, rather than on a tick.</summary>
    private void ScheduleDrawingClock()
    {
        _drawingClock?.Dispose();
        _drawingClock = null;
        if (_disposed || DrawingStore.NextExpiryUtc is not { } next)
        {
            return;
        }

        var due = next - _timeProvider.GetUtcNow();
        _drawingClock = _timeProvider.CreateTimer(
            _ => DrawingStore.Expire(),
            null,
            due < TimeSpan.Zero ? TimeSpan.Zero : due + TimeSpan.FromMilliseconds(50),
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>A line's plan points as world x, z, for a squadmate's companion to project with its own transform.</summary>
    internal static GroupDrawingView? ToWorld(MapCatalogTransform transform, double height, RaidDrawing drawing)
    {
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(drawing);
        var points = new List<(double X, double Z)>(drawing.Points.Count);
        foreach (var point in drawing.Points)
        {
            if (!transform.TryUnproject(point, height, out var world))
            {
                return null;
            }

            points.Add((world.X, world.Z));
        }

        return new(drawing.Id.ToString("N", CultureInfo.InvariantCulture)[..12], drawing.MapId, drawing.FloorId, points, drawing.Width);
    }

    /// <summary>
    /// The lines on this map: ours in the player's colour, each squadmate's in theirs, all on the
    /// floor they were drawn on.
    /// </summary>
    internal static (MapSceneLayer? Layer, IReadOnlyList<MapSceneObject> Objects, IReadOnlyDictionary<MapSceneObjectId, MapSceneObjectStyle> Styles) BuildDrawingLayer(
        IReadOnlyList<RaidDrawing> own,
        string mapId,
        IEnumerable<(string Member, string Color, GroupDrawingView Drawing)> squad,
        Func<string, bool> isOnMap,
        Func<WorldPosition, MapScenePoint?> project,
        DateTimeOffset nowUtc)
    {
        var objects = new List<MapSceneObject>();
        var styles = new Dictionary<MapSceneObjectId, MapSceneObjectStyle>();
        foreach (var drawing in own.Where(drawing => string.Equals(drawing.MapId, mapId, StringComparison.OrdinalIgnoreCase)))
        {
            var id = new MapSceneObjectId($"{DrawingPrefix}{drawing.Id}");
            objects.Add(new(
                id,
                DrawingsLayerId,
                MapSceneObjectKind.Route,
                MapSceneTruthKind.UserAuthored,
                RaidText.MyDrawing,
                DrawingHoverDetail(drawing),
                new(MapSceneGeometryKind.Line, [.. drawing.Points.Select(point => new MapScenePoint(point.X, point.Y))]),
                drawing.FloorId is null ? [] : [drawing.FloorId],
                new DataProvenance("local-drawing", drawing.CreatedUtc),
                expiresUtc: drawing.ExpiresUtc));
            styles[id] = new(InkColor, LineThickness: drawing.Width, Opacity: 0.95);
        }

        foreach (var (member, color, drawing) in squad)
        {
            if (!isOnMap(drawing.MapId))
            {
                continue;
            }

            var points = drawing.Points
                .Select(point => project(new WorldPosition(point.X, 0, point.Z)))
                .OfType<MapScenePoint>()
                .ToArray();
            if (points.Length < 2)
            {
                continue;
            }

            var id = new MapSceneObjectId($"{SquadDrawingPrefix}{member}:{drawing.Id}");
            objects.Add(new(
                id,
                DrawingsLayerId,
                MapSceneObjectKind.Route,
                MapSceneTruthKind.UserAuthored,
                RaidText.MemberDrawing(member),
                RaidText.DrawnBy(member),
                new(MapSceneGeometryKind.Line, points),
                drawing.FloorId is null ? [] : [drawing.FloorId],
                new DataProvenance("group-relay", nowUtc)));
            // [#919] A squadmate on an older companion sends no width: the width every line had then.
            styles[id] = new(color, LineThickness: drawing.Width ?? RaidDrawingWidths.Unstated, Opacity: 0.9);
        }

        return objects.Count == 0
            ? (null, [], styles)
            : (new MapSceneLayer(DrawingsLayerId, RaidText.LayerDrawings, 45, true), objects, styles);
    }

    /// <summary>"Squad · This raid" or "Just me · 5 min · ends 21:04", for a hover.</summary>
    internal static string DrawingHoverDetail(RaidDrawing drawing)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        var ends = drawing.ExpiresUtc is { } expires ? RaidText.Ends(LocalTime.ShortTime(expires)) : string.Empty;
        return string.Join(
            " · ",
            new[] { RaidText.MarkScope(drawing.Scope), RaidText.MarkLifetime(drawing.Lifetime), ends }.Where(part => part.Length > 0));
    }

    /// <summary>The drawings layer for the scene being built, with its styles.</summary>
    private (MapSceneLayer? Layer, IReadOnlyList<MapSceneObject> Objects, IReadOnlyDictionary<MapSceneObjectId, MapSceneObjectStyle> Styles) BuildDrawings(
        MapRenderModel model,
        DateTimeOffset nowUtc)
    {
        var compatible = TarkovCompanion.Application.Services.Quests.QuestMapProjectionService.CompatibleMapIds(model.Location, model.Variant);
        var squad = _groupSession is null
            ? []
            : _stateStore.Current.Group.Members
                .SelectMany(member => member.Drawings.Select(drawing => (member.Name, _map.GroupColorFor(member.Name), drawing)))
                .ToArray();
        return BuildDrawingLayer(
            DrawingStore.Drawings,
            model.Location.Id,
            squad,
            mapId => compatible.Contains(mapId),
            position => TryPlan(model, position, out var point) ? point : null,
            nowUtc);
    }
}
