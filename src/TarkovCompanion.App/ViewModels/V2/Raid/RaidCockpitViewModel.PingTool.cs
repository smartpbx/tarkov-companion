using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>Who a ping placed now can reach, from the group's state at that moment.</summary>
internal enum PingReach
{
    /// <summary>The relay answers: a Squad ping goes out at once.</summary>
    Squad,

    /// <summary>A squad is set up and the relay is not answering: a Squad ping waits for it.</summary>
    RelayOffline,

    /// <summary>A squad is set up but Local only or the squad switch keeps it on this PC.</summary>
    SharingOff,

    /// <summary>No squad is set up.</summary>
    NoSquad,
}

/// <summary>
/// #983: the Ping tool, and a line on the map saying where each ping went.
/// </summary>
/// <remarks>
/// "i STILL cannot ping on the map at all", the third report after #584, #707 and #929. Placing
/// was a right-click and nothing on the page said so: the arm buttons and the sentence that
/// explained them went in V2 rough package 46. And whatever happened after the press happened in
/// silence. A ping placed while the group was between exchanges became "Just me" for good and
/// never left this PC; one the relay refused waited in a queue nobody could see; one the store
/// could not save was never drawn at all. So the mode group has a Ping tool (a plain click pings),
/// the map says how to ping until the player has, and every ping placed says, for five seconds,
/// whether the squad has it and if not why not.
/// </remarks>
public sealed partial class RaidCockpitViewModel
{
    private static readonly TimeSpan PingStatusLifetime = TimeSpan.FromSeconds(5);

    private ICommand? _pingModeCommand;
    private string _pingStatus = string.Empty;
    private ITimer? _pingStatusTimer;
    private Guid? _awaitedDelivery;
    private RaidMarkKind _awaitedKind;
    private bool _hasPlacedMark;
    private bool _myMarksWereOff;

    public bool IsPingMode => _interactionMode == MapInteractionMode.Ping;

    /// <summary>Pressing the lit tool again goes back to Navigate, like the other modes.</summary>
    public ICommand PingModeCommand => _pingModeCommand ??= new DelegateCommand(() =>
        SetInteractionMode(IsPingMode ? MapInteractionMode.Navigate : MapInteractionMode.Ping));

    /// <summary>"Ping sent to squad", "Relay offline · ping kept on this PC", …; empty when there is nothing to say.</summary>
    public string PingStatus => _pingStatus;

    public bool HasPingStatus => _pingStatus.Length > 0;

    /// <summary>How to ping, on the map, until the player has placed one this session.</summary>
    public bool ShowsPingHint => !_hasPlacedMark && HasRenderer && IsNavigateMode && !HasPingStatus;

    /// <summary>Who a ping placed now would reach.</summary>
    internal PingReach CurrentPingReach => ReachOf(_groupSession is not null, _stateStore.Current.Group);

    internal static PingReach ReachOf(bool hasSession, GroupSnapshot group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!hasSession)
        {
            return PingReach.NoSquad;
        }

        if (group.IsSharing)
        {
            return PingReach.Squad;
        }

        return group.Status?.Code switch
        {
            // Set up and trying: the relay is down, slow, or has not answered yet.
            GroupStatus.ServerUnreachable or GroupStatus.NoAnswerInTime or GroupStatus.SharingFailed
                or GroupStatus.LastHeard or GroupStatus.ServerAnswered => PingReach.RelayOffline,
            GroupStatus.LocalOnly or GroupStatus.SwitchedOff => PingReach.SharingOff,
            _ => PingReach.NoSquad,
        };
    }

    /// <summary>What the map says about a mark just placed, before any send has finished.</summary>
    internal static string DescribePlaced(RaidMarkKind kind, RaidMarkScope scope, PingReach reach)
    {
        var name = kind == RaidMarkKind.Ping ? RaidText.Ping : RaidText.Waypoint;
        return (scope, reach) switch
        {
            (RaidMarkScope.Squad, PingReach.Squad) => RaidText.PingSending(name),
            (RaidMarkScope.Squad, _) => RaidText.PingRelayOffline(name),
            (_, PingReach.NoSquad) => RaidText.PingNoSquad(name),
            (_, PingReach.SharingOff) => RaidText.PingSharingOff(name),
            _ => RaidText.PingJustMe(name),
        };
    }

    /// <summary>What the map says once the send has an answer.</summary>
    internal static string DescribeDelivery(RaidMarkKind kind, GroupMarkDelivery delivery)
    {
        var name = kind == RaidMarkKind.Ping ? RaidText.Ping : RaidText.Waypoint;
        return delivery switch
        {
            GroupMarkDelivery.Sent => RaidText.PingSent(name),
            GroupMarkDelivery.Queued => RaidText.PingRelayOffline(name),
            _ => RaidText.PingUnplaceable(name),
        };
    }

    /// <summary>Places a mark and says on the map where it went; never silent.</summary>
    private async Task PlaceAndReportAsync(Func<Task<RaidMark>> place)
    {
        // Before the store changes, so the rebuild its change asks for already draws the layer.
        _myMarksWereOff = RevealOwnMarks();
        RaidMark mark;
        try
        {
            mark = await place().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The store keeps it and draws it (#983); only the file is behind.
            NotePlaced();
            ShowPingStatus(RaidText.PingNotSaved);
            return;
        }

        NotePlaced();
        var reach = CurrentPingReach;
        if (mark.Scope == RaidMarkScope.Squad && reach == PingReach.Squad)
        {
            _awaitedDelivery = mark.Id;
            _awaitedKind = mark.Kind;
        }

        ShowPingStatus(DescribePlaced(mark.Kind, mark.Scope, reach));
    }

    private void GroupMarkDelivered(Guid markId, GroupMarkDelivery delivery) => Dispatch(() =>
    {
        if (_disposed || _awaitedDelivery != markId)
        {
            return;
        }

        _awaitedDelivery = null;
        ShowPingStatus(DescribeDelivery(_awaitedKind, delivery));
    });

    private void NotePlaced()
    {
        if (_hasPlacedMark)
        {
            return;
        }

        _hasPlacedMark = true;
        OnPropertyChanged(nameof(ShowsPingHint));
    }

    private void ShowPingStatus(string status)
    {
        if (_disposed)
        {
            return;
        }

        _pingStatus = _myMarksWereOff && status.Length > 0 ? RaidText.PingMyMarksWasOff(status) : status;
        _pingStatusTimer?.Dispose();
        _pingStatusTimer = _timeProvider.CreateTimer(_ => Dispatch(ClearPingStatus), null, PingStatusLifetime, Timeout.InfiniteTimeSpan);
        RaisePingStatus();
    }

    /// <summary>
    /// Turns My marks back on for a mark the player has just placed; answers whether it was off.
    /// </summary>
    /// <remarks>
    /// [#983, #933] Before #933 one press of Loot focus saved "my-marks:0" as the player's own
    /// choice, for good and on every map, and a switch the player never touched kept every ping
    /// they placed off the map while the line said the squad had it. A ping placed is a ping the
    /// player wants to see, so placing one shows the layer again and the line says it was off.
    /// </remarks>
    private bool RevealOwnMarks()
    {
        var storedOff = _layerVisibility.Get(MarksLayerId) == false;
        var shownOff = Renderer?.Scene.View.Layers.FirstOrDefault(state => state.LayerId == MarksLayerId)?.IsVisible == false;
        if (!storedOff && !shownOff)
        {
            return false;
        }

        _layerVisibility.Set(MarksLayerId, true);
        if (shownOff && Renderer is { } renderer)
        {
            _ = _layerVisibility.ApplyChange(renderer, new MapSceneViewChange(
                Guid.NewGuid(),
                renderer.Scene.Revision,
                MapSceneViewChangeKind.SetLayerVisibility,
                LayerId: MarksLayerId,
                IsVisible: true));
        }

        return true;
    }

    private void ClearPingStatus()
    {
        _pingStatusTimer?.Dispose();
        _pingStatusTimer = null;
        _myMarksWereOff = false;
        if (_pingStatus.Length == 0)
        {
            return;
        }

        _pingStatus = string.Empty;
        RaisePingStatus();
    }

    private void RaisePingStatus()
    {
        OnPropertyChanged(nameof(PingStatus));
        OnPropertyChanged(nameof(HasPingStatus));
        OnPropertyChanged(nameof(ShowsPingHint));
    }

    private void PingModeChanged()
    {
        OnPropertyChanged(nameof(IsPingMode));
        OnPropertyChanged(nameof(ShowsPingHint));
    }

    private void DisposePingTool()
    {
        _pingStatusTimer?.Dispose();
        _pingStatusTimer = null;
    }

    /// <summary>A plain click in Ping mode: a ping where it landed.</summary>
    private void PingClicked(MapScenePoint point) => PlaceMarkAt(point, RaidMarkKind.Ping);
}
