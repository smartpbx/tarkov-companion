using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.Storage;

namespace TarkovCompanion.GroupServer.StateSync;

public sealed record RelayQueuedFrame(
    long DeliveryId,
    OpaqueRelayFrame Frame,
    DateTimeOffset ReceivedUtc);

public sealed record RelayFrameBatch(
    CompanionProtocolVersion ProtocolVersion,
    IReadOnlyList<RelayQueuedFrame> Frames,
    bool RequiresReconnect,
    DateTimeOffset ServerUtc);

public readonly record struct RelayFramePublishResult(bool Accepted, string Code, int RecipientCount)
{
    public static RelayFramePublishResult Reject(string code) => new(false, code, 0);
}

public readonly record struct RelayAcknowledgementResult(bool Accepted, string Code)
{
    public static RelayAcknowledgementResult Permit { get; } = new(true, "accepted");

    public static RelayAcknowledgementResult Reject(string code) => new(false, code);
}

/// <summary>Routes bounded #276 ciphertext while the authenticated desktop stays canonical.</summary>
/// <remarks>
/// A frame names the traffic session whose key protects it, while its delivery queue belongs to
/// the recipient. Those are intentionally different for tablet-to-desktop traffic: many tablet
/// channels feed one desktop queue. Replay state is therefore advanced on the target traffic
/// session and never on a publisher-wide channel counter.
/// </remarks>
public sealed class OpaqueRelayFrameHub
{
    private readonly RelayDeviceRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<DeviceSessionId, RecipientQueue> _queues = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _waiting;
    private long _wakeGeneration;

    /// <summary>The longest a frame read is held, whatever the caller asked for.</summary>
    /// <remarks>
    /// [#604] The same twenty seconds <c>RelayMapSurfaceStore.MaximumWait</c> and the group
    /// exchange use, for the same reason: well inside Kestrel's keep-alive.
    /// </remarks>
    public static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(20);

    /// <summary>How many frame reads may be held at once; past it a read is answered at once.</summary>
    public const int MaximumConcurrentWaits = 256;

    /// <summary>How many frame reads are being held right now, for <c>/health</c>.</summary>
    public int WaitingCount => Volatile.Read(ref _waiting);

    public OpaqueRelayFrameHub(RelayDeviceRegistry registry, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _registry = registry;
        _timeProvider = timeProvider;
    }

    public async ValueTask<RelayFramePublishResult> PublishAsync(
        RelayPrincipal principal,
        OpaqueRelayFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(frame);
        var now = Now();
        if (!RelayAuthorization.Decide(principal, RelayPermission.PublishOpaqueFrames, now).Allowed ||
            !_registry.IsCurrent(principal))
        {
            return RelayFramePublishResult.Reject("not-authorized");
        }

        if (!CompanionProtocolVersion.Current.CanRead(frame.ProtocolVersion) ||
            frame.IssuedUtc > now.Add(ProtocolBounds.MaxClientClockSkew) || frame.ExpiresUtc <= now)
        {
            return RelayFramePublishResult.Reject(
                !CompanionProtocolVersion.Current.CanRead(frame.ProtocolVersion)
                    ? "unsupported-version"
                    : "frame-rejected");
        }

        PairingTrafficDirection direction;
        RelaySessionRoute[] recipients;
        if (frame.SessionId == principal.SessionId)
        {
            direction = PairingTrafficDirection.TabletToDesktop;
            recipients = _registry.ActiveOwners()
                .Where(route => route.SessionId != principal.SessionId)
                .Take(RelaySecurityBounds.MaximumChannelParticipants)
                .ToArray();
        }
        else
        {
            direction = PairingTrafficDirection.DesktopToTablet;
            var target = _registry.ActiveRoute(frame.SessionId);
            recipients = target is null ? [] : [target];
        }

        if (recipients.Length == 0)
        {
            return RelayFramePublishResult.Reject("route-rejected");
        }

        lock (_gate)
        {
            SweepCore(now);
            var resultingChannels = _queues.Values.Select(queue => queue.RecipientChannelId).ToHashSet();
            foreach (var recipient in recipients.Where(recipient => !_queues.ContainsKey(recipient.SessionId)))
            {
                resultingChannels.Add(recipient.ChannelId);
            }

            if (resultingChannels.Count > RelaySecurityBounds.MaximumChannels ||
                _queues.Keys.Union(recipients.Select(recipient => recipient.SessionId)).Count() >
                    RelaySecurityBounds.MaximumSessions)
            {
                return RelayFramePublishResult.Reject("channel-limit");
            }
        }

        var admission = await _registry.AdvanceFrameSequenceAsync(
            principal,
            frame,
            direction,
            cancellationToken).ConfigureAwait(false);
        if (!admission.Accepted)
        {
            return RelayFramePublishResult.Reject(admission.Code);
        }

        var frameBytes = frame.CiphertextLength;
        var enqueued = 0;
        lock (_gate)
        {
            // Registry bounds guarantee that a recipient set which passed the preflight cannot
            // exceed these caps before this serialized enqueue. A revoked recipient is skipped.
            foreach (var recipient in recipients)
            {
                var live = _registry.ActiveRoute(recipient.SessionId);
                if (live is null || live.DeviceId != recipient.DeviceId)
                {
                    continue;
                }

                if (!_queues.TryGetValue(recipient.SessionId, out var queue))
                {
                    queue = new RecipientQueue(recipient.ChannelId);
                    _queues.Add(recipient.SessionId, queue);
                }

                while (queue.Frames.Count >= RelaySecurityBounds.MaximumQueuedFramesPerParticipant ||
                       queue.QueuedBytes + frameBytes > RelaySecurityBounds.MaximumQueuedBytesPerParticipant)
                {
                    if (!queue.Frames.TryDequeue(out var removed))
                    {
                        break;
                    }

                    queue.QueuedBytes -= removed.SizeBytes;
                    queue.RequiresReconnect = true;
                }

                var deliveryId = checked(++queue.LastIssuedDeliveryId);
                queue.Frames.Enqueue(new StoredFrame(
                    new RelayQueuedFrame(deliveryId, frame, now),
                    frameBytes));
                queue.QueuedBytes += frameBytes;
                enqueued++;
            }

            if (enqueued > 0)
            {
                SwapCore().TrySetResult();
            }
        }

        return enqueued == 0
            ? RelayFramePublishResult.Reject("route-rejected")
            : new RelayFramePublishResult(true, "accepted", enqueued);
    }

    public RelayFrameBatch Read(RelayPrincipal principal, long afterDeliveryId, int maximumItems = 32)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var now = Now();
        if (!RelayAuthorization.Decide(principal, RelayPermission.ReceiveOpaqueFrames, now).Allowed ||
            !_registry.IsCurrent(principal) || afterDeliveryId < 0 ||
            maximumItems is < 1 or > RelaySecurityBounds.MaximumQueuedFramesPerParticipant)
        {
            return new RelayFrameBatch(CompanionProtocolVersion.Current, [], true, now);
        }

        lock (_gate)
        {
            SweepCore(now);
            if (!_queues.TryGetValue(principal.SessionId, out var queue))
            {
                // Queues are deliberately memory-only. A nonzero cursor with no queue therefore
                // means this relay restarted or discarded delivery state; an empty batch must not
                // masquerade as continuity.
                return new RelayFrameBatch(
                    CompanionProtocolVersion.Current,
                    [],
                    afterDeliveryId != 0,
                    now);
            }

            var frames = queue.Frames
                .Select(stored => stored.Value)
                .Where(item => item.DeliveryId > afterDeliveryId)
                .Take(maximumItems)
                .ToArray();
            return new RelayFrameBatch(
                CompanionProtocolVersion.Current,
                frames,
                queue.RequiresReconnect ||
                afterDeliveryId < queue.LastAcknowledgedDeliveryId ||
                afterDeliveryId > queue.LastIssuedDeliveryId,
                now);
        }
    }

    /// <summary>
    /// Reads after <paramref name="afterDeliveryId"/>, holding the read until there is something
    /// to answer or <paramref name="wait"/> runs out.
    /// </summary>
    /// <remarks>
    /// [#604] The desktop read its queue every two seconds, so a tablet in Control moved the desk
    /// in two-second jumps. A held read comes back the moment a frame is queued. "Something to
    /// answer" is a frame, a queue that needs a reconnect, or <see cref="Wake"/> (a returning
    /// tablet's resume ticket, which rides on the owner's read). Re-authorised on every turn, the
    /// same as <c>RelayMapSurfaceStore.WaitAsync</c>.
    /// </remarks>
    public async Task<RelayFrameBatch> WaitAsync(
        RelayPrincipal principal,
        long afterDeliveryId,
        TimeSpan wait,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var batch = Read(principal, afterDeliveryId);
        if (wait <= TimeSpan.Zero || batch.Frames.Count > 0 || batch.RequiresReconnect)
        {
            return batch;
        }

        if (Interlocked.Increment(ref _waiting) > MaximumConcurrentWaits)
        {
            Interlocked.Decrement(ref _waiting);
            return batch;
        }

        var woken = Interlocked.Read(ref _wakeGeneration);
        using var expiry = new CancellationTokenSource(wait > MaximumWait ? MaximumWait : wait, _timeProvider);
        using var hold = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        try
        {
            while (true)
            {
                // Taken before the read, so a frame queued between the two is waited on, not missed.
                Task changed;
                lock (_gate)
                {
                    changed = _changed.Task;
                }

                batch = Read(principal, afterDeliveryId);
                if (batch.Frames.Count > 0 || batch.RequiresReconnect || hold.IsCancellationRequested ||
                    Interlocked.Read(ref _wakeGeneration) != woken)
                {
                    return batch;
                }

                await changed.WaitAsync(hold.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return Read(principal, afterDeliveryId);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    /// <summary>Answers every held read now, with whatever it would read.</summary>
    public void Wake()
    {
        Interlocked.Increment(ref _wakeGeneration);
        lock (_gate)
        {
            SwapCore().TrySetResult();
        }
    }

    private TaskCompletionSource SwapCore()
    {
        var woken = _changed;
        _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return woken;
    }

    public RelayAcknowledgementResult Acknowledge(RelayPrincipal principal, long deliveryId)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var now = Now();
        if (!RelayAuthorization.Decide(principal, RelayPermission.ReceiveOpaqueFrames, now).Allowed ||
            !_registry.IsCurrent(principal) || deliveryId <= 0)
        {
            return RelayAcknowledgementResult.Reject("acknowledgement-rejected");
        }

        lock (_gate)
        {
            SweepCore(now);
            if (!_queues.TryGetValue(principal.SessionId, out var queue) || queue.RequiresReconnect ||
                deliveryId <= queue.LastAcknowledgedDeliveryId || deliveryId > queue.LastIssuedDeliveryId)
            {
                return RelayAcknowledgementResult.Reject("acknowledgement-rejected");
            }

            while (queue.Frames.TryPeek(out var frame) && frame.Value.DeliveryId <= deliveryId)
            {
                queue.Frames.Dequeue();
                queue.QueuedBytes -= frame.SizeBytes;
            }

            queue.LastAcknowledgedDeliveryId = deliveryId;
            return RelayAcknowledgementResult.Permit;
        }
    }

    /// <summary>Clears a discontinuous queue after authenticated peers request a full snapshot.</summary>
    public RelayAcknowledgementResult ResetAfterReconnect(RelayPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var now = Now();
        if (!RelayAuthorization.Decide(principal, RelayPermission.ReceiveOpaqueFrames, now).Allowed ||
            !_registry.IsCurrent(principal))
        {
            return RelayAcknowledgementResult.Reject("reconnect-rejected");
        }

        lock (_gate)
        {
            SweepCore(now);
            if (!_queues.TryGetValue(principal.SessionId, out var queue))
            {
                return RelayAcknowledgementResult.Permit;
            }

            queue.Frames.Clear();
            queue.QueuedBytes = 0;
            queue.LastAcknowledgedDeliveryId = queue.LastIssuedDeliveryId;
            queue.RequiresReconnect = false;
            SwapCore().TrySetResult();
            return RelayAcknowledgementResult.Permit;
        }
    }

    /// <summary>
    /// The last delivery id this session's queue has issued, or zero with no queue: where a
    /// reader's cursor belongs once it has asked for <see cref="ResetAfterReconnect"/>.
    /// </summary>
    public long LastIssuedDeliveryId(RelayPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        lock (_gate)
        {
            return _registry.IsCurrent(principal) && _queues.TryGetValue(principal.SessionId, out var queue)
                ? queue.LastIssuedDeliveryId
                : 0;
        }
    }

    public int Sweep()
    {
        lock (_gate)
        {
            var before = _queues.Count;
            SweepCore(Now());
            return before - _queues.Count;
        }
    }

    public int ChannelCount
    {
        get
        {
            lock (_gate)
            {
                SweepCore(Now());
                return _queues.Values.Select(queue => queue.RecipientChannelId).Distinct().Count();
            }
        }
    }

    private void SweepCore(DateTimeOffset now)
    {
        foreach (var sessionId in _queues.Keys.ToArray())
        {
            var queue = _queues[sessionId];
            var route = _registry.ActiveRoute(sessionId);
            if (route is null || route.ChannelId != queue.RecipientChannelId)
            {
                _queues.Remove(sessionId);
                continue;
            }

            var count = queue.Frames.Count;
            for (var index = 0; index < count; index++)
            {
                var frame = queue.Frames.Dequeue();
                if (frame.Value.Frame.ExpiresUtc <= now)
                {
                    queue.QueuedBytes -= frame.SizeBytes;
                    queue.RequiresReconnect = true;
                }
                else
                {
                    queue.Frames.Enqueue(frame);
                }
            }
        }
    }

    private DateTimeOffset Now()
    {
        var utc = _timeProvider.GetUtcNow().ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    private sealed class RecipientQueue(RelayChannelId recipientChannelId)
    {
        public RelayChannelId RecipientChannelId { get; } = recipientChannelId;

        public Queue<StoredFrame> Frames { get; } = new();

        public int QueuedBytes { get; set; }

        public long LastIssuedDeliveryId { get; set; }

        public long LastAcknowledgedDeliveryId { get; set; }

        public bool RequiresReconnect { get; set; }
    }

    private sealed record StoredFrame(RelayQueuedFrame Value, int SizeBytes);
}
