using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.CompanionProtocol;

/// <summary>An independently bounded queue inside one device's delivery stream.</summary>
public enum DeliveryChannel
{
    DeviceModes = 1,
    Workspace,
    Marks,
    CaptureIntent,

    /// <summary>Command acknowledgements, snapshots, and deprecation notices.</summary>
    Control,
}

/// <summary>
/// One sequenced delivery to one device: a server message, or a marker that the channel overflowed
/// and the transport must send a current canonical snapshot at this sequence instead.
/// </summary>
public sealed record DeliveryItem
{
    public DeliveryItem(
        DeliverySequence sequence,
        DeliveryChannel channel,
        DateTimeOffset enqueuedUtc,
        ServerMessage? message,
        bool snapshotRequired)
    {
        Sequence = sequence.Value > 0 ? sequence : throw new ArgumentOutOfRangeException(nameof(sequence));
        Channel = ProtocolGuard.Defined(channel, nameof(channel));
        EnqueuedUtc = ProtocolGuard.Utc(enqueuedUtc, nameof(enqueuedUtc));
        Message = message;
        SnapshotRequired = snapshotRequired;
        if (snapshotRequired == (message is not null) ||
            (message is not null && DeliveryLedger.ChannelOf(message) != channel))
        {
            throw new ArgumentException("A delivery is one message on its own channel or a snapshot-required marker.");
        }
    }

    public DeliverySequence Sequence { get; }

    public DeliveryChannel Channel { get; }

    public DateTimeOffset EnqueuedUtc { get; }

    public ServerMessage? Message { get; }

    public bool SnapshotRequired { get; }

    /// <summary>The message to send at this sequence; a coalesced marker becomes the current snapshot.</summary>
    public ServerMessage Resolve(CanonicalCompanionState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return Message ?? new CanonicalSnapshotMessage(current);
    }
}

/// <summary>
/// One device's single ordered delivery stream. Every server envelope to the device consumes the
/// next sequence, whatever channel it belongs to, so a missing sequence is always detectable.
/// </summary>
public sealed record DeviceDeliveryState
{
    public DeviceDeliveryState(
        CompanionDeviceId deviceId,
        DeliverySequence lastAssigned,
        DeliverySequence lastAcknowledged,
        IReadOnlyList<DeliveryItem> pending)
    {
        DeviceId = deviceId.Value == Guid.Empty
            ? throw new ArgumentException("A delivery device is required.", nameof(deviceId))
            : deviceId;
        LastAssigned = lastAssigned;
        LastAcknowledged = lastAcknowledged.Value <= lastAssigned.Value
            ? lastAcknowledged
            : throw new ArgumentOutOfRangeException(nameof(lastAcknowledged), "A device cannot acknowledge an unassigned sequence.");
        Pending = ProtocolGuard.List(
            pending,
            nameof(pending),
            ProtocolBounds.MaxDeliveryItemsPerChannel * Enum.GetValues<DeliveryChannel>().Length);

        for (var index = 0; index < Pending.Count; index++)
        {
            var sequence = Pending[index].Sequence.Value;
            if (sequence <= lastAcknowledged.Value || sequence > lastAssigned.Value ||
                (index > 0 && sequence <= Pending[index - 1].Sequence.Value))
            {
                throw new ArgumentException("Pending deliveries are ascending, assigned, and unacknowledged.", nameof(pending));
            }
        }

        if (Pending.GroupBy(item => item.Channel).Any(group =>
                group.Count() > ProtocolBounds.MaxDeliveryItemsPerChannel ||
                (group.Any(item => item.SnapshotRequired) && group.Count() != 1)))
        {
            throw new ArgumentException("A channel holds at most 64 deliveries, or exactly one snapshot marker.", nameof(pending));
        }
    }

    public CompanionDeviceId DeviceId { get; }

    public DeliverySequence LastAssigned { get; }

    public DeliverySequence LastAcknowledged { get; }

    public IReadOnlyList<DeliveryItem> Pending { get; }
}

/// <summary>
/// Isolated backpressure for every paired device. A full channel coalesces to one snapshot marker
/// for that device and channel; other channels of the device and every other device keep
/// accepting deliveries, so a slow tablet cannot block unrelated state.
/// </summary>
public sealed record DeliveryLedger
{
    public DeliveryLedger(IReadOnlyList<DeviceDeliveryState> devices)
    {
        Devices = ProtocolGuard.List(devices, nameof(devices), ProtocolBounds.MaxDevices);
        if (Devices.Select(item => item.DeviceId).Distinct().Count() != Devices.Count)
        {
            throw new ArgumentException("A device has one delivery stream.", nameof(devices));
        }
    }

    public IReadOnlyList<DeviceDeliveryState> Devices { get; }

    public static DeliveryLedger Empty { get; } = new([]);

    public static DeliveryChannel ChannelOf(ServerMessage message) => message switch
    {
        CanonicalUpdateMessage update => (DeliveryChannel)update.Update.Aggregate,
        CommandAcknowledgementMessage or CanonicalSnapshotMessage or DeprecationMessage => DeliveryChannel.Control,
        null => throw new ArgumentNullException(nameof(message)),
        _ => throw new ArgumentOutOfRangeException(nameof(message)),
    };

    public DeviceDeliveryState? For(CompanionDeviceId deviceId) =>
        Devices.FirstOrDefault(item => item.DeviceId == deviceId);

    public DeliveryEnqueueResult Enqueue(CompanionDeviceId deviceId, ServerMessage message, DateTimeOffset enqueuedUtc)
    {
        ArgumentNullException.ThrowIfNull(message);
        var devices = Devices.ToList();
        var index = devices.FindIndex(item => item.DeviceId == deviceId);
        if (index < 0 && devices.Count >= ProtocolBounds.MaxDevices)
        {
            throw new InvalidOperationException("The delivery device bound has been reached.");
        }

        var prior = index < 0
            ? new DeviceDeliveryState(deviceId, new DeliverySequence(0), new DeliverySequence(0), [])
            : devices[index];
        var channel = ChannelOf(message);
        var sequence = prior.LastAssigned.Next();
        var sameChannel = prior.Pending.Where(existing => existing.Channel == channel).ToArray();
        var coalesce = sameChannel.Length >= ProtocolBounds.MaxDeliveryItemsPerChannel ||
                       sameChannel.Any(existing => existing.SnapshotRequired);
        var delivery = coalesce
            ? new DeliveryItem(sequence, channel, enqueuedUtc, null, snapshotRequired: true)
            : new DeliveryItem(sequence, channel, enqueuedUtc, message, snapshotRequired: false);
        var pending = coalesce
            ? prior.Pending.Where(existing => existing.Channel != channel).Append(delivery)
            : prior.Pending.Append(delivery);
        var next = new DeviceDeliveryState(deviceId, sequence, prior.LastAcknowledged, pending.ToArray());
        if (index < 0)
        {
            devices.Add(next);
        }
        else
        {
            devices[index] = next;
        }

        return new DeliveryEnqueueResult(new DeliveryLedger(devices), delivery);
    }

    /// <summary>Acknowledges the device stream through one sequence; an old acknowledgement is ignored.</summary>
    public DeliveryLedger Acknowledge(CompanionDeviceId deviceId, DeliverySequence through)
    {
        var devices = Devices.ToList();
        var index = devices.FindIndex(item => item.DeviceId == deviceId);
        if (index < 0 || through.Value <= devices[index].LastAcknowledged.Value)
        {
            return this;
        }

        var current = devices[index];
        var bounded = new DeliverySequence(Math.Min(through.Value, current.LastAssigned.Value));
        devices[index] = new DeviceDeliveryState(
            deviceId,
            current.LastAssigned,
            bounded,
            current.Pending.Where(item => item.Sequence.Value > bounded.Value).ToArray());
        return new DeliveryLedger(devices);
    }

    /// <summary>
    /// Applies a live client acknowledgement. One from another authority lifetime, or one claiming
    /// a revision or change the desktop does not hold, is refused and the client must resynchronize.
    /// </summary>
    public DeliveryAcknowledgementResult Acknowledge(
        CompanionDeviceId deviceId,
        ClientDeliveryAcknowledgement acknowledgement,
        CanonicalCompanionState canonical)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        ArgumentNullException.ThrowIfNull(canonical);
        if (acknowledgement.AuthorityEpoch != canonical.AuthorityEpoch)
        {
            return new DeliveryAcknowledgementResult(this, false, "authority-epoch-mismatch");
        }

        if (acknowledgement.GlobalRevision.Value > canonical.GlobalRevision.Value ||
            !AggregateAcknowledgement.AgreeWith(canonical, acknowledgement.AggregateAcknowledgements))
        {
            return new DeliveryAcknowledgementResult(this, false, "acknowledged-state-diverges");
        }

        var state = For(deviceId);
        if (state is null || acknowledgement.ThroughDeliverySequence.Value > state.LastAssigned.Value)
        {
            return new DeliveryAcknowledgementResult(this, false, "delivery-sequence-not-assigned");
        }

        return new DeliveryAcknowledgementResult(Acknowledge(deviceId, acknowledgement.ThroughDeliverySequence), true, "acknowledged");
    }

    public IReadOnlyList<DeliveryItem> PendingFor(CompanionDeviceId deviceId) =>
        For(deviceId)?.Pending ?? [];

    /// <summary>Drops a revoked, expired, or replaced device's stream.</summary>
    public DeliveryLedger RemoveDevice(CompanionDeviceId deviceId) =>
        new(Devices.Where(item => item.DeviceId != deviceId).ToArray());
}

public sealed record DeliveryEnqueueResult(DeliveryLedger Ledger, DeliveryItem Item);

public sealed record DeliveryAcknowledgementResult(DeliveryLedger Ledger, bool Accepted, string Code);
