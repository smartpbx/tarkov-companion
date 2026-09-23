using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>
/// Sends the pings and waypoints placed on the V2 map to the group (#707).
/// </summary>
/// <remarks>
/// The V2 Raid map keeps its marks in <see cref="IRaidMarkStore"/>, and a tablet's marks land in
/// the same store through the relay bridge. Nothing sent either to the group: V1's map posted
/// its marks straight to the relay, and V2 replaced that gesture with a local one, so a ping
/// placed on V2 reached nobody — in a raid or out of one. Clayton, out of a raid with a
/// squadmate still inside: "i want to be able to ping to my teammate who is still in".
///
/// Only marks placed while this runs are sent. The store keeps marks between runs, and replaying
/// yesterday's waypoints into tonight's group would be a plan nobody made.
///
/// The relay has no move and no rename, so a moved waypoint is taken off and sent again. A mark
/// removed here, or a ping that expires here, is taken off the relay too.
///
/// #289: a "Just me" mark is never sent. Narrowing a sent one to "Just me" takes it off the relay,
/// and widening one to "Squad" sends it then, however old it is: that is the player asking. A new
/// lifetime can change the kind (only the 45-second one is a ping), which the relay cannot change
/// either, so that is a remove and resend like a move. The relay forgets nothing on a timer except
/// pings, so a five-minute waypoint leaves it when the local store expires it, like any removal.
/// </remarks>
internal sealed class GroupMarkForwarder : IDisposable
{
    /// <summary>Sends one mark, answering with the relay's id, or null when it was not sent.</summary>
    public delegate Task<long?> SendMark(string mapId, WorldPosition position, bool isPing, CancellationToken cancellationToken);

    /// <summary>How old a mark may be when first seen and still count as just placed.</summary>
    private static readonly TimeSpan JustPlaced = TimeSpan.FromSeconds(30);

    private readonly IRaidMarkStore _marks;
    private readonly Func<RaidMark, WorldPosition?> _locate;
    private readonly SendMark _send;
    private readonly Func<long, Task> _remove;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly HashSet<Guid> _seen = [];
    private readonly HashSet<Guid> _private = [];
    private readonly Dictionary<Guid, Sent> _sent = [];
    private bool _disposed;

    public GroupMarkForwarder(
        IRaidMarkStore marks,
        Func<RaidMark, WorldPosition?> locate,
        SendMark send,
        Func<long, Task> remove,
        TimeProvider? clock = null)
    {
        _marks = marks ?? throw new ArgumentNullException(nameof(marks));
        _locate = locate ?? throw new ArgumentNullException(nameof(locate));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _remove = remove ?? throw new ArgumentNullException(nameof(remove));
        _clock = clock ?? TimeProvider.System;
        foreach (var mark in _marks.Marks)
        {
            _seen.Add(mark.Id);
            if (mark.Scope == RaidMarkScope.Private)
            {
                _private.Add(mark.Id);
            }
        }

        _marks.Changed += MarksChanged;
    }

    /// <summary>Raised when a sent mark's relay id is known, so the map can stop drawing it twice.</summary>
    public event Action? Changed;

    /// <summary>
    /// Whether a group mark is one of ours, already drawn from the local store.
    /// </summary>
    public bool IsForwarded(long groupId)
    {
        lock (_gate)
        {
            return groupId > 0 && _sent.Values.Any(sent => sent.GroupId == groupId);
        }
    }

    /// <summary>The local mark a relay id was sent for, or null when it is not one of ours.</summary>
    public Guid? LocalIdFor(long groupId)
    {
        lock (_gate)
        {
            foreach (var (id, sent) in _sent)
            {
                if (sent.GroupId == groupId)
                {
                    return id;
                }
            }

            return null;
        }
    }

    private void MarksChanged()
    {
        var current = _marks.Marks;
        var now = _clock.GetUtcNow();
        var toSend = new List<RaidMark>();
        var toRemove = new List<long>();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var present = current.ToDictionary(mark => mark.Id);
            foreach (var mark in current)
            {
                var isPrivate = mark.Scope == RaidMarkScope.Private;
                var wasPrivate = isPrivate ? !_private.Add(mark.Id) : _private.Remove(mark.Id);
                if (_seen.Add(mark.Id))
                {
                    if (!isPrivate && now - mark.CreatedUtc <= JustPlaced)
                    {
                        toSend.Add(mark);
                    }

                    continue;
                }

                if (!_sent.TryGetValue(mark.Id, out var sent))
                {
                    // Widened from "Just me" to "Squad": the player chose to share it now.
                    if (!isPrivate && wasPrivate)
                    {
                        toSend.Add(mark);
                    }

                    continue;
                }

                if (isPrivate)
                {
                    // Narrowed to "Just me": it leaves the group.
                    _sent.Remove(mark.Id);
                    if (sent.GroupId is { } narrowedId)
                    {
                        toRemove.Add(narrowedId);
                    }

                    continue;
                }

                // A waypoint dragged somewhere else, or a new lifetime that made a ping a
                // waypoint: the relay cannot change either, so the old one goes and the mark is
                // sent again as a fresh one.
                if ((sent.X != mark.State.X || sent.Y != mark.State.Y || sent.Kind != mark.Kind) &&
                    sent.GroupId is { } movedId)
                {
                    _sent.Remove(mark.Id);
                    toRemove.Add(movedId);
                    toSend.Add(mark);
                }
            }

            _private.RemoveWhere(id => !present.ContainsKey(id));

            foreach (var (id, sent) in _sent.ToArray())
            {
                if (!present.ContainsKey(id))
                {
                    _sent.Remove(id);
                    if (sent.GroupId is { } goneId)
                    {
                        toRemove.Add(goneId);
                    }
                }
            }

            foreach (var mark in toSend)
            {
                // Recorded before the send, with no id yet, so a removal that lands while the
                // send is in flight is noticed when the id comes back.
                _sent[mark.Id] = new(null, mark.State.X, mark.State.Y, mark.Kind);
            }
        }

        foreach (var id in toRemove.Where(id => id > 0))
        {
            _ = RemoveQuietlyAsync(id);
        }

        foreach (var mark in toSend)
        {
            _ = SendAsync(mark);
        }
    }

    private async Task SendAsync(RaidMark mark)
    {
        if (_locate(mark) is not { } position)
        {
            lock (_gate)
            {
                _sent.Remove(mark.Id);
            }

            return;
        }

        long? groupId;
        try
        {
            groupId = await _send(mark.State.MapId, position, mark.Kind == RaidMarkKind.Ping, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            groupId = null;
        }

        var stillWanted = false;
        lock (_gate)
        {
            if (_sent.TryGetValue(mark.Id, out var sent) && sent.GroupId is null &&
                sent.X == mark.State.X && sent.Y == mark.State.Y && sent.Kind == mark.Kind && groupId is not null)
            {
                _sent[mark.Id] = sent with { GroupId = groupId };
                stillWanted = true;
            }
            else if (groupId is null)
            {
                _sent.Remove(mark.Id);
            }
        }

        if (!stillWanted && groupId is > 0)
        {
            // Removed or moved while it was on its way: take the one that arrived back off.
            await RemoveQuietlyAsync(groupId.Value).ConfigureAwait(true);
            return;
        }

        if (stillWanted)
        {
            Changed?.Invoke();
        }
    }

    private async Task RemoveQuietlyAsync(long groupId)
    {
        try
        {
            await _remove(groupId).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The relay forgets a ping by itself, and a stale waypoint can be removed from the
            // Team list; neither is worth failing the map for.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _marks.Changed -= MarksChanged;
    }

    /// <summary>
    /// Compares the group's current waypoints with what was sent, and answers the local marks
    /// whose relay copy somebody else removed.
    /// </summary>
    /// <remarks>
    /// #289 conflict state. Anyone in the group can remove any mark, and until now a squadmate
    /// removing one of this player's waypoints left it on this map only: the player went on
    /// seeing a "Squad" mark the squad no longer had. The later action wins, so the local copy
    /// goes too. Only a waypoint the relay was seen holding counts, so a send still in flight is
    /// never mistaken for a removal; pings are left out because the relay expires those on its
    /// own clock. The relay does not say who removed it, so neither does the note.
    /// Pass only a snapshot from a live connection: an offline one is not news.
    /// </remarks>
    public IReadOnlyList<Guid> ObserveGroup(IReadOnlyCollection<long> waypointIds)
    {
        ArgumentNullException.ThrowIfNull(waypointIds);
        var lost = new List<Guid>();
        lock (_gate)
        {
            if (_disposed)
            {
                return [];
            }

            foreach (var (id, sent) in _sent.ToArray())
            {
                if (sent.GroupId is not { } groupId || sent.Kind != RaidMarkKind.Waypoint)
                {
                    continue;
                }

                if (waypointIds.Contains(groupId))
                {
                    if (!sent.SeenOnRelay)
                    {
                        _sent[id] = sent with { SeenOnRelay = true };
                    }
                }
                else if (sent.SeenOnRelay)
                {
                    _sent.Remove(id);
                    lost.Add(id);
                }
            }
        }

        return lost;
    }

    private sealed record Sent(long? GroupId, double X, double Y, RaidMarkKind Kind, bool SeenOnRelay = false);
}
