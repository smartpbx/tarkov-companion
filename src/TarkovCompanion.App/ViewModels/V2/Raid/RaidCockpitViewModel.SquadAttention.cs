using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>
/// [#712 0-5, 0-6] SQUAD's waypoint and the direction-aware ping pulse; the late-raid leave margin.
/// </summary>
public sealed partial class RaidCockpitViewModel
{
    private readonly SquadPingWatch _squadPings = new();
    private SquadEdgePulseViewModel? _squadEdge;
    private LeaveMarginSetting? _leaveMargin;
    private ICommand? _lessLeaveMarginCommand;
    private ICommand? _moreLeaveMarginCommand;

    /// <summary>The map edge lit toward a squadmate's new ping.</summary>
    public SquadEdgePulseViewModel SquadEdge => _squadEdge ??= new(_timeProvider, PostToInterface);

    private LeaveMarginSetting LeaveMarginChoice => _leaveMargin ??= new(_layout);

    public string LeaveMarginLabel => NowText.MarginValue(LeaveMarginChoice.Minutes);

    public ICommand LessLeaveMarginCommand => _lessLeaveMarginCommand ??= new DelegateCommand(() => ChangeLeaveMargin(-1));

    public ICommand MoreLeaveMarginCommand => _moreLeaveMarginCommand ??= new DelegateCommand(() => ChangeLeaveMargin(1));

    private void ChangeLeaveMargin(int steps)
    {
        LeaveMarginChoice.Change(steps);
        ApplyLeaveMargin();
    }

    /// <summary>Backup &amp; reset replaced the layout: read the margin again.</summary>
    private void ReloadLeaveMargin()
    {
        LeaveMarginChoice.Reload();
        ApplyLeaveMargin();
    }

    private void ApplyLeaveMargin()
    {
        OnPropertyChanged(nameof(LeaveMarginLabel));
        if (_nowHost?.Panel is { } panel)
        {
            panel.LeaveMargin = LeaveMarginChoice.Value;
        }
    }

    /// <summary>A squad waypoint at a squadmate's last shared spot: the relay's position for them, nothing else.</summary>
    private async Task<bool> WaypointSquadmateAsync(string name, CancellationToken cancellationToken)
    {
        if (_groupSession is not { } session ||
            _stateStore.Current.Group.Members.FirstOrDefault(member => string.Equals(member.Name, name, StringComparison.Ordinal)) is not
            { MapId: { } mapId, Position: { } position })
        {
            return false;
        }

        return await session.SendMarkAsync(mapId, position, label: name, isPing: false, cancellationToken).ConfigureAwait(true) is not null;
    }

    /// <summary>
    /// Called with each runtime snapshot on the interface thread: a squadmate's new ping flashes
    /// their SQUAD row and lights the map edge toward it.
    /// </summary>
    private void ObserveSquadPings(GroupSnapshot group)
    {
        foreach (var ping in _squadPings.Arrived(group, IsOwnForwardedMark))
        {
            _nowHost?.Panel?.FlashMember(ping.By);
            var colour = _map.GroupColorFor(ping.By) is { Length: 9 } argb ? "#" + argb[3..] : null;
            SquadEdge.Pulse(EdgeToward(ping), colour);
        }
    }

    private MapEdge EdgeToward(GroupPingView ping)
    {
        if (Renderer is not { } renderer ||
            _map.RenderModel is not { } model ||
            !string.Equals(model.Location.Id, ping.MapId, StringComparison.OrdinalIgnoreCase) ||
            !TryPlan(model, new WorldPosition(ping.X, ping.Y, ping.Z), out var target) ||
            !renderer.TryViewportPointAt(target, out var toX, out var toY))
        {
            return MapEdge.None;
        }

        var (fromX, fromY) = (renderer.CanvasWidth / 2, renderer.CanvasHeight / 2);
        if (_stateStore.Current.Raid.LastKnownPosition?.Position is { } you &&
            TryPlan(model, you, out var here) &&
            renderer.TryViewportPointAt(here, out var youX, out var youY))
        {
            (fromX, fromY) = (youX, youY);
        }

        return SquadPingWatch.EdgeToward(fromX, fromY, toX, toY, renderer.CanvasWidth, renderer.CanvasHeight);
    }
}
