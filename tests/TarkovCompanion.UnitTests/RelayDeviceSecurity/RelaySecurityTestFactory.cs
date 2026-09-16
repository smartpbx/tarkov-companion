using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.Storage;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

internal sealed class RelayTestClock : TimeProvider
{
    public RelayTestClock(DateTimeOffset utcNow) => UtcNow = utcNow;

    public DateTimeOffset UtcNow { get; private set; }

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan elapsed) => UtcNow = UtcNow.Add(elapsed);
}

internal sealed record RelayTestContext(
    RelayTestClock Clock,
    OwnerRecoveryProtector Recovery,
    RelayDeviceRegistry Registry,
    RelaySessionCredential OwnerCredential,
    PairingAttempt OwnerPairing) : IDisposable
{
    public DevicePublicKey OwnerKey => OwnerPairing.Request!.DeviceKey;

    public void Dispose() => Recovery.Dispose();

    public async ValueTask<RelayPrincipal> AuthenticateOwnerAsync()
    {
        var result = await Registry.AuthenticateAsync(OwnerCredential.SessionId, OwnerCredential.Secret);
        return Assert.IsType<RelayPrincipal>(result.Principal);
    }
}

internal static class RelaySecurityTestFactory
{
    private static readonly ConcurrentDictionary<string, DevicePublicKey> DeviceKeys = new(StringComparer.Ordinal);
    private static readonly byte[] SourceHashKey = Enumerable.Repeat((byte)0x6d, 32).ToArray();

    public static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    public static async ValueTask<RelayTestContext> BootstrapAsync(string? storePath = null)
    {
        var clock = new RelayTestClock(Now);
        var recovery = new OwnerRecoveryProtector(Enumerable.Repeat((byte)0x5a, 32).ToArray(), clock);
        var store = storePath is null ? null : new VerifiedRelayRegistryStore(storePath);
        var registry = await RelayDeviceRegistry.OpenAsync(clock, recovery, store);
        var ownerId = new CompanionDeviceId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var pairing = await CompletedPairingAsync(
            "owner",
            clock.UtcNow,
            ownerId,
            new DeviceSessionId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            new RelayChannelId(Guid.Parse("33333333-3333-3333-3333-333333333333")));
        var grant = recovery.CreateGrant(ownerId, pairing.Request!.DeviceKey.KeyId);
        var recovered = await registry.RecoverOwnerAsync(grant, pairing, CompanionSurfaceKind.Desktop);
        return new RelayTestContext(
            clock,
            recovery,
            registry,
            Assert.IsType<RelaySessionCredential>(recovered.Value),
            pairing);
    }

    public static DevicePublicKey DeviceKey(string suffix) => DeviceKeys.GetOrAdd(suffix, static value =>
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: false);
        byte[] cose =
        [
            0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20,
            .. parameters.Q.X!,
            0x22, 0x58, 0x20,
            .. parameters.Q.Y!,
        ];
        return new DevicePublicKey(
            new DeviceKeyId(Base64Url(SHA256.HashData(cose))),
            DeviceKeyAlgorithm.WebAuthnEs256,
            Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes("credential/" + value))[..16]),
            Base64Url(cose));
    });

    public static async ValueTask<PairingAttempt> CompletedPairingAsync(
        string suffix,
        DateTimeOffset now,
        CompanionDeviceId? deviceId = null,
        DeviceSessionId? sessionId = null,
        RelayChannelId? channelId = null,
        long keyEpoch = 1)
    {
        using var signer = new TestSigner();
        using var desktopEphemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var tabletEphemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var attemptId = new PairingAttemptId(Guid.NewGuid());
        var desktopNonce = Base64Url(RandomNumberGenerator.GetBytes(ProtocolBounds.PairingNonceBytes));
        var attempt = PairingStateMachine.Offer(
            attemptId,
            signer.PublicKey,
            Ephemeral(desktopEphemeral),
            desktopNonce,
            now);
        var request = new PairingRequest(
            attemptId,
            CompanionProtocolVersion.Current,
            DeviceKey(suffix),
            Ephemeral(tabletEphemeral),
            Base64Url(RandomNumberGenerator.GetBytes(ProtocolBounds.PairingNonceBytes)),
            new SealedDeviceName(
                Base64Url(Encoding.UTF8.GetBytes("Device " + suffix)),
                Base64Url(RandomNumberGenerator.GetBytes(ProtocolBounds.RelayAuthenticationTagBytes))));
        var bound = PairingStateMachine.BindResolvedCode(
            attempt,
            request,
            CompanionProtocolVersion.Current,
            now);
        var assignment = new SessionAssignment(
            CompanionProtocolVersion.Current,
            deviceId ?? new CompanionDeviceId(Guid.NewGuid()),
            sessionId ?? new DeviceSessionId(Guid.NewGuid()),
            channelId ?? new RelayChannelId(Guid.NewGuid()),
            keyEpoch,
            RelayCipherSuite.P256HkdfSha256Aes256Gcm,
            now.AddHours(6));
        var challenge = PairingStateMachine.CreateChallenge(
            bound,
            new HandshakeChallengeId(Guid.NewGuid()),
            assignment,
            signer,
            now,
            now.Add(ProtocolBounds.HandshakeChallengeLifetime));
        var approved = PairingStateMachine.Approve(bound, challenge, now);
        return await PairingStateMachine.CompleteAsync(
            approved,
            Proof(challenge),
            AcceptingProofVerifier.Instance,
            now);
    }

    public static async ValueTask<SessionResumeAttempt> CompletedResumeAsync(
        RelayPrincipal principal,
        DevicePublicKey key,
        DateTimeOffset deviceCreatedUtc,
        DateTimeOffset now,
        string suffix = "resume")
    {
        var device = new PairedDevice(
            principal.DeviceId,
            "Device " + suffix,
            key,
            principal.Role,
            principal.Capabilities,
            DeviceLifecycleStatus.Active,
            deviceCreatedUtc,
            now,
            principal.KeyEpoch,
            deviceCreatedUtc.AddDays(30),
            deviceCreatedUtc);
        using var signer = new TestSigner();
        using var desktopEphemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var tabletEphemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var request = new SessionResumeRequest(
            CompanionProtocolVersion.Current,
            principal.DeviceId,
            key.KeyId,
            key.CredentialIdBase64Url,
            Ephemeral(tabletEphemeral),
            Base64Url(RandomNumberGenerator.GetBytes(ProtocolBounds.PairingNonceBytes)));
        var assignment = new SessionAssignment(
            CompanionProtocolVersion.Current,
            principal.DeviceId,
            new DeviceSessionId(Guid.NewGuid()),
            new RelayChannelId(Guid.NewGuid()),
            principal.KeyEpoch + 1,
            RelayCipherSuite.P256HkdfSha256Aes256Gcm,
            now.AddHours(6));
        var attempt = SessionResumption.Begin(
            device,
            request,
            CompanionProtocolVersion.Current,
            new HandshakeChallengeId(Guid.NewGuid()),
            assignment,
            Ephemeral(desktopEphemeral),
            Base64Url(RandomNumberGenerator.GetBytes(ProtocolBounds.PairingNonceBytes)),
            signer,
            now,
            now.Add(ProtocolBounds.HandshakeChallengeLifetime));
        return await SessionResumption.CompleteAsync(
            attempt,
            device,
            Proof(attempt.Challenge),
            AcceptingProofVerifier.Instance,
            now);
    }

    public static PairingOffer Offer(string suffix, DateTimeOffset now, PairingAttemptId? attemptId = null)
    {
        using var signer = new TestSigner();
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return PairingStateMachine.Offer(
            attemptId ?? new PairingAttemptId(Guid.NewGuid()),
            signer.PublicKey,
            Ephemeral(ephemeral),
            Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes("nonce/" + suffix))),
            now).Offer;
    }

    public static OpaqueRelayFrame Frame(
        RelayPrincipal target,
        DateTimeOffset issuedUtc,
        long senderSequence,
        CompanionProtocolVersion? version = null,
        long? keyEpoch = null,
        RelayChannelId? channelId = null,
        DateTimeOffset? expiresUtc = null,
        int ciphertextLength = 4)
    {
        var ciphertext = Enumerable.Repeat((byte)0x4a, ciphertextLength).ToArray();
        var encoded = Base64Url(ciphertext);
        var chunks = Enumerable.Range(
                0,
                (encoded.Length + ProtocolBounds.RelayCiphertextChunkCharacters - 1) /
                ProtocolBounds.RelayCiphertextChunkCharacters)
            .Select(index => encoded.Substring(
                index * ProtocolBounds.RelayCiphertextChunkCharacters,
                Math.Min(
                    ProtocolBounds.RelayCiphertextChunkCharacters,
                    encoded.Length - (index * ProtocolBounds.RelayCiphertextChunkCharacters))))
            .ToArray();
        var epoch = keyEpoch ?? target.KeyEpoch;
        return new OpaqueRelayFrame(
            version ?? target.ProtocolVersion,
            channelId ?? target.ChannelId,
            target.SessionId,
            epoch,
            senderSequence,
            RelayCipherSuite.P256HkdfSha256Aes256Gcm,
            Base64Url(PairingCryptography.EncodeRelayNonce(epoch, senderSequence)),
            ciphertextLength,
            chunks,
            Base64Url(new byte[ProtocolBounds.RelayAuthenticationTagBytes]),
            issuedUtc,
            expiresUtc ?? issuedUtc.AddMinutes(1));
    }

    public static string SourceHash(string address) =>
        RelayRateLimiter.HashSource(SourceHashKey, IPAddress.Parse(address));

    public static string Base64Url(string value) => Base64Url(Encoding.UTF8.GetBytes(value));

    public static string Base64Url(ReadOnlySpan<byte> value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static EphemeralPublicKey Ephemeral(ECDiffieHellman key) => new(
        EphemeralKeyAlgorithm.EcdhP256,
        Base64Url(key.ExportSubjectPublicKeyInfo()));

    private static DeviceKeyProof Proof(HandshakeChallenge challenge)
    {
        var authenticatorData = new byte[ProtocolBounds.MinAuthenticatorDataBytes];
        authenticatorData[32] = 0x05;
        var clientData = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["type"] = "webauthn.get",
            ["challenge"] = challenge.TranscriptHashBase64Url,
            ["crossOrigin"] = false,
        });
        return new DeviceKeyProof(
            challenge.ChallengeId,
            new WebAuthnAssertion(
                challenge.CredentialIdBase64Url,
                Base64Url(authenticatorData),
                Base64Url(clientData),
                Base64Url(new byte[] { 1 })));
    }

    private sealed class AcceptingProofVerifier : IDeviceKeyProofVerifier
    {
        public static AcceptingProofVerifier Instance { get; } = new();

        public ValueTask<bool> VerifyAsync(
            DevicePublicKey deviceKey,
            HandshakeChallenge challenge,
            DeviceKeyProof proof,
            CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class TestSigner : IDesktopIdentitySigner, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public TestSigner()
        {
            var spki = _key.ExportSubjectPublicKeyInfo();
            PublicKey = new DesktopIdentityKey(
                new DeviceKeyId(Base64Url(SHA256.HashData(spki))),
                DesktopIdentityKeyAlgorithm.EcdsaP256Sha256,
                Base64Url(spki));
        }

        public DesktopIdentityKey PublicKey { get; }

        public byte[] Sign(ReadOnlySpan<byte> signatureInput) => _key.SignData(
            signatureInput,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        public void Dispose() => _key.Dispose();
    }
}
