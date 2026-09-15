using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.CompanionProtocol;

public enum DeviceKeyAlgorithm
{
    WebAuthnEs256 = 1,
}

public enum EphemeralKeyAlgorithm
{
    EcdhP256 = 1,
}

public enum DesktopIdentityKeyAlgorithm
{
    EcdsaP256Sha256 = 1,
}

public enum HandshakePurpose
{
    Pairing = 1,
    SessionResume,
}

/// <summary>
/// The tablet's reusable WebAuthn ES256 credential. Only public material crosses the wire; the
/// key id is the SHA-256 thumbprint of the exact COSE key bytes, so it cannot name another key.
/// </summary>
public sealed record DevicePublicKey
{
    public DevicePublicKey(
        DeviceKeyId keyId,
        DeviceKeyAlgorithm algorithm,
        string credentialIdBase64Url,
        string cosePublicKeyBase64Url)
    {
        Algorithm = ProtocolGuard.Defined(algorithm, nameof(algorithm));
        CredentialIdBase64Url = ProtocolGuard.Base64Url(
            credentialIdBase64Url,
            nameof(credentialIdBase64Url),
            maximumDecodedBytes: ProtocolBounds.MaxCredentialIdBytes);
        var cose = ProtocolGuard.DecodeBase64Url(
            cosePublicKeyBase64Url,
            nameof(cosePublicKeyBase64Url),
            maximumDecodedBytes: ProtocolBounds.MaxCosePublicKeyBytes);
        CosePublicKeyBase64Url = cosePublicKeyBase64Url;
        KeyId = string.Equals(keyId.Value, ProtocolGuard.Thumbprint(cose), StringComparison.Ordinal)
            ? keyId
            : throw new ArgumentException("A device key id is the SHA-256 thumbprint of its COSE key.", nameof(keyId));
    }

    public DeviceKeyId KeyId { get; }

    public DeviceKeyAlgorithm Algorithm { get; }

    public string CredentialIdBase64Url { get; }

    public string CosePublicKeyBase64Url { get; }
}

/// <summary>A fresh P-256 ECDH public key for exactly one pairing or session handshake.</summary>
public sealed record EphemeralPublicKey
{
    public EphemeralPublicKey(EphemeralKeyAlgorithm algorithm, string subjectPublicKeyInfoBase64Url)
    {
        Algorithm = ProtocolGuard.Defined(algorithm, nameof(algorithm));
        _ = ProtocolGuard.P256SubjectPublicKeyInfo(subjectPublicKeyInfoBase64Url, nameof(subjectPublicKeyInfoBase64Url));
        SubjectPublicKeyInfoBase64Url = subjectPublicKeyInfoBase64Url;
    }

    public EphemeralKeyAlgorithm Algorithm { get; }

    public string SubjectPublicKeyInfoBase64Url { get; }
}

/// <summary>
/// The desktop's long-lived ECDSA P-256 identity. A tablet learns it out of band from the QR
/// payload or by comparing the verification code, then authenticates every handshake with it so
/// a relay cannot impersonate the desktop.
/// </summary>
public sealed record DesktopIdentityKey
{
    public DesktopIdentityKey(
        DeviceKeyId keyId,
        DesktopIdentityKeyAlgorithm algorithm,
        string subjectPublicKeyInfoBase64Url)
    {
        Algorithm = ProtocolGuard.Defined(algorithm, nameof(algorithm));
        var spki = ProtocolGuard.P256SubjectPublicKeyInfo(
            subjectPublicKeyInfoBase64Url,
            nameof(subjectPublicKeyInfoBase64Url));
        SubjectPublicKeyInfoBase64Url = subjectPublicKeyInfoBase64Url;
        KeyId = string.Equals(keyId.Value, ProtocolGuard.Thumbprint(spki), StringComparison.Ordinal)
            ? keyId
            : throw new ArgumentException("A desktop identity key id is the SHA-256 thumbprint of its SPKI.", nameof(keyId));
    }

    public DeviceKeyId KeyId { get; }

    public DesktopIdentityKeyAlgorithm Algorithm { get; }

    public string SubjectPublicKeyInfoBase64Url { get; }
}

/// <summary>
/// The identity, route, and lifetime allocated by the desktop before device proof. It is part of
/// the signed transcript, so neither a gateway nor a relay can substitute the final session.
/// </summary>
public sealed record SessionAssignment
{
    public SessionAssignment(
        CompanionProtocolVersion protocolVersion,
        CompanionDeviceId deviceId,
        DeviceSessionId sessionId,
        RelayChannelId relayChannelId,
        long keyEpoch,
        RelayCipherSuite cipherSuite,
        DateTimeOffset sessionExpiresUtc)
    {
        ProtocolVersion = ProtocolGuard.Version(protocolVersion, nameof(protocolVersion));
        DeviceId = deviceId.Value == Guid.Empty
            ? throw new ArgumentException("An assigned device id is required.", nameof(deviceId))
            : deviceId;
        SessionId = sessionId.Value == Guid.Empty
            ? throw new ArgumentException("An assigned session id is required.", nameof(sessionId))
            : sessionId;
        RelayChannelId = relayChannelId.Value == Guid.Empty
            ? throw new ArgumentException("An assigned relay channel is required.", nameof(relayChannelId))
            : relayChannelId;
        KeyEpoch = ProtocolGuard.KeyEpoch(keyEpoch, nameof(keyEpoch));
        CipherSuite = ProtocolGuard.Defined(cipherSuite, nameof(cipherSuite));
        SessionExpiresUtc = ProtocolGuard.Utc(sessionExpiresUtc, nameof(sessionExpiresUtc));
    }

    public CompanionProtocolVersion ProtocolVersion { get; }

    public CompanionDeviceId DeviceId { get; }

    public DeviceSessionId SessionId { get; }

    public RelayChannelId RelayChannelId { get; }

    public long KeyEpoch { get; }

    public RelayCipherSuite CipherSuite { get; }

    public DateTimeOffset SessionExpiresUtc { get; }
}

/// <summary>
/// Returned after the transport consumes the rate-limited one-time code. It commits the desktop's
/// identity, ephemeral key, and nonce before the tablet reveals its own request.
/// </summary>
public sealed record PairingOffer
{
    public PairingOffer(
        PairingAttemptId attemptId,
        DesktopIdentityKey desktopIdentityKey,
        EphemeralPublicKey desktopEphemeralKey,
        string desktopNonceBase64Url,
        DateTimeOffset offeredUtc,
        DateTimeOffset expiresUtc)
    {
        AttemptId = attemptId.Value == Guid.Empty
            ? throw new ArgumentException("A pairing attempt id is required.", nameof(attemptId))
            : attemptId;
        DesktopIdentityKey = ProtocolGuard.NotNull(desktopIdentityKey, nameof(desktopIdentityKey));
        DesktopEphemeralKey = ProtocolGuard.NotNull(desktopEphemeralKey, nameof(desktopEphemeralKey));
        DesktopNonceBase64Url = ProtocolGuard.Base64Url(
            desktopNonceBase64Url,
            nameof(desktopNonceBase64Url),
            exactDecodedBytes: ProtocolBounds.PairingNonceBytes);
        OfferedUtc = ProtocolGuard.Utc(offeredUtc, nameof(offeredUtc));
        ExpiresUtc = ProtocolGuard.Utc(expiresUtc, nameof(expiresUtc));
        if (ExpiresUtc <= OfferedUtc || ExpiresUtc - OfferedUtc > ProtocolBounds.MaximumPairingLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresUtc), "A pairing offer expires within ten minutes.");
        }
    }

    public PairingAttemptId AttemptId { get; }

    public DesktopIdentityKey DesktopIdentityKey { get; }

    public EphemeralPublicKey DesktopEphemeralKey { get; }

    public string DesktopNonceBase64Url { get; }

    public DateTimeOffset OfferedUtc { get; }

    public DateTimeOffset ExpiresUtc { get; }
}

/// <summary>
/// The requested display name sealed to the offer's desktop ephemeral key, so a relay that routes
/// a pairing request cannot read the name. See <see cref="PairingCryptography.SealDeviceName"/>.
/// </summary>
public sealed record SealedDeviceName
{
    public SealedDeviceName(string ciphertextBase64Url, string authenticationTagBase64Url)
    {
        CiphertextBase64Url = ProtocolGuard.Base64Url(
            ciphertextBase64Url,
            nameof(ciphertextBase64Url),
            maximumDecodedBytes: ProtocolBounds.MaxDeviceNameBytes);
        AuthenticationTagBase64Url = ProtocolGuard.Base64Url(
            authenticationTagBase64Url,
            nameof(authenticationTagBase64Url),
            exactDecodedBytes: ProtocolBounds.RelayAuthenticationTagBytes);
    }

    public string CiphertextBase64Url { get; }

    public string AuthenticationTagBase64Url { get; }
}

/// <summary>
/// The JSON pairing body deliberately omits the human short code. A transport resolves that
/// one-time code from a redacted header, rate limits it, and then binds this public key to the
/// attempt. Desktop approval and proof of the bound device key are still required.
/// </summary>
public sealed record PairingRequest
{
    public PairingRequest(
        PairingAttemptId attemptId,
        CompanionProtocolVersion negotiatedVersion,
        DevicePublicKey deviceKey,
        EphemeralPublicKey ephemeralKey,
        string clientNonceBase64Url,
        SealedDeviceName requestedDeviceName)
    {
        AttemptId = attemptId.Value == Guid.Empty
            ? throw new ArgumentException("A pairing attempt id is required.", nameof(attemptId))
            : attemptId;
        NegotiatedVersion = ProtocolGuard.Version(negotiatedVersion, nameof(negotiatedVersion));
        DeviceKey = ProtocolGuard.NotNull(deviceKey, nameof(deviceKey));
        EphemeralKey = ProtocolGuard.NotNull(ephemeralKey, nameof(ephemeralKey));
        ClientNonceBase64Url = ProtocolGuard.Base64Url(
            clientNonceBase64Url,
            nameof(clientNonceBase64Url),
            exactDecodedBytes: ProtocolBounds.PairingNonceBytes);
        RequestedDeviceName = ProtocolGuard.NotNull(requestedDeviceName, nameof(requestedDeviceName));
    }

    public PairingAttemptId AttemptId { get; }

    public CompanionProtocolVersion NegotiatedVersion { get; }

    public DevicePublicKey DeviceKey { get; }

    public EphemeralPublicKey EphemeralKey { get; }

    public string ClientNonceBase64Url { get; }

    public SealedDeviceName RequestedDeviceName { get; }
}

/// <summary>
/// Starts a new session for an already paired device, for example after a browser reload
/// discarded its memory-only traffic keys. It re-proves the device key and re-keys the channel.
/// </summary>
public sealed record SessionResumeRequest
{
    public SessionResumeRequest(
        CompanionProtocolVersion negotiatedVersion,
        CompanionDeviceId deviceId,
        DeviceKeyId deviceKeyId,
        string credentialIdBase64Url,
        EphemeralPublicKey ephemeralKey,
        string clientNonceBase64Url)
    {
        NegotiatedVersion = ProtocolGuard.Version(negotiatedVersion, nameof(negotiatedVersion));
        DeviceId = deviceId.Value == Guid.Empty
            ? throw new ArgumentException("A paired device id is required.", nameof(deviceId))
            : deviceId;
        DeviceKeyId = string.IsNullOrEmpty(deviceKeyId.Value)
            ? throw new ArgumentException("A device key id is required.", nameof(deviceKeyId))
            : deviceKeyId;
        CredentialIdBase64Url = ProtocolGuard.Base64Url(
            credentialIdBase64Url,
            nameof(credentialIdBase64Url),
            maximumDecodedBytes: ProtocolBounds.MaxCredentialIdBytes);
        EphemeralKey = ProtocolGuard.NotNull(ephemeralKey, nameof(ephemeralKey));
        ClientNonceBase64Url = ProtocolGuard.Base64Url(
            clientNonceBase64Url,
            nameof(clientNonceBase64Url),
            exactDecodedBytes: ProtocolBounds.PairingNonceBytes);
    }

    public CompanionProtocolVersion NegotiatedVersion { get; }

    public CompanionDeviceId DeviceId { get; }

    public DeviceKeyId DeviceKeyId { get; }

    public string CredentialIdBase64Url { get; }

    public EphemeralPublicKey EphemeralKey { get; }

    public string ClientNonceBase64Url { get; }
}

/// <summary>
/// One desktop challenge for pairing or session resume. It carries every desktop-side transcript
/// input, the transcript hash the WebAuthn assertion must sign, and the desktop identity
/// signature over that hash. Construction verifies the signature, so a challenge object is always
/// authenticated by the identity key it names.
/// </summary>
public sealed record HandshakeChallenge
{
    public HandshakeChallenge(
        HandshakeChallengeId challengeId,
        HandshakePurpose purpose,
        PairingAttemptId? attemptId,
        string? pairingCommitmentBase64Url,
        DeviceKeyId deviceKeyId,
        string credentialIdBase64Url,
        SessionAssignment assignment,
        DesktopIdentityKey desktopIdentityKey,
        EphemeralPublicKey desktopEphemeralKey,
        string desktopNonceBase64Url,
        string transcriptHashBase64Url,
        string desktopSignatureBase64Url,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc)
    {
        ChallengeId = challengeId.Value == Guid.Empty
            ? throw new ArgumentException("A challenge id is required.", nameof(challengeId))
            : challengeId;
        Purpose = ProtocolGuard.Defined(purpose, nameof(purpose));
        var pairing = purpose == HandshakePurpose.Pairing;
        if (pairing != (attemptId is not null) || pairing != (pairingCommitmentBase64Url is not null))
        {
            throw new ArgumentException("Only a pairing challenge names an attempt and pairing commitment.", nameof(attemptId));
        }

        AttemptId = attemptId is { } attempt && attempt.Value == Guid.Empty
            ? throw new ArgumentException("A pairing attempt id is required.", nameof(attemptId))
            : attemptId;
        PairingCommitmentBase64Url = pairingCommitmentBase64Url is null
            ? null
            : ProtocolGuard.Base64Url(
                pairingCommitmentBase64Url,
                nameof(pairingCommitmentBase64Url),
                exactDecodedBytes: ProtocolBounds.TranscriptHashBytes);
        DeviceKeyId = string.IsNullOrEmpty(deviceKeyId.Value)
            ? throw new ArgumentException("A device key id is required.", nameof(deviceKeyId))
            : deviceKeyId;
        CredentialIdBase64Url = ProtocolGuard.Base64Url(
            credentialIdBase64Url,
            nameof(credentialIdBase64Url),
            maximumDecodedBytes: ProtocolBounds.MaxCredentialIdBytes);
        Assignment = ProtocolGuard.NotNull(assignment, nameof(assignment));
        DesktopIdentityKey = ProtocolGuard.NotNull(desktopIdentityKey, nameof(desktopIdentityKey));
        DesktopEphemeralKey = ProtocolGuard.NotNull(desktopEphemeralKey, nameof(desktopEphemeralKey));
        DesktopNonceBase64Url = ProtocolGuard.Base64Url(
            desktopNonceBase64Url,
            nameof(desktopNonceBase64Url),
            exactDecodedBytes: ProtocolBounds.PairingNonceBytes);
        var transcriptHash = ProtocolGuard.DecodeBase64Url(
            transcriptHashBase64Url,
            nameof(transcriptHashBase64Url),
            exactDecodedBytes: ProtocolBounds.TranscriptHashBytes);
        TranscriptHashBase64Url = transcriptHashBase64Url;
        var signature = ProtocolGuard.DecodeBase64Url(
            desktopSignatureBase64Url,
            nameof(desktopSignatureBase64Url),
            exactDecodedBytes: ProtocolBounds.DesktopSignatureBytes);
        DesktopSignatureBase64Url = desktopSignatureBase64Url;
        IssuedUtc = ProtocolGuard.Utc(issuedUtc, nameof(issuedUtc));
        ExpiresUtc = ProtocolGuard.Utc(expiresUtc, nameof(expiresUtc));

        var maximumLifetime = pairing
            ? ProtocolBounds.MaximumPairingLifetime
            : ProtocolBounds.HandshakeChallengeLifetime;
        if (ExpiresUtc <= IssuedUtc || ExpiresUtc - IssuedUtc > maximumLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresUtc), "A handshake challenge is short lived.");
        }

        if (Assignment.SessionExpiresUtc <= ExpiresUtc ||
            Assignment.SessionExpiresUtc - IssuedUtc > ProtocolBounds.MaximumSessionLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(assignment),
                "An assigned session outlives its challenge and stays within the session lifetime.");
        }

        if (!PairingCryptography.VerifyDesktopSignature(DesktopIdentityKey, transcriptHash, signature))
        {
            throw new ArgumentException("The desktop identity signature does not cover this transcript hash.", nameof(desktopSignatureBase64Url));
        }
    }

    public HandshakeChallengeId ChallengeId { get; }

    public HandshakePurpose Purpose { get; }

    public PairingAttemptId? AttemptId { get; }

    public string? PairingCommitmentBase64Url { get; }

    public DeviceKeyId DeviceKeyId { get; }

    public string CredentialIdBase64Url { get; }

    public SessionAssignment Assignment { get; }

    public DesktopIdentityKey DesktopIdentityKey { get; }

    public EphemeralPublicKey DesktopEphemeralKey { get; }

    public string DesktopNonceBase64Url { get; }

    /// <summary>base64url(SHA-256(handshake transcript)); the raw 32 bytes are the WebAuthn challenge.</summary>
    public string TranscriptHashBase64Url { get; }

    /// <summary>IEEE P1363 ECDSA P-256/SHA-256 signature over the desktop signature input.</summary>
    public string DesktopSignatureBase64Url { get; }

    public DateTimeOffset IssuedUtc { get; }

    public DateTimeOffset ExpiresUtc { get; }
}

/// <summary>
/// A WebAuthn get() assertion. The protocol checks its structure, credential, flags, type, and
/// challenge; <see cref="IDeviceKeyProofVerifier"/> checks the signature, relying-party id hash,
/// origin, and signature counter against the bound COSE key.
/// </summary>
public sealed record WebAuthnAssertion
{
    private const byte UserPresentFlag = 0x01;
    private const byte UserVerifiedFlag = 0x04;
    private const int FlagsOffset = 32;

    public WebAuthnAssertion(
        string credentialIdBase64Url,
        string authenticatorDataBase64Url,
        string clientDataJsonBase64Url,
        string signatureBase64Url)
    {
        CredentialIdBase64Url = ProtocolGuard.Base64Url(
            credentialIdBase64Url,
            nameof(credentialIdBase64Url),
            maximumDecodedBytes: ProtocolBounds.MaxCredentialIdBytes);
        var authenticatorData = ProtocolGuard.DecodeBase64Url(
            authenticatorDataBase64Url,
            nameof(authenticatorDataBase64Url),
            maximumDecodedBytes: ProtocolBounds.MaxAuthenticatorDataBytes);
        if (authenticatorData.Length < ProtocolBounds.MinAuthenticatorDataBytes)
        {
            throw new ArgumentException("Authenticator data is at least 37 bytes.", nameof(authenticatorDataBase64Url));
        }

        var flags = authenticatorData[FlagsOffset];
        if ((flags & UserPresentFlag) == 0 || (flags & UserVerifiedFlag) == 0)
        {
            throw new ArgumentException("A paired-device proof requires user presence and user verification.", nameof(authenticatorDataBase64Url));
        }

        AuthenticatorDataBase64Url = authenticatorDataBase64Url;
        _ = ProtocolGuard.DecodeBase64Url(
            clientDataJsonBase64Url,
            nameof(clientDataJsonBase64Url),
            maximumDecodedBytes: ProtocolBounds.MaxClientDataJsonBytes);
        ClientDataJsonBase64Url = clientDataJsonBase64Url;
        SignatureBase64Url = ProtocolGuard.Base64Url(
            signatureBase64Url,
            nameof(signatureBase64Url),
            maximumDecodedBytes: ProtocolBounds.MaxAssertionSignatureBytes);
    }

    public string CredentialIdBase64Url { get; }

    public string AuthenticatorDataBase64Url { get; }

    public string ClientDataJsonBase64Url { get; }

    public string SignatureBase64Url { get; }

    /// <summary>
    /// True when the client data is a get() ceremony for exactly this challenge and is not a
    /// cross-origin iframe assertion. Malformed client data is treated as not matching.
    /// </summary>
    public bool ClientDataMatches(string expectedChallengeBase64Url)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedChallengeBase64Url);
        try
        {
            var bytes = ProtocolGuard.DecodeBase64Url(ClientDataJsonBase64Url, nameof(ClientDataJsonBase64Url));
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("type", out var type) &&
                   type.ValueKind == JsonValueKind.String &&
                   type.ValueEquals("webauthn.get") &&
                   root.TryGetProperty("challenge", out var challenge) &&
                   challenge.ValueKind == JsonValueKind.String &&
                   challenge.ValueEquals(expectedChallengeBase64Url) &&
                   (!root.TryGetProperty("crossOrigin", out var crossOrigin) ||
                    crossOrigin.ValueKind == JsonValueKind.False);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public sealed record DeviceKeyProof
{
    public DeviceKeyProof(HandshakeChallengeId challengeId, WebAuthnAssertion assertion)
    {
        ChallengeId = challengeId.Value == Guid.Empty
            ? throw new ArgumentException("A challenge id is required.", nameof(challengeId))
            : challengeId;
        Assertion = ProtocolGuard.NotNull(assertion, nameof(assertion));
    }

    public HandshakeChallengeId ChallengeId { get; }

    public WebAuthnAssertion Assertion { get; }
}

/// <summary>The successful handshake response; it contains no reusable bearer or private material.</summary>
public sealed record SessionEstablished
{
    public SessionEstablished(
        HandshakeChallengeId challengeId,
        HandshakePurpose purpose,
        DeviceKeyId deviceKeyId,
        SessionAssignment assignment,
        string transcriptHashBase64Url,
        DateTimeOffset establishedUtc)
    {
        ChallengeId = challengeId.Value == Guid.Empty
            ? throw new ArgumentException("A challenge id is required.", nameof(challengeId))
            : challengeId;
        Purpose = ProtocolGuard.Defined(purpose, nameof(purpose));
        DeviceKeyId = string.IsNullOrEmpty(deviceKeyId.Value)
            ? throw new ArgumentException("A device key id is required.", nameof(deviceKeyId))
            : deviceKeyId;
        Assignment = ProtocolGuard.NotNull(assignment, nameof(assignment));
        TranscriptHashBase64Url = ProtocolGuard.Base64Url(
            transcriptHashBase64Url,
            nameof(transcriptHashBase64Url),
            exactDecodedBytes: ProtocolBounds.TranscriptHashBytes);
        EstablishedUtc = ProtocolGuard.Utc(establishedUtc, nameof(establishedUtc));
        if (Assignment.SessionExpiresUtc <= EstablishedUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(establishedUtc), "A session cannot be established after it expires.");
        }
    }

    public HandshakeChallengeId ChallengeId { get; }

    public HandshakePurpose Purpose { get; }

    public DeviceKeyId DeviceKeyId { get; }

    public SessionAssignment Assignment { get; }

    public string TranscriptHashBase64Url { get; }

    public DateTimeOffset EstablishedUtc { get; }

    [JsonIgnore]
    public DateTimeOffset SessionExpiresUtc => Assignment.SessionExpiresUtc;
}

/// <summary>Verifies the WebAuthn signature and relying-party checks for a bound device key.</summary>
public interface IDeviceKeyProofVerifier
{
    ValueTask<bool> VerifyAsync(
        DevicePublicKey deviceKey,
        HandshakeChallenge challenge,
        DeviceKeyProof proof,
        CancellationToken cancellationToken);
}

/// <summary>
/// Signs handshake transcripts with the desktop identity key. Downstream implementations keep
/// the private key DPAPI-protected; the protocol only sees the public key and signatures.
/// </summary>
public interface IDesktopIdentitySigner
{
    DesktopIdentityKey PublicKey { get; }

    /// <summary>Returns the 64-byte IEEE P1363 ECDSA P-256/SHA-256 signature of the exact input.</summary>
    byte[] Sign(ReadOnlySpan<byte> signatureInput);
}
