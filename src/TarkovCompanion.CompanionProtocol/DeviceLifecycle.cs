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
        long lastKeyEpoch,
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
        LastKeyEpoch = ProtocolGuard.KeyEpoch(lastKeyEpoch, nameof(lastKeyEpoch));
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

    /// <summary>
    /// The key epoch of the device's most recently established session. A pairing or resume
    /// challenge must assign a greater epoch, so a captured proof can never re-establish a session.
    /// </summary>
    public long LastKeyEpoch { get; }

    public DateTimeOffset ExpiresUtc { get; }

    public DateTimeOffset StatusChangedUtc { get; }

    public CompanionDeviceId? ReplacedByDeviceId { get; }

    public string? LifecycleReason { get; }
}

public sealed record DeviceSession
{
    public DeviceSession(
        SessionEstablished establishment,
        DeviceSessionStatus status,
        CompanionTransportKind transport,
        CompanionSurfaceKind surface,
        IReadOnlyList<DeviceCapability> capabilities,
        DateTimeOffset lastUsedUtc,
        DateTimeOffset? endedUtc = null,
        string? lifecycleReason = null)
    {
        Establishment = ProtocolGuard.NotNull(establishment, nameof(establishment));
        Status = ProtocolGuard.Defined(status, nameof(status));
        Transport = ProtocolGuard.Defined(transport, nameof(transport));
        Surface = ProtocolGuard.Defined(surface, nameof(surface));
        Capabilities = ProtocolGuard.List(
            ProtocolGuard.List(capabilities, nameof(capabilities), 32).Distinct(),
            nameof(capabilities),
            32);
        LastUsedUtc = ProtocolGuard.Utc(lastUsedUtc, nameof(lastUsedUtc));
        EndedUtc = ProtocolGuard.UtcOptional(endedUtc, nameof(endedUtc));
        LifecycleReason = ProtocolGuard.Optional(lifecycleReason, nameof(lifecycleReason));

        if (LastUsedUtc < CreatedUtc || LastUsedUtc > ExpiresUtc ||
            (EndedUtc is { } ended && ended < LastUsedUtc))
        {
            throw new ArgumentException("Session timestamps are not monotonic.");
        }

        if ((status == DeviceSessionStatus.Active) == (endedUtc is not null))
        {
            throw new ArgumentException("An active session has no end time; every terminal session has one.", nameof(endedUtc));
        }
    }

    /// <summary>The verified pairing or resume handshake that created this session.</summary>
    public SessionEstablished Establishment { get; }

    public DeviceSessionId SessionId => Establishment.Assignment.SessionId;

    public CompanionDeviceId DeviceId => Establishment.Assignment.DeviceId;

    public DeviceKeyId DeviceKeyId => Establishment.DeviceKeyId;

    public CompanionProtocolVersion ProtocolVersion => Establishment.Assignment.ProtocolVersion;

    public RelayChannelId RelayChannelId => Establishment.Assignment.RelayChannelId;

    public long KeyEpoch => Establishment.Assignment.KeyEpoch;

    public RelayCipherSuite CipherSuite => Establishment.Assignment.CipherSuite;

    public string TranscriptHashBase64Url => Establishment.TranscriptHashBase64Url;

    public DeviceSessionStatus Status { get; }

    public CompanionTransportKind Transport { get; }

    public CompanionSurfaceKind Surface { get; }

    public IReadOnlyList<DeviceCapability> Capabilities { get; }

    public DateTimeOffset CreatedUtc => Establishment.EstablishedUtc;

    public DateTimeOffset LastUsedUtc { get; }

    public DateTimeOffset ExpiresUtc => Establishment.Assignment.SessionExpiresUtc;

    public DateTimeOffset? EndedUtc { get; }

    public string? LifecycleReason { get; }
}

public static class DeviceLifecycle
{
    /// <summary>
    /// A device can authenticate at this instant: it is active, before its absolute expiry, and
    /// used within the absence window. Maintenance treats every other device as terminated.
    /// </summary>
    public static bool IsLive(PairedDevice device, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(device);
        var now = ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        return device.Status == DeviceLifecycleStatus.Active &&
               now < device.ExpiresUtc &&
               now - device.LastUsedUtc < ProtocolBounds.DeviceInactivityExpiry;
    }

    /// <summary>A session is live when it is active, unexpired, and its device is live.</summary>
    public static bool IsLive(DeviceSession session, PairedDevice device, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(device);
        var now = ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        return session.Status == DeviceSessionStatus.Active &&
               now < session.ExpiresUtc &&
               session.DeviceId == device.DeviceId &&
               session.DeviceKeyId == device.DeviceKey.KeyId &&
               IsLive(device, now);
    }

    /// <summary>
    /// Creates the paired-device record from a completed pairing. The device key, id, first key
    /// epoch, and creation time all come from the verified establishment; the display name is the
    /// one the user approved after <see cref="PairingCryptography.OpenDeviceName"/>.
    /// </summary>
    public static PairedDevice Pair(
        PairingAttempt completed,
        string approvedDisplayName,
        DeviceAuthorizationRole role,
        IReadOnlyList<DeviceCapability> capabilities,
        DateTimeOffset expiresUtc)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (completed.Stage != PairingAttemptStage.Completed || completed.Establishment is not { } establishment)
        {
            throw new InvalidOperationException("Only a completed pairing creates a paired device.");
        }

        var established = establishment.EstablishedUtc;
        return new PairedDevice(
            establishment.Assignment.DeviceId,
            PairingCryptography.ValidateDeviceName(approvedDisplayName),
            completed.Request!.DeviceKey,
            role,
            capabilities,
            DeviceLifecycleStatus.Active,
            established,
            established,
            establishment.Assignment.KeyEpoch,
            expiresUtc,
            established);
    }

    /// <summary>
    /// Records a newly established resume session: its key epoch must exceed every epoch
    /// the device has used, and the establishment counts as authenticated use.
    /// </summary>
    public static PairedDevice RecordSession(PairedDevice device, SessionEstablished establishment)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(establishment);
        if (establishment.Assignment.DeviceId != device.DeviceId || establishment.DeviceKeyId != device.DeviceKey.KeyId)
        {
            throw new UnauthorizedAccessException("The session was not established by this device's bound key.");
        }

        if (establishment.Purpose != HandshakePurpose.SessionResume || establishment.Assignment.KeyEpoch <= device.LastKeyEpoch)
        {
            throw new InvalidOperationException("Only a resume with a fresh key epoch records a new session for a paired device.");
        }

        if (!IsLive(device, establishment.EstablishedUtc))
        {
            throw new UnauthorizedAccessException("A device that is not live cannot record a session.");
        }

        return WithUse(device, Later(device.LastUsedUtc, establishment.EstablishedUtc), establishment.Assignment.KeyEpoch);
    }

    /// <summary>
    /// Records an authenticated frame from a live session. Without it, a device that stays connected
    /// would still reach the inactivity expiry; with it, only real absence ends the device.
    /// </summary>
    public static (PairedDevice Device, DeviceSession Session) RecordUse(
        PairedDevice device,
        DeviceSession session,
        DateTimeOffset nowUtc)
    {
        var now = ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        if (!IsLive(session, device, now))
        {
            throw new UnauthorizedAccessException("Only a live session of a live device records use.");
        }

        var updatedSession = new DeviceSession(
            session.Establishment,
            session.Status,
            session.Transport,
            session.Surface,
            session.Capabilities,
            Later(session.LastUsedUtc, now));
        return (WithUse(device, Later(device.LastUsedUtc, now), device.LastKeyEpoch), updatedSession);
    }

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
            session.Establishment,
            status,
            session.Transport,
            session.Surface,
            session.Capabilities,
            session.LastUsedUtc,
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
            device.LastKeyEpoch,
            device.ExpiresUtc,
            now,
            replacement,
            reason);
    }

    private static PairedDevice WithUse(PairedDevice device, DateTimeOffset lastUsedUtc, long lastKeyEpoch) =>
        new(
            device.DeviceId,
            device.DisplayName,
            device.DeviceKey,
            device.Role,
            device.Capabilities,
            device.Status,
            device.CreatedUtc,
            lastUsedUtc,
            lastKeyEpoch,
            device.ExpiresUtc,
            device.StatusChangedUtc,
            device.ReplacedByDeviceId,
            device.LifecycleReason);

    private static DateTimeOffset Later(DateTimeOffset current, DateTimeOffset candidate) =>
        candidate > current ? candidate : current;
}
