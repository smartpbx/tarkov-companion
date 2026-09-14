using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.CompanionProtocol;

public sealed record DeliveryChannelKey(CompanionDeviceId DeviceId, CanonicalAggregateKind Aggregate)
{
    public CompanionDeviceId DeviceId { get; } = DeviceId.Value == Guid.Empty
        ? throw new ArgumentException("A delivery device is required.", nameof(DeviceId))
        : DeviceId;

    public CanonicalAggregateKind Aggregate { get; } = ProtocolGuard.Defined(Aggregate, nameof(Aggregate));
}

public sealed record DeliveryItem
{
    public DeliveryItem(DeliverySequence sequence, CanonicalUpdate? update, bool snapshotRequired)
    {
        Sequence = sequence.Value > 0 ? sequence : throw new ArgumentOutOfRangeException(nameof(sequence));
        Update = update;
        SnapshotRequired = snapshotRequired;
        if (snapshotRequired == (update is not null))
        {
            throw new ArgumentException("A delivery is either one update or a snapshot-required marker.");
        }
    }

    public DeliverySequence Sequence { get; }

    public CanonicalUpdate? Update { get; }

    public bool SnapshotRequired { get; }
}

public sealed record DeliveryChannelState
{
    public DeliveryChannelState(
        DeliveryChannelKey key,
        DeliverySequence lastAcknowledged,
        IReadOnlyList<DeliveryItem> pending)
    {
        Key = ProtocolGuard.NotNull(key, nameof(key));
        LastAcknowledged = lastAcknowledged;
        Pending = ProtocolGuard.List(pending, nameof(pending), ProtocolBounds.MaxDeliveryItemsPerAggregate);
        if (Pending.Any(item => item.Sequence.Value <= lastAcknowledged.Value) ||
            !Pending.Select(item => item.Sequence).SequenceEqual(Pending.Select(item => item.Sequence).OrderBy(value => value.Value)) ||
            Pending.Any(item => item.Update is not null && item.Update.Aggregate != key.Aggregate))
        {
            throw new ArgumentException("Delivery entries are ordered, unacknowledged, and belong to their channel.", nameof(pending));
        }
    }

    public DeliveryChannelKey Key { get; }

    public DeliverySequence LastAcknowledged { get; }

    public IReadOnlyList<DeliveryItem> Pending { get; }
}

public sealed record DeviceDeliveryCounter(CompanionDeviceId DeviceId, DeliverySequence LastAssigned)
{
    public CompanionDeviceId DeviceId { get; } = DeviceId.Value == Guid.Empty
        ? throw new ArgumentException("A delivery device is required.", nameof(DeviceId))
        : DeviceId;

    public DeliverySequence LastAssigned { get; } = LastAssigned.Value > 0
        ? LastAssigned
        : throw new ArgumentOutOfRangeException(nameof(LastAssigned));
}

public sealed record DeliveryLedger
{
    public DeliveryLedger(
        IReadOnlyList<DeviceDeliveryCounter> deviceCounters,
        IReadOnlyList<DeliveryChannelState> channels)
    {
        DeviceCounters = ProtocolGuard.List(deviceCounters, nameof(deviceCounters), ProtocolBounds.MaxDevices);
        Channels = ProtocolGuard.List(
            channels,
            nameof(channels),
            ProtocolBounds.MaxDevices * Enum.GetValues<CanonicalAggregateKind>().Length);
        if (DeviceCounters.Select(item => item.DeviceId).Distinct().Count() != DeviceCounters.Count ||
            Channels.Select(item => item.Key).Distinct().Count() != Channels.Count)
        {
            throw new ArgumentException("Delivery counters and channels are unique.");
        }
    }

    public IReadOnlyList<DeviceDeliveryCounter> DeviceCounters { get; }

    public IReadOnlyList<DeliveryChannelState> Channels { get; }

    public static DeliveryLedger Empty { get; } = new([], []);

    public DeliveryEnqueueResult Enqueue(CompanionDeviceId deviceId, CanonicalUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var counters = DeviceCounters.ToList();
        var counterIndex = counters.FindIndex(item => item.DeviceId == deviceId);
        var sequence = counterIndex < 0
            ? new DeliverySequence(1)
            : counters[counterIndex].LastAssigned.Next();
        if (counterIndex < 0)
        {
            if (counters.Count >= ProtocolBounds.MaxDevices)
            {
                throw new InvalidOperationException("The delivery device bound has been reached.");
            }

            counters.Add(new DeviceDeliveryCounter(deviceId, sequence));
        }
        else
        {
            counters[counterIndex] = new DeviceDeliveryCounter(deviceId, sequence);
        }

        var channels = Channels.ToList();
        var key = new DeliveryChannelKey(deviceId, update.Aggregate);
        var channelIndex = channels.FindIndex(item => item.Key == key);
        var prior = channelIndex < 0
            ? new DeliveryChannelState(key, new DeliverySequence(0), [])
            : channels[channelIndex];
        var mustSnapshot = prior.Pending.Count >= ProtocolBounds.MaxDeliveryItemsPerAggregate ||
                           prior.Pending.Any(item => item.SnapshotRequired);
        var item = new DeliveryItem(sequence, mustSnapshot ? null : update, mustSnapshot);
        var pending = mustSnapshot ? [item] : prior.Pending.Append(item).ToArray();
        var nextChannel = new DeliveryChannelState(key, prior.LastAcknowledged, pending);
        if (channelIndex < 0)
        {
            channels.Add(nextChannel);
        }
        else
        {
            channels[channelIndex] = nextChannel;
        }

        return new DeliveryEnqueueResult(new DeliveryLedger(counters, channels), item);
    }

    public DeliveryLedger Acknowledge(
        CompanionDeviceId deviceId,
        CanonicalAggregateKind aggregate,
        DeliverySequence through)
    {
        var channels = Channels.ToList();
        var key = new DeliveryChannelKey(deviceId, aggregate);
        var index = channels.FindIndex(item => item.Key == key);
        if (index < 0 || through.Value <= channels[index].LastAcknowledged.Value)
        {
            return this;
        }

        var current = channels[index];
        var maximumAssigned = DeviceCounters.FirstOrDefault(item => item.DeviceId == deviceId)?.LastAssigned.Value ?? 0;
        var boundedThrough = new DeliverySequence(Math.Min(through.Value, maximumAssigned));
        channels[index] = new DeliveryChannelState(
            key,
            boundedThrough,
            current.Pending.Where(item => item.Sequence.Value > boundedThrough.Value).ToArray());
        return new DeliveryLedger(DeviceCounters, channels);
    }

    public IReadOnlyList<DeliveryItem> PendingFor(CompanionDeviceId deviceId) =>
        ProtocolGuard.List(
            Channels.Where(channel => channel.Key.DeviceId == deviceId)
                .SelectMany(channel => channel.Pending)
                .OrderBy(item => item.Sequence.Value),
            nameof(deviceId),
            ProtocolBounds.MaxDeliveryItemsPerAggregate * Enum.GetValues<CanonicalAggregateKind>().Length);
}

public sealed record DeliveryEnqueueResult(DeliveryLedger Ledger, DeliveryItem Item);
