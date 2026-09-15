using System.Security.Cryptography;

namespace TarkovCompanion.CompanionProtocol;

public enum PairingAttemptStage
{
    Offered = 1,
    AwaitingDesktopApproval,
    AwaitingDeviceProof,
    Completed,
    Denied,
    Expired,
}

/// <summary>
/// Every input to one handshake transcript, in the frozen field order documented in
/// docs/PAIRED_DEVICE_PROTOCOL.md. The desktop signs its SHA-256 hash and the tablet's WebAuthn
/// credential signs the same 32 bytes as its challenge, so both keys authenticate one ECDH exchange.
/// </summary>
public sealed record HandshakeTranscript
{
    private HandshakeTranscript(
        HandshakePurpose purpose,
        PairingAttemptId? attemptId,
        string? pairingCommitmentBase64Url,
        HandshakeChallengeId challengeId,
        DeviceKeyId deviceKeyId,
        string credentialIdBase64Url,
        SessionAssignment assignment,
        DesktopIdentityKey desktopIdentityKey,
        EphemeralPublicKey tabletEphemeralKey,
        EphemeralPublicKey desktopEphemeralKey,
        string clientNonceBase64Url,
        string desktopNonceBase64Url,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc)
    {
        Purpose = ProtocolGuard.Defined(purpose, nameof(purpose));
        AttemptId = attemptId;
        PairingCommitmentBase64Url = pairingCommitmentBase64Url;
        ChallengeId = challengeId.Value == Guid.Empty
            ? throw new ArgumentException("A challenge id is required.", nameof(challengeId))
            : challengeId;
        DeviceKeyId = deviceKeyId;
        CredentialIdBase64Url = credentialIdBase64Url;
        Assignment = ProtocolGuard.NotNull(assignment, nameof(assignment));
        DesktopIdentityKey = ProtocolGuard.NotNull(desktopIdentityKey, nameof(desktopIdentityKey));
        TabletEphemeralKey = ProtocolGuard.NotNull(tabletEphemeralKey, nameof(tabletEphemeralKey));
        DesktopEphemeralKey = ProtocolGuard.NotNull(desktopEphemeralKey, nameof(desktopEphemeralKey));
        ClientNonceBase64Url = ProtocolGuard.Base64Url(
            clientNonceBase64Url,
            nameof(clientNonceBase64Url),
            exactDecodedBytes: ProtocolBounds.PairingNonceBytes);
        DesktopNonceBase64Url = ProtocolGuard.Base64Url(
            desktopNonceBase64Url,
            nameof(desktopNonceBase64Url),
            exactDecodedBytes: ProtocolBounds.PairingNonceBytes);
        IssuedUtc = ProtocolGuard.Utc(issuedUtc, nameof(issuedUtc));
        ExpiresUtc = ProtocolGuard.Utc(expiresUtc, nameof(expiresUtc));

        // A reflected nonce or ephemeral key would let one side's contribution stand in for the
        // other's, so both must be fresh and distinct.
        if (string.Equals(ClientNonceBase64Url, DesktopNonceBase64Url, StringComparison.Ordinal) ||
            string.Equals(
                TabletEphemeralKey.SubjectPublicKeyInfoBase64Url,
                DesktopEphemeralKey.SubjectPublicKeyInfoBase64Url,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("Tablet and desktop nonces and ephemeral keys are distinct.");
        }
    }

    public HandshakePurpose Purpose { get; }

    public PairingAttemptId? AttemptId { get; }

    public string? PairingCommitmentBase64Url { get; }

    public HandshakeChallengeId ChallengeId { get; }

    public DeviceKeyId DeviceKeyId { get; }

    public string CredentialIdBase64Url { get; }

    public SessionAssignment Assignment { get; }

    public DesktopIdentityKey DesktopIdentityKey { get; }

    public EphemeralPublicKey TabletEphemeralKey { get; }

    public EphemeralPublicKey DesktopEphemeralKey { get; }

    public string ClientNonceBase64Url { get; }

    public string DesktopNonceBase64Url { get; }

    public DateTimeOffset IssuedUtc { get; }

    public DateTimeOffset ExpiresUtc { get; }

    public static HandshakeTranscript ForPairing(
        PairingOffer offer,
        PairingRequest request,
        PairingNonceReveal reveal,
        HandshakeChallengeId challengeId,
        SessionAssignment assignment,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(assignment);
        if (assignment.ProtocolVersion != request.NegotiatedVersion)
        {
            throw new ArgumentException("The assigned session uses the negotiated pairing version.", nameof(assignment));
        }

        return new HandshakeTranscript(
            HandshakePurpose.Pairing,
            offer.AttemptId,
            ProtocolGuard.EncodeBase64Url(PairingCryptography.ComputePairingCommitment(offer, request, reveal)),
            challengeId,
            request.DeviceKey.KeyId,
            request.DeviceKey.CredentialIdBase64Url,
            assignment,
            offer.DesktopIdentityKey,
            request.EphemeralKey,
            offer.DesktopEphemeralKey,
            request.ClientNonceBase64Url,
            reveal.DesktopNonceBase64Url,
            issuedUtc,
            expiresUtc);
    }

    public static HandshakeTranscript ForSessionResume(
        SessionResumeRequest request,
        HandshakeChallengeId challengeId,
        SessionAssignment assignment,
        DesktopIdentityKey desktopIdentityKey,
        EphemeralPublicKey desktopEphemeralKey,
        string desktopNonceBase64Url,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(assignment);
        if (assignment.ProtocolVersion != request.NegotiatedVersion || assignment.DeviceId != request.DeviceId)
        {
            throw new ArgumentException("The assigned session belongs to the resuming device and negotiated version.", nameof(assignment));
        }

        return new HandshakeTranscript(
            HandshakePurpose.SessionResume,
            null,
            null,
            challengeId,
            request.DeviceKeyId,
            request.CredentialIdBase64Url,
            assignment,
            desktopIdentityKey,
            request.EphemeralKey,
            desktopEphemeralKey,
            request.ClientNonceBase64Url,
            desktopNonceBase64Url,
            issuedUtc,
            expiresUtc);
    }

    /// <summary>
    /// Rebuilds a pairing transcript from a received challenge, the offer, the tablet's own request,
    /// and the nonce reveal that opened the offer's commitment.
    /// </summary>
    public static HandshakeTranscript FromChallenge(
        HandshakeChallenge challenge,
        PairingOffer offer,
        PairingRequest request,
        PairingNonceReveal reveal)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reveal);
        if (challenge.Purpose != HandshakePurpose.Pairing ||
            challenge.AttemptId != offer.AttemptId ||
            !PairingCryptography.IsRevealOf(offer, reveal) ||
            challenge.DesktopIdentityKey != offer.DesktopIdentityKey ||
            challenge.DesktopEphemeralKey != offer.DesktopEphemeralKey ||
            !string.Equals(challenge.DesktopNonceBase64Url, reveal.DesktopNonceBase64Url, StringComparison.Ordinal) ||
            challenge.DeviceKeyId != request.DeviceKey.KeyId ||
            !string.Equals(challenge.CredentialIdBase64Url, request.DeviceKey.CredentialIdBase64Url, StringComparison.Ordinal))
        {
            throw new ArgumentException("The challenge does not answer this pairing offer, request, and nonce reveal.", nameof(challenge));
        }

        return ForPairing(offer, request, reveal, challenge.ChallengeId, challenge.Assignment, challenge.IssuedUtc, challenge.ExpiresUtc);
    }

    /// <summary>
    /// Rebuilds a session-resume transcript from a received challenge and the tablet's own request.
    /// The desktop identity key is the one the tablet pinned at pairing, never the key the challenge
    /// names, so a relay cannot answer a resume with its own identity.
    /// </summary>
    public static HandshakeTranscript FromChallenge(
        HandshakeChallenge challenge,
        SessionResumeRequest request,
        DesktopIdentityKey pinnedDesktopIdentityKey)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pinnedDesktopIdentityKey);
        if (challenge.Purpose != HandshakePurpose.SessionResume ||
            challenge.DesktopIdentityKey != pinnedDesktopIdentityKey ||
            challenge.DeviceKeyId != request.DeviceKeyId ||
            !string.Equals(challenge.CredentialIdBase64Url, request.CredentialIdBase64Url, StringComparison.Ordinal))
        {
            throw new ArgumentException("The challenge does not answer this session resume request.", nameof(challenge));
        }

        return ForSessionResume(
            request,
            challenge.ChallengeId,
            challenge.Assignment,
            pinnedDesktopIdentityKey,
            challenge.DesktopEphemeralKey,
            challenge.DesktopNonceBase64Url,
            challenge.IssuedUtc,
            challenge.ExpiresUtc);
    }

    public byte[] Encode()
    {
        var writer = new ProtocolBinaryWriter();
        writer.Utf8(PairingCryptography.TranscriptDomain);
        writer.UInt16((ushort)Purpose);
        writer.Version(Assignment.ProtocolVersion);
        writer.Uuid(ChallengeId.Value);
        writer.Uuid(AttemptId?.Value ?? Guid.Empty);
        if (PairingCommitmentBase64Url is null)
        {
            writer.Bytes([]);
        }
        else
        {
            writer.Base64Url(PairingCommitmentBase64Url);
        }

        writer.Uuid(Assignment.DeviceId.Value);
        writer.Base64Url(DeviceKeyId.Value);
        writer.Base64Url(CredentialIdBase64Url);
        writer.Base64Url(DesktopIdentityKey.KeyId.Value);
        writer.UInt16((ushort)TabletEphemeralKey.Algorithm);
        writer.Base64Url(TabletEphemeralKey.SubjectPublicKeyInfoBase64Url);
        writer.UInt16((ushort)DesktopEphemeralKey.Algorithm);
        writer.Base64Url(DesktopEphemeralKey.SubjectPublicKeyInfoBase64Url);
        writer.Base64Url(ClientNonceBase64Url);
        writer.Base64Url(DesktopNonceBase64Url);
        writer.Uuid(Assignment.SessionId.Value);
        writer.Uuid(Assignment.RelayChannelId.Value);
        writer.UInt16((ushort)Assignment.CipherSuite);
        writer.UInt32((uint)Assignment.KeyEpoch);
        writer.Instant(Assignment.SessionExpiresUtc);
        writer.Instant(IssuedUtc);
        writer.Instant(ExpiresUtc);
        return writer.ToArray();
    }

    public byte[] ComputeHash() => SHA256.HashData(Encode());

    public string ComputeHashBase64Url() => ProtocolGuard.EncodeBase64Url(ComputeHash());

    /// <summary>True when the challenge carries exactly this transcript's inputs and hash.</summary>
    public bool Matches(HandshakeChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        var fieldsMatch =
            challenge.ChallengeId == ChallengeId &&
            challenge.Purpose == Purpose &&
            challenge.AttemptId == AttemptId &&
            string.Equals(challenge.PairingCommitmentBase64Url, PairingCommitmentBase64Url, StringComparison.Ordinal) &&
            challenge.DeviceKeyId == DeviceKeyId &&
            string.Equals(challenge.CredentialIdBase64Url, CredentialIdBase64Url, StringComparison.Ordinal) &&
            challenge.Assignment == Assignment &&
            challenge.DesktopIdentityKey == DesktopIdentityKey &&
            challenge.DesktopEphemeralKey == DesktopEphemeralKey &&
            string.Equals(challenge.DesktopNonceBase64Url, DesktopNonceBase64Url, StringComparison.Ordinal) &&
            challenge.IssuedUtc == IssuedUtc &&
            challenge.ExpiresUtc == ExpiresUtc;
        var expected = ComputeHash();
        var actual = ProtocolGuard.DecodeBase64Url(
            challenge.TranscriptHashBase64Url,
            nameof(challenge),
            exactDecodedBytes: ProtocolBounds.TranscriptHashBytes);
        return CryptographicOperations.FixedTimeEquals(expected, actual) && fieldsMatch;
    }

    /// <summary>Hashes and signs the transcript with the desktop identity key it names.</summary>
    public HandshakeChallenge Sign(IDesktopIdentitySigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);
        if (signer.PublicKey != DesktopIdentityKey)
        {
            throw new ArgumentException("The signer holds another desktop identity key.", nameof(signer));
        }

        var hash = ComputeHash();
        var signature = signer.Sign(PairingCryptography.EncodeDesktopSignatureInput(hash));
        return new HandshakeChallenge(
            ChallengeId,
            Purpose,
            AttemptId,
            PairingCommitmentBase64Url,
            DeviceKeyId,
            CredentialIdBase64Url,
            Assignment,
            DesktopIdentityKey,
            DesktopEphemeralKey,
            DesktopNonceBase64Url,
            ProtocolGuard.EncodeBase64Url(hash),
            ProtocolGuard.EncodeBase64Url(signature),
            IssuedUtc,
            ExpiresUtc);
    }
}

public sealed record PairingAttempt
{
    public PairingAttempt(
        PairingOffer offer,
        string desktopNonceBase64Url,
        PairingAttemptStage stage,
        DateTimeOffset? codeConsumedUtc = null,
        PairingRequest? request = null,
        HandshakeChallenge? challenge = null,
        SessionEstablished? establishment = null,
        DateTimeOffset? endedUtc = null)
    {
        Offer = ProtocolGuard.NotNull(offer, nameof(offer));
        DesktopNonceBase64Url = ProtocolGuard.Base64Url(
            desktopNonceBase64Url,
            nameof(desktopNonceBase64Url),
            exactDecodedBytes: ProtocolBounds.PairingNonceBytes);
        Stage = ProtocolGuard.Defined(stage, nameof(stage));
        CodeConsumedUtc = ProtocolGuard.UtcOptional(codeConsumedUtc, nameof(codeConsumedUtc));
        Request = request;
        Challenge = challenge;
        Establishment = establishment;
        EndedUtc = ProtocolGuard.UtcOptional(endedUtc, nameof(endedUtc));

        if (!PairingCryptography.IsRevealOf(offer, new PairingNonceReveal(offer.AttemptId, DesktopNonceBase64Url)))
        {
            throw new ArgumentException("The desktop nonce must open the offer's nonce commitment.", nameof(desktopNonceBase64Url));
        }

        if (request is not null && request.AttemptId != offer.AttemptId)
        {
            throw new ArgumentException("The request belongs to another pairing attempt.", nameof(request));
        }

        if (challenge is not null && (request is null || !ChallengeAnswers(challenge, offer, request, RevealFor(offer, DesktopNonceBase64Url))))
        {
            throw new ArgumentException("The challenge must bind this offer, request, nonce, and transcript.", nameof(challenge));
        }

        if (establishment is not null && (challenge is null || !establishment.Answers(challenge)))
        {
            throw new ArgumentException("The session must be established from the approved challenge.", nameof(establishment));
        }

        if (new[] { CodeConsumedUtc, challenge?.IssuedUtc, EndedUtc }
            .OfType<DateTimeOffset>()
            .Any(value => value < offer.OfferedUtc || value > offer.ExpiresUtc) ||
            (challenge is not null && challenge.ExpiresUtc > offer.ExpiresUtc))
        {
            throw new ArgumentException("Pairing transition times must stay inside the offer lifetime.");
        }

        var validShape = stage switch
        {
            PairingAttemptStage.Offered =>
                request is null && codeConsumedUtc is null && challenge is null && establishment is null && endedUtc is null,
            PairingAttemptStage.AwaitingDesktopApproval =>
                request is not null && codeConsumedUtc is not null && challenge is null && establishment is null && endedUtc is null,
            PairingAttemptStage.AwaitingDeviceProof =>
                request is not null && codeConsumedUtc is not null && challenge is not null && establishment is null && endedUtc is null,
            PairingAttemptStage.Completed =>
                request is not null && codeConsumedUtc is not null && challenge is not null && establishment is not null &&
                endedUtc == establishment.EstablishedUtc,
            PairingAttemptStage.Denied =>
                request is not null && codeConsumedUtc is not null && challenge is null && establishment is null && endedUtc is not null,
            PairingAttemptStage.Expired => establishment is null && endedUtc is not null &&
                ((request is null && codeConsumedUtc is null && challenge is null) ||
                 (request is not null && codeConsumedUtc is not null)),
            _ => false,
        };

        if (!validShape)
        {
            throw new ArgumentException($"The pairing fields are inconsistent with stage {stage}.");
        }
    }

    public PairingOffer Offer { get; }

    /// <summary>
    /// The committed desktop nonce. It is desktop-local until a request is bound: only
    /// <see cref="PairingStateMachine.RevealNonce"/> releases it, and never before binding.
    /// </summary>
    public string DesktopNonceBase64Url { get; }

    public PairingAttemptId AttemptId => Offer.AttemptId;

    public DateTimeOffset OfferedUtc => Offer.OfferedUtc;

    public DateTimeOffset ExpiresUtc => Offer.ExpiresUtc;

    public PairingAttemptStage Stage { get; }

    /// <summary>The one-time lookup code ceased to be usable at this instant; the code is never stored here.</summary>
    public DateTimeOffset? CodeConsumedUtc { get; }

    public PairingRequest? Request { get; }

    public HandshakeChallenge? Challenge { get; }

    public SessionEstablished? Establishment { get; }

    public DateTimeOffset? EndedUtc { get; }

    internal PairingNonceReveal Reveal => RevealFor(Offer, DesktopNonceBase64Url);

    private static PairingNonceReveal RevealFor(PairingOffer offer, string nonce) => new(offer.AttemptId, nonce);

    private static bool ChallengeAnswers(
        HandshakeChallenge challenge,
        PairingOffer offer,
        PairingRequest request,
        PairingNonceReveal reveal)
    {
        try
        {
            return HandshakeTranscript.FromChallenge(challenge, offer, request, reveal).Matches(challenge);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// Single-use, desktop-approved, device-key-bound pairing. A short code without local approval,
/// a matching verification code, and a valid device-key proof cannot complete pairing.
/// </summary>
/// <remarks>
/// The verification code is commit-then-reveal: the offer publishes only a commitment to the
/// desktop nonce, the first request is bound, and only then is the nonce revealed. Whoever chooses a
/// request (the tablet or a relay) has fixed it before the nonce is known, so each attempt matches
/// a chosen code with probability one in a million and a failed attempt consumes the one-time code.
/// </remarks>
public static class PairingStateMachine
{
    public static PairingAttempt Offer(
        PairingAttemptId attemptId,
        DesktopIdentityKey desktopIdentityKey,
        EphemeralPublicKey desktopEphemeralKey,
        string desktopNonceBase64Url,
        DateTimeOffset nowUtc)
    {
        var now = ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        return new PairingAttempt(
            new PairingOffer(
                attemptId,
                desktopIdentityKey,
                desktopEphemeralKey,
                PairingCryptography.ComputeDesktopNonceCommitment(desktopNonceBase64Url),
                now,
                now.Add(ProtocolBounds.PairingLifetime)),
            desktopNonceBase64Url,
            PairingAttemptStage.Offered);
    }

    /// <summary>
    /// Binds the first request after the transport has matched and consumed the rate-limited
    /// one-time code. A second request cannot replace the bound key. The request must use the
    /// version this connection's hello negotiated.
    /// </summary>
    public static PairingAttempt BindResolvedCode(
        PairingAttempt attempt,
        PairingRequest request,
        CompanionProtocolVersion negotiatedVersion,
        DateTimeOffset nowUtc)
    {
        RequireLiveStage(attempt, PairingAttemptStage.Offered, nowUtc);
        ArgumentNullException.ThrowIfNull(request);
        if (request.AttemptId != attempt.AttemptId)
        {
            throw new ArgumentException("The request names another attempt.", nameof(request));
        }

        if (!CompanionProtocolVersion.Current.CanRead(request.NegotiatedVersion) ||
            request.NegotiatedVersion != negotiatedVersion)
        {
            throw new ArgumentException("The pairing request does not use this connection's negotiated version.", nameof(request));
        }

        if (string.Equals(request.ClientNonceBase64Url, attempt.DesktopNonceBase64Url, StringComparison.Ordinal) ||
            string.Equals(
                request.EphemeralKey.SubjectPublicKeyInfoBase64Url,
                attempt.Offer.DesktopEphemeralKey.SubjectPublicKeyInfoBase64Url,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("A pairing request cannot reflect the desktop nonce or ephemeral key.", nameof(request));
        }

        return new PairingAttempt(
            attempt.Offer,
            attempt.DesktopNonceBase64Url,
            PairingAttemptStage.AwaitingDesktopApproval,
            nowUtc,
            request);
    }

    /// <summary>
    /// The nonce reveal the desktop returns for the bound request. It is available only once a
    /// request is bound, so nobody can choose a request after seeing the nonce.
    /// </summary>
    public static PairingNonceReveal RevealNonce(PairingAttempt attempt, DateTimeOffset nowUtc)
    {
        RequireLiveStage(attempt, PairingAttemptStage.AwaitingDesktopApproval, nowUtc);
        return attempt.Reveal;
    }

    /// <summary>The code the desktop approval prompt and the tablet both display.</summary>
    public static string VerificationCode(PairingAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return PairingCryptography.ComputeVerificationCode(
            attempt.Offer,
            attempt.Request ?? throw new InvalidOperationException("No pairing request has been bound."),
            attempt.Reveal);
    }

    /// <summary>Allocates the session and signs the transcript once the user approves locally.</summary>
    public static HandshakeChallenge CreateChallenge(
        PairingAttempt attempt,
        HandshakeChallengeId challengeId,
        SessionAssignment assignment,
        IDesktopIdentitySigner signer,
        DateTimeOffset nowUtc,
        DateTimeOffset expiresUtc)
    {
        RequireLiveStage(attempt, PairingAttemptStage.AwaitingDesktopApproval, nowUtc);
        if (expiresUtc > attempt.ExpiresUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresUtc), "A pairing challenge cannot outlive its offer.");
        }

        return HandshakeTranscript
            .ForPairing(attempt.Offer, attempt.Request!, attempt.Reveal, challengeId, assignment, nowUtc, expiresUtc)
            .Sign(signer);
    }

    public static PairingAttempt Approve(
        PairingAttempt attempt,
        HandshakeChallenge challenge,
        DateTimeOffset nowUtc)
    {
        RequireLiveStage(attempt, PairingAttemptStage.AwaitingDesktopApproval, nowUtc);
        ArgumentNullException.ThrowIfNull(challenge);
        if (challenge.IssuedUtc > nowUtc || challenge.ExpiresUtc <= nowUtc)
        {
            throw new ArgumentException("The approval challenge is not live at this instant.", nameof(challenge));
        }

        return new PairingAttempt(
            attempt.Offer,
            attempt.DesktopNonceBase64Url,
            PairingAttemptStage.AwaitingDeviceProof,
            attempt.CodeConsumedUtc,
            attempt.Request,
            challenge);
    }

    public static PairingAttempt Deny(PairingAttempt attempt, DateTimeOffset nowUtc)
    {
        RequireLiveStage(attempt, PairingAttemptStage.AwaitingDesktopApproval, nowUtc);
        return new PairingAttempt(
            attempt.Offer,
            attempt.DesktopNonceBase64Url,
            PairingAttemptStage.Denied,
            attempt.CodeConsumedUtc,
            attempt.Request,
            endedUtc: nowUtc);
    }

    public static async ValueTask<PairingAttempt> CompleteAsync(
        PairingAttempt attempt,
        DeviceKeyProof proof,
        IDeviceKeyProofVerifier verifier,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        RequireLiveStage(attempt, PairingAttemptStage.AwaitingDeviceProof, nowUtc);
        var establishment = await DeviceKeyHandshake.VerifyProofAsync(
            attempt.Request!.DeviceKey,
            attempt.Challenge!,
            proof,
            verifier,
            nowUtc,
            cancellationToken).ConfigureAwait(false);
        return new PairingAttempt(
            attempt.Offer,
            attempt.DesktopNonceBase64Url,
            PairingAttemptStage.Completed,
            attempt.CodeConsumedUtc,
            attempt.Request,
            attempt.Challenge,
            establishment,
            nowUtc);
    }

    public static PairingAttempt Expire(PairingAttempt attempt, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        if (nowUtc < attempt.ExpiresUtc)
        {
            throw new InvalidOperationException("A live pairing attempt cannot expire early.");
        }

        if (attempt.Stage is PairingAttemptStage.Completed or PairingAttemptStage.Denied or PairingAttemptStage.Expired)
        {
            throw new InvalidOperationException("A terminal pairing attempt cannot transition again.");
        }

        return new PairingAttempt(
            attempt.Offer,
            attempt.DesktopNonceBase64Url,
            PairingAttemptStage.Expired,
            attempt.CodeConsumedUtc,
            attempt.Request,
            attempt.Challenge,
            establishment: null,
            endedUtc: attempt.ExpiresUtc);
    }

    private static void RequireLiveStage(
        PairingAttempt attempt,
        PairingAttemptStage required,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        if (attempt.Stage != required)
        {
            throw new InvalidOperationException($"Expected pairing stage {required}, not {attempt.Stage}.");
        }

        if (nowUtc >= attempt.ExpiresUtc)
        {
            throw new InvalidOperationException("The pairing attempt has expired.");
        }
    }
}

public enum SessionResumeStage
{
    AwaitingDeviceProof = 1,
    Completed,
    Expired,
}

/// <summary>
/// One desktop-issued resume challenge and its single outcome. The desktop persists it by challenge
/// id: once completed or expired it cannot establish a session again, and a completed resume's key
/// epoch is recorded on the device so no earlier or equal epoch can ever be established again.
/// </summary>
public sealed record SessionResumeAttempt
{
    public SessionResumeAttempt(
        SessionResumeRequest request,
        HandshakeChallenge challenge,
        SessionResumeStage stage,
        SessionEstablished? establishment = null,
        DateTimeOffset? endedUtc = null)
    {
        Request = ProtocolGuard.NotNull(request, nameof(request));
        Challenge = ProtocolGuard.NotNull(challenge, nameof(challenge));
        Stage = ProtocolGuard.Defined(stage, nameof(stage));
        Establishment = establishment;
        EndedUtc = ProtocolGuard.UtcOptional(endedUtc, nameof(endedUtc));

        bool answers;
        try
        {
            answers = challenge.Assignment.DeviceId == request.DeviceId &&
                      HandshakeTranscript.FromChallenge(challenge, request, challenge.DesktopIdentityKey).Matches(challenge);
        }
        catch (ArgumentException)
        {
            answers = false;
        }

        if (!answers)
        {
            throw new ArgumentException("The challenge must bind this resume request and transcript.", nameof(challenge));
        }

        if (establishment is not null && !establishment.Answers(challenge))
        {
            throw new ArgumentException("The session must be established from this challenge.", nameof(establishment));
        }

        var validShape = stage switch
        {
            SessionResumeStage.AwaitingDeviceProof => establishment is null && endedUtc is null,
            SessionResumeStage.Completed => establishment is not null && endedUtc == establishment.EstablishedUtc,
            SessionResumeStage.Expired => establishment is null && endedUtc == challenge.ExpiresUtc,
            _ => false,
        };

        if (!validShape)
        {
            throw new ArgumentException($"The resume fields are inconsistent with stage {stage}.");
        }
    }

    public SessionResumeRequest Request { get; }

    public HandshakeChallenge Challenge { get; }

    public HandshakeChallengeId ChallengeId => Challenge.ChallengeId;

    public SessionResumeStage Stage { get; }

    public SessionEstablished? Establishment { get; }

    public DateTimeOffset? EndedUtc { get; }
}

/// <summary>
/// Re-proves a paired device key and re-keys the channel for a new session. The desktop ends the
/// device's previous active session as <see cref="DeviceSessionStatus.Replaced"/> once this succeeds
/// and records the new session with <see cref="DeviceLifecycle.RecordSession"/>.
/// </summary>
public static class SessionResumption
{
    public static SessionResumeAttempt Begin(
        PairedDevice device,
        SessionResumeRequest request,
        CompanionProtocolVersion negotiatedVersion,
        HandshakeChallengeId challengeId,
        SessionAssignment assignment,
        EphemeralPublicKey desktopEphemeralKey,
        string desktopNonceBase64Url,
        IDesktopIdentitySigner signer,
        DateTimeOffset nowUtc,
        DateTimeOffset expiresUtc)
    {
        RequireResumable(device, request, nowUtc);
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(signer);
        if (request.NegotiatedVersion != negotiatedVersion)
        {
            throw new ArgumentException("The resume request does not use this connection's negotiated version.", nameof(request));
        }

        if (assignment.DeviceId != device.DeviceId || assignment.SessionExpiresUtc > device.ExpiresUtc)
        {
            throw new ArgumentException("A resumed session belongs to the device and ends before the device expires.", nameof(assignment));
        }

        if (assignment.KeyEpoch <= device.LastKeyEpoch)
        {
            throw new ArgumentException("A resumed session uses a key epoch above every epoch the device has used.", nameof(assignment));
        }

        var challenge = HandshakeTranscript
            .ForSessionResume(
                request,
                challengeId,
                assignment,
                signer.PublicKey,
                desktopEphemeralKey,
                desktopNonceBase64Url,
                nowUtc,
                expiresUtc)
            .Sign(signer);
        return new SessionResumeAttempt(request, challenge, SessionResumeStage.AwaitingDeviceProof);
    }

    public static async ValueTask<SessionResumeAttempt> CompleteAsync(
        SessionResumeAttempt attempt,
        PairedDevice device,
        DeviceKeyProof proof,
        IDeviceKeyProofVerifier verifier,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Stage != SessionResumeStage.AwaitingDeviceProof)
        {
            throw new InvalidOperationException("A resume challenge establishes at most one session.");
        }

        RequireResumable(device, attempt.Request, nowUtc);
        if (attempt.Challenge.Assignment.DeviceId != device.DeviceId ||
            attempt.Challenge.Assignment.KeyEpoch <= device.LastKeyEpoch)
        {
            throw new UnauthorizedAccessException("The challenge does not bind a fresh session for this device.");
        }

        var establishment = await DeviceKeyHandshake.VerifyProofAsync(
            device.DeviceKey,
            attempt.Challenge,
            proof,
            verifier,
            nowUtc,
            cancellationToken).ConfigureAwait(false);
        return new SessionResumeAttempt(
            attempt.Request,
            attempt.Challenge,
            SessionResumeStage.Completed,
            establishment,
            establishment.EstablishedUtc);
    }

    public static SessionResumeAttempt Expire(SessionResumeAttempt attempt, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var now = ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        if (attempt.Stage != SessionResumeStage.AwaitingDeviceProof || now < attempt.Challenge.ExpiresUtc)
        {
            throw new InvalidOperationException("Only a live resume challenge past its expiry can expire.");
        }

        return new SessionResumeAttempt(
            attempt.Request,
            attempt.Challenge,
            SessionResumeStage.Expired,
            endedUtc: attempt.Challenge.ExpiresUtc);
    }

    private static void RequireResumable(PairedDevice device, SessionResumeRequest request, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(request);
        if (!DeviceLifecycle.IsLive(device, nowUtc))
        {
            throw new UnauthorizedAccessException("A revoked, replaced, or expired device cannot resume a session.");
        }

        if (request.DeviceId != device.DeviceId ||
            request.DeviceKeyId != device.DeviceKey.KeyId ||
            !string.Equals(request.CredentialIdBase64Url, device.DeviceKey.CredentialIdBase64Url, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The resume request does not name this device's bound key.");
        }

        if (!CompanionProtocolVersion.Current.CanRead(request.NegotiatedVersion))
        {
            throw new ArgumentException("The resume request does not use a supported negotiated version.", nameof(request));
        }
    }
}

internal static class DeviceKeyHandshake
{
    public static async ValueTask<SessionEstablished> VerifyProofAsync(
        DevicePublicKey deviceKey,
        HandshakeChallenge challenge,
        DeviceKeyProof proof,
        IDeviceKeyProofVerifier verifier,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceKey);
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(verifier);
        var now = ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        if (now < challenge.IssuedUtc || now >= challenge.ExpiresUtc)
        {
            throw new InvalidOperationException("The device proof challenge is not live.");
        }

        if (proof.ChallengeId != challenge.ChallengeId)
        {
            throw new InvalidOperationException("The proof names another challenge.");
        }

        if (challenge.DeviceKeyId != deviceKey.KeyId ||
            !string.Equals(challenge.CredentialIdBase64Url, deviceKey.CredentialIdBase64Url, StringComparison.Ordinal) ||
            !string.Equals(proof.Assertion.CredentialIdBase64Url, deviceKey.CredentialIdBase64Url, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The proof does not use the bound credential.");
        }

        if (!proof.Assertion.ClientDataMatches(challenge.TranscriptHashBase64Url))
        {
            throw new UnauthorizedAccessException("The WebAuthn client data does not answer this challenge.");
        }

        if (!await verifier.VerifyAsync(deviceKey, challenge, proof, cancellationToken).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("The bound device key did not prove the challenge.");
        }

        return new SessionEstablished(
            challenge.ChallengeId,
            challenge.Purpose,
            deviceKey.KeyId,
            challenge.Assignment,
            challenge.TranscriptHashBase64Url,
            now);
    }
}

public sealed record PairingRateObservation(string SourceHash, DateTimeOffset ObservedUtc)
{
    public string SourceHash { get; } = ProtocolGuard.Base64Url(
        SourceHash,
        nameof(SourceHash),
        ProtocolBounds.MaxShortStringBytes);

    public DateTimeOffset ObservedUtc { get; } = ProtocolGuard.Utc(ObservedUtc, nameof(ObservedUtc));
}

public sealed record PairingRateState
{
    public PairingRateState(IReadOnlyList<PairingRateObservation> observations)
    {
        Observations = ProtocolGuard.List(
            observations,
            nameof(observations),
            PairingRateLimiter.MaxObservations);
    }

    public IReadOnlyList<PairingRateObservation> Observations { get; }

    public static PairingRateState Empty { get; } = new([]);
}

public sealed record PairingRateDecision(bool Accepted, DateTimeOffset? RetryAfterUtc, PairingRateState State);

/// <summary>
/// Bounds pairing-code attempts per source within <see cref="ProtocolBounds.PairingRateWindow"/>.
/// The source hash is the transport's keyed hash of the remote network source; see the transport
/// binding in docs/PAIRED_DEVICE_PROTOCOL.md.
/// </summary>
public static class PairingRateLimiter
{
    /// <summary>The most observations the desktop retains; beyond it every new attempt fails closed.</summary>
    public const int MaxObservations = ProtocolBounds.MaxDevices * ProtocolBounds.MaxPairingAttemptsPerWindow;

    public static PairingRateDecision TryConsume(
        PairingRateState state,
        string sourceHash,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        var source = ProtocolGuard.Base64Url(sourceHash, nameof(sourceHash), ProtocolBounds.MaxShortStringBytes);
        var now = ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        var cutoff = now.Subtract(ProtocolBounds.PairingRateWindow);
        var live = state.Observations.Where(item => item.ObservedUtc > cutoff).ToList();
        var matching = live.Where(item => string.Equals(item.SourceHash, source, StringComparison.Ordinal)).ToArray();
        if (matching.Length >= ProtocolBounds.MaxPairingAttemptsPerWindow)
        {
            return new PairingRateDecision(
                false,
                matching.Min(item => item.ObservedUtc).Add(ProtocolBounds.PairingRateWindow),
                new PairingRateState(live));
        }

        // A flood of distinct sources must neither grow desktop memory nor evict an attacker's own
        // history and reset its count, so a full window refuses every attempt until one ages out.
        if (live.Count >= MaxObservations)
        {
            return new PairingRateDecision(
                false,
                live.Min(item => item.ObservedUtc).Add(ProtocolBounds.PairingRateWindow),
                new PairingRateState(live));
        }

        live.Add(new PairingRateObservation(source, now));
        return new PairingRateDecision(true, null, new PairingRateState(live));
    }
}
