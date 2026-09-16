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

/// <summary>Routes only bounded #276 ciphertext while the desktop remains canonical.</summary>
/// <remarks>
/// The relay never deserializes the ciphertext and cannot mint canonical acknowledgements. It
/// validates only authenticated routing metadata, expiry, protocol version, sequence replay, and
/// resource bounds. Overflow is surfaced as a reconnect requirement instead of pretending a
/// discontinuous queue is complete.
/// </remarks>
public sealed class OpaqueRelayFrameHub
{
    private readonly RelayDeviceRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<RelayChannelId, Channel> _channels = [];

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
        if (!RelayAuthorization.Decide(principal, RelayPermission.PublishOpaqueFrames).Allowed)
        {
            return RelayFramePublishResult.Reject("not-authorized");
        }

        var now = Now();
        if (frame.ChannelId != principal.ChannelId ||
            !CompanionProtocolVersion.Current.CanRead(frame.ProtocolVersion) ||
            frame.IssuedUtc > now.Add(RelaySecurityBounds.ClockSkew) ||
            frame.ExpiresUtc <= now ||
            !TryMeasure(frame, out var frameBytes))
        {
            return RelayFramePublishResult.Reject(
                !CompanionProtocolVersion.Current.CanRead(frame.ProtocolVersion)
                    ? "unsupported-version"
                    : "frame-rejected");
        }

        var routes = _registry.ActiveRoutes(principal.ChannelId);
        var target = routes.SingleOrDefault(route => route.SessionId == frame.SessionId);
        if (principal.Role != DeviceAuthorizationRole.Owner && frame.SessionId != principal.SessionId)
        {
            return RelayFramePublishResult.Reject("not-authorized");
        }

        if (principal.Role == DeviceAuthorizationRole.Owner && frame.SessionId != principal.SessionId &&
            (target is null || target.Role == DeviceAuthorizationRole.Owner))
        {
            return RelayFramePublishResult.Reject("route-rejected");
        }

        var sequence = await _registry.AdvanceFrameSequenceAsync(
            principal,
            frame.KeyEpoch,
            frame.SenderSequence,
            cancellationToken).ConfigureAwait(false);
        if (!sequence.Accepted)
        {
            return RelayFramePublishResult.Reject(sequence.Code);
        }

        var recipients = principal.Role == DeviceAuthorizationRole.Owner && target is not null &&
            frame.SessionId != principal.SessionId
            ? new[] { target }
            : routes.Where(route => route.DeviceId != principal.DeviceId &&
                                    route.Role == DeviceAuthorizationRole.Owner).ToArray();
        lock (_gate)
        {
            SweepCore(now, principal.ChannelId, routes);
            if (!_channels.TryGetValue(principal.ChannelId, out var channel))
            {
                if (_channels.Count >= RelaySecurityBounds.MaximumChannels)
                {
                    return RelayFramePublishResult.Reject("channel-limit");
                }

                channel = new Channel();
                _channels.Add(principal.ChannelId, channel);
            }

            foreach (var recipient in recipients.Take(RelaySecurityBounds.MaximumChannelParticipants))
            {
                if (!channel.Queues.TryGetValue(recipient.SessionId, out var queue))
                {
                    if (channel.Queues.Count >= RelaySecurityBounds.MaximumChannelParticipants)
                    {
                        continue;
                    }

                    queue = new RecipientQueue();
                    channel.Queues.Add(recipient.SessionId, queue);
                }

                while (queue.Frames.Count >= RelaySecurityBounds.MaximumQueuedFramesPerParticipant ||
                       queue.QueuedBytes + frameBytes > RelaySecurityBounds.MaximumQueuedBytesPerParticipant)
                {
                    if (queue.Frames.Count == 0)
                    {
                        break;
                    }

                    var removed = queue.Frames.Dequeue();
                    queue.QueuedBytes -= removed.SizeBytes;
                    queue.RequiresReconnect = true;
                }

                var delivery = checked(++queue.LastIssuedDeliveryId);
                queue.Frames.Enqueue(new StoredFrame(
                    new RelayQueuedFrame(delivery, frame, now),
                    frameBytes));
                queue.QueuedBytes += frameBytes;
            }
        }

        return new RelayFramePublishResult(true, "accepted", recipients.Length);
    }

    public RelayFrameBatch Read(RelayPrincipal principal, long afterDeliveryId, int maximumItems = 32)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!RelayAuthorization.Decide(principal, RelayPermission.ReceiveOpaqueFrames).Allowed ||
            afterDeliveryId < 0 || maximumItems is < 1 or > RelaySecurityBounds.MaximumQueuedFramesPerParticipant)
        {
            return new RelayFrameBatch(CompanionProtocolVersion.Current, [], true, Now());
        }

        var now = Now();
        var routes = _registry.ActiveRoutes(principal.ChannelId);
        if (!routes.Any(route => route.SessionId == principal.SessionId && route.DeviceId == principal.DeviceId))
        {
            return new RelayFrameBatch(CompanionProtocolVersion.Current, [], true, now);
        }

        lock (_gate)
        {
            SweepCore(now, principal.ChannelId, routes);
            if (!_channels.TryGetValue(principal.ChannelId, out var channel) ||
                !channel.Queues.TryGetValue(principal.SessionId, out var queue))
            {
                return new RelayFrameBatch(CompanionProtocolVersion.Current, [], false, now);
            }

            var frames = queue.Frames
                .Select(stored => stored.Value)
                .Where(item => item.DeliveryId > afterDeliveryId)
                .Take(maximumItems)
                .ToArray();
            return new RelayFrameBatch(
                CompanionProtocolVersion.Current,
                frames,
                queue.RequiresReconnect || afterDeliveryId < queue.LastAcknowledgedDeliveryId,
                now);
        }
    }

    public RelayAcknowledgementResult Acknowledge(RelayPrincipal principal, long deliveryId)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!RelayAuthorization.Decide(principal, RelayPermission.ReceiveOpaqueFrames).Allowed || deliveryId <= 0)
        {
            return RelayAcknowledgementResult.Reject("acknowledgement-rejected");
        }

        var routes = _registry.ActiveRoutes(principal.ChannelId);
        if (!routes.Any(route => route.SessionId == principal.SessionId && route.DeviceId == principal.DeviceId))
        {
            return RelayAcknowledgementResult.Reject("acknowledgement-rejected");
        }

        lock (_gate)
        {
            SweepCore(Now(), principal.ChannelId, routes);
            if (!_channels.TryGetValue(principal.ChannelId, out var channel) ||
                !channel.Queues.TryGetValue(principal.SessionId, out var queue) ||
                queue.RequiresReconnect ||
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
            if (queue.Frames.Count == 0)
            {
                queue.RequiresReconnect = false;
            }

            return RelayAcknowledgementResult.Permit;
        }
    }

    /// <summary>Clears a discontinuous queue after the authenticated peers choose a full snapshot.</summary>
    public RelayAcknowledgementResult ResetAfterReconnect(RelayPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var routes = _registry.ActiveRoutes(principal.ChannelId);
        if (!routes.Any(route => route.SessionId == principal.SessionId && route.DeviceId == principal.DeviceId))
        {
            return RelayAcknowledgementResult.Reject("reconnect-rejected");
        }

        lock (_gate)
        {
            if (!_channels.TryGetValue(principal.ChannelId, out var channel) ||
                !channel.Queues.TryGetValue(principal.SessionId, out var queue))
            {
                return RelayAcknowledgementResult.Permit;
            }

            queue.Frames.Clear();
            queue.QueuedBytes = 0;
            queue.LastAcknowledgedDeliveryId = queue.LastIssuedDeliveryId;
            queue.RequiresReconnect = false;
            return RelayAcknowledgementResult.Permit;
        }
    }

    public int Sweep()
    {
        lock (_gate)
        {
            var before = _channels.Count;
            foreach (var channelId in _channels.Keys.ToArray())
            {
                SweepCore(Now(), channelId, _registry.ActiveRoutes(channelId));
            }

            return before - _channels.Count;
        }
    }

    public int ChannelCount
    {
        get
        {
            lock (_gate)
            {
                return _channels.Count;
            }
        }
    }

    private void SweepCore(
        DateTimeOffset now,
        RelayChannelId channelId,
        IReadOnlyCollection<RelaySessionRoute> routes)
    {
        var live = routes.Where(route => route.ExpiresUtc > now).Select(route => route.SessionId).ToHashSet();
        if (!_channels.TryGetValue(channelId, out var channel))
        {
            return;
        }

        foreach (var sessionId in channel.Queues.Keys.Where(sessionId => !live.Contains(sessionId)).ToArray())
        {
            channel.Queues.Remove(sessionId);
        }

        foreach (var queue in channel.Queues.Values)
        {
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

        if (channel.Queues.Count == 0)
        {
            _channels.Remove(channelId);
        }
    }

    private static bool TryMeasure(OpaqueRelayFrame frame, out int bytes)
    {
        try
        {
            bytes = frame.CiphertextChunksBase64Url.Sum(chunk =>
                RelayCsrfProtector.DecodeBase64Url(chunk).Length);
            return bytes is > 0 and <= RelaySecurityBounds.MaximumRequestBytes;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            bytes = 0;
            return false;
        }
    }

    private DateTimeOffset Now()
    {
        var utc = _timeProvider.GetUtcNow().ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    private sealed class Channel
    {
        public Dictionary<DeviceSessionId, RecipientQueue> Queues { get; } = [];
    }

    private sealed class RecipientQueue
    {
        public Queue<StoredFrame> Frames { get; } = new();

        public int QueuedBytes { get; set; }

        public long LastIssuedDeliveryId { get; set; }

        public long LastAcknowledgedDeliveryId { get; set; }

        public bool RequiresReconnect { get; set; }
    }

    private sealed record StoredFrame(RelayQueuedFrame Value, int SizeBytes);
}
