using System.Collections.Concurrent;

namespace TarkovCompanion.GroupServer;

/// <summary>
/// Counts what has changed in a room, and lets one member wait for the next change.
/// </summary>
/// <remarks>
/// A member published and read the room in the same exchange, on a five-second tick, so a
/// position that arrived a moment after somebody's tick waited most of a tick to be published
/// and most of another to be collected. The data is a few hundred bytes; the wait was the
/// whole of the delay.
///
/// So a caller may say which revision of the room it already has and how long it is willing to
/// hold. If the room has moved on it is answered at once; otherwise the answer waits here until
/// it moves or the hold expires. That is the only thing this adds. A caller that says neither
/// is answered exactly as before, which is what keeps older builds working against the same
/// relay while newer ones are connected to it.
///
/// The revision one member sees counts changes made by everybody else. A member's own publish
/// cannot be what wakes them, or every hold would return immediately on the exchange that
/// started it. Both counters only ever rise, so the difference between them does too.
/// </remarks>
public sealed class GroupRoomChanges(TimeProvider timeProvider)
{
    /// <summary>The longest this relay will hold a request, whatever the caller asked for.</summary>
    /// <remarks>
    /// Twenty seconds, well inside Kestrel's 130-second keep-alive and its own limits, so a
    /// held request is never the thing that times out. A client that wants a shorter hold says
    /// so; nothing may ask for a longer one.
    /// </remarks>
    public static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(20);

    /// <summary>How many requests may be held at once across every room.</summary>
    /// <remarks>
    /// A held request costs a socket and a continuation, not a thread, so this is generous
    /// against the handful of friends a relay serves and still a bound. Past it a caller is
    /// answered immediately rather than refused: the exchange still works, it is just as slow
    /// as it was before any of this.
    /// </remarks>
    public const int MaximumConcurrentWaits = 256;

    /// <summary>How many members one room counts separately before the tally is reset.</summary>
    private const int MaximumTrackedMembers = 64;

    private readonly ConcurrentDictionary<string, RoomPulse> _rooms = new(StringComparer.Ordinal);
    private int _waiting;

    /// <summary>How many requests are being held right now, for /health to report.</summary>
    public int WaitingCount => Volatile.Read(ref _waiting);

    /// <summary>Records that a room changed, and wakes whoever was waiting for it.</summary>
    /// <param name="room">The room hash.</param>
    /// <param name="byMemberKey">
    /// Who caused it, when one member did. Their own hold is not woken by it, because a member
    /// republishing themselves has learnt nothing about the others.
    /// </param>
    public void Record(string room, string? byMemberKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        PulseFor(room)?.Record(byMemberKey);
    }

    /// <summary>The revision of this room as one member sees it.</summary>
    public long RevisionFor(string room, string memberKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        return _rooms.TryGetValue(room, out var pulse) ? pulse.RevisionFor(memberKey ?? string.Empty) : 0;
    }

    /// <summary>
    /// Waits until this room moves past the revision the caller already has.
    /// </summary>
    /// <returns>The revision to answer with, whether it moved or the hold simply expired.</returns>
    public async Task<long> WaitAsync(
        string room,
        string memberKey,
        long since,
        TimeSpan wait,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        memberKey ??= string.Empty;
        var pulse = PulseFor(room);
        if (pulse is null || wait <= TimeSpan.Zero)
        {
            return pulse?.RevisionFor(memberKey) ?? 0;
        }

        if (wait > MaximumWait)
        {
            wait = MaximumWait;
        }

        if (Interlocked.Increment(ref _waiting) > MaximumConcurrentWaits)
        {
            // Over the bound the exchange degrades to what it always was rather than failing.
            Interlocked.Decrement(ref _waiting);
            return pulse.RevisionFor(memberKey);
        }

        // One source for both ends of the hold: the caller hanging up and the hold expiring
        // are the same outcome, and neither may leave a timer running behind the answer.
        using var expiry = new CancellationTokenSource(wait, timeProvider);
        using var hold = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        try
        {
            while (true)
            {
                // The signal is taken before the revision is read, so a change landing between
                // the two is waited on rather than missed.
                var (revision, changed) = pulse.Observe(memberKey);
                if (revision > since || hold.IsCancellationRequested)
                {
                    return revision;
                }

                await changed.WaitAsync(hold.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The hold expired, or the caller hung up. Either way the room is answered as it
            // stands, which is what a caller that never asked to wait would have been given.
            return pulse.RevisionFor(memberKey);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    /// <summary>
    /// The counter for one room, creating it while there is room to.
    /// </summary>
    /// <remarks>
    /// Bounded like <see cref="GroupRooms.MaximumRooms"/> and for the same reason: a key is
    /// whatever somebody sent, so a guesser walking the key space must not leave an entry
    /// behind per attempt. At the bound the least recently touched room is dropped, which costs
    /// that room one immediate answer and nothing else.
    /// </remarks>
    private RoomPulse? PulseFor(string room)
    {
        if (_rooms.TryGetValue(room, out var existing))
        {
            existing.Touch(timeProvider.GetUtcNow());
            return existing;
        }

        if (_rooms.Count >= GroupRooms.MaximumRooms)
        {
            var oldest = _rooms.OrderBy(pair => pair.Value.LastTouchedUtc).FirstOrDefault();
            if (oldest.Key is not null)
            {
                _rooms.TryRemove(oldest.Key, out _);
            }

            if (_rooms.Count >= GroupRooms.MaximumRooms)
            {
                return null;
            }
        }

        return _rooms.GetOrAdd(room, _ => new(timeProvider.GetUtcNow()));
    }

    private sealed class RoomPulse(DateTimeOffset createdUtc)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, long> _byMember = new(StringComparer.Ordinal);
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _revision;

        public DateTimeOffset LastTouchedUtc { get; private set; } = createdUtc;

        public void Touch(DateTimeOffset now) => LastTouchedUtc = now;

        public long RevisionFor(string memberKey)
        {
            lock (_gate)
            {
                return _revision - Own(memberKey);
            }
        }

        public (long Revision, Task Changed) Observe(string memberKey)
        {
            lock (_gate)
            {
                return (_revision - Own(memberKey), _changed.Task);
            }
        }

        public void Record(string? memberKey)
        {
            TaskCompletionSource woken;
            lock (_gate)
            {
                _revision++;
                if (memberKey is { Length: > 0 })
                {
                    if (_byMember.Count >= MaximumTrackedMembers && !_byMember.ContainsKey(memberKey))
                    {
                        // A room cannot hold this many members, so this is a client inventing
                        // names. Forgetting the tally only makes the next hold return at once.
                        _byMember.Clear();
                    }

                    _byMember[memberKey] = Own(memberKey) + 1;
                }

                woken = _changed;
                _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            woken.TrySetResult();
        }

        private long Own(string memberKey) =>
            _byMember.TryGetValue(memberKey, out var own) ? own : 0;
    }
}
