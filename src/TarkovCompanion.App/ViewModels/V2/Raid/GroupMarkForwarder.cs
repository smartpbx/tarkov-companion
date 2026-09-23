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
                if (_seen.Add(mark.Id))
                {
                    if (now - mark.CreatedUtc <= JustPlaced)
                    {
                        toSend.Add(mark);
                    }

                    continue;
                }

                // A waypoint dragged somewhere else: the relay cannot move one, so the old one
                // goes and the new place is sent as a fresh mark.
                if (_sent.TryGetValue(mark.Id, out var sent) &&
                    (sent.X != mark.State.X || sent.Y != mark.State.Y) &&
                    sent.GroupId is { } movedId)
                {
                    _sent.Remove(mark.Id);
                    toRemove.Add(movedId);
                    toSend.Add(mark);
                }
            }

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
                _sent[mark.Id] = new(null, mark.State.X, mark.State.Y);
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
                sent.X == mark.State.X && sent.Y == mark.State.Y && groupId is not null)
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

    private sealed record Sent(long? GroupId, double X, double Y);
}
