using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.ViewModels.V2.Team;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>
/// The squad on the Raid map, including after the player's own raid is over (#707).
/// </summary>
/// <remarks>
/// Three things were missing between a V2 Raid map and the group. Marks placed on it never left
/// this machine (<see cref="GroupMarkForwarder"/>). The group's own pings and waypoints were
/// listed beside the map but never drawn on it. And once the player extracted there was nothing
/// saying which map the squadmates still inside were on, or a way to go there.
/// </remarks>
public sealed partial class RaidCockpitViewModel
{
    private const string GroupWaypointPrefix = "group-waypoint:";
    private const string GroupPingPrefix = "group-ping:";

    private GroupMarkForwarder? _groupForwarder;
    private ICommand? _followSquadmateCommand;

    /// <summary>Starts forwarding this map's marks to the group, when there is a group session.</summary>
    private void AttachGroupMarks()
    {
        if (_groupSession is not { } session)
        {
            return;
        }

        _groupForwarder = new(
            _marks,
            LocateMark,
            (mapId, position, isPing, cancellationToken) =>
                session.SendMarkAsync(mapId, position, label: null, isPing, cancellationToken),
            id => session.RemoveMarkAsync(id, CancellationToken.None),
            _timeProvider);
        _groupForwarder.Changed += MarksChanged;
    }

    private void DetachGroupMarks()
    {
        if (_groupForwarder is { } forwarder)
        {
            forwarder.Changed -= MarksChanged;
            forwarder.Dispose();
        }
    }

    /// <summary>Whether a group mark is one of this player's own, already drawn from the local store.</summary>
    private bool IsOwnForwardedMark(long groupId) => _groupForwarder?.IsForwarded(groupId) == true;

    /// <summary>
    /// Where in the world a local mark is, so the group's companions can place it on their maps.
    /// </summary>
    /// <remarks>
    /// The plan point is Leaflet map units, which the variant's transform inverts exactly. The
    /// height the plan cannot say comes from the mark's floor, then from the player's own last
    /// screenshot, and is zero otherwise; the group's "reached" test allows fifty metres.
    /// </remarks>
    private WorldPosition? LocateMark(RaidMark mark)
    {
        if (_map.RenderModel is not { } model ||
            !string.Equals(model.Location.Id, mark.State.MapId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return LocateMark(model, mark, _stateStore.Current.Raid.LastKnownPosition?.Position.Y);
    }

    internal static WorldPosition? LocateMark(MapRenderModel model, RaidMark mark, double? playerHeight)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(mark);
        if (model.TransformAvailability != MapTransformAvailability.Valid || model.Variant.Transform is not { } transform)
        {
            return null;
        }

        var height = FloorHeight(model, mark.State.FloorId) ?? playerHeight ?? 0;
        return transform.TryUnproject(new MapPoint(mark.State.X, mark.State.Y), height, out var position)
            ? position
            : null;
    }

    private static double? FloorHeight(MapRenderModel model, string? floorId)
    {
        if (floorId is null ||
            model.Floors.FirstOrDefault(floor => string.Equals(floor.Id, floorId, StringComparison.OrdinalIgnoreCase)) is not { } floor)
        {
            return null;
        }

        foreach (var extent in floor.Extents)
        {
            if (extent.MinimumHeight is { } low && extent.MaximumHeight is { } high)
            {
                return (low + high) / 2;
            }

            if (extent.MinimumHeight is { } only)
            {
                return only + 1;
            }
        }

        return null;
    }

    /// <summary>
    /// The group's pings and waypoints on this map, drawn on it rather than only listed beside it.
    /// </summary>
    /// <remarks>
    /// The same objects the Team workspace draws, from the same builder. This player's own
    /// forwarded marks are left out: the local layer already draws them, with their names.
    /// </remarks>
    private (MapSceneLayer? Layer, IReadOnlyList<MapSceneObject> Objects) BuildGroupMarksLayer(MapRenderModel model, DateTimeOffset nowUtc)
    {
        if (_groupSession is null)
        {
            return (null, []);
        }

        var compatible = TarkovCompanion.Application.Services.Quests.QuestMapProjectionService.CompatibleMapIds(model.Location, model.Variant);
        var group = _stateStore.Current.Group;
        var others = group with
        {
            Waypoints = [.. group.Waypoints.Where(waypoint => !IsOwnForwardedMark(waypoint.Id))],
            Pings = [.. group.Pings.Where(ping => !IsOwnForwardedMark(ping.Id))],
        };
        return TeamWorkspaceViewModel.BuildGroupMarks(
            others,
            mapId => compatible.Contains(mapId),
            position => TryPlan(model, position, out var point) ? point : null,
            nowUtc);
    }

    /// <summary>Removes the group mark a right-click hit, as the Team map does.</summary>
    private bool TryRemoveGroupMarkAt(MapSceneObjectId objectId)
    {
        if (_groupSession is not { } session)
        {
            return false;
        }

        var value = objectId.Value;
        var prefix = value.StartsWith(GroupWaypointPrefix, StringComparison.Ordinal) ? GroupWaypointPrefix
            : value.StartsWith(GroupPingPrefix, StringComparison.Ordinal) ? GroupPingPrefix
            : null;
        if (prefix is null ||
            !long.TryParse(value.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return false;
        }

        _ = session.RemoveMarkAsync(id, CancellationToken.None);
        return true;
    }

    /// <summary>The squadmate still in a raid, while this player is out of theirs.</summary>
    private GroupMemberView? WatchedSquadmate
    {
        get
        {
            var snapshot = _stateStore.Current;
            return SquadRaidPresence.StillInRaid(snapshot.Raid.State, snapshot.Group.Members);
        }
    }

    /// <summary>"Watching Geo" on their map, "Follow Geo · Customs" on another; empty otherwise.</summary>
    public string SquadWatchLabel => DescribeSquadWatch(WatchedSquadmate, _map.IsOnOpenMap, NameOfMap);

    public bool HasSquadWatch => SquadWatchLabel.Length > 0;

    /// <summary>True when one press would switch the map to the squadmate's.</summary>
    public bool CanFollowSquadmate => WatchedSquadmate is { } member && !_map.IsOnOpenMap(member.MapId);

    public ICommand FollowSquadmateCommand => _followSquadmateCommand ??= new DelegateCommand(FollowSquadmate);

    internal static string DescribeSquadWatch(
        GroupMemberView? member,
        Func<string?, bool> isOnOpenMap,
        Func<string?, string?> nameOfMap)
    {
        if (member is null)
        {
            return string.Empty;
        }

        return isOnOpenMap(member.MapId)
            ? $"Watching {member.Name}"
            : $"Follow {member.Name} · {nameOfMap(member.MapId) ?? member.MapId}";
    }

    private void FollowSquadmate()
    {
        if (WatchedSquadmate is not { MapId: { } mapId } || ResolveLocation(mapId) is not { } location)
        {
            return;
        }

        _ = SelectMapAsync(location.Id);
    }

    private string? NameOfMap(string? mapId) => ResolveLocation(mapId)?.Name;

    /// <summary>A squadmate's companion may name a map by its source id rather than its slug.</summary>
    private MapLocation? ResolveLocation(string? mapId) =>
        string.IsNullOrWhiteSpace(mapId)
            ? null
            : _map.Locations.FirstOrDefault(location =>
                string.Equals(location.Id, mapId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(location.SourceId, mapId, StringComparison.OrdinalIgnoreCase));

    private void NotifySquadWatch()
    {
        OnPropertyChanged(nameof(SquadWatchLabel));
        OnPropertyChanged(nameof(HasSquadWatch));
        OnPropertyChanged(nameof(CanFollowSquadmate));
    }
}
