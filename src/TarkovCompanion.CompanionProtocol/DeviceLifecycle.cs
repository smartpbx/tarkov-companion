using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.CompanionProtocol;

public enum DeviceAuthorizationRole
{
    Owner = 1,
    Member,
    Observer,
}

public enum DeviceCapability
{
    FollowDesktop = 1,
    RequestControl,
    ShowOnDesktop,
    ManageOwnMarks,
    PublishTeamMarks,
    RequestCaptureIntent,
    ReviewCaptureResult,
    ManageDevices,
    ResolveControlRequests,
    ReportCaptureProgress,
}

public enum DeviceLifecycleStatus
{
    Active = 1,
    Revoked,
    Expired,
    Replaced,
}

public enum DeviceSessionStatus
{
    Active = 1,
    Closed,
    Revoked,
    Expired,
    Replaced,
}

public enum CompanionTransportKind
{
    DirectLan = 1,
    EndToEndRelay,
}

public enum CompanionSurfaceKind
{
    Desktop = 1,
    TabletPortrait,
    TabletLandscape,
    NarrowPhone,
    DesktopBrowser,
}

public sealed record PairedDevice
{
    public PairedDevice(
        CompanionDeviceId deviceId,
        string displayName,
        DevicePublicKey deviceKey,
        DeviceAuthorizationRole role,
        IReadOnlyList<DeviceCapability> capabilities,
        DeviceLifecycleStatus status,
        DateTimeOffset createdUtc,
        DateTimeOffset lastUsedUtc,
        DateTimeOffset expiresUtc,
        DateTimeOffset statusChangedUtc,
        CompanionDeviceId? replacedByDeviceId = null,
        string? lifecycleReason = null)
    {
        DeviceId = deviceId.Value == Guid.Empty
            ? throw new ArgumentException("A device id is required.", nameof(deviceId))
            : deviceId;
        DisplayName = ProtocolGuard.Required(displayName, nameof(displayName), ProtocolBounds.MaxShortStringBytes);
        DeviceKey = ProtocolGuard.NotNull(deviceKey, nameof(deviceKey));
        Role = ProtocolGuard.Defined(role, nameof(role));
        Capabilities = ProtocolGuard.List(
            ProtocolGuard.List(capabilities, nameof(capabilities), 32).Distinct(),
            nameof(capabilities),
            32);
        Status = ProtocolGuard.Defined(status, nameof(status));
        CreatedUtc = ProtocolGuard.Utc(createdUtc, nameof(createdUtc));
        LastUsedUtc = ProtocolGuard.Utc(lastUsedUtc, nameof(lastUsedUtc));
        ExpiresUtc = ProtocolGuard.Utc(expiresUtc, nameof(expiresUtc));
        StatusChangedUtc = ProtocolGuard.Utc(statusChangedUtc, nameof(statusChangedUtc));
        ReplacedByDeviceId = replacedByDeviceId;
        LifecycleReason = ProtocolGuard.Optional(lifecycleReason, nameof(lifecycleReason));

        if (LastUsedUtc < CreatedUtc || ExpiresUtc <= CreatedUtc || StatusChangedUtc < CreatedUtc)
        {
            throw new ArgumentException("Device timestamps are not monotonic.");
        }

        if ((status == DeviceLifecycleStatus.Replaced) != (replacedByDeviceId is not null))
        {
            throw new ArgumentException("Only a replaced device names its replacement.", nameof(replacedByDeviceId));
        }

        if (replacedByDeviceId is { } replacement && replacement.Value == Guid.Empty)
        {
            throw new ArgumentException("A replacement device id is required.", nameof(replacedByDeviceId));
        }

        if (replacedByDeviceId == deviceId)
        {
            throw new ArgumentException("A device cannot replace itself.", nameof(replacedByDeviceId));
        }

        if (status != DeviceLifecycleStatus.Active && StatusChangedUtc < LastUsedUtc)
        {
            throw new ArgumentException("A terminal lifecycle change cannot precede the last authenticated use.");
        }
    }

    public CompanionDeviceId DeviceId { get; }

    public string DisplayName { get; }

    public DevicePublicKey DeviceKey { get; }

    public DeviceAuthorizationRole Role { get; }

    public IReadOnlyList<DeviceCapability> Capabilities { get; }

    public DeviceLifecycleStatus Status { get; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset LastUsedUtc { get; }

    public DateTimeOffset ExpiresUtc { get; }

    public DateTimeOffset StatusChangedUtc { get; }

    public CompanionDeviceId? ReplacedByDeviceId { get; }

    public string? LifecycleReason { get; }
}

public sealed record DeviceSession
{
    public DeviceSession(
        DeviceSessionId sessionId,
        CompanionDeviceId deviceId,
        DeviceKeyId deviceKeyId,
        DeviceSessionStatus status,
        CompanionTransportKind transport,
        CompanionSurfaceKind surface,
        IReadOnlyList<DeviceCapability> capabilities,
        DateTimeOffset createdUtc,
        DateTimeOffset lastUsedUtc,
        DateTimeOffset expiresUtc,
        DateTimeOffset? endedUtc = null,
        string? lifecycleReason = null)
    {
        SessionId = sessionId.Value == Guid.Empty
            ? throw new ArgumentException("A session id is required.", nameof(sessionId))
            : sessionId;
        DeviceId = deviceId.Value == Guid.Empty
            ? throw new ArgumentException("A device id is required.", nameof(deviceId))
            : deviceId;
        DeviceKeyId = string.IsNullOrWhiteSpace(deviceKeyId.Value)
            ? throw new ArgumentException("A device key id is required.", nameof(deviceKeyId))
            : deviceKeyId;
        Status = ProtocolGuard.Defined(status, nameof(status));
        Transport = ProtocolGuard.Defined(transport, nameof(transport));
        Surface = ProtocolGuard.Defined(surface, nameof(surface));
        Capabilities = ProtocolGuard.List(
            ProtocolGuard.List(capabilities, nameof(capabilities), 32).Distinct(),
            nameof(capabilities),
            32);
        CreatedUtc = ProtocolGuard.Utc(createdUtc, nameof(createdUtc));
        LastUsedUtc = ProtocolGuard.Utc(lastUsedUtc, nameof(lastUsedUtc));
        ExpiresUtc = ProtocolGuard.Utc(expiresUtc, nameof(expiresUtc));
        EndedUtc = ProtocolGuard.UtcOptional(endedUtc, nameof(endedUtc));
        LifecycleReason = ProtocolGuard.Optional(lifecycleReason, nameof(lifecycleReason));

        if (LastUsedUtc < CreatedUtc || ExpiresUtc <= CreatedUtc ||
            (EndedUtc is { } ended && ended < LastUsedUtc))
        {
            throw new ArgumentException("Session timestamps are not monotonic.");
        }

        if ((status == DeviceSessionStatus.Active) == (endedUtc is not null))
        {
            throw new ArgumentException("An active session has no end time; every terminal session has one.", nameof(endedUtc));
        }
    }

    public DeviceSessionId SessionId { get; }

    public CompanionDeviceId DeviceId { get; }

    public DeviceKeyId DeviceKeyId { get; }

    public DeviceSessionStatus Status { get; }

    public CompanionTransportKind Transport { get; }

    public CompanionSurfaceKind Surface { get; }

    public IReadOnlyList<DeviceCapability> Capabilities { get; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset LastUsedUtc { get; }

    public DateTimeOffset ExpiresUtc { get; }

    public DateTimeOffset? EndedUtc { get; }

    public string? LifecycleReason { get; }
}

public static class DeviceLifecycle
{
    public static PairedDevice Revoke(PairedDevice device, DateTimeOffset nowUtc, string reason) =>
        Transition(device, DeviceLifecycleStatus.Revoked, nowUtc, reason);

    public static PairedDevice Expire(PairedDevice device, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(device);
        var now = ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        if (now < device.ExpiresUtc && now - device.LastUsedUtc < ProtocolBounds.DeviceInactivityExpiry)
        {
            throw new InvalidOperationException("A device has not reached its absolute or inactivity expiry.");
        }

        return Transition(device, DeviceLifecycleStatus.Expired, now, "device-expired");
    }

    public static PairedDevice Replace(
        PairedDevice device,
        CompanionDeviceId replacement,
        DateTimeOffset nowUtc,
        string reason) =>
        Transition(device, DeviceLifecycleStatus.Replaced, nowUtc, reason, replacement);

    public static DeviceSession EndSession(
        DeviceSession session,
        DeviceSessionStatus status,
        DateTimeOffset nowUtc,
        string reason)
    {
        if (session.Status != DeviceSessionStatus.Active || status == DeviceSessionStatus.Active)
        {
            throw new InvalidOperationException("Only an active session can enter a terminal state.");
        }

        var now = ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        return new DeviceSession(
            session.SessionId,
            session.DeviceId,
            session.DeviceKeyId,
            status,
            session.Transport,
            session.Surface,
            session.Capabilities,
            session.CreatedUtc,
            session.LastUsedUtc,
            session.ExpiresUtc,
            now,
            reason);
    }

    private static PairedDevice Transition(
        PairedDevice device,
        DeviceLifecycleStatus status,
        DateTimeOffset nowUtc,
        string reason,
        CompanionDeviceId? replacement = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Status != DeviceLifecycleStatus.Active)
        {
            throw new InvalidOperationException("A terminal device lifecycle cannot transition again.");
        }

        var now = ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        return new PairedDevice(
            device.DeviceId,
            device.DisplayName,
            device.DeviceKey,
            device.Role,
            device.Capabilities,
            status,
            device.CreatedUtc,
            device.LastUsedUtc,
            device.ExpiresUtc,
            now,
            replacement,
            reason);
    }
}
