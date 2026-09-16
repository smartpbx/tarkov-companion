using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.Application.Services.Devices;

public enum PairingOfferResolutionStatus
{
    Accepted = 1,
    NotFound,
    RateLimited,
}

public sealed record DesktopPairingInvitation(
    PairingOffer Offer,
    string PairingCode,
    string QrPayload);

public sealed record PairingOfferResolution(
    PairingOfferResolutionStatus Status,
    PairingOffer? Offer,
    DateTimeOffset? RetryAfterUtc);

public sealed record DesktopPairingApproval(
    PairingAttemptId AttemptId,
    PairingNonceReveal NonceReveal,
    string RequestedDisplayName,
    DeviceKeyId DeviceKeyId,
    string VerificationCode,
    DateTimeOffset ExpiresUtc);

public sealed record PairingDeviceGrant(
    DeviceAuthorizationRole Role,
    IReadOnlyList<DeviceCapability> DeviceCapabilities,
    IReadOnlyList<DeviceCapability> SessionCapabilities,
    DateTimeOffset DeviceExpiresUtc,
    CompanionTransportKind Transport,
    CompanionSurfaceKind Surface);

/// <summary>
/// One established session and its fresh direction-specific traffic keys.
/// </summary>
/// <remarks>
/// The keys exist only in memory. The transport owns this result and must dispose it after moving
/// the keys into its live cipher state; disposal zeroes both buffers.
/// </remarks>
public sealed class EstablishedDesktopSession : IDisposable
{
    private byte[]? _tabletToDesktopKey;
    private byte[]? _desktopToTabletKey;

    internal EstablishedDesktopSession(
        SessionEstablished establishment,
        AuthorityMutation mutation,
        byte[] tabletToDesktopKey,
        byte[] desktopToTabletKey)
    {
        Establishment = establishment;
        Mutation = mutation;
        _tabletToDesktopKey = tabletToDesktopKey;
        _desktopToTabletKey = desktopToTabletKey;
    }

    public SessionEstablished Establishment { get; }

    public AuthorityMutation Mutation { get; }

    public ReadOnlyMemory<byte> TabletToDesktopKey =>
        _tabletToDesktopKey ?? throw new ObjectDisposedException(nameof(EstablishedDesktopSession));

    public ReadOnlyMemory<byte> DesktopToTabletKey =>
        _desktopToTabletKey ?? throw new ObjectDisposedException(nameof(EstablishedDesktopSession));

    public void Dispose()
    {
        var tabletToDesktop = Interlocked.Exchange(ref _tabletToDesktopKey, null);
        var desktopToTablet = Interlocked.Exchange(ref _desktopToTabletKey, null);
        if (tabletToDesktop is not null)
        {
            CryptographicOperations.ZeroMemory(tabletToDesktop);
        }

        if (desktopToTablet is not null)
        {
            CryptographicOperations.ZeroMemory(desktopToTablet);
        }
    }
}

/// <summary>
/// Owns the desktop side of pairing and session-resume ceremonies before an authenticated frame
/// can reach <see cref="DesktopCompanionAuthority"/>.
/// </summary>
/// <remarks>
/// Pending codes, nonces, ECDH private keys, and traffic keys are memory-only and bounded. A code
/// resolves once, local approval must explicitly confirm the commit/reveal verification code, and
/// a proved session is persisted by the canonical authority before its traffic keys are returned.
/// </remarks>
public sealed class DesktopPairingCoordinator : IDisposable
{
    private readonly DesktopCompanionAuthority _authority;
    private readonly IDesktopIdentitySigner _identitySigner;
    private readonly IDeviceKeyProofVerifier _proofVerifier;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<PairingAttemptId, PendingPairing> _pairings = [];
    private readonly Dictionary<string, PairingAttemptId> _codes = new(StringComparer.Ordinal);
    private readonly Dictionary<HandshakeChallengeId, PendingResume> _resumes = [];
    private readonly byte[] _sourceHashKey = RandomNumberGenerator.GetBytes(
        PairedTransportBinding.MinimumSourceHashKeyBytes);
    private PairingRateState _rateState = PairingRateState.Empty;
    private bool _disposed;
    private int _disposeStarted;

    public DesktopPairingCoordinator(
        DesktopCompanionAuthority authority,
        IDesktopIdentitySigner identitySigner,
        IDeviceKeyProofVerifier proofVerifier)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _identitySigner = identitySigner ?? throw new ArgumentNullException(nameof(identitySigner));
        _proofVerifier = proofVerifier ?? throw new ArgumentNullException(nameof(proofVerifier));
    }

    public async ValueTask<DesktopPairingInvitation> CreateInvitationAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Prune(nowUtc);
            if (_pairings.Count >= ProtocolBounds.MaxDevices)
            {
                throw new InvalidOperationException("The pending pairing limit was reached.");
            }

            var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            try
            {
                var publicKey = new EphemeralPublicKey(
                    EphemeralKeyAlgorithm.EcdhP256,
                    Base64Url.EncodeToString(ephemeralKey.ExportSubjectPublicKeyInfo()));
                var nonce = Base64Url.EncodeToString(
                    RandomNumberGenerator.GetBytes(ProtocolBounds.PairingNonceBytes));
                var attempt = PairingStateMachine.Offer(
                    new PairingAttemptId(Guid.NewGuid()),
                    _identitySigner.PublicKey,
                    publicKey,
                    nonce,
                    nowUtc);
                string code;
                do
                {
                    code = PairedTransportBinding.GeneratePairingCode();
                }
                while (_codes.ContainsKey(code));

                _pairings.Add(attempt.AttemptId, new PendingPairing(attempt, code, ephemeralKey));
                _codes.Add(code, attempt.AttemptId);
                ephemeralKey = null!;
                return new DesktopPairingInvitation(
                    attempt.Offer,
                    code,
                    PairedTransportBinding.FormatQrPayload(code, _identitySigner.PublicKey.KeyId));
            }
            finally
            {
                ephemeralKey?.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<PairingOfferResolution> ResolveOfferAsync(
        string pairingCode,
        IPAddress remoteAddress,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var normalizedCode = PairedTransportBinding.NormalizePairingCode(pairingCode);
        ArgumentNullException.ThrowIfNull(remoteAddress);
        await WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Prune(nowUtc);
            var sourceHash = PairedTransportBinding.ComputeSourceHash(_sourceHashKey, remoteAddress);
            var rate = PairingRateLimiter.TryConsume(_rateState, sourceHash, nowUtc);
            _rateState = rate.State;
            if (!rate.Accepted)
            {
                return new PairingOfferResolution(
                    PairingOfferResolutionStatus.RateLimited,
                    null,
                    rate.RetryAfterUtc);
            }

            if (!_codes.Remove(normalizedCode, out var attemptId) ||
                !_pairings.TryGetValue(attemptId, out var pending) ||
                pending.CodeResolved)
            {
                return new PairingOfferResolution(PairingOfferResolutionStatus.NotFound, null, null);
            }

            pending.CodeResolved = true;
            pending.PairingCode = null;
            return new PairingOfferResolution(PairingOfferResolutionStatus.Accepted, pending.Attempt.Offer, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<DesktopPairingApproval> BindRequestAsync(
        PairingRequest request,
        CompanionProtocolVersion negotiatedVersion,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Prune(nowUtc);
            var pending = RequirePairing(request.AttemptId);
            if (!pending.CodeResolved)
            {
                throw new UnauthorizedAccessException("The one-time pairing code has not been resolved.");
            }

            if (pending.Attempt.Stage != PairingAttemptStage.Offered)
            {
                if (pending.Attempt.Request == request &&
                    request.NegotiatedVersion == negotiatedVersion &&
                    pending.Approval is { } existingApproval)
                {
                    return existingApproval;
                }

                throw new InvalidOperationException("Another pairing request is already bound to this one-time code.");
            }

            var bound = PairingStateMachine.BindResolvedCode(
                pending.Attempt,
                request,
                negotiatedVersion,
                nowUtc);
            // Binding must happen before opening attacker-controlled ciphertext. If authentication
            // of the sealed name fails, this attempt is consumed instead of allowing another key
            // to replace the first structurally valid request for the one-time code.
            pending.Attempt = bound;
            byte[]? sharedSecret = null;
            try
            {
                sharedSecret = PairingCryptography.DeriveP256SharedSecret(
                    pending.EphemeralKey,
                    request.EphemeralKey);
                pending.RequestedDisplayName = PairingCryptography.OpenDeviceName(
                    sharedSecret,
                    bound.Offer,
                    request);
            }
            catch
            {
                RemovePairing(pending);
                throw;
            }
            finally
            {
                if (sharedSecret is not null)
                {
                    CryptographicOperations.ZeroMemory(sharedSecret);
                }
            }

            var approval = new DesktopPairingApproval(
                bound.AttemptId,
                PairingStateMachine.RevealNonce(bound, nowUtc),
                pending.RequestedDisplayName!,
                request.DeviceKey.KeyId,
                PairingStateMachine.VerificationCode(bound),
                bound.ExpiresUtc);
            pending.Approval = approval;
            return approval;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<HandshakeChallenge> ApproveAsync(
        PairingAttemptId attemptId,
        bool userConfirmedMatchingVerificationCode,
        PairingDeviceGrant grant,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        await WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Prune(nowUtc);
            var pending = RequirePairing(attemptId);
            if (pending.Attempt.Stage == PairingAttemptStage.AwaitingDeviceProof)
            {
                var validatedRetryGrant = ValidateGrant(grant, nowUtc);
                if (userConfirmedMatchingVerificationCode &&
                    pending.Grant is { } existingGrant &&
                    GrantsMatch(existingGrant, validatedRetryGrant))
                {
                    return pending.Attempt.Challenge!;
                }

                throw new InvalidOperationException("The pairing attempt has already been approved with another grant.");
            }

            if (!userConfirmedMatchingVerificationCode)
            {
                _ = PairingStateMachine.Deny(pending.Attempt, nowUtc);
                RemovePairing(pending);
                throw new UnauthorizedAccessException("Local approval requires a matching verification code.");
            }

            var validatedGrant = ValidateGrant(grant, nowUtc);
            var challengeExpiresUtc = Earlier(
                nowUtc.Add(ProtocolBounds.HandshakeChallengeLifetime),
                pending.Attempt.ExpiresUtc);
            var sessionExpiresUtc = Earlier(
                nowUtc.Add(ProtocolBounds.MaximumSessionLifetime),
                validatedGrant.DeviceExpiresUtc);
            if (sessionExpiresUtc <= challengeExpiresUtc)
            {
                throw new ArgumentOutOfRangeException(nameof(grant), "The paired device must outlive its initial challenge and session.");
            }

            var assignment = CreateAssignment(
                pending.Attempt.Request!.NegotiatedVersion,
                new CompanionDeviceId(Guid.NewGuid()),
                keyEpoch: 1,
                sessionExpiresUtc);
            var challenge = PairingStateMachine.CreateChallenge(
                pending.Attempt,
                new HandshakeChallengeId(Guid.NewGuid()),
                assignment,
                _identitySigner,
                nowUtc,
                challengeExpiresUtc);
            pending.Attempt = PairingStateMachine.Approve(pending.Attempt, challenge, nowUtc);
            pending.Grant = validatedGrant;
            return challenge;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DenyAsync(
        PairingAttemptId attemptId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Prune(nowUtc);
            var pending = RequirePairing(attemptId);
            _ = PairingStateMachine.Deny(pending.Attempt, nowUtc);
            RemovePairing(pending);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<EstablishedDesktopSession> CompletePairingAsync(
        PairingAttemptId attemptId,
        DeviceKeyProof proof,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proof);
        await WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Prune(nowUtc);
            var pending = RequirePairing(attemptId);
            var grant = pending.Grant
                ?? throw new InvalidOperationException("The pairing attempt has not received local approval.");
            var completed = await PairingStateMachine.CompleteAsync(
                pending.Attempt,
                proof,
                _proofVerifier,
                nowUtc,
                cancellationToken).ConfigureAwait(false);
            var establishment = completed.Establishment!;
            var keys = DeriveTrafficKeys(
                pending.EphemeralKey,
                completed.Request!.EphemeralKey,
                establishment.TranscriptHashBase64Url);
            try
            {
                var mutation = await _authority.RegisterPairingAsync(
                    completed,
                    pending.RequestedDisplayName!,
                    grant.Role,
                    grant.DeviceCapabilities,
                    grant.SessionCapabilities,
                    grant.DeviceExpiresUtc,
                    grant.Transport,
                    grant.Surface,
                    cancellationToken).ConfigureAwait(false);
                RemovePairing(pending);
                return new EstablishedDesktopSession(
                    establishment,
                    mutation,
                    keys.TabletToDesktop,
                    keys.DesktopToTablet);
            }
            catch
            {
                keys.Dispose();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<HandshakeChallenge> BeginResumeAsync(
        SessionResumeRequest request,
        CompanionProtocolVersion negotiatedVersion,
        IReadOnlyList<DeviceCapability> sessionCapabilities,
        CompanionTransportKind transport,
        CompanionSurfaceKind surface,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sessionCapabilities);
        await WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Prune(nowUtc);
            var existingResume = _resumes.Values.SingleOrDefault(
                item => item.Attempt.Request.DeviceId == request.DeviceId);
            if (existingResume is not null)
            {
                ValidateConnection(transport, surface);
                var retryCapabilities = ValidateCapabilities(sessionCapabilities, nameof(sessionCapabilities));
                if (existingResume.Attempt.Request == request &&
                    request.NegotiatedVersion == negotiatedVersion &&
                    existingResume.Transport == transport &&
                    existingResume.Surface == surface &&
                    existingResume.SessionCapabilities.SequenceEqual(retryCapabilities))
                {
                    return existingResume.Attempt.Challenge;
                }

                throw new InvalidOperationException("A resume challenge for this device is already pending.");
            }

            var device = _authority.Snapshot.Devices.SingleOrDefault(item => item.DeviceId == request.DeviceId)
                         ?? throw new UnauthorizedAccessException("The paired device is unknown.");
            if (device.LastKeyEpoch >= ProtocolBounds.MaxKeyEpoch)
            {
                throw new InvalidOperationException("The device key epoch is exhausted and the device must pair again.");
            }

            ValidateConnection(transport, surface);
            var capabilities = ValidateSessionCapabilities(device, sessionCapabilities);
            var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            try
            {
                var publicKey = new EphemeralPublicKey(
                    EphemeralKeyAlgorithm.EcdhP256,
                    Base64Url.EncodeToString(ephemeralKey.ExportSubjectPublicKeyInfo()));
                var challengeExpiresUtc = Earlier(
                    nowUtc.Add(ProtocolBounds.HandshakeChallengeLifetime),
                    device.ExpiresUtc);
                var sessionExpiresUtc = Earlier(
                    nowUtc.Add(ProtocolBounds.MaximumSessionLifetime),
                    device.ExpiresUtc);
                if (sessionExpiresUtc <= challengeExpiresUtc)
                {
                    throw new UnauthorizedAccessException("The paired device expires before a fresh session can be established.");
                }

                var assignment = CreateAssignment(
                    negotiatedVersion,
                    device.DeviceId,
                    device.LastKeyEpoch + 1,
                    sessionExpiresUtc);
                var attempt = SessionResumption.Begin(
                    device,
                    request,
                    negotiatedVersion,
                    new HandshakeChallengeId(Guid.NewGuid()),
                    assignment,
                    publicKey,
                    Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ProtocolBounds.PairingNonceBytes)),
                    _identitySigner,
                    nowUtc,
                    challengeExpiresUtc);
                _resumes.Add(
                    attempt.ChallengeId,
                    new PendingResume(attempt, ephemeralKey, capabilities, transport, surface));
                ephemeralKey = null!;
                return attempt.Challenge;
            }
            finally
            {
                ephemeralKey?.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<EstablishedDesktopSession> CompleteResumeAsync(
        HandshakeChallengeId challengeId,
        DeviceKeyProof proof,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proof);
        await WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Prune(nowUtc);
            if (!_resumes.TryGetValue(challengeId, out var pending))
            {
                throw new UnauthorizedAccessException("The resume challenge is unknown or no longer live.");
            }

            var device = _authority.Snapshot.Devices.SingleOrDefault(
                             item => item.DeviceId == pending.Attempt.Request.DeviceId)
                         ?? throw new UnauthorizedAccessException("The paired device is unknown.");
            var completed = await SessionResumption.CompleteAsync(
                pending.Attempt,
                device,
                proof,
                _proofVerifier,
                nowUtc,
                cancellationToken).ConfigureAwait(false);
            var establishment = completed.Establishment!;
            var keys = DeriveTrafficKeys(
                pending.EphemeralKey,
                completed.Request.EphemeralKey,
                establishment.TranscriptHashBase64Url);
            try
            {
                var mutation = await _authority.RegisterResumedSessionAsync(
                    completed,
                    pending.SessionCapabilities,
                    pending.Transport,
                    pending.Surface,
                    cancellationToken).ConfigureAwait(false);
                RemoveResume(pending);
                return new EstablishedDesktopSession(
                    establishment,
                    mutation,
                    keys.TabletToDesktop,
                    keys.DesktopToTablet);
            }
            catch
            {
                keys.Dispose();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<AuthorityMutation> RevokeDeviceAsync(
        CompanionDeviceId deviceId,
        DateTimeOffset nowUtc,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mutation = await _authority.RevokeDeviceAsync(
                deviceId,
                nowUtc,
                reason,
                cancellationToken).ConfigureAwait(false);
            foreach (var resume in _resumes.Values
                         .Where(item => item.Attempt.Request.DeviceId == deviceId)
                         .ToArray())
            {
                RemoveResume(resume);
            }

            return mutation;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _gate.Wait();
        try
        {
            _disposed = true;
            foreach (var pairing in _pairings.Values)
            {
                pairing.Dispose();
            }

            foreach (var resume in _resumes.Values)
            {
                resume.Dispose();
            }

            _pairings.Clear();
            _codes.Clear();
            _resumes.Clear();
            CryptographicOperations.ZeroMemory(_sourceHashKey);
        }
        finally
        {
            _gate.Release();
        }

        // A request can pass the pre-wait disposal guard immediately before disposal begins.
        // Leaving this managed semaphore alive lets every such waiter drain and fail closed.
    }

    private async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_disposed || Volatile.Read(ref _disposeStarted) != 0)
        {
            _gate.Release();
            throw new ObjectDisposedException(nameof(DesktopPairingCoordinator));
        }
    }

    private void Prune(DateTimeOffset nowUtc)
    {
        if (nowUtc == default || nowUtc.Offset != TimeSpan.Zero ||
            nowUtc.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentException("A millisecond-precision UTC instant is required.", nameof(nowUtc));
        }

        foreach (var pending in _pairings.Values
                     .Where(item => nowUtc >= item.Attempt.ExpiresUtc)
                     .ToArray())
        {
            RemovePairing(pending);
        }

        foreach (var pending in _resumes.Values
                     .Where(item => nowUtc >= item.Attempt.Challenge.ExpiresUtc)
                     .ToArray())
        {
            RemoveResume(pending);
        }
    }

    private PendingPairing RequirePairing(PairingAttemptId attemptId) =>
        _pairings.TryGetValue(attemptId, out var pending)
            ? pending
            : throw new UnauthorizedAccessException("The pairing attempt is unknown or no longer live.");

    private void RemovePairing(PendingPairing pending)
    {
        _pairings.Remove(pending.Attempt.AttemptId);
        if (pending.PairingCode is { } code)
        {
            _codes.Remove(code);
        }

        pending.Dispose();
    }

    private void RemoveResume(PendingResume pending)
    {
        _resumes.Remove(pending.Attempt.ChallengeId);
        pending.Dispose();
    }

    private static PairingDeviceGrant ValidateGrant(PairingDeviceGrant grant, DateTimeOffset nowUtc)
    {
        if (!Enum.IsDefined(grant.Role))
        {
            throw new ArgumentOutOfRangeException(nameof(grant), "The device grant contains an undefined value.");
        }

        ValidateConnection(grant.Transport, grant.Surface);

        var deviceCapabilities = ValidateCapabilities(grant.DeviceCapabilities, nameof(grant));
        var sessionCapabilities = ValidateCapabilities(grant.SessionCapabilities, nameof(grant));
        if (sessionCapabilities.Except(deviceCapabilities).Any())
        {
            throw new ArgumentException("Session capabilities must be a subset of the device grant.", nameof(grant));
        }

        if (deviceCapabilities.Contains(DeviceCapability.ReportCaptureProgress))
        {
            throw new ArgumentException("A paired tablet cannot report desktop capture progress.", nameof(grant));
        }

        if (grant.DeviceExpiresUtc.Offset != TimeSpan.Zero ||
            grant.DeviceExpiresUtc.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            grant.DeviceExpiresUtc <= nowUtc)
        {
            throw new ArgumentException("The device expiry must be a future millisecond-precision UTC instant.", nameof(grant));
        }

        return grant with
        {
            DeviceCapabilities = deviceCapabilities,
            SessionCapabilities = sessionCapabilities,
        };
    }

    private static bool GrantsMatch(PairingDeviceGrant left, PairingDeviceGrant right) =>
        left.Role == right.Role &&
        left.DeviceExpiresUtc == right.DeviceExpiresUtc &&
        left.Transport == right.Transport &&
        left.Surface == right.Surface &&
        left.DeviceCapabilities.SequenceEqual(right.DeviceCapabilities) &&
        left.SessionCapabilities.SequenceEqual(right.SessionCapabilities);

    private static IReadOnlyList<DeviceCapability> ValidateSessionCapabilities(
        PairedDevice device,
        IReadOnlyList<DeviceCapability> sessionCapabilities)
    {
        var validated = ValidateCapabilities(sessionCapabilities, nameof(sessionCapabilities));
        if (validated.Except(device.Capabilities).Any())
        {
            throw new UnauthorizedAccessException("A resumed session cannot expand its paired device grant.");
        }

        return validated;
    }

    private static void ValidateConnection(
        CompanionTransportKind transport,
        CompanionSurfaceKind surface)
    {
        if (!Enum.IsDefined(transport) || !Enum.IsDefined(surface))
        {
            throw new ArgumentOutOfRangeException(nameof(transport), "The connection contains an undefined value.");
        }

        if (surface == CompanionSurfaceKind.Desktop)
        {
            throw new ArgumentException("A paired client cannot claim the native desktop surface.", nameof(surface));
        }
    }

    private static IReadOnlyList<DeviceCapability> ValidateCapabilities(
        IReadOnlyList<DeviceCapability> capabilities,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(capabilities, parameterName);
        if (capabilities.Count > 32 || capabilities.Any(capability => !Enum.IsDefined(capability)))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Capabilities must be defined and bounded.");
        }

        return capabilities.Distinct().ToArray();
    }

    private static SessionAssignment CreateAssignment(
        CompanionProtocolVersion version,
        CompanionDeviceId deviceId,
        long keyEpoch,
        DateTimeOffset expiresUtc) => new(
            version,
            deviceId,
            new DeviceSessionId(Guid.NewGuid()),
            new RelayChannelId(Guid.NewGuid()),
            keyEpoch,
            RelayCipherSuite.P256HkdfSha256Aes256Gcm,
            expiresUtc);

    private static DateTimeOffset Earlier(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    private static TrafficKeys DeriveTrafficKeys(
        ECDiffieHellman localKey,
        EphemeralPublicKey remoteKey,
        string transcriptHashBase64Url)
    {
        var sharedSecret = PairingCryptography.DeriveP256SharedSecret(localKey, remoteKey);
        byte[]? tabletToDesktop = null;
        byte[]? desktopToTablet = null;
        try
        {
            tabletToDesktop = PairingCryptography.DeriveTrafficKey(
                sharedSecret,
                transcriptHashBase64Url,
                PairingTrafficDirection.TabletToDesktop);
            desktopToTablet = PairingCryptography.DeriveTrafficKey(
                sharedSecret,
                transcriptHashBase64Url,
                PairingTrafficDirection.DesktopToTablet);
            var result = new TrafficKeys(tabletToDesktop, desktopToTablet);
            tabletToDesktop = null;
            desktopToTablet = null;
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
            if (tabletToDesktop is not null)
            {
                CryptographicOperations.ZeroMemory(tabletToDesktop);
            }

            if (desktopToTablet is not null)
            {
                CryptographicOperations.ZeroMemory(desktopToTablet);
            }
        }
    }

    private sealed class PendingPairing(
        PairingAttempt attempt,
        string pairingCode,
        ECDiffieHellman ephemeralKey) : IDisposable
    {
        public PairingAttempt Attempt { get; set; } = attempt;

        public string? PairingCode { get; set; } = pairingCode;

        public ECDiffieHellman EphemeralKey { get; } = ephemeralKey;

        public bool CodeResolved { get; set; }

        public string? RequestedDisplayName { get; set; }

        public DesktopPairingApproval? Approval { get; set; }

        public PairingDeviceGrant? Grant { get; set; }

        public void Dispose() => EphemeralKey.Dispose();
    }

    private sealed class PendingResume(
        SessionResumeAttempt attempt,
        ECDiffieHellman ephemeralKey,
        IReadOnlyList<DeviceCapability> sessionCapabilities,
        CompanionTransportKind transport,
        CompanionSurfaceKind surface) : IDisposable
    {
        public SessionResumeAttempt Attempt { get; } = attempt;

        public ECDiffieHellman EphemeralKey { get; } = ephemeralKey;

        public IReadOnlyList<DeviceCapability> SessionCapabilities { get; } = sessionCapabilities;

        public CompanionTransportKind Transport { get; } = transport;

        public CompanionSurfaceKind Surface { get; } = surface;

        public void Dispose() => EphemeralKey.Dispose();
    }

    private sealed class TrafficKeys(byte[] tabletToDesktop, byte[] desktopToTablet) : IDisposable
    {
        public byte[] TabletToDesktop { get; } = tabletToDesktop;

        public byte[] DesktopToTablet { get; } = desktopToTablet;

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(TabletToDesktop);
            CryptographicOperations.ZeroMemory(DesktopToTablet);
        }
    }
}
