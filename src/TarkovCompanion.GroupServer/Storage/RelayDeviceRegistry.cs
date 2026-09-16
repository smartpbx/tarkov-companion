using System.Security.Cryptography;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.GroupServer.Security;

namespace TarkovCompanion.GroupServer.Storage;

public sealed record RelaySessionCredential(
    DeviceSessionId SessionId,
    CompanionDeviceId DeviceId,
    DeviceKeyId DeviceKeyId,
    RelayChannelId ChannelId,
    string Secret,
    string CsrfToken,
    DateTimeOffset ExpiresUtc);

public sealed record RelayAuthenticationResult(bool Authenticated, string Code, RelayPrincipal? Principal)
{
    public static RelayAuthenticationResult Reject(string code = "not-authenticated") => new(false, code, null);
}

public sealed record RelayMutationResult<T>(bool Succeeded, string Code, T? Value)
{
    public static RelayMutationResult<T> Success(T value) => new(true, "accepted", value);

    public static RelayMutationResult<T> Reject(string code) => new(false, code, default);
}

public readonly record struct RelayFrameAdmission(bool Accepted, string Code)
{
    public static RelayFrameAdmission Permit { get; } = new(true, "accepted");

    public static RelayFrameAdmission Reject(string code) => new(false, code);
}

public sealed record RelaySessionRoute(
    DeviceSessionId SessionId,
    CompanionDeviceId DeviceId,
    RelayChannelId ChannelId,
    DeviceAuthorizationRole Role,
    DateTimeOffset ExpiresUtc);

/// <summary>Authoritative relay registry for devices, sessions, replay state, and audit history.</summary>
/// <remarks>
/// Every reusable credential is random and stored only as a SHA-256 digest bound to its session
/// id. Mutations are written through the verified store before becoming visible in memory. A
/// storage failure closes authentication rather than silently continuing with an unpersisted
/// revocation or replay counter.
/// </remarks>
public sealed class RelayDeviceRegistry
{
    private readonly TimeProvider _timeProvider;
    private readonly OwnerRecoveryProtector _recovery;
    private readonly VerifiedRelayRegistryStore? _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RelayRegistryState _state;
    private RelayRegistryLoadStatus _loadStatus;

    private RelayDeviceRegistry(
        TimeProvider timeProvider,
        OwnerRecoveryProtector recovery,
        VerifiedRelayRegistryStore? store,
        RelayRegistryState state,
        RelayRegistryLoadStatus loadStatus)
    {
        _timeProvider = timeProvider;
        _recovery = recovery;
        _store = store;
        _state = state;
        _loadStatus = loadStatus;
    }

    public RelayRegistryLoadStatus LoadStatus => _loadStatus;

    public bool CanAuthenticate =>
        _state.IsInitialized &&
        _loadStatus is (RelayRegistryLoadStatus.PrimaryVerified or RelayRegistryLoadStatus.BackupRestored);

    public static async ValueTask<RelayDeviceRegistry> OpenAsync(
        TimeProvider timeProvider,
        OwnerRecoveryProtector recovery,
        VerifiedRelayRegistryStore? store = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(recovery);
        var loaded = store is null
            ? new RelayRegistryLoadResult(RelayRegistryState.Empty, RelayRegistryLoadStatus.Uninitialized)
            : await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new RelayDeviceRegistry(timeProvider, recovery, store, loaded.State, loaded.Status);
    }

    /// <summary>
    /// Creates a replacement owner after a protected, short-lived recovery ceremony.
    /// </summary>
    /// <remarks>
    /// This is the only operation allowed against missing or corrupt state. It replaces rather
    /// than opens the registry and invalidates every surviving session when valid-but-ownerless
    /// state is being recovered.
    /// </remarks>
    public async ValueTask<RelayMutationResult<RelaySessionCredential>> RecoverOwnerAsync(
        OwnerRecoveryGrant grant,
        string displayName,
        DevicePublicKey deviceKey,
        RelayChannelId channelId,
        CompanionSurfaceKind surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(deviceKey);
        if (grant.NewOwnerKeyId != deviceKey.KeyId || !_recovery.TryConsume(grant))
        {
            return RelayMutationResult<RelaySessionCredential>.Reject("recovery-rejected");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Now();
            if (_state.Devices.Any(device => device.Status == DeviceLifecycleStatus.Active &&
                                             device.Role == DeviceAuthorizationRole.Owner))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("owner-active");
            }

            var devices = _loadStatus == RelayRegistryLoadStatus.Corrupt
                ? new List<PairedDevice>()
                : _state.Devices.ToList();
            // Recovery invalidates every old bearer credential. Keeping terminal records would
            // also let accumulated abandoned sessions prevent issuance of the recovered owner.
            var sessions = new List<RelaySessionRecord>();

            var prior = devices.FindIndex(device => device.DeviceId == grant.NewOwnerDeviceId);
            if (prior >= 0)
            {
                devices.RemoveAt(prior);
            }

            if (devices.Count >= ProtocolBounds.MaxDevices)
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("device-limit");
            }

            var owner = new PairedDevice(
                grant.NewOwnerDeviceId,
                displayName,
                deviceKey,
                DeviceAuthorizationRole.Owner,
                RelayAuthorization.CapabilitiesFor(DeviceAuthorizationRole.Owner),
                DeviceLifecycleStatus.Active,
                now,
                now,
                now.AddDays(30),
                now);
            devices.Add(owner);
            var issued = CreateSession(owner, channelId, surface, now);
            sessions.Add(issued.Record);
            var audit = AppendAudit(
                _loadStatus == RelayRegistryLoadStatus.Corrupt ? [] : _state.Audit,
                new RelayAuditEvent(
                    Guid.NewGuid(),
                    RelayAuditAction.OwnerRecovered,
                    now,
                    "recovered",
                    subjectDeviceId: owner.DeviceId,
                    sessionId: issued.Credential.SessionId));
            var candidate = new RelayRegistryState(true, devices, sessions, audit);
            await CommitAsync(candidate, cancellationToken).ConfigureAwait(false);
            return RelayMutationResult<RelaySessionCredential>.Success(issued.Credential);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<RelayAuthenticationResult> AuthenticateAsync(
        DeviceSessionId sessionId,
        string? presentedCredential,
        CancellationToken cancellationToken = default)
    {
        if (sessionId.Value == Guid.Empty || !TryCredentialDigest(sessionId, presentedCredential, out var actualDigest))
        {
            return RelayAuthenticationResult.Reject();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!CanAuthenticate)
            {
                return RelayAuthenticationResult.Reject("unavailable");
            }

            var index = _state.Sessions.ToList().FindIndex(record => record.Session.SessionId == sessionId);
            if (index < 0)
            {
                return RelayAuthenticationResult.Reject();
            }

            var record = _state.Sessions[index];
            byte[] expectedDigest;
            try
            {
                expectedDigest = RelayCsrfProtector.DecodeBase64Url(record.CredentialDigestBase64Url);
            }
            catch (FormatException)
            {
                CloseInMemory();
                return RelayAuthenticationResult.Reject("unavailable");
            }

            if (!CryptographicOperations.FixedTimeEquals(expectedDigest, actualDigest))
            {
                return RelayAuthenticationResult.Reject();
            }

            var now = Now();
            var devices = _state.Devices.ToList();
            var deviceIndex = devices.FindIndex(device => device.DeviceId == record.Session.DeviceId);
            if (deviceIndex < 0)
            {
                CloseInMemory();
                return RelayAuthenticationResult.Reject("unavailable");
            }

            var device = devices[deviceIndex];
            if (record.Session.Status != DeviceSessionStatus.Active || device.Status != DeviceLifecycleStatus.Active)
            {
                return RelayAuthenticationResult.Reject();
            }

            var deviceExpired = now >= device.ExpiresUtc ||
                now - device.LastUsedUtc >= ProtocolBounds.DeviceInactivityExpiry;
            if (now >= record.Session.ExpiresUtc || deviceExpired)
            {
                var expiredSessions = _state.Sessions
                    .Select(item => (deviceExpired && item.Session.DeviceId == device.DeviceId) ||
                                    item.Session.SessionId == sessionId
                        ? EndIfActive(item, DeviceSessionStatus.Expired, now, "expired")
                        : item)
                    .ToArray();
                var expiredDevice = deviceExpired && device.Status == DeviceLifecycleStatus.Active
                    ? DeviceLifecycle.Expire(device, now)
                    : device;
                devices[deviceIndex] = expiredDevice;
                var expiredState = new RelayRegistryState(
                    true,
                    devices,
                    expiredSessions,
                    AppendAudit(_state.Audit, new RelayAuditEvent(
                        Guid.NewGuid(),
                        RelayAuditAction.SessionRejected,
                        now,
                        "expired",
                        subjectDeviceId: device.DeviceId,
                        sessionId: sessionId)));
                await CommitAsync(expiredState, cancellationToken).ConfigureAwait(false);
                return RelayAuthenticationResult.Reject("expired");
            }

            var touchedDevice = new PairedDevice(
                device.DeviceId,
                device.DisplayName,
                device.DeviceKey,
                device.Role,
                device.Capabilities,
                device.Status,
                device.CreatedUtc,
                now,
                device.ExpiresUtc,
                device.StatusChangedUtc,
                device.ReplacedByDeviceId,
                device.LifecycleReason);
            var touchedSession = new DeviceSession(
                record.Session.SessionId,
                record.Session.DeviceId,
                record.Session.DeviceKeyId,
                record.Session.Status,
                record.Session.Transport,
                record.Session.Surface,
                record.Session.Capabilities,
                record.Session.CreatedUtc,
                now,
                record.Session.ExpiresUtc);
            devices[deviceIndex] = touchedDevice;
            var sessions = _state.Sessions.ToList();
            sessions[index] = record.WithSession(touchedSession);
            await CommitAsync(new RelayRegistryState(true, devices, sessions, _state.Audit), cancellationToken)
                .ConfigureAwait(false);

            return new RelayAuthenticationResult(
                true,
                "authenticated",
                new RelayPrincipal(
                    touchedDevice.DeviceId,
                    touchedDevice.DeviceKey.KeyId,
                    touchedSession.SessionId,
                    record.ChannelId,
                    touchedDevice.Role,
                    touchedSession.Capabilities,
                    touchedSession.Surface,
                    now,
                    touchedSession.ExpiresUtc));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Checks and rotates the memory-only CSRF nonce after each cookie-authenticated mutation.</summary>
    public async ValueTask<RelayMutationResult<string>> ValidateAndRotateCsrfAsync(
        RelayPrincipal principal,
        string? presentedToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!CanAuthenticate || !StillCurrent(principal))
            {
                return RelayMutationResult<string>.Reject("unavailable");
            }

            var sessions = _state.Sessions.ToList();
            var index = sessions.FindIndex(record => record.Session.SessionId == principal.SessionId);
            if (index < 0 || sessions[index].Session.Status != DeviceSessionStatus.Active)
            {
                return RelayMutationResult<string>.Reject("csrf-rejected");
            }

            var current = sessions[index];
            var now = Now();
            if (!RelayCsrfProtector.Validate(
                    principal.SessionId,
                    presentedToken,
                    current.CsrfDigestBase64Url,
                    current.CsrfExpiresUtc,
                    now))
            {
                var rejected = new RelayRegistryState(
                    true,
                    _state.Devices,
                    sessions,
                    AppendAudit(_state.Audit, new RelayAuditEvent(
                        Guid.NewGuid(),
                        RelayAuditAction.CsrfRejected,
                        now,
                        "csrf-rejected",
                        principal.DeviceId,
                        principal.DeviceId,
                        principal.SessionId)));
                await CommitAsync(rejected, cancellationToken).ConfigureAwait(false);
                return RelayMutationResult<string>.Reject("csrf-rejected");
            }

            var lifetime = current.Session.ExpiresUtc - now;
            var next = RelayCsrfProtector.Issue(principal.SessionId, now, lifetime);
            sessions[index] = current.WithCsrf(next);
            await CommitAsync(new RelayRegistryState(true, _state.Devices, sessions, _state.Audit), cancellationToken)
                .ConfigureAwait(false);
            return RelayMutationResult<string>.Success(next.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Creates a non-owner device only after the #276 proof state machine completed.</summary>
    public async ValueTask<RelayMutationResult<RelaySessionCredential>> AddPairedDeviceAsync(
        RelayPrincipal owner,
        PairingAttempt completedAttempt,
        DeviceAuthorizationRole role,
        CompanionSurfaceKind surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(completedAttempt);
        if (!RelayAuthorization.Decide(owner, RelayPermission.ApprovePairingInvitation).Allowed)
        {
            return RelayMutationResult<RelaySessionCredential>.Reject("not-authorized");
        }

        if (completedAttempt.Stage != PairingAttemptStage.Completed || completedAttempt.Request is null ||
            role is not (DeviceAuthorizationRole.Member or DeviceAuthorizationRole.Observer))
        {
            return RelayMutationResult<RelaySessionCredential>.Reject("pairing-incomplete");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!StillCurrent(owner))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("not-authenticated");
            }

            if (_state.Devices.Count >= ProtocolBounds.MaxDevices ||
                _state.Sessions.Count >= RelaySecurityBounds.MaximumSessions ||
                _state.Devices.Any(device => device.DeviceKey.KeyId == completedAttempt.Request.DeviceKey.KeyId))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("device-limit-or-duplicate");
            }

            var now = Now();
            var device = new PairedDevice(
                new CompanionDeviceId(Guid.NewGuid()),
                completedAttempt.Request.RequestedDeviceName,
                completedAttempt.Request.DeviceKey,
                role,
                RelayAuthorization.CapabilitiesFor(role),
                DeviceLifecycleStatus.Active,
                now,
                now,
                now.AddDays(30),
                now);
            var issued = CreateSession(device, owner.ChannelId, surface, now);
            var candidate = new RelayRegistryState(
                true,
                [.. _state.Devices, device],
                [.. _state.Sessions, issued.Record],
                AppendAudit(_state.Audit, new RelayAuditEvent(
                    Guid.NewGuid(),
                    RelayAuditAction.DevicePaired,
                    now,
                    "paired",
                    owner.DeviceId,
                    device.DeviceId,
                    issued.Credential.SessionId)));
            await CommitAsync(candidate, cancellationToken).ConfigureAwait(false);
            return RelayMutationResult<RelaySessionCredential>.Success(issued.Credential);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<RelayMutationResult<RelaySessionCredential>> RotateSessionAsync(
        RelayPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!RelayAuthorization.Decide(principal, RelayPermission.RotateOwnSession, principal.DeviceId).Allowed)
        {
            return RelayMutationResult<RelaySessionCredential>.Reject("not-authorized");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!StillCurrent(principal))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("not-authenticated");
            }

            var device = _state.Devices.Single(item => item.DeviceId == principal.DeviceId);
            var now = Now();
            var sessions = _state.Sessions
                .Select(record => record.Session.SessionId == principal.SessionId
                    ? EndIfActive(record, DeviceSessionStatus.Replaced, now, "session-rotated")
                    : record)
                .ToList();
            if (sessions.Count >= RelaySecurityBounds.MaximumSessions)
            {
                sessions.RemoveAll(record => record.Session.Status != DeviceSessionStatus.Active);
            }

            var issued = CreateSession(device, principal.ChannelId, principal.Surface, now);
            sessions.Add(issued.Record);
            var candidate = new RelayRegistryState(
                true,
                _state.Devices,
                sessions,
                AppendAudit(_state.Audit, new RelayAuditEvent(
                    Guid.NewGuid(),
                    RelayAuditAction.SessionRotated,
                    now,
                    "rotated",
                    principal.DeviceId,
                    principal.DeviceId,
                    principal.SessionId)));
            await CommitAsync(candidate, cancellationToken).ConfigureAwait(false);
            return RelayMutationResult<RelaySessionCredential>.Success(issued.Credential);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<RelayMutationResult<bool>> CloseOwnSessionAsync(
        RelayPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!RelayAuthorization.Decide(principal, RelayPermission.CloseOwnSession, principal.DeviceId).Allowed)
        {
            return RelayMutationResult<bool>.Reject("not-authorized");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!StillCurrent(principal))
            {
                return RelayMutationResult<bool>.Reject("not-authenticated");
            }

            var now = Now();
            var sessions = _state.Sessions
                .Select(record => record.Session.SessionId == principal.SessionId
                    ? EndIfActive(record, DeviceSessionStatus.Closed, now, "session-closed")
                    : record)
                .ToArray();
            var candidate = new RelayRegistryState(
                true,
                _state.Devices,
                sessions,
                AppendAudit(_state.Audit, new RelayAuditEvent(
                    Guid.NewGuid(),
                    RelayAuditAction.SessionClosed,
                    now,
                    "closed",
                    principal.DeviceId,
                    principal.DeviceId,
                    principal.SessionId)));
            await CommitAsync(candidate, cancellationToken).ConfigureAwait(false);
            return RelayMutationResult<bool>.Success(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<RelayMutationResult<bool>> RevokeDeviceAsync(
        RelayPrincipal owner,
        CompanionDeviceId targetDeviceId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!RelayAuthorization.Decide(owner, RelayPermission.RevokeDevice, targetDeviceId).Allowed)
        {
            return RelayMutationResult<bool>.Reject("not-authorized");
        }

        RelayAuditEvent.ValidateBoundedToken(reason, nameof(reason));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!StillCurrent(owner))
            {
                return RelayMutationResult<bool>.Reject("not-authenticated");
            }

            var devices = _state.Devices.ToList();
            var index = devices.FindIndex(device => device.DeviceId == targetDeviceId);
            if (index < 0 || devices[index].Status != DeviceLifecycleStatus.Active)
            {
                return RelayMutationResult<bool>.Reject("device-not-active");
            }

            var target = devices[index];
            if (target.Role == DeviceAuthorizationRole.Owner &&
                !_state.Devices.Any(device => device.DeviceId != targetDeviceId &&
                                             device.Role == DeviceAuthorizationRole.Owner &&
                                             device.Status == DeviceLifecycleStatus.Active))
            {
                return RelayMutationResult<bool>.Reject("owner-recovery-required");
            }

            var now = Now();
            devices[index] = DeviceLifecycle.Revoke(target, now, reason);
            var sessions = _state.Sessions
                .Select(record => record.Session.DeviceId == targetDeviceId
                    ? EndIfActive(record, DeviceSessionStatus.Revoked, now, reason)
                    : record)
                .ToArray();
            var candidate = new RelayRegistryState(
                true,
                devices,
                sessions,
                AppendAudit(_state.Audit, new RelayAuditEvent(
                    Guid.NewGuid(),
                    RelayAuditAction.DeviceRevoked,
                    now,
                    reason,
                    owner.DeviceId,
                    targetDeviceId)));
            await CommitAsync(candidate, cancellationToken).ConfigureAwait(false);
            return RelayMutationResult<bool>.Success(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Persists replay state before a frame can enter a delivery queue.</summary>
    public async ValueTask<RelayFrameAdmission> AdvanceFrameSequenceAsync(
        RelayPrincipal principal,
        long keyEpoch,
        long senderSequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!RelayAuthorization.Decide(principal, RelayPermission.PublishOpaqueFrames).Allowed)
        {
            return RelayFrameAdmission.Reject("not-authorized");
        }

        if (keyEpoch <= 0 || senderSequence <= 0)
        {
            return RelayFrameAdmission.Reject("sequence-invalid");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!StillCurrent(principal))
            {
                return RelayFrameAdmission.Reject("not-authenticated");
            }

            var sessions = _state.Sessions.ToList();
            var index = sessions.FindIndex(record => record.Session.SessionId == principal.SessionId);
            var current = sessions[index];
            var accepted = keyEpoch == current.KeyEpoch
                ? senderSequence > current.LastSenderSequence
                : keyEpoch > current.KeyEpoch && senderSequence == 1;
            if (!accepted)
            {
                var rejected = new RelayRegistryState(
                    true,
                    _state.Devices,
                    sessions,
                    AppendAudit(_state.Audit, new RelayAuditEvent(
                        Guid.NewGuid(),
                        RelayAuditAction.FrameRejected,
                        Now(),
                        "replay-rejected",
                        principal.DeviceId,
                        principal.DeviceId,
                        principal.SessionId)));
                await CommitAsync(rejected, cancellationToken).ConfigureAwait(false);
                return RelayFrameAdmission.Reject("replay-rejected");
            }

            sessions[index] = current.WithSequence(keyEpoch, senderSequence);
            await CommitAsync(new RelayRegistryState(true, _state.Devices, sessions, _state.Audit), cancellationToken)
                .ConfigureAwait(false);
            return RelayFrameAdmission.Permit;
        }
        finally
        {
            _gate.Release();
        }
    }

    public RelayMutationResult<IReadOnlyList<RelayAuditEvent>> ReadAudit(RelayPrincipal owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return RelayAuthorization.Decide(owner, RelayPermission.ReadSecurityAudit).Allowed && StillCurrent(owner)
            ? RelayMutationResult<IReadOnlyList<RelayAuditEvent>>.Success(_state.Audit.ToArray())
            : RelayMutationResult<IReadOnlyList<RelayAuditEvent>>.Reject("not-authorized");
    }

    /// <summary>Returns only non-secret routing metadata for live sessions on one opaque channel.</summary>
    public IReadOnlyList<RelaySessionRoute> ActiveRoutes(RelayChannelId channelId)
    {
        var now = Now();
        var devices = _state.Devices.ToDictionary(device => device.DeviceId);
        return _state.Sessions
            .Where(record => record.ChannelId == channelId &&
                             record.Session.Status == DeviceSessionStatus.Active &&
                             record.Session.ExpiresUtc > now &&
                             devices.TryGetValue(record.Session.DeviceId, out var device) &&
                             device.Status == DeviceLifecycleStatus.Active &&
                             device.ExpiresUtc > now)
            .Select(record => new RelaySessionRoute(
                record.Session.SessionId,
                record.Session.DeviceId,
                record.ChannelId,
                devices[record.Session.DeviceId].Role,
                record.Session.ExpiresUtc))
            .Take(RelaySecurityBounds.MaximumChannelParticipants)
            .ToArray();
    }

    private bool StillCurrent(RelayPrincipal principal)
    {
        if (!CanAuthenticate)
        {
            return false;
        }

        var session = _state.Sessions.SingleOrDefault(record => record.Session.SessionId == principal.SessionId);
        var device = _state.Devices.SingleOrDefault(item => item.DeviceId == principal.DeviceId);
        return session is not null && device is not null &&
            session.Session.Status == DeviceSessionStatus.Active &&
            device.Status == DeviceLifecycleStatus.Active &&
            session.Session.DeviceId == principal.DeviceId &&
            session.Session.DeviceKeyId == principal.DeviceKeyId &&
            session.ChannelId == principal.ChannelId;
    }

    private async ValueTask CommitAsync(RelayRegistryState candidate, CancellationToken cancellationToken)
    {
        try
        {
            if (_store is not null)
            {
                await _store.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
                _loadStatus = RelayRegistryLoadStatus.PrimaryVerified;
            }
            else if (_loadStatus == RelayRegistryLoadStatus.Uninitialized)
            {
                _loadStatus = RelayRegistryLoadStatus.PrimaryVerified;
            }

            _state = candidate;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or System.Text.Json.JsonException or NotSupportedException or
            ArgumentException or InvalidOperationException)
        {
            CloseInMemory();
            throw new RelayRegistryUnavailableException("Relay security storage is unavailable.", exception);
        }
    }

    private void CloseInMemory()
    {
        _state = RelayRegistryState.Empty;
        _loadStatus = RelayRegistryLoadStatus.Corrupt;
    }

    private (RelaySessionRecord Record, RelaySessionCredential Credential) CreateSession(
        PairedDevice device,
        RelayChannelId channelId,
        CompanionSurfaceKind surface,
        DateTimeOffset now)
    {
        var sessionId = new DeviceSessionId(Guid.NewGuid());
        var expires = new[] { now.Add(RelaySecurityBounds.SessionLifetime), device.ExpiresUtc }.Min();
        var session = new DeviceSession(
            sessionId,
            device.DeviceId,
            device.DeviceKey.KeyId,
            DeviceSessionStatus.Active,
            CompanionTransportKind.EndToEndRelay,
            surface,
            RelayAuthorization.CapabilitiesFor(device.Role),
            now,
            now,
            expires);
        var secret = RelayCsrfProtector.Base64Url(RandomNumberGenerator.GetBytes(32));
        var csrf = RelayCsrfProtector.Issue(sessionId, now, expires - now);
        var digest = CredentialDigest(sessionId, secret);
        var record = new RelaySessionRecord(session, channelId, digest, csrf.DigestBase64Url, csrf.ExpiresUtc);
        return (
            record,
            new RelaySessionCredential(
                sessionId,
                device.DeviceId,
                device.DeviceKey.KeyId,
                channelId,
                secret,
                csrf.Token,
                expires));
    }

    private static RelaySessionRecord EndIfActive(
        RelaySessionRecord record,
        DeviceSessionStatus status,
        DateTimeOffset now,
        string reason) => record.Session.Status == DeviceSessionStatus.Active
            ? record.WithSession(DeviceLifecycle.EndSession(record.Session, status, now, reason))
            : record;

    private static IReadOnlyList<RelayAuditEvent> AppendAudit(
        IEnumerable<RelayAuditEvent> existing,
        RelayAuditEvent next) =>
        existing.Append(next).TakeLast(RelaySecurityBounds.MaximumAuditEvents).ToArray();

    private static bool TryCredentialDigest(
        DeviceSessionId sessionId,
        string? credential,
        out byte[] digest)
    {
        digest = [];
        if (string.IsNullOrWhiteSpace(credential) || credential.Length > 64)
        {
            return false;
        }

        try
        {
            if (RelayCsrfProtector.DecodeBase64Url(credential).Length != 32)
            {
                return false;
            }

            digest = RelayCsrfProtector.DecodeBase64Url(CredentialDigest(sessionId, credential));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string CredentialDigest(DeviceSessionId sessionId, string secret) =>
        RelayCsrfProtector.Digest(sessionId, secret);

    private DateTimeOffset Now()
    {
        var utc = _timeProvider.GetUtcNow().ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }
}

public sealed class RelayRegistryUnavailableException : Exception
{
    public RelayRegistryUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
