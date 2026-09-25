using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>
/// Who a mark is for and how long it lasts, chosen by the player (#289).
/// </summary>
/// <remarks>
/// Until this, every mark went to the group and a ping lasted 45 seconds while a waypoint lasted
/// forever; there was no way to keep a mark to yourself or make one last five minutes. The fast
/// gestures are unchanged (right-click pings, shift+right-click places a waypoint), and they take
/// the scope the Marks card's "New marks" switch shows: Squad while group sharing is on, Just me
/// otherwise. Ctrl+right-click places with any lifetime, and right-clicking a mark of ours, or its
/// row's Options, changes either afterwards.
/// </remarks>
public sealed partial class RaidCockpitViewModel
{
    private RaidMarkScope? _chosenNewMarkScope;
    private RaidLifecycleState? _markRaidState;
    private GroupSnapshot? _markConflictGroup;
    private string _markNote = string.Empty;
    private ITimer? _markNoteTimer;
    private ITimer? _markClock;
    private ICommand? _newMarksJustMeCommand;
    private ICommand? _newMarksSquadCommand;

    /// <summary>How long the "removed in the group" note stays; it says something once, not forever.</summary>
    private static readonly TimeSpan MarkNoteLifetime = TimeSpan.FromSeconds(20);

    /// <summary>The scope a new mark gets: the player's pick, else Squad while sharing is on.</summary>
    public RaidMarkScope NewMarkScope => _chosenNewMarkScope ??
        (_groupSession is not null && _stateStore.Current.Group.IsSharing ? RaidMarkScope.Squad : RaidMarkScope.Private);

    public bool NewMarksAreSquad => NewMarkScope == RaidMarkScope.Squad;

    public bool NewMarksAreJustMe => NewMarkScope == RaidMarkScope.Private;

    public ICommand NewMarksJustMeCommand => _newMarksJustMeCommand ??= new DelegateCommand(() => ChooseNewMarkScope(RaidMarkScope.Private));

    public ICommand NewMarksSquadCommand => _newMarksSquadCommand ??= new DelegateCommand(() => ChooseNewMarkScope(RaidMarkScope.Squad));

    /// <summary>A one-time note about a mark somebody else changed; empty when there is nothing to say.</summary>
    public string MarkNote => _markNote;

    public bool HasMarkNote => _markNote.Length > 0;

    private void ChooseNewMarkScope(RaidMarkScope scope)
    {
        _chosenNewMarkScope = scope;
        // [#902 P4] Kept between launches, as the tablet keeps its own copy.
        _layout?.Set(WorkspaceLayoutKeys.RaidMarkScope, scope == RaidMarkScope.Squad ? "squad" : "private");
        RaiseNewMarkScope();
    }

    private void RaiseNewMarkScope()
    {
        OnPropertyChanged(nameof(NewMarkScope));
        OnPropertyChanged(nameof(NewMarksAreSquad));
        OnPropertyChanged(nameof(NewMarksAreJustMe));
        // [#286] New lines take the same switch.
        OnPropertyChanged(nameof(DrawSettingsLabel));
        OnPropertyChanged(nameof(DrawModeLabel));
        RouteScopeChanged();
    }

    /// <summary>Places a mark with a chosen lifetime where a Ctrl+right-click landed.</summary>
    public void PlaceMarkAt(MapScenePoint point, RaidMarkLifetime lifetime)
    {
        if (_map.RenderModel is not { } model)
        {
            return;
        }

        var floorId = Renderer?.Scene.View.SelectedFloorId ?? model.SelectedFloor?.Id;
        _ = _marks.PlaceAsync(model.Location.Id, floorId, point.X, point.Y, null, NewMarkScope, lifetime, colour: NewMarkColour);
    }

    /// <summary>The gestures' own placement: the gesture picks the kind, the Marks card the scope.</summary>
    private Task PlaceWithScopeAsync(RaidMarkKind kind, string mapId, string? floorId, double x, double y) =>
        _marks.PlaceAsync(mapId, floorId, x, y, null, NewMarkScope, RaidMarkLifetimes.DefaultFor(kind), colour: NewMarkColour);

    internal Task SetMarkOptionsAsync(Guid id, RaidMarkScope scope, RaidMarkLifetime lifetime)
    {
        ClearMarkNote();
        return _marks.SetOptionsAsync(id, scope, lifetime);
    }

    /// <summary>The row for one of our own marks, for the map's right-click menu; null for anything else.</summary>
    public RaidMarkRowViewModel? OwnMarkRow(MapSceneObjectId objectId) =>
        TryParseMarkId(objectId, out var id) ? Marks.FirstOrDefault(row => !row.IsGroupMark && row.Id == id) : null;

    /// <summary>Our own mark that a relay id stands for, so the Team list can show its real scope and lifetime.</summary>
    internal RaidMark? LocalMarkForGroupId(long groupId) =>
        _groupForwarder?.LocalIdFor(groupId) is { } id ? _marks.Marks.FirstOrDefault(mark => mark.Id == id) : null;

    /// <summary>Our "Just me" marks, which the relay never sees, for the Team list.</summary>
    internal IReadOnlyList<RaidMark> PrivateMarks => [.. _marks.Marks.Where(mark => mark.Scope == RaidMarkScope.Private)];

    internal Task RemoveLocalMarkAsync(Guid id) => _marks.RemoveAsync(id);

    /// <summary>"Squad · 5 min · ends 21:04": what hovering a mark adds to its name.</summary>
    /// <remarks>
    /// An end time rather than a countdown: the scene object is rebuilt when marks change, not
    /// every second, and a countdown frozen at the last rebuild would be wrong within a second.
    /// </remarks>
    internal static string MarkHoverDetail(RaidMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        var ends = mark.State.ExpiresUtc is { } expires ? RaidText.Ends(TarkovCompanion.Core.Common.LocalTime.ShortTime(expires)) : string.Empty;
        return string.Join(
            " · ",
            new[] { RaidText.MarkScope(mark.Scope), RaidText.MarkLifetime(mark.Lifetime), ends }.Where(part => part.Length > 0));
    }

    /// <summary>
    /// Runs on every runtime publication, before the cockpit's own "anything new?" check, because
    /// the rebuild also records what it saw and would hide a transition from that check.
    /// </summary>
    private void ObserveMarkLifetimes(ApplicationRuntimeSnapshot snapshot)
    {
        var state = snapshot.Raid?.State;
        var raidEnded = _markRaidState == RaidLifecycleState.InRaid && state is not null && state != RaidLifecycleState.InRaid;
        _markRaidState = state;
        if (raidEnded)
        {
            _ = _marks.EndRaidAsync();
            EndRaidDrawings();
        }

        var group = snapshot.Group;
        if (ReferenceEquals(group, _markConflictGroup))
        {
            return;
        }

        _markConflictGroup = group;
        if (_groupForwarder is not { } forwarder || !group.IsSharing || group.StaleSince is not null)
        {
            return;
        }

        var lost = forwarder.ObserveGroup(group.Waypoints.Select(waypoint => waypoint.Id).ToHashSet(), group.Room);
        if (lost.Count == 0)
        {
            return;
        }

        var names = Marks.Where(row => lost.Contains(row.Id)).Select(row => row.Label).ToArray();
        foreach (var id in lost)
        {
            _ = _marks.RemoveAsync(id);
        }

        ShowMarkNote(names.Length == 1
            ? RaidText.WaypointRemovedBySquad(names[0])
            : RaidText.WaypointsRemovedBySquad(lost.Count));
    }

    private void ShowMarkNote(string note)
    {
        _markNoteTimer?.Dispose();
        _markNote = note;
        _markNoteTimer = _timeProvider.CreateTimer(_ => Dispatch(ClearMarkNote), null, MarkNoteLifetime, Timeout.InfiniteTimeSpan);
        OnPropertyChanged(nameof(MarkNote));
        OnPropertyChanged(nameof(HasMarkNote));
    }

    private void ClearMarkNote()
    {
        _markNoteTimer?.Dispose();
        _markNoteTimer = null;
        if (_markNote.Length == 0)
        {
            return;
        }

        _markNote = string.Empty;
        OnPropertyChanged(nameof(MarkNote));
        OnPropertyChanged(nameof(HasMarkNote));
    }

    /// <summary>
    /// Keeps the Marks rows' countdowns current: one timer, running only while a row has one.
    /// </summary>
    private void ScheduleMarkClock()
    {
        if (!Marks.Any(row => row.Expires))
        {
            _markClock?.Dispose();
            _markClock = null;
            return;
        }

        _markClock ??= _timeProvider.CreateTimer(_ => Dispatch(TickMarkRows), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void TickMarkRows()
    {
        if (_disposed)
        {
            _markClock?.Dispose();
            return;
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var row in Marks)
        {
            row.Tick(now);
        }
    }

    /// <summary>
    /// A tablet's short route (#290): its stops, in order, joined by one line per route on the
    /// marks layer. Dashed and in the player's own colour, so it reads as this player's plan and
    /// never as an extract route or a squadmate's trail. A route needs two stops on this map.
    /// </summary>
    internal static (IReadOnlyList<MapSceneObject> Objects, int Routes) BuildMarkRoutes(
        IReadOnlyList<RaidMark> marks,
        string mapId,
        DateTimeOffset nowUtc)
    {
        var objects = new List<MapSceneObject>();
        foreach (var route in marks
            .Where(mark => mark.Route is not null && string.Equals(mark.State.MapId, mapId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(mark => mark.Route!.RouteId))
        {
            var stops = route.OrderBy(mark => mark.Route!.Step).Select(mark => new MapScenePoint(mark.State.X, mark.State.Y)).ToArray();
            if (stops.Length < 2)
            {
                continue;
            }

            objects.Add(new(
                new($"{MarkRoutePrefix}{route.Key:N}"),
                MarksLayerId,
                MapSceneObjectKind.Route,
                MapSceneTruthKind.UserAuthored,
                RaidText.RouteStops(stops.Length),
                null,
                new(MapSceneGeometryKind.Line, stops),
                [],
                new DataProvenance("local-mark", nowUtc)));
        }

        return (objects, objects.Count);
    }

    private const string MarkRoutePrefix = "mark-route:";

    private static IReadOnlyDictionary<MapSceneObjectId, MapSceneObjectStyle> WithRouteStyles(
        IReadOnlyDictionary<MapSceneObjectId, MapSceneObjectStyle> styles,
        IEnumerable<MapSceneObject> routes)
    {
        var merged = new Dictionary<MapSceneObjectId, MapSceneObjectStyle>(styles);
        foreach (var route in routes)
        {
            merged[route.Id] = new(PlayerColor, LineThickness: 3, Opacity: 0.95, Dashed: true);
        }

        return merged;
    }

    private static void Dispatch(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(action);
        }
    }
}
