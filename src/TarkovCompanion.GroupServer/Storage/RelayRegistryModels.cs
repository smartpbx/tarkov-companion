using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.GroupServer.Security;

namespace TarkovCompanion.GroupServer.Storage;

public enum RelayAuditAction
{
    OwnerRecovered = 1,
    DevicePaired,
    DeviceRevoked,
    DeviceReplaced,
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

/// <summary>A protocol session plus relay-only credential and routing state.</summary>
public sealed record RelaySessionRecord
{
    public RelaySessionRecord(
        DeviceSession session,
        RelayChannelId channelId,
        string credentialDigestBase64Url,
        string csrfDigestBase64Url,
        DateTimeOffset csrfExpiresUtc,
        long keyEpoch = 0,
        long lastSenderSequence = 0)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (channelId.Value == Guid.Empty || keyEpoch < 0 || lastSenderSequence < 0 ||
            (keyEpoch == 0) != (lastSenderSequence == 0))
        {
            throw new ArgumentException("Relay session routing or sequence state is invalid.");
        }

        ValidateDigest(credentialDigestBase64Url, nameof(credentialDigestBase64Url));
        ValidateDigest(csrfDigestBase64Url, nameof(csrfDigestBase64Url));
        RelayCsrfProtector.ValidateUtc(csrfExpiresUtc, nameof(csrfExpiresUtc));
        if (csrfExpiresUtc > session.ExpiresUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(csrfExpiresUtc));
        }

        Session = session;
        ChannelId = channelId;
        CredentialDigestBase64Url = credentialDigestBase64Url;
        CsrfDigestBase64Url = csrfDigestBase64Url;
        CsrfExpiresUtc = csrfExpiresUtc;
        KeyEpoch = keyEpoch;
        LastSenderSequence = lastSenderSequence;
    }

    public DeviceSession Session { get; }

    public RelayChannelId ChannelId { get; }

    public string CredentialDigestBase64Url { get; }

    public string CsrfDigestBase64Url { get; }

    public DateTimeOffset CsrfExpiresUtc { get; }

    public long KeyEpoch { get; }

    public long LastSenderSequence { get; }

    public RelaySessionRecord WithSession(DeviceSession session) => new(
        session,
        ChannelId,
        CredentialDigestBase64Url,
        CsrfDigestBase64Url,
        CsrfExpiresUtc,
        KeyEpoch,
        LastSenderSequence);

    public RelaySessionRecord WithCsrf(RelayCsrfToken token) => new(
        Session,
        ChannelId,
        CredentialDigestBase64Url,
        token.DigestBase64Url,
        token.ExpiresUtc,
        KeyEpoch,
        LastSenderSequence);

    public RelaySessionRecord WithSequence(long keyEpoch, long senderSequence) => new(
        Session,
        ChannelId,
        CredentialDigestBase64Url,
        CsrfDigestBase64Url,
        CsrfExpiresUtc,
        keyEpoch,
        senderSequence);

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
        IReadOnlyList<PairedDevice> devices,
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
            copiedAudit.Select(entry => entry.EventId).Distinct().Count() != copiedAudit.Length)
        {
            throw new ArgumentException("Registry identifiers must be unique.");
        }

        var deviceMap = copiedDevices.ToDictionary(device => device.DeviceId);
        foreach (var record in copiedSessions)
        {
            if (!deviceMap.TryGetValue(record.Session.DeviceId, out var device) ||
                record.Session.DeviceKeyId != device.DeviceKey.KeyId ||
                !RelayAuthorization.IsCapabilitySetValid(device.Role, record.Session.Capabilities))
            {
                throw new ArgumentException("Every session must bind one registered device key and valid role capabilities.");
            }
        }

        if (copiedSessions
            .Where(record => record.Session.Status == DeviceSessionStatus.Active)
            .GroupBy(record => record.Session.DeviceId)
            .Any(group => group.Count() > RelaySecurityBounds.MaximumSessionsPerDevice))
        {
            throw new ArgumentException("A device has too many active sessions.");
        }

        if (!isInitialized && (copiedDevices.Length != 0 || copiedSessions.Length != 0))
        {
            throw new ArgumentException("An uninitialized registry cannot authorize devices or sessions.");
        }

        IsInitialized = isInitialized;
        Devices = Array.AsReadOnly(copiedDevices);
        Sessions = Array.AsReadOnly(copiedSessions);
        Audit = Array.AsReadOnly(copiedAudit);
    }

    public bool IsInitialized { get; }

    public ReadOnlyCollection<PairedDevice> Devices { get; }

    public ReadOnlyCollection<RelaySessionRecord> Sessions { get; }

    public ReadOnlyCollection<RelayAuditEvent> Audit { get; }

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
