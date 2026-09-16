using System.Collections.ObjectModel;
using System.Text;
using TarkovCompanion.CompanionProtocol;

namespace TarkovCompanion.Application.Services.Devices;

/// <summary>
/// The one crash-consistent record owned by the desktop paired-device authority.
/// </summary>
/// <remarks>
/// Canonical state and its delivery ledger must be committed together. Persisting either one on
/// its own can make a reconnect acknowledge a change the desktop forgot, or replay a sequence for
/// state it never committed. Session traffic keys are deliberately absent and remain memory-only.
/// </remarks>
public sealed record DesktopCompanionAuthorityState
{
    public const int MaximumRetainedSessions = ProtocolBounds.MaxDevices * 4;

    public DesktopCompanionAuthorityState(
        CanonicalCompanionState canonicalState,
        IReadOnlyList<PairedDevice> devices,
        IReadOnlyList<DeviceSession> sessions,
        DeliveryLedger deliveryLedger)
    {
        CanonicalState = canonicalState ?? throw new ArgumentNullException(nameof(canonicalState));
        Devices = Copy(devices, nameof(devices), ProtocolBounds.MaxDevices);
        Sessions = Copy(sessions, nameof(sessions), MaximumRetainedSessions);
        DeliveryLedger = deliveryLedger ?? throw new ArgumentNullException(nameof(deliveryLedger));

        if (Devices.Select(device => device.DeviceId).Distinct().Count() != Devices.Count)
        {
            throw new ArgumentException("A paired device id is unique.", nameof(devices));
        }

        if (Devices.Select(device => device.DeviceKey.KeyId).Distinct().Count() != Devices.Count)
        {
            throw new ArgumentException("A bound device key belongs to one paired device.", nameof(devices));
        }

        if (Sessions.Select(session => session.SessionId).Distinct().Count() != Sessions.Count)
        {
            throw new ArgumentException("A device session id is unique.", nameof(sessions));
        }

        var devicesById = Devices.ToDictionary(device => device.DeviceId);
        if (Sessions.Any(session =>
                !devicesById.TryGetValue(session.DeviceId, out var device) ||
                session.DeviceKeyId != device.DeviceKey.KeyId))
        {
            throw new ArgumentException("Every retained session is bound to its paired device key.", nameof(sessions));
        }

        if (Sessions
            .Where(session => session.Status == DeviceSessionStatus.Active)
            .GroupBy(session => session.DeviceId)
            .Any(group => group.Count() != 1))
        {
            throw new ArgumentException("A device has at most one active session; resume replaces the prior tab.", nameof(sessions));
        }

        if (DeliveryLedger.Devices.Any(stream => !devicesById.ContainsKey(stream.DeviceId)))
        {
            throw new ArgumentException("A delivery stream belongs to a retained paired device.", nameof(deliveryLedger));
        }

        var knownDeviceIds = devicesById.Keys.ToHashSet();
        if (CanonicalState.DeviceModes.Devices.Any(mode => !knownDeviceIds.Contains(mode.DeviceId)) ||
            (CanonicalState.DeviceModes.PendingControl is { } pending && !knownDeviceIds.Contains(pending.DeviceId)) ||
            (CanonicalState.DeviceModes.ControlLease is { } lease && !knownDeviceIds.Contains(lease.DeviceId)))
        {
            throw new ArgumentException("Canonical device modes cannot name an unknown paired device.", nameof(canonicalState));
        }
    }

    public CanonicalCompanionState CanonicalState { get; }

    public IReadOnlyList<PairedDevice> Devices { get; }

    public IReadOnlyList<DeviceSession> Sessions { get; }

    public DeliveryLedger DeliveryLedger { get; }

    public static DesktopCompanionAuthorityState Create(CanonicalCompanionState canonicalState) =>
        new(canonicalState, [], [], DeliveryLedger.Empty);

    public DesktopCompanionAuthorityState With(
        CanonicalCompanionState? canonicalState = null,
        IReadOnlyList<PairedDevice>? devices = null,
        IReadOnlyList<DeviceSession>? sessions = null,
        DeliveryLedger? deliveryLedger = null) =>
        new(
            canonicalState ?? CanonicalState,
            devices ?? Devices,
            sessions ?? Sessions,
            deliveryLedger ?? DeliveryLedger);

    private static ReadOnlyCollection<T> Copy<T>(IReadOnlyList<T> values, string parameterName, int maximum)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"At most {maximum} entries may be retained.");
        }

        var copy = values.ToArray();
        if (copy.Any(value => value is null))
        {
            throw new ArgumentException("A persisted authority collection cannot contain null entries.", parameterName);
        }

        return Array.AsReadOnly(copy);
    }
}

/// <summary>
/// Loads and atomically replaces the complete desktop authority record.
/// </summary>
public interface IDesktopCompanionAuthorityStore
{
    ValueTask<DesktopCompanionAuthorityState?> LoadAsync(CancellationToken cancellationToken);

    ValueTask SaveAsync(DesktopCompanionAuthorityState state, CancellationToken cancellationToken);
}

/// <summary>
/// Proof from a transport adapter that it opened a frame with this live session's traffic key.
/// Device identity and capabilities are resolved from persisted records rather than client JSON.
/// </summary>
public sealed record AuthenticatedPairedFrame
{
    public AuthenticatedPairedFrame(
        DeviceSessionId sessionId,
        long keyEpoch,
        string clientInstanceId,
        DateTimeOffset receivedUtc)
    {
        SessionId = sessionId.Value == Guid.Empty
            ? throw new ArgumentException("An authenticated session id is required.", nameof(sessionId))
            : sessionId;
        KeyEpoch = keyEpoch is > 0 and <= ProtocolBounds.MaxKeyEpoch
            ? keyEpoch
            : throw new ArgumentOutOfRangeException(nameof(keyEpoch));
        var instance = clientInstanceId?.Trim();
        ClientInstanceId = string.IsNullOrEmpty(instance) || Encoding.UTF8.GetByteCount(instance) > ProtocolBounds.MaxShortStringBytes
            ? throw new ArgumentException("A bounded client instance id is required.", nameof(clientInstanceId))
            : instance;
        ReceivedUtc = receivedUtc == default || receivedUtc.Offset != TimeSpan.Zero ||
                      receivedUtc.Ticks % TimeSpan.TicksPerMillisecond != 0
            ? throw new ArgumentException("A millisecond-precision UTC receive time is required.", nameof(receivedUtc))
            : receivedUtc;
    }

    public DeviceSessionId SessionId { get; }

    public long KeyEpoch { get; }

    public string ClientInstanceId { get; }

    public DateTimeOffset ReceivedUtc { get; }
}

public sealed record AuthorityDelivery(CompanionDeviceId DeviceId, DeliveryItem Item);

public sealed record AuthorityMutation(
    DesktopCompanionAuthorityState State,
    IReadOnlyList<AuthorityDelivery> Deliveries);

public sealed record PairedCommandApplication(
    DesktopCompanionAuthorityState State,
    CommandAcknowledgement Acknowledgement,
    IReadOnlyList<AuthorityDelivery> Deliveries);

public sealed record DeliveryAcknowledgementApplication(
    DesktopCompanionAuthorityState State,
    bool Accepted,
    string Code);

public sealed record ReconnectApplication(
    DesktopCompanionAuthorityState State,
    ReconnectPlan Plan);
