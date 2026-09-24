using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.App.ViewModels.V2.Team;

/// <summary>
/// The player's own marks in the Team list, with the scope and lifetime they chose (#289).
/// </summary>
/// <remarks>
/// The relay knows neither: to it every mark is the team's, a waypoint lasts until removed and a
/// ping 45 seconds. So for a mark this desktop sent, the row reads the local mark behind it (a
/// five-minute waypoint says how long it has left), and a "Just me" mark, which the relay never
/// sees, is listed from the local store so the list still holds every mark on the map.
/// </remarks>
public sealed partial class TeamWorkspaceViewModel
{
    /// <summary>"TTL · 4m 12s left" for one of our own sent marks; null for anybody else's.</summary>
    private string? OwnMarkTtl(long groupId, DateTimeOffset now) =>
        _raidCockpit?.LocalMarkForGroupId(groupId) is { } mark
            ? TeamText.Ttl(RaidMarkLifetimes.TimeLeft(mark, now))
            : null;

    private IEnumerable<TeamMarkRowViewModel> PrivateMarkRows(DateTimeOffset now)
    {
        if (_raidCockpit is not { } cockpit)
        {
            yield break;
        }

        foreach (var mark in cockpit.PrivateMarks.OrderBy(mark => mark.CreatedUtc))
        {
            var isPing = mark.Kind == RaidMarkKind.Ping;
            var kind = isPing ? TeamText.Ping : TeamText.Waypoint;
            var age = TeamText.Ago(GroupSessionService.Ago(now - mark.CreatedUtc));
            var timeLeft = RaidMarkLifetimes.TimeLeft(mark, now);
            var id = mark.Id;
            yield return new(0, kind, mark.State.Label ?? kind, mark.State.MapId, TeamText.MarkedByYou, age, isPing ? timeLeft : null, false)
            {
                RemoveCommand = new AsyncDelegateCommand(() => cockpit.RemoveLocalMarkAsync(id)),
                // Not numbered: the map numbers the squad's waypoints, and this one is not the squad's.
                Number = isPing ? null : "·",
                Title = mark.State.Label ?? kind,
                Detail = JoinDetail(MapLabel(mark.State.MapId), age),
                MetadataLabel = JoinDetail(TeamText.ScopeJustMe, TeamText.Ttl(timeLeft)),
            };
        }
    }

    /// <summary>
    /// #289: our "Squad" marks whose send failed, listed as "Queued" until the relay takes them.
    /// </summary>
    /// <remarks>
    /// Neither on the relay nor "Just me", so without these rows the Team list simply lacked them
    /// and the player had no way to tell that the squad had not seen them.
    /// </remarks>
    private IEnumerable<TeamMarkRowViewModel> QueuedMarkRows(DateTimeOffset now)
    {
        if (_raidCockpit is not { } cockpit)
        {
            yield break;
        }

        foreach (var mark in cockpit.QueuedMarks)
        {
            yield return QueuedMarkRow(mark, now, () => cockpit.RemoveLocalMarkAsync(mark.Id));
        }
    }

    internal static TeamMarkRowViewModel QueuedMarkRow(RaidMark mark, DateTimeOffset now, Func<Task> remove)
    {
        ArgumentNullException.ThrowIfNull(mark);
        var isPing = mark.Kind == RaidMarkKind.Ping;
        var kind = isPing ? TeamText.Ping : TeamText.Waypoint;
        var age = TeamText.Ago(GroupSessionService.Ago(now - mark.CreatedUtc));
        var timeLeft = RaidMarkLifetimes.TimeLeft(mark, now);
        return new(0, kind, mark.State.Label ?? kind, mark.State.MapId, TeamText.MarkedByYou, age, isPing ? timeLeft : null, false)
        {
            RemoveCommand = new AsyncDelegateCommand(remove),
            Number = isPing ? null : "·",
            Title = mark.State.Label ?? kind,
            Detail = JoinDetail(MapLabel(mark.State.MapId), age),
            MetadataLabel = JoinDetail(TeamText.QueuedSendsOnReconnect, TeamText.Ttl(timeLeft)),
            IsQueued = true,
        };
    }
}
