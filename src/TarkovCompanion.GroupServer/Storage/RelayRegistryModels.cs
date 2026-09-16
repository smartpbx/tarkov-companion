using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.GroupServer.Security;

namespace TarkovCompanion.GroupServer.Storage;

public enum RelayAuditAction
{
    OwnerRecovered = 1,
    DevicePaired,
    DeviceRevoked,
    DeviceReplaced,
    DeviceExpired,
    SessionIssued,
    SessionClosed,
    SessionRotated,
    SessionRejected,
    CsrfRejected,
    FrameAccepted,
    FrameRejected,
}

/// <summary>Security history excludes credentials, display names, payloads, addresses, and exceptions.</summary>
public sealed record RelayAuditEvent
{
    public RelayAuditEvent(
        Guid eventId,
        RelayAuditAction action,
        DateTimeOffset occurredUtc,
        string outcomeCode,
        CompanionDeviceId? actorDeviceId = null,
        CompanionDeviceId? subjectDeviceId = null,
        DeviceSessionId? sessionId = null)
    {
        if (eventId == Guid.Empty || !Enum.IsDefined(action))
        {
            throw new ArgumentException("An audit event requires an id and action.");
        }

        RelayCsrfProtector.ValidateUtc(occurredUtc, nameof(occurredUtc));
        ValidateBoundedToken(outcomeCode, nameof(outcomeCode));
        if (actorDeviceId?.Value == Guid.Empty || subjectDeviceId?.Value == Guid.Empty || sessionId?.Value == Guid.Empty)
        {
            throw new ArgumentException("Optional audit identifiers cannot be empty.");
        }

        EventId = eventId;
        Action = action;
        OccurredUtc = occurredUtc;
        OutcomeCode = outcomeCode;
        ActorDeviceId = actorDeviceId;
        SubjectDeviceId = subjectDeviceId;
        SessionId = sessionId;
    }

    public Guid EventId { get; }

    public RelayAuditAction Action { get; }

    public DateTimeOffset OccurredUtc { get; }

    public string OutcomeCode { get; }

    public CompanionDeviceId? ActorDeviceId { get; }

    public CompanionDeviceId? SubjectDeviceId { get; }

    public DeviceSessionId? SessionId { get; }

    internal static void ValidateBoundedToken(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (Encoding.UTF8.GetByteCount(value) > ProtocolBounds.MaxShortStringBytes ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException("Expected a bounded machine-readable token.", parameterName);
        }
    }
}

/// <summary>The relay's name-free authorization record for one cryptographic device identity.</summary>
/// <remarks>
/// A hosted relay never needs the display name sealed by the paired handshake. Keeping a separate
/// record from <see cref="PairedDevice"/> prevents an approved name from entering relay storage,
/// diagnostics, authorization decisions, or routing metadata.
/// </remarks>
public sealed record RelayDeviceRecord
{
    public RelayDeviceRecord(
        CompanionDeviceId deviceId,
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
        if (deviceId.Value == Guid.Empty)
        {
            throw new ArgumentException("A relay device id is required.", nameof(deviceId));
        }

        ArgumentNullException.ThrowIfNull(deviceKey);
        if (!Enum.IsDefined(role) || !Enum.IsDefined(status) ||
            lastKeyEpoch is <= 0 or > ProtocolBounds.MaxKeyEpoch)
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        ArgumentNullException.ThrowIfNull(capabilities);
        var copiedCapabilities = capabilities.Distinct().ToArray();
        if (copiedCapabilities.Length is 0 or > 32 ||
            !RelayAuthorization.IsCapabilitySetValid(role, copiedCapabilities))
        {
            throw new ArgumentException("Relay device capabilities must be a least-privilege role subset.", nameof(capabilities));
        }

        RelayCsrfProtector.ValidateUtc(createdUtc, nameof(createdUtc));
        RelayCsrfProtector.ValidateUtc(lastUsedUtc, nameof(lastUsedUtc));
        RelayCsrfProtector.ValidateUtc(expiresUtc, nameof(expiresUtc));
        RelayCsrfProtector.ValidateUtc(statusChangedUtc, nameof(statusChangedUtc));
        if (lastUsedUtc < createdUtc || lastUsedUtc > expiresUtc || expiresUtc <= createdUtc ||
            statusChangedUtc < createdUtc ||
            (status != DeviceLifecycleStatus.Active && statusChangedUtc < lastUsedUtc))
        {
            throw new ArgumentException("Relay device timestamps are not monotonic.");
        }

        if ((status == DeviceLifecycleStatus.Replaced) != (replacedByDeviceId is not null) ||
            replacedByDeviceId?.Value == Guid.Empty || replacedByDeviceId == deviceId)
        {
            throw new ArgumentException("Only a replaced device names a distinct replacement.", nameof(replacedByDeviceId));
        }

        if (lifecycleReason is not null)
        {
            RelayAuditEvent.ValidateBoundedToken(lifecycleReason, nameof(lifecycleReason));
        }

        DeviceId = deviceId;
        DeviceKey = deviceKey;
        Role = role;
        Capabilities = Array.AsReadOnly(copiedCapabilities);
        Status = status;
        CreatedUtc = createdUtc;
        LastUsedUtc = lastUsedUtc;
        LastKeyEpoch = lastKeyEpoch;
        ExpiresUtc = expiresUtc;
        StatusChangedUtc = statusChangedUtc;
        ReplacedByDeviceId = replacedByDeviceId;
        LifecycleReason = lifecycleReason;
    }

    public CompanionDeviceId DeviceId { get; }

    public DevicePublicKey DeviceKey { get; }

    public DeviceAuthorizationRole Role { get; }

    public IReadOnlyList<DeviceCapability> Capabilities { get; }

    public DeviceLifecycleStatus Status { get; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset LastUsedUtc { get; }

    public long LastKeyEpoch { get; }

    public DateTimeOffset ExpiresUtc { get; }

    public DateTimeOffset StatusChangedUtc { get; }

    public CompanionDeviceId? ReplacedByDeviceId { get; }

    public string? LifecycleReason { get; }
}

/// <summary>A signed protocol session plus relay-only cookie, CSRF, and directional replay state.</summary>
public sealed record RelaySessionRecord
{
    public RelaySessionRecord(
        DeviceSession session,
        string credentialDigestBase64Url,
        string csrfDigestBase64Url,
        DateTimeOffset csrfExpiresUtc,
        long lastTabletSenderSequence = 0,
        long lastDesktopSenderSequence = 0)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (lastTabletSenderSequence is < 0 or > ProtocolBounds.MaxSenderSequence ||
            lastDesktopSenderSequence is < 0 or > ProtocolBounds.MaxSenderSequence)
        {
            throw new ArgumentOutOfRangeException(nameof(lastTabletSenderSequence));
        }

        ValidateDigest(credentialDigestBase64Url, nameof(credentialDigestBase64Url));
        ValidateDigest(csrfDigestBase64Url, nameof(csrfDigestBase64Url));
        RelayCsrfProtector.ValidateUtc(csrfExpiresUtc, nameof(csrfExpiresUtc));
        if (csrfExpiresUtc <= session.CreatedUtc || csrfExpiresUtc > session.ExpiresUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(csrfExpiresUtc));
        }

        Session = session;
        CredentialDigestBase64Url = credentialDigestBase64Url;
        CsrfDigestBase64Url = csrfDigestBase64Url;
        CsrfExpiresUtc = csrfExpiresUtc;
        LastTabletSenderSequence = lastTabletSenderSequence;
        LastDesktopSenderSequence = lastDesktopSenderSequence;
    }

    public DeviceSession Session { get; }

    public RelayChannelId ChannelId => Session.RelayChannelId;

    public string CredentialDigestBase64Url { get; }

    public string CsrfDigestBase64Url { get; }

    public DateTimeOffset CsrfExpiresUtc { get; }

    public long LastTabletSenderSequence { get; }

    public long LastDesktopSenderSequence { get; }

    public RelaySessionRecord WithSession(DeviceSession session) => new(
        session,
        CredentialDigestBase64Url,
        CsrfDigestBase64Url,
        CsrfExpiresUtc,
        LastTabletSenderSequence,
        LastDesktopSenderSequence);

    public RelaySessionRecord WithCsrf(RelayCsrfToken token) => new(
        Session,
        CredentialDigestBase64Url,
        token.DigestBase64Url,
        token.ExpiresUtc,
        LastTabletSenderSequence,
        LastDesktopSenderSequence);

    public RelaySessionRecord WithSequence(PairingTrafficDirection direction, long senderSequence) => direction switch
    {
        PairingTrafficDirection.TabletToDesktop => new RelaySessionRecord(
            Session,
            CredentialDigestBase64Url,
            CsrfDigestBase64Url,
            CsrfExpiresUtc,
            senderSequence,
            LastDesktopSenderSequence),
        PairingTrafficDirection.DesktopToTablet => new RelaySessionRecord(
            Session,
            CredentialDigestBase64Url,
            CsrfDigestBase64Url,
            CsrfExpiresUtc,
            LastTabletSenderSequence,
            senderSequence),
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    public long LastSequence(PairingTrafficDirection direction) => direction switch
    {
        PairingTrafficDirection.TabletToDesktop => LastTabletSenderSequence,
        PairingTrafficDirection.DesktopToTablet => LastDesktopSenderSequence,
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    internal static void ValidateDigest(string digest, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digest, parameterName);
        byte[] decoded;
        try
        {
            decoded = RelayCsrfProtector.DecodeBase64Url(digest);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Expected a SHA-256 base64url digest.", parameterName, exception);
        }

        if (decoded.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException("Expected a SHA-256 base64url digest.", parameterName);
        }
    }
}

/// <summary>The complete bounded device/session registry payload.</summary>
public sealed record RelayRegistryState
{
    public RelayRegistryState(
        bool isInitialized,
        IReadOnlyList<RelayDeviceRecord> devices,
        IReadOnlyList<RelaySessionRecord> sessions,
        IReadOnlyList<RelayAuditEvent> audit)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(audit);
        var copiedDevices = devices.ToArray();
        var copiedSessions = sessions.ToArray();
        var copiedAudit = audit.ToArray();
        if (copiedDevices.Length > ProtocolBounds.MaxDevices ||
            copiedSessions.Length > RelaySecurityBounds.MaximumSessions ||
            copiedAudit.Length > RelaySecurityBounds.MaximumAuditEvents ||
            copiedDevices.Any(device => device is null) ||
            copiedSessions.Any(session => session is null) ||
            copiedAudit.Any(entry => entry is null))
        {
            throw new ArgumentOutOfRangeException(nameof(devices), "The relay registry exceeds a storage bound.");
        }

        if (copiedDevices.Select(device => device.DeviceId).Distinct().Count() != copiedDevices.Length ||
            copiedDevices.Select(device => device.DeviceKey.KeyId).Distinct().Count() != copiedDevices.Length ||
            copiedSessions.Select(record => record.Session.SessionId).Distinct().Count() != copiedSessions.Length ||
            copiedSessions.Select(record => record.Session.Establishment.ChallengeId).Distinct().Count() != copiedSessions.Length ||
            copiedSessions.Select(record => record.Session.TranscriptHashBase64Url).Distinct(StringComparer.Ordinal).Count() != copiedSessions.Length ||
            copiedSessions.Select(record => record.ChannelId).Distinct().Count() != copiedSessions.Length ||
            copiedAudit.Select(entry => entry.EventId).Distinct().Count() != copiedAudit.Length)
        {
            throw new ArgumentException("Registry identifiers must be unique.");
        }

        var deviceMap = copiedDevices.ToDictionary(device => device.DeviceId);
        foreach (var record in copiedSessions)
        {
            if (!deviceMap.TryGetValue(record.Session.DeviceId, out var device) ||
                record.Session.Transport != CompanionTransportKind.EndToEndRelay ||
                record.Session.DeviceKeyId != device.DeviceKey.KeyId ||
                record.Session.KeyEpoch > device.LastKeyEpoch ||
                record.Session.ExpiresUtc > device.ExpiresUtc ||
                record.Session.CreatedUtc < device.CreatedUtc ||
                record.Session.LastUsedUtc > device.LastUsedUtc ||
                (device.Role == DeviceAuthorizationRole.Owner &&
                 record.Session.Surface is not (CompanionSurfaceKind.Desktop or CompanionSurfaceKind.DesktopBrowser)) ||
                !record.Session.Capabilities.ToHashSet().SetEquals(device.Capabilities) ||
                !RelayAuthorization.IsCapabilitySetValid(device.Role, record.Session.Capabilities) ||
                (record.Session.Status == DeviceSessionStatus.Active && record.Session.KeyEpoch != device.LastKeyEpoch) ||
                (record.Session.Status == DeviceSessionStatus.Active && device.Status != DeviceLifecycleStatus.Active))
            {
                throw new ArgumentException("Every session must bind one registered device key, epoch, and role capability set.");
            }
        }

        if (copiedSessions
            .Where(record => record.Session.Status == DeviceSessionStatus.Active)
            .GroupBy(record => record.Session.DeviceId)
            .Any(group => group.Count() > RelaySecurityBounds.MaximumSessionsPerDevice))
        {
            throw new ArgumentException("Active session and relay-channel ownership must be unique and bounded.");
        }

        foreach (var device in copiedDevices)
        {
            var deviceSessions = copiedSessions.Where(record => record.Session.DeviceId == device.DeviceId).ToArray();
            var pairingSessions = deviceSessions
                .Where(record => record.Session.Establishment.Purpose == HandshakePurpose.Pairing)
                .ToArray();
            if (deviceSessions.Select(record => record.Session.KeyEpoch).Distinct().Count() != deviceSessions.Length ||
                pairingSessions.Length > 1 ||
                (pairingSessions.Length == 1 && pairingSessions[0].Session.CreatedUtc != device.CreatedUtc))
            {
                throw new ArgumentException("Retained pairing sessions and device session epochs must be unique.");
            }
        }

        if ((!isInitialized && (copiedDevices.Length != 0 || copiedSessions.Length != 0)) ||
            (isInitialized && copiedDevices.Length == 0))
        {
            throw new ArgumentException("Only an initialized nonempty registry can authorize devices or sessions.");
        }

        IsInitialized = isInitialized;
        Devices = Array.AsReadOnly(copiedDevices);
        Sessions = Array.AsReadOnly(copiedSessions);
        Audit = Array.AsReadOnly(copiedAudit);
    }

    public bool IsInitialized { get; }

    public IReadOnlyList<RelayDeviceRecord> Devices { get; }

    public IReadOnlyList<RelaySessionRecord> Sessions { get; }

    public IReadOnlyList<RelayAuditEvent> Audit { get; }

    public static RelayRegistryState Empty { get; } = new(false, [], [], []);
}

public enum RelayRegistryLoadStatus
{
    Uninitialized = 1,
    PrimaryVerified,
    BackupRestored,
    Corrupt,
}

public sealed record RelayRegistryLoadResult(RelayRegistryState State, RelayRegistryLoadStatus Status)
{
    public bool CanAuthenticate =>
        State.IsInitialized &&
        Status is (RelayRegistryLoadStatus.PrimaryVerified or RelayRegistryLoadStatus.BackupRestored);
}
