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
    CompanionProtocolVersion ProtocolVersion,
    long KeyEpoch,
    RelayCipherSuite CipherSuite,
    DeviceAuthorizationRole Role,
    CompanionSurfaceKind Surface,
    DateTimeOffset ExpiresUtc);

/// <summary>Authoritative relay registry for cryptographic devices, sessions, replay, and audit.</summary>
/// <remarks>
/// Device ids, session ids, channels, epochs, and key ids come only from a completed signed #276
/// pairing or resume. The relay adds an outer random cookie credential, stores only its digest,
/// and persists replay movement before a frame can be queued. A storage failure closes the in-
/// memory authority instead of continuing with an unpersisted revocation or replay counter.
/// </remarks>
public sealed class RelayDeviceRegistry
{
    private static readonly TimeSpan DeviceLifetime = TimeSpan.FromDays(30);
    private readonly TimeProvider _timeProvider;
    private readonly OwnerRecoveryProtector _recovery;
    private readonly VerifiedRelayRegistryStore? _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile RelayRegistryState _state;
    private volatile RelayRegistryLoadStatus _loadStatus;

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
        _loadStatus is RelayRegistryLoadStatus.PrimaryVerified or RelayRegistryLoadStatus.BackupRestored;

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

    /// <summary>Replaces a missing owner after both operator recovery and device-key proof.</summary>
    public async ValueTask<RelayMutationResult<RelaySessionCredential>> RecoverOwnerAsync(
        OwnerRecoveryGrant grant,
        PairingAttempt completedPairing,
        CompanionSurfaceKind surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(completedPairing);
        if (surface is not (CompanionSurfaceKind.Desktop or CompanionSurfaceKind.DesktopBrowser))
        {
            return RelayMutationResult<RelaySessionCredential>.Reject("recovery-rejected");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Now();
            // [V2 rough package 48] One code for five causes cost an evening: a desktop was told
            // "recovery-rejected" for a clock difference, and nothing on either side could tell
            // that apart from a wrong grant or an owner that already existed. Each clause names
            // itself now. None of them reveals anything a caller does not already hold — this route
            // is behind the operator's admin key, and the material is the caller's own.
            if (!TryCompletedPairing(completedPairing, now, out var deviceKey, out var establishment))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("claim-not-completed");
            }

            if (grant.NewOwnerDeviceId != establishment.Assignment.DeviceId ||
                grant.NewOwnerKeyId != deviceKey.KeyId)
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("claim-grant-mismatch");
            }

            if (_loadStatus != RelayRegistryLoadStatus.Corrupt &&
                _state.Devices.Any(device => device.Role == DeviceAuthorizationRole.Owner && IsLive(device, now)))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("owner-already-live");
            }

            if (!_recovery.TryConsume(grant))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("recovery-grant-rejected");
            }

            var owner = CreateDevice(deviceKey, establishment, DeviceAuthorizationRole.Owner);
            var issued = IssueSession(owner, establishment, surface, now);
            var retainedAudit = _loadStatus == RelayRegistryLoadStatus.Corrupt ? [] : _state.Audit;
            var audit = AppendAudit(retainedAudit, new RelayAuditEvent(
                Guid.NewGuid(),
                RelayAuditAction.OwnerRecovered,
                now,
                "recovered",
                subjectDeviceId: owner.DeviceId,
                sessionId: issued.Credential.SessionId));
            await CommitAsync(
                new RelayRegistryState(true, [owner], [issued.Record], audit),
                cancellationToken).ConfigureAwait(false);
            return RelayMutationResult<RelaySessionCredential>.Success(issued.Credential);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The same desktop claiming again, recognised by the key it claimed with: no admin key, at
    /// any time, and it replaces that owner's own earlier session and nothing else.
    /// </summary>
    /// <remarks>
    /// [#289] An owner session lives twelve hours and an owner two idle, so every morning the
    /// relay wanted its admin key typed again, and <see cref="RecoverOwnerAsync"/> then wiped every
    /// paired tablet along with the old owner. Neither bound is what was wrong: they are what
    /// stops an admin-key holder displacing a live owner. What was missing is that the relay
    /// could not tell the owner coming back from a stranger. It can: the owner's public key is on
    /// record from the first claim, and <paramref name="completedPairing"/> is signed by whoever
    /// holds the private half — its challenge's signature covers a transcript that contains the
    /// nonce this relay just issued (the caller has already spent it), so it cannot be a replay.
    /// A caller whose key is not the one on record is refused here and is left with
    /// <see cref="RecoverOwnerAsync"/>, exactly as before.
    /// </remarks>
    public async ValueTask<RelayMutationResult<RelaySessionCredential>> ResumeOwnerByKeyAsync(
        PairingAttempt completedPairing,
        CompanionSurfaceKind surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completedPairing);
        if (surface is not (CompanionSurfaceKind.Desktop or CompanionSurfaceKind.DesktopBrowser))
        {
            return RelayMutationResult<RelaySessionCredential>.Reject("claim-not-completed");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Now();
            if (!CanAuthenticate)
            {
                // Never claimed, or storage that could not be verified: no key is on record.
                return RelayMutationResult<RelaySessionCredential>.Reject("owner-unknown");
            }

            if (!TryCompletedPairing(completedPairing, now, out var deviceKey, out var establishment))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("claim-not-completed");
            }

            // Expired is "has not been heard from", which is the case this exists for. Revoked and
            // replaced are decisions somebody made, and a key does not undo those.
            var recorded = _state.Devices
                .Where(device => device.Role == DeviceAuthorizationRole.Owner &&
                    device.Status is DeviceLifecycleStatus.Active or DeviceLifecycleStatus.Expired)
                .OrderByDescending(device => device.CreatedUtc)
                .FirstOrDefault();
            if (recorded is null)
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("owner-unknown");
            }

            // The key named in the request is the one on record, and it is also the key that
            // signed: a desktop's relay device key is its identity key, re-encoded.
            if (recorded.DeviceKey.KeyId != deviceKey.KeyId ||
                !string.Equals(recorded.DeviceKey.CosePublicKeyBase64Url, deviceKey.CosePublicKeyBase64Url, StringComparison.Ordinal) ||
                completedPairing.Challenge!.DesktopIdentityKey != completedPairing.Offer.DesktopIdentityKey ||
                !PairingCryptography.IsSameKey(recorded.DeviceKey, completedPairing.Offer.DesktopIdentityKey))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("owner-key-mismatch");
            }

            var retainedDevices = _state.Devices.Where(device => device.DeviceId != recorded.DeviceId).ToList();
            var retainedSessions = _state.Sessions
                .Where(record => record.Session.DeviceId != recorded.DeviceId)
                .ToList();
            if (retainedDevices.Any(device => device.DeviceId == establishment.Assignment.DeviceId) ||
                retainedSessions.Any(record => CollidesWith(record, establishment)))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("claim-collision");
            }

            var owner = CreateDevice(deviceKey, establishment, DeviceAuthorizationRole.Owner);
            var issued = IssueSession(owner, establishment, surface, now);
            var sessions = MakeRoomForSession(retainedSessions);
            sessions.Add(issued.Record);
            await CommitAsync(
                new RelayRegistryState(
                    true,
                    [owner, .. retainedDevices],
                    sessions,
                    AppendAudit(_state.Audit, new RelayAuditEvent(
                        Guid.NewGuid(),
                        RelayAuditAction.OwnerRecovered,
                        now,
                        "resumed-by-key",
                        subjectDeviceId: owner.DeviceId,
                        sessionId: issued.Credential.SessionId))),
                cancellationToken).ConfigureAwait(false);
            return RelayMutationResult<RelaySessionCredential>.Success(issued.Credential);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The paired (never owner) device this relay has on record under a key, whatever became of
    /// it: a caller reads <see cref="RelayDeviceRecord.Status"/> to tell "has been away" from
    /// "was revoked".
    /// </summary>
    public RelayDeviceRecord? FindPairedDeviceByKey(DeviceKeyId keyId) =>
        CanAuthenticate
            ? _state.Devices.FirstOrDefault(device =>
                device.Role != DeviceAuthorizationRole.Owner && device.DeviceKey.KeyId == keyId)
            : null;

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

            var sessions = _state.Sessions.ToList();
            var sessionIndex = sessions.FindIndex(record => record.Session.SessionId == sessionId);
            if (sessionIndex < 0)
            {
                return RelayAuthenticationResult.Reject();
            }

            var record = sessions[sessionIndex];
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
            if (!IsLive(record.Session, device, now))
            {
                var expired = ExpireForAuthentication(devices, sessions, deviceIndex, sessionIndex, now);
                await CommitAsync(expired, cancellationToken).ConfigureAwait(false);
                return RelayAuthenticationResult.Reject("expired");
            }

            var touchedDevice = Touch(device, now, device.LastKeyEpoch);
            var touchedSession = Touch(record.Session, now);
            devices[deviceIndex] = touchedDevice;
            sessions[sessionIndex] = record.WithSession(touchedSession);
            await CommitAsync(
                new RelayRegistryState(true, devices, sessions, _state.Audit),
                cancellationToken).ConfigureAwait(false);

            return new RelayAuthenticationResult(
                true,
                "authenticated",
                Principal(touchedDevice, touchedSession, now));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualDigest);
            _gate.Release();
        }
    }

    /// <summary>Checks and rotates the memory-only CSRF nonce after each cookie mutation.</summary>
    public async ValueTask<RelayMutationResult<string>> ValidateAndRotateCsrfAsync(
        RelayPrincipal principal,
        string? presentedToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Now();
            if (!StillCurrent(principal, now, _state))
            {
                return RelayMutationResult<string>.Reject("not-authenticated");
            }

            var sessions = _state.Sessions.ToList();
            var index = sessions.FindIndex(record => record.Session.SessionId == principal.SessionId);
            var current = sessions[index];
            if (!RelayCsrfProtector.Validate(
                    principal.SessionId,
                    presentedToken,
                    current.CsrfDigestBase64Url,
                    current.CsrfExpiresUtc,
                    now))
            {
                await CommitAsync(
                    new RelayRegistryState(
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
                            principal.SessionId))),
                    cancellationToken).ConfigureAwait(false);
                return RelayMutationResult<string>.Reject("csrf-rejected");
            }

            var next = RelayCsrfProtector.Issue(
                principal.SessionId,
                now,
                current.Session.ExpiresUtc - now);
            sessions[index] = current.WithCsrf(next);
            await CommitAsync(
                new RelayRegistryState(true, _state.Devices, sessions, _state.Audit),
                cancellationToken).ConfigureAwait(false);
            return RelayMutationResult<string>.Success(next.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<RelayMutationResult<RelaySessionCredential>> AddPairedDeviceAsync(
        RelayPrincipal owner,
        PairingAttempt completedPairing,
        DeviceAuthorizationRole role,
        CompanionSurfaceKind surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(completedPairing);
        if (role is not (DeviceAuthorizationRole.Member or DeviceAuthorizationRole.Observer) ||
            !Enum.IsDefined(surface))
        {
            return RelayMutationResult<RelaySessionCredential>.Reject("pairing-rejected");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Now();
            if (!RelayAuthorization.Decide(owner, RelayPermission.ApprovePairingInvitation, now).Allowed ||
                !StillCurrent(owner, now, _state))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("not-authorized");
            }

            if (!TryCompletedPairing(completedPairing, now, out var deviceKey, out var establishment))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("pairing-incomplete");
            }

            // [#290] The same tablet pairing again. Its device key never changes, so once this
            // registry kept an owner's devices across a restart instead of being wiped by every
            // re-claim, a tablet that had expired, been revoked, or lost its page could never be
            // registered again: its old record collided with the new one for good. The live owner
            // presenting a freshly completed pairing for that key is the authority saying the old
            // record is finished, so it is dropped here rather than left to block its successor.
            // Never an owner's own record: that one changes hands by recovery alone.
            var retainedDevices = _state.Devices
                .Where(existing => existing.Role == DeviceAuthorizationRole.Owner ||
                    existing.DeviceKey.KeyId != deviceKey.KeyId)
                .ToList();
            var retainedIds = retainedDevices.Select(existing => existing.DeviceId).ToHashSet();
            var retainedSessions = _state.Sessions
                .Where(record => retainedIds.Contains(record.Session.DeviceId))
                .ToList();
            if (retainedDevices.Count >= ProtocolBounds.MaxDevices ||
                retainedDevices.Any(existing => existing.DeviceId == establishment.Assignment.DeviceId ||
                    existing.DeviceKey.KeyId == deviceKey.KeyId) ||
                retainedSessions.Any(record => CollidesWith(record, establishment)))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("device-limit-or-duplicate");
            }

            var device = CreateDevice(deviceKey, establishment, role);
            var issued = IssueSession(device, establishment, surface, now);
            var sessions = MakeRoomForSession(retainedSessions);
            sessions.Add(issued.Record);
            await CommitAsync(
                new RelayRegistryState(
                    true,
                    [.. retainedDevices, device],
                    sessions,
                    AppendAudit(_state.Audit, new RelayAuditEvent(
                        Guid.NewGuid(),
                        RelayAuditAction.DevicePaired,
                        now,
                        "paired",
                        owner.DeviceId,
                        device.DeviceId,
                        issued.Credential.SessionId))),
                cancellationToken).ConfigureAwait(false);
            return RelayMutationResult<RelaySessionCredential>.Success(issued.Credential);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<RelayMutationResult<RelaySessionCredential>> RotateSessionAsync(
        RelayPrincipal principal,
        SessionResumeAttempt completedResume,
        CompanionSurfaceKind surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(completedResume);
        if (!Enum.IsDefined(surface))
        {
            return RelayMutationResult<RelaySessionCredential>.Reject("resume-rejected");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Now();
            if (!RelayAuthorization.Decide(
                    principal,
                    RelayPermission.RotateOwnSession,
                    now,
                    principal.DeviceId).Allowed ||
                !StillCurrent(principal, now, _state))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("not-authenticated");
            }

            if (completedResume.Stage != SessionResumeStage.Completed ||
                completedResume.Establishment is not { } establishment ||
                establishment.Purpose != HandshakePurpose.SessionResume ||
                establishment.EstablishedUtc > now ||
                now - establishment.EstablishedUtc > ProtocolBounds.HandshakeChallengeLifetime ||
                establishment.Assignment.SessionExpiresUtc <= now ||
                !CompanionProtocolVersion.Current.CanRead(establishment.Assignment.ProtocolVersion))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("resume-incomplete");
            }

            var devices = _state.Devices.ToList();
            var deviceIndex = devices.FindIndex(device => device.DeviceId == principal.DeviceId);
            var device = devices[deviceIndex];
            if (device.Role == DeviceAuthorizationRole.Owner &&
                surface is not (CompanionSurfaceKind.Desktop or CompanionSurfaceKind.DesktopBrowser))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("resume-rejected");
            }

            if (completedResume.Request.DeviceId != device.DeviceId ||
                completedResume.Request.DeviceKeyId != device.DeviceKey.KeyId ||
                establishment.Assignment.DeviceId != device.DeviceId ||
                establishment.DeviceKeyId != device.DeviceKey.KeyId ||
                establishment.Assignment.KeyEpoch <= device.LastKeyEpoch ||
                establishment.Assignment.SessionExpiresUtc > device.ExpiresUtc ||
                HasSessionCollision(establishment))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("resume-rejected");
            }

            devices[deviceIndex] = Touch(device, establishment.EstablishedUtc, establishment.Assignment.KeyEpoch);
            var sessions = _state.Sessions
                .Select(record => record.Session.DeviceId == device.DeviceId
                    ? EndIfActive(record, DeviceSessionStatus.Replaced, now, "session-rotated")
                    : record)
                .ToList();
            sessions = MakeRoomForSession(sessions);
            var issued = IssueSession(devices[deviceIndex], establishment, surface, now);
            sessions.Add(issued.Record);
            await CommitAsync(
                new RelayRegistryState(
                    true,
                    devices,
                    sessions,
                    AppendAudit(_state.Audit, new RelayAuditEvent(
                        Guid.NewGuid(),
                        RelayAuditAction.SessionRotated,
                        now,
                        "rotated",
                        principal.DeviceId,
                        principal.DeviceId,
                        issued.Credential.SessionId))),
                cancellationToken).ConfigureAwait(false);
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Now();
            if (!RelayAuthorization.Decide(
                    principal,
                    RelayPermission.CloseOwnSession,
                    now,
                    principal.DeviceId).Allowed ||
                !StillCurrent(principal, now, _state))
            {
                return RelayMutationResult<bool>.Reject("not-authenticated");
            }

            var sessions = _state.Sessions
                .Select(record => record.Session.SessionId == principal.SessionId
                    ? EndIfActive(record, DeviceSessionStatus.Closed, now, "session-closed")
                    : record)
                .ToArray();
            await CommitAsync(
                new RelayRegistryState(
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
                        principal.SessionId))),
                cancellationToken).ConfigureAwait(false);
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
        RelayAuditEvent.ValidateBoundedToken(reason, nameof(reason));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Now();
            if (!RelayAuthorization.Decide(owner, RelayPermission.RevokeDevice, now, targetDeviceId).Allowed ||
                !StillCurrent(owner, now, _state))
            {
                return RelayMutationResult<bool>.Reject("not-authorized");
            }

            var devices = _state.Devices.ToList();
            var index = devices.FindIndex(device => device.DeviceId == targetDeviceId);
            // [#289] A device that has merely been away is still revoked, and the record says so.
            // This used to refuse anything not live, which was harmless while a device that had
            // been away could only come back by pairing again in front of the owner. It can now
            // come back on its key alone, so "expired" must not be where a revoke gets lost.
            if (index < 0 ||
                devices[index].Status is not (DeviceLifecycleStatus.Active or DeviceLifecycleStatus.Expired))
            {
                return RelayMutationResult<bool>.Reject("device-not-active");
            }

            var target = devices[index];
            if (target.Role == DeviceAuthorizationRole.Owner &&
                !devices.Any(device => device.DeviceId != targetDeviceId &&
                    device.Role == DeviceAuthorizationRole.Owner && IsLive(device, now)))
            {
                return RelayMutationResult<bool>.Reject("owner-recovery-required");
            }

            devices[index] = target.Status == DeviceLifecycleStatus.Active
                ? Transition(target, DeviceLifecycleStatus.Revoked, now, reason)
                : new RelayDeviceRecord(
                    target.DeviceId,
                    target.DeviceKey,
                    target.Role,
                    target.Capabilities,
                    DeviceLifecycleStatus.Revoked,
                    target.CreatedUtc,
                    target.LastUsedUtc,
                    target.LastKeyEpoch,
                    target.ExpiresUtc,
                    now,
                    lifecycleReason: reason);
            var sessions = _state.Sessions
                .Select(record => record.Session.DeviceId == targetDeviceId
                    ? EndIfActive(record, DeviceSessionStatus.Revoked, now, reason)
                    : record)
                .ToArray();
            await CommitAsync(
                new RelayRegistryState(
                    true,
                    devices,
                    sessions,
                    AppendAudit(_state.Audit, new RelayAuditEvent(
                        Guid.NewGuid(),
                        RelayAuditAction.DeviceRevoked,
                        now,
                        reason,
                        owner.DeviceId,
                        targetDeviceId))),
                cancellationToken).ConfigureAwait(false);
            return RelayMutationResult<bool>.Success(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<RelayMutationResult<RelaySessionCredential>> ReplaceDeviceAsync(
        RelayPrincipal owner,
        CompanionDeviceId targetDeviceId,
        PairingAttempt completedReplacement,
        CompanionSurfaceKind surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(completedReplacement);
        if (!Enum.IsDefined(surface))
        {
            return RelayMutationResult<RelaySessionCredential>.Reject("replacement-rejected");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Now();
            if (!RelayAuthorization.Decide(owner, RelayPermission.ReplaceDevice, now, targetDeviceId).Allowed ||
                !StillCurrent(owner, now, _state))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("not-authorized");
            }

            var devices = _state.Devices.ToList();
            var targetIndex = devices.FindIndex(device => device.DeviceId == targetDeviceId);
            if (targetIndex < 0 || !IsLive(devices[targetIndex], now) ||
                !TryCompletedPairing(completedReplacement, now, out var deviceKey, out var establishment) ||
                _state.Devices.Count >= ProtocolBounds.MaxDevices ||
                HasIdentityCollision(deviceKey, establishment))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("replacement-rejected");
            }

            var target = devices[targetIndex];
            if (target.Role == DeviceAuthorizationRole.Owner &&
                surface is not (CompanionSurfaceKind.Desktop or CompanionSurfaceKind.DesktopBrowser))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("replacement-rejected");
            }

            var replacement = CreateDevice(deviceKey, establishment, target.Role, target.Capabilities);
            devices[targetIndex] = Transition(
                target,
                DeviceLifecycleStatus.Replaced,
                now,
                "device-replaced",
                replacement.DeviceId);
            devices.Add(replacement);
            var sessions = _state.Sessions
                .Select(record => record.Session.DeviceId == targetDeviceId
                    ? EndIfActive(record, DeviceSessionStatus.Replaced, now, "device-replaced")
                    : record)
                .ToList();
            sessions = MakeRoomForSession(sessions);
            var issued = IssueSession(replacement, establishment, surface, now);
            sessions.Add(issued.Record);
            await CommitAsync(
                new RelayRegistryState(
                    true,
                    devices,
                    sessions,
                    AppendAudit(_state.Audit, new RelayAuditEvent(
                        Guid.NewGuid(),
                        RelayAuditAction.DeviceReplaced,
                        now,
                        "replaced",
                        owner.DeviceId,
                        targetDeviceId,
                        issued.Credential.SessionId))),
                cancellationToken).ConfigureAwait(false);
            return RelayMutationResult<RelaySessionCredential>.Success(issued.Credential);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Persists target-session replay state before the hub enqueues the frame.</summary>
    public async ValueTask<RelayFrameAdmission> AdvanceFrameSequenceAsync(
        RelayPrincipal principal,
        OpaqueRelayFrame frame,
        PairingTrafficDirection direction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(frame);
        if (!Enum.IsDefined(direction))
        {
            return RelayFrameAdmission.Reject("sequence-invalid");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Now();
            if (!RelayAuthorization.Decide(principal, RelayPermission.PublishOpaqueFrames, now).Allowed ||
                !StillCurrent(principal, now, _state))
            {
                return RelayFrameAdmission.Reject("not-authenticated");
            }

            var sessions = _state.Sessions.ToList();
            var targetIndex = sessions.FindIndex(record => record.Session.SessionId == frame.SessionId);
            if (targetIndex < 0)
            {
                return RelayFrameAdmission.Reject("route-rejected");
            }

            var target = sessions[targetIndex];
            var devices = _state.Devices.ToList();
            var targetDevice = devices.Single(device => device.DeviceId == target.Session.DeviceId);
            var directionAllowed = direction switch
            {
                PairingTrafficDirection.TabletToDesktop =>
                    principal.SessionId == target.Session.SessionId &&
                    principal.DeviceId == target.Session.DeviceId,
                PairingTrafficDirection.DesktopToTablet =>
                    principal.Role == DeviceAuthorizationRole.Owner &&
                    principal.Capabilities.Contains(DeviceCapability.ManageDevices) &&
                    targetDevice.Role is DeviceAuthorizationRole.Member or DeviceAuthorizationRole.Observer &&
                    target.Session.DeviceId != principal.DeviceId,
                _ => false,
            };
            var frameBound = target.Session.Status == DeviceSessionStatus.Active &&
                IsLive(target.Session, targetDevice, now) &&
                frame.ChannelId == target.Session.RelayChannelId &&
                frame.ProtocolVersion == target.Session.ProtocolVersion &&
                frame.KeyEpoch == target.Session.KeyEpoch &&
                frame.CipherSuite == target.Session.CipherSuite &&
                frame.ExpiresUtc > now &&
                frame.IssuedUtc <= now.Add(ProtocolBounds.MaxClientClockSkew);
            if (!directionAllowed || !frameBound)
            {
                return await RejectFrameAsync(
                    principal,
                    sessions,
                    now,
                    directionAllowed ? "frame-binding-rejected" : "route-rejected",
                    cancellationToken).ConfigureAwait(false);
            }

            if (frame.SenderSequence <= target.LastSequence(direction))
            {
                return await RejectFrameAsync(
                    principal,
                    sessions,
                    now,
                    "replay-rejected",
                    cancellationToken).ConfigureAwait(false);
            }

            sessions[targetIndex] = target.WithSequence(direction, frame.SenderSequence);
            var actorDeviceIndex = devices.FindIndex(device => device.DeviceId == principal.DeviceId);
            var actorSessionIndex = sessions.FindIndex(record => record.Session.SessionId == principal.SessionId);
            devices[actorDeviceIndex] = Touch(devices[actorDeviceIndex], now, devices[actorDeviceIndex].LastKeyEpoch);
            sessions[actorSessionIndex] = sessions[actorSessionIndex].WithSession(
                Touch(sessions[actorSessionIndex].Session, now));
            await CommitAsync(
                new RelayRegistryState(
                    true,
                    devices,
                    sessions,
                    AppendAudit(_state.Audit, new RelayAuditEvent(
                        Guid.NewGuid(),
                        RelayAuditAction.FrameAccepted,
                        now,
                        "accepted",
                        principal.DeviceId,
                        target.Session.DeviceId,
                        target.Session.SessionId))),
                cancellationToken).ConfigureAwait(false);
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
        var now = Now();
        return RelayAuthorization.Decide(owner, RelayPermission.ReadSecurityAudit, now).Allowed &&
               StillCurrent(owner, now, _state)
            ? RelayMutationResult<IReadOnlyList<RelayAuditEvent>>.Success(_state.Audit.ToArray())
            : RelayMutationResult<IReadOnlyList<RelayAuditEvent>>.Reject("not-authorized");
    }

    public bool IsCurrent(RelayPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return StillCurrent(principal, Now(), _state);
    }

    public RelaySessionRoute? ActiveRoute(DeviceSessionId sessionId)
    {
        var now = Now();
        var state = _state;
        var record = state.Sessions.SingleOrDefault(item => item.Session.SessionId == sessionId);
        if (record is null)
        {
            return null;
        }

        var device = state.Devices.SingleOrDefault(item => item.DeviceId == record.Session.DeviceId);
        return device is not null && IsLive(record.Session, device, now)
            ? Route(record.Session, device)
            : null;
    }

    public IReadOnlyList<RelaySessionRoute> ActiveOwners()
    {
        var now = Now();
        var state = _state;
        var devices = state.Devices.ToDictionary(device => device.DeviceId);
        return state.Sessions
            .Where(record => devices.TryGetValue(record.Session.DeviceId, out var device) &&
                device.Role == DeviceAuthorizationRole.Owner &&
                record.Session.Surface is CompanionSurfaceKind.Desktop or CompanionSurfaceKind.DesktopBrowser &&
                IsLive(record.Session, device, now))
            .Select(record => Route(record.Session, devices[record.Session.DeviceId]))
            .Take(RelaySecurityBounds.MaximumChannelParticipants)
            .ToArray();
    }

    public IReadOnlyList<RelaySessionRoute> ActiveRoutes(RelayChannelId channelId)
    {
        var now = Now();
        var state = _state;
        var devices = state.Devices.ToDictionary(device => device.DeviceId);
        return state.Sessions
            .Where(record => record.ChannelId == channelId &&
                devices.TryGetValue(record.Session.DeviceId, out var device) &&
                IsLive(record.Session, device, now))
            .Select(record => Route(record.Session, devices[record.Session.DeviceId]))
            .Take(RelaySecurityBounds.MaximumChannelParticipants)
            .ToArray();
    }

    public async ValueTask<int> SweepExpiredAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!CanAuthenticate)
            {
                return 0;
            }

            var now = Now();
            var devices = _state.Devices.ToList();
            var sessions = _state.Sessions.ToList();
            var audit = _state.Audit;
            var changed = 0;
            for (var index = 0; index < devices.Count; index++)
            {
                var device = devices[index];
                if (device.Status != DeviceLifecycleStatus.Active || IsLive(device, now))
                {
                    continue;
                }

                devices[index] = Transition(device, DeviceLifecycleStatus.Expired, now, "device-expired");
                for (var sessionIndex = 0; sessionIndex < sessions.Count; sessionIndex++)
                {
                    if (sessions[sessionIndex].Session.DeviceId == device.DeviceId)
                    {
                        sessions[sessionIndex] = EndIfActive(
                            sessions[sessionIndex],
                            DeviceSessionStatus.Expired,
                            now,
                            "device-expired");
                    }
                }

                audit = AppendAudit(audit, new RelayAuditEvent(
                    Guid.NewGuid(),
                    RelayAuditAction.DeviceExpired,
                    now,
                    "expired",
                    subjectDeviceId: device.DeviceId));
                changed++;
            }

            for (var index = 0; index < sessions.Count; index++)
            {
                var record = sessions[index];
                if (record.Session.Status == DeviceSessionStatus.Active && now >= record.Session.ExpiresUtc)
                {
                    sessions[index] = EndIfActive(record, DeviceSessionStatus.Expired, now, "session-expired");
                    changed++;
                }
            }

            if (changed != 0)
            {
                await CommitAsync(
                    new RelayRegistryState(true, devices, sessions, audit),
                    cancellationToken).ConfigureAwait(false);
            }

            return changed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<RelayFrameAdmission> RejectFrameAsync(
        RelayPrincipal principal,
        IReadOnlyList<RelaySessionRecord> sessions,
        DateTimeOffset now,
        string code,
        CancellationToken cancellationToken)
    {
        await CommitAsync(
            new RelayRegistryState(
                true,
                _state.Devices,
                sessions,
                AppendAudit(_state.Audit, new RelayAuditEvent(
                    Guid.NewGuid(),
                    RelayAuditAction.FrameRejected,
                    now,
                    code,
                    principal.DeviceId,
                    principal.DeviceId,
                    principal.SessionId))),
            cancellationToken).ConfigureAwait(false);
        return RelayFrameAdmission.Reject(code);
    }

    private RelayRegistryState ExpireForAuthentication(
        List<RelayDeviceRecord> devices,
        List<RelaySessionRecord> sessions,
        int deviceIndex,
        int sessionIndex,
        DateTimeOffset now)
    {
        var device = devices[deviceIndex];
        var deviceExpired = !IsLive(device, now);
        if (deviceExpired && device.Status == DeviceLifecycleStatus.Active)
        {
            devices[deviceIndex] = Transition(device, DeviceLifecycleStatus.Expired, now, "device-expired");
            for (var index = 0; index < sessions.Count; index++)
            {
                if (sessions[index].Session.DeviceId == device.DeviceId)
                {
                    sessions[index] = EndIfActive(
                        sessions[index],
                        DeviceSessionStatus.Expired,
                        now,
                        "device-expired");
                }
            }
        }
        else
        {
            sessions[sessionIndex] = EndIfActive(
                sessions[sessionIndex],
                DeviceSessionStatus.Expired,
                now,
                "session-expired");
        }

        return new RelayRegistryState(
            true,
            devices,
            sessions,
            AppendAudit(_state.Audit, new RelayAuditEvent(
                Guid.NewGuid(),
                RelayAuditAction.SessionRejected,
                now,
                "expired",
                subjectDeviceId: device.DeviceId,
                sessionId: sessions[sessionIndex].Session.SessionId)));
    }

    private bool HasIdentityCollision(DevicePublicKey key, SessionEstablished establishment) =>
        _state.Devices.Any(device => device.DeviceId == establishment.Assignment.DeviceId ||
            device.DeviceKey.KeyId == key.KeyId) ||
        HasSessionCollision(establishment);

    private bool HasSessionCollision(SessionEstablished establishment) =>
        _state.Sessions.Any(record => CollidesWith(record, establishment));

    private static bool CollidesWith(RelaySessionRecord record, SessionEstablished establishment) =>
        record.Session.SessionId == establishment.Assignment.SessionId ||
        record.ChannelId == establishment.Assignment.RelayChannelId ||
        record.Session.Establishment.ChallengeId == establishment.ChallengeId ||
        string.Equals(
            record.Session.TranscriptHashBase64Url,
            establishment.TranscriptHashBase64Url,
            StringComparison.Ordinal);

    private static bool TryCompletedPairing(
        PairingAttempt attempt,
        DateTimeOffset now,
        out DevicePublicKey deviceKey,
        out SessionEstablished establishment)
    {
        deviceKey = null!;
        establishment = null!;
        // The establishment was timestamped on another machine — the desktop that built it, or the
        // tablet whose session it is — so it is compared with the same one-minute tolerance every
        // other cross-machine timestamp in this relay uses (CompanionPairingMailbox.RegisterOffer's
        // offer check, OwnerRecoveryProtector.TryConsume's grant check). It used to require
        // `EstablishedUtc <= now` exactly, with no tolerance at all: a desktop whose clock was
        // 250 ms ahead of the relay's had every owner claim refused as "recovery-rejected", which
        // is two machines that are not NTP-tight with each other, which is most of them. Measured
        // against the clamped instant so a future-dated establishment cannot buy extra lifetime.
        if (attempt.Stage != PairingAttemptStage.Completed || attempt.Request is null ||
            attempt.Establishment is not { } completed || completed.Purpose != HandshakePurpose.Pairing ||
            completed.DeviceKeyId != attempt.Request.DeviceKey.KeyId ||
            completed.Assignment.ProtocolVersion != attempt.Request.NegotiatedVersion ||
            !CompanionProtocolVersion.Current.CanRead(completed.Assignment.ProtocolVersion) ||
            completed.EstablishedUtc > now.Add(ProtocolBounds.MaxClientClockSkew) ||
            now - Earliest(completed.EstablishedUtc, now) > ProtocolBounds.MaximumPairingLifetime ||
            completed.Assignment.SessionExpiresUtc <= now)
        {
            return false;
        }

        deviceKey = attempt.Request.DeviceKey;
        establishment = completed;
        return true;
    }

    /// <summary>The establishment's own instant, or the relay's if it is dated in the future.</summary>
    private static DateTimeOffset Earliest(DateTimeOffset establishedUtc, DateTimeOffset now) =>
        establishedUtc < now ? establishedUtc : now;

    private static RelayDeviceRecord CreateDevice(
        DevicePublicKey key,
        SessionEstablished establishment,
        DeviceAuthorizationRole role,
        IReadOnlyList<DeviceCapability>? capabilities = null)
    {
        var created = establishment.EstablishedUtc;
        return new RelayDeviceRecord(
            establishment.Assignment.DeviceId,
            key,
            role,
            capabilities ?? RelayAuthorization.CapabilitiesFor(role),
            DeviceLifecycleStatus.Active,
            created,
            created,
            establishment.Assignment.KeyEpoch,
            created.Add(DeviceLifetime),
            created);
    }

    private static RelayDeviceRecord Touch(RelayDeviceRecord device, DateTimeOffset usedUtc, long keyEpoch) => new(
        device.DeviceId,
        device.DeviceKey,
        device.Role,
        device.Capabilities,
        device.Status,
        device.CreatedUtc,
        usedUtc > device.LastUsedUtc ? usedUtc : device.LastUsedUtc,
        keyEpoch,
        device.ExpiresUtc,
        device.StatusChangedUtc,
        device.ReplacedByDeviceId,
        device.LifecycleReason);

    private static DeviceSession Touch(DeviceSession session, DateTimeOffset usedUtc) => new(
        session.Establishment,
        session.Status,
        session.Transport,
        session.Surface,
        session.Capabilities,
        usedUtc > session.LastUsedUtc ? usedUtc : session.LastUsedUtc);

    private static RelayDeviceRecord Transition(
        RelayDeviceRecord device,
        DeviceLifecycleStatus status,
        DateTimeOffset now,
        string reason,
        CompanionDeviceId? replacement = null)
    {
        if (device.Status != DeviceLifecycleStatus.Active || status == DeviceLifecycleStatus.Active)
        {
            throw new InvalidOperationException("Only an active relay device can enter a terminal state.");
        }

        return new RelayDeviceRecord(
            device.DeviceId,
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

    private static bool IsLive(RelayDeviceRecord device, DateTimeOffset now) =>
        device.Status == DeviceLifecycleStatus.Active &&
        now >= device.CreatedUtc &&
        now >= device.LastUsedUtc &&
        now < device.ExpiresUtc &&
        now - device.LastUsedUtc < ProtocolBounds.DeviceInactivityExpiry;

    private static bool IsLive(DeviceSession session, RelayDeviceRecord device, DateTimeOffset now) =>
        session.Status == DeviceSessionStatus.Active &&
        now >= session.CreatedUtc &&
        now >= session.LastUsedUtc &&
        now < session.ExpiresUtc &&
        CompanionProtocolVersion.Current.CanRead(session.ProtocolVersion) &&
        session.DeviceId == device.DeviceId &&
        session.DeviceKeyId == device.DeviceKey.KeyId &&
        session.KeyEpoch == device.LastKeyEpoch &&
        IsLive(device, now);

    private static RelayPrincipal Principal(
        RelayDeviceRecord device,
        DeviceSession session,
        DateTimeOffset authenticatedUtc) => new(
        device.DeviceId,
        device.DeviceKey.KeyId,
        session.SessionId,
        session.RelayChannelId,
        session.ProtocolVersion,
        session.KeyEpoch,
        device.Role,
        session.Capabilities,
        session.Surface,
        authenticatedUtc,
        session.ExpiresUtc);

    private static RelaySessionRoute Route(DeviceSession session, RelayDeviceRecord device) => new(
        session.SessionId,
        session.DeviceId,
        session.RelayChannelId,
        session.ProtocolVersion,
        session.KeyEpoch,
        session.CipherSuite,
        device.Role,
        session.Surface,
        session.ExpiresUtc);

    private bool StillCurrent(RelayPrincipal principal, DateTimeOffset now, RelayRegistryState state)
    {
        if (!CanAuthenticate || now >= principal.ExpiresUtc ||
            principal.AuthenticatedUtc > now.Add(ProtocolBounds.MaxClientClockSkew))
        {
            return false;
        }

        var session = state.Sessions.SingleOrDefault(record => record.Session.SessionId == principal.SessionId);
        var device = state.Devices.SingleOrDefault(item => item.DeviceId == principal.DeviceId);
        return session is not null && device is not null && IsLive(session.Session, device, now) &&
            session.Session.DeviceId == principal.DeviceId &&
            session.Session.DeviceKeyId == principal.DeviceKeyId &&
            session.ChannelId == principal.ChannelId &&
            session.Session.ProtocolVersion == principal.ProtocolVersion &&
            session.Session.KeyEpoch == principal.KeyEpoch &&
            session.Session.Surface == principal.Surface &&
            session.Session.ExpiresUtc == principal.ExpiresUtc &&
            principal.AuthenticatedUtc >= session.Session.CreatedUtc &&
            principal.AuthenticatedUtc <= session.Session.LastUsedUtc &&
            device.Role == principal.Role &&
            session.Session.Capabilities.ToHashSet().SetEquals(principal.Capabilities);
    }

    private (RelaySessionRecord Record, RelaySessionCredential Credential) IssueSession(
        RelayDeviceRecord device,
        SessionEstablished establishment,
        CompanionSurfaceKind surface,
        DateTimeOffset now)
    {
        if (establishment.Assignment.DeviceId != device.DeviceId ||
            establishment.DeviceKeyId != device.DeviceKey.KeyId ||
            establishment.Assignment.KeyEpoch != device.LastKeyEpoch ||
            establishment.Assignment.SessionExpiresUtc > device.ExpiresUtc ||
            establishment.Assignment.SessionExpiresUtc <= now)
        {
            throw new ArgumentException("The establishment does not bind a live session for this device.", nameof(establishment));
        }

        var session = new DeviceSession(
            establishment,
            DeviceSessionStatus.Active,
            CompanionTransportKind.EndToEndRelay,
            surface,
            device.Capabilities,
            establishment.EstablishedUtc);
        var secret = RelayCsrfProtector.Base64Url(RandomNumberGenerator.GetBytes(32));
        // [V2 rough package 48] Clamped to this relay's own bound, because the expiry it is derived
        // from was chosen on another machine. A desktop asking for the protocol's maximum session
        // (which DesktopRelayOwnerClaim did) while its clock read a second ahead of the relay's made
        // this lifetime one second over the browser-session maximum, and RelayCsrfProtector.Issue
        // threw — turning a clock difference into an unhandled 500 on the claim route. The bound is
        // the relay's to enforce, never a remote timestamp's to set.
        var csrfLifetime = session.ExpiresUtc - now;
        if (csrfLifetime > RelaySecurityBounds.MaximumBrowserSessionLifetime)
        {
            csrfLifetime = RelaySecurityBounds.MaximumBrowserSessionLifetime;
        }

        var csrf = RelayCsrfProtector.Issue(session.SessionId, now, csrfLifetime);
        var record = new RelaySessionRecord(
            session,
            CredentialDigest(session.SessionId, secret),
            csrf.DigestBase64Url,
            csrf.ExpiresUtc);
        return (
            record,
            new RelaySessionCredential(
                session.SessionId,
                device.DeviceId,
                device.DeviceKey.KeyId,
                session.RelayChannelId,
                secret,
                csrf.Token,
                session.ExpiresUtc));
    }

    private static List<RelaySessionRecord> MakeRoomForSession(IEnumerable<RelaySessionRecord> existing)
    {
        var sessions = existing.ToList();
        while (sessions.Count >= RelaySecurityBounds.MaximumSessions)
        {
            var oldest = sessions
                .Where(record => record.Session.Status != DeviceSessionStatus.Active)
                .OrderBy(record => record.Session.EndedUtc)
                .FirstOrDefault();
            if (oldest is null)
            {
                throw new InvalidOperationException("The relay session bound is exhausted by active sessions.");
            }

            sessions.Remove(oldest);
        }

        return sessions;
    }

    private static RelaySessionRecord EndIfActive(
        RelaySessionRecord record,
        DeviceSessionStatus status,
        DateTimeOffset now,
        string reason) => record.Session.Status == DeviceSessionStatus.Active
            ? record.WithSession(DeviceLifecycle.EndSession(record.Session, status, now, reason))
            : record;

    private async ValueTask CommitAsync(RelayRegistryState candidate, CancellationToken cancellationToken)
    {
        try
        {
            if (_store is not null)
            {
                await _store.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
                _loadStatus = RelayRegistryLoadStatus.PrimaryVerified;
            }
            else if (_loadStatus is RelayRegistryLoadStatus.Uninitialized or RelayRegistryLoadStatus.Corrupt)
            {
                _loadStatus = RelayRegistryLoadStatus.PrimaryVerified;
            }

            _state = candidate;
        }
        catch (OperationCanceledException) when (_store is not null)
        {
            // Cancellation can arrive after the backup was atomically advanced but before the
            // primary or in-memory state was. Close authority until a fresh verified load selects
            // and repairs the newest durable generation.
            CloseInMemory();
            throw;
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
        catch (Exception exception) when (exception is ArgumentException or FormatException)
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
