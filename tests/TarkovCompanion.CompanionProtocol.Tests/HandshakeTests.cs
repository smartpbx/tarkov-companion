using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TarkovCompanion.Core.Abstractions.V2;
using static TarkovCompanion.CompanionProtocol.Tests.ProtocolTestData;

namespace TarkovCompanion.CompanionProtocol.Tests;

public sealed class HandshakeTests
{
    private static readonly DateTimeOffset Offered = new(2026, 9, 14, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Bound = Offered.AddSeconds(10);
    private static readonly DateTimeOffset Issued = Offered.AddSeconds(30);
    private static readonly DateTimeOffset ChallengeExpires = Offered.AddSeconds(150);
    private static readonly DateTimeOffset Established = Offered.AddSeconds(40);
    private static readonly DateTimeOffset ResumeIssued = Offered.AddHours(1);
    private static readonly DateTimeOffset ResumeExpires = ResumeIssued.AddMinutes(2);
    private static readonly DateTimeOffset ResumeEstablished = ResumeIssued.AddSeconds(10);

    [Fact]
    public async Task PairingCompletesOnlyAfterApprovalOfTheVerificationCodeAndAValidDeviceKeyProof()
    {
        var offer = GoldenRoot<PairingOffer>("handshake/pairing-offer.json");
        var request = GoldenRoot<PairingRequest>("handshake/pairing-request.json");
        var goldenChallenge = GoldenRoot<HandshakeChallenge>("handshake/pairing-challenge.json");
        var attempt = PairingStateMachine.Offer(
            offer.AttemptId,
            CryptoVectors.DesktopIdentity(),
            CryptoVectors.Ephemeral("desktopEphemeralPairing"),
            offer.DesktopNonceBase64Url,
            Offered);

        AssertJsonEqual(Golden("handshake/pairing-offer.json"), CompanionProtocolJson.Serialize(attempt.Offer));

        var bound = PairingStateMachine.BindResolvedCode(attempt, request, Bound);
        Assert.Throws<InvalidOperationException>(() => PairingStateMachine.BindResolvedCode(bound, request, Bound.AddSeconds(1)));
        Assert.Equal(CryptoVectors.Text("pairing", "verificationCode"), PairingStateMachine.VerificationCode(bound));

        using (var desktopEphemeral = CryptoVectors.AgreementKey("desktopEphemeralPairing"))
        {
            var secret = PairingCryptography.DeriveP256SharedSecret(desktopEphemeral, request.EphemeralKey);
            Assert.Equal(CryptoVectors.Text("pairing", "deviceName"), PairingCryptography.OpenDeviceName(secret, offer, request));
        }

        using var signer = TestDesktopSigner.FromVectors();
        var created = PairingStateMachine.CreateChallenge(bound, goldenChallenge.ChallengeId, goldenChallenge.Assignment, signer, Issued, ChallengeExpires);
        Assert.Equal(goldenChallenge.TranscriptHashBase64Url, created.TranscriptHashBase64Url);
        Assert.True(HandshakeTranscript.FromChallenge(created, offer, request).Matches(goldenChallenge));

        var approved = PairingStateMachine.Approve(bound, goldenChallenge, Issued);
        var proof = GoldenRoot<DeviceKeyProof>("handshake/pairing-proof.json");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await PairingStateMachine.CompleteAsync(approved, proof, new FixedVerifier(false), Established));

        var verifier = new ReferenceWebAuthnVerifier(TestAuthenticator.RpId);
        var completed = await PairingStateMachine.CompleteAsync(approved, proof, verifier, Established);

        Assert.Equal(PairingAttemptStage.Completed, completed.Stage);
        Assert.Equal(1, verifier.Calls);
        AssertJsonEqual(Golden("handshake/pairing-established.json"), CompanionProtocolJson.Serialize(completed.Establishment!));
    }

    [Fact]
    public void ATabletRejectsARelaySubstitutedDesktopIdentityOrTranscript()
    {
        var offer = GoldenRoot<PairingOffer>("handshake/pairing-offer.json");
        var request = GoldenRoot<PairingRequest>("handshake/pairing-request.json");
        var challenge = GoldenRoot<HandshakeChallenge>("handshake/pairing-challenge.json");
        using var impostor = TestDesktopSigner.Random();
        var impostorOffer = new PairingOffer(offer.AttemptId, impostor.PublicKey, offer.DesktopEphemeralKey, offer.DesktopNonceBase64Url, offer.OfferedUtc, offer.ExpiresUtc);
        var forged = HandshakeTranscript.ForPairing(impostorOffer, request, challenge.ChallengeId, challenge.Assignment, Issued, ChallengeExpires).Sign(impostor);

        Assert.Throws<ArgumentException>(() => HandshakeTranscript.FromChallenge(forged, offer, request));

        var rerouted = GoldenNode("handshake/pairing-challenge.json");
        rerouted["assignment"]!["relayChannelId"]!["value"] = "90000000-0000-4000-8000-0000000000ff";
        var reroutedChallenge = CompanionProtocolJson.Deserialize<HandshakeChallenge>(Encoding.UTF8.GetBytes(rerouted.ToJsonString()));
        Assert.False(HandshakeTranscript.FromChallenge(reroutedChallenge, offer, request).Matches(reroutedChallenge));

        var bound = PairingStateMachine.BindResolvedCode(PairingStateMachine.Offer(offer.AttemptId, offer.DesktopIdentityKey, offer.DesktopEphemeralKey, offer.DesktopNonceBase64Url, Offered), request, Bound);
        Assert.Throws<ArgumentException>(() => PairingStateMachine.Approve(bound, reroutedChallenge, Issued));

        var unsigned = GoldenNode("handshake/pairing-challenge.json");
        unsigned["transcriptHashBase64Url"] = Base64UrlOf(SHA256.HashData("another transcript"u8));
        Assert.Throws<System.Text.Json.JsonException>(() =>
            CompanionProtocolJson.Deserialize<HandshakeChallenge>(Encoding.UTF8.GetBytes(unsigned.ToJsonString())));
    }

    [Fact]
    public void ARelaySubstitutedTabletKeyOrNameChangesTheCodeOrFailsToOpen()
    {
        var offer = GoldenRoot<PairingOffer>("handshake/pairing-offer.json");
        var request = GoldenRoot<PairingRequest>("handshake/pairing-request.json");
        using var relayEphemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var relayKey = new EphemeralPublicKey(EphemeralKeyAlgorithm.EcdhP256, Base64UrlOf(relayEphemeral.ExportSubjectPublicKeyInfo()));
        var relaySecret = PairingCryptography.DeriveP256SharedSecret(relayEphemeral, offer.DesktopEphemeralKey);
        var relayContext = PairingCryptography.ComputePairingRequestContextHash(offer, request.NegotiatedVersion, request.DeviceKey, relayKey, request.ClientNonceBase64Url);
        var substituted = new PairingRequest(
            request.AttemptId,
            request.NegotiatedVersion,
            request.DeviceKey,
            relayKey,
            request.ClientNonceBase64Url,
            PairingCryptography.SealDeviceName(relaySecret, relayContext, "Living-room tablet"));
        var renamed = new PairingRequest(
            request.AttemptId,
            request.NegotiatedVersion,
            request.DeviceKey,
            request.EphemeralKey,
            request.ClientNonceBase64Url,
            substituted.RequestedDeviceName);

        Assert.NotEqual(
            PairingCryptography.ComputeVerificationCode(offer, request),
            PairingCryptography.ComputeVerificationCode(offer, substituted));
        using var desktopEphemeral = CryptoVectors.AgreementKey("desktopEphemeralPairing");
        var desktopSecret = PairingCryptography.DeriveP256SharedSecret(desktopEphemeral, request.EphemeralKey);
        Assert.ThrowsAny<CryptographicException>(() => PairingCryptography.OpenDeviceName(desktopSecret, offer, renamed));
    }

    [Fact]
    public void APairingRequestCannotReflectTheDesktopNonceOrEphemeralKey()
    {
        var offer = GoldenRoot<PairingOffer>("handshake/pairing-offer.json");
        var attempt = PairingStateMachine.Offer(offer.AttemptId, offer.DesktopIdentityKey, offer.DesktopEphemeralKey, offer.DesktopNonceBase64Url, Offered);
        var reflectedNonce = GoldenNode("handshake/pairing-request.json");
        reflectedNonce["clientNonceBase64Url"] = offer.DesktopNonceBase64Url;
        var reflectedKey = GoldenNode("handshake/pairing-request.json");
        reflectedKey["ephemeralKey"]!["subjectPublicKeyInfoBase64Url"] = offer.DesktopEphemeralKey.SubjectPublicKeyInfoBase64Url;

        foreach (var node in new[] { reflectedNonce, reflectedKey })
        {
            var request = CompanionProtocolJson.Deserialize<PairingRequest>(Encoding.UTF8.GetBytes(node.ToJsonString()));
            Assert.Throws<ArgumentException>(() => PairingStateMachine.BindResolvedCode(attempt, request, Bound));
        }
    }

    [Fact]
    public async Task ADeviceKeyProofMustAnswerThisChallengeWithUserVerificationAndTheBoundCredential()
    {
        var approved = ApprovedAttempt();
        var challenge = approved.Challenge!;
        var verifier = new ReferenceWebAuthnVerifier(TestAuthenticator.RpId);

        Assert.Throws<ArgumentException>(() => TestAuthenticator.Prove(challenge, flags: 0x01));
        foreach (var proof in new[]
                 {
                     TestAuthenticator.Prove(challenge, type: "webauthn.create"),
                     TestAuthenticator.Prove(challenge, challengeOverride: Base64UrlOf(SHA256.HashData("other"u8))),
                     TestAuthenticator.Prove(challenge, credentialOverride: Base64UrlOf("other-credential"u8)),
                 })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
                await PairingStateMachine.CompleteAsync(approved, proof, verifier, Established));
        }

        var valid = TestAuthenticator.Prove(challenge);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await PairingStateMachine.CompleteAsync(
            approved,
            new DeviceKeyProof(new HandshakeChallengeId(Guid.Parse("5a1d3c2e-7b10-4c00-8a00-0000000000ee")), valid.Assertion),
            verifier,
            Established));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PairingStateMachine.CompleteAsync(approved, valid, verifier, ChallengeExpires));

        var completed = await PairingStateMachine.CompleteAsync(approved, valid, verifier, Established);
        Assert.Equal(PairingAttemptStage.Completed, completed.Stage);
        Assert.Equal(1, verifier.Calls);
    }

    [Fact]
    public async Task SessionResumeReprovesTheDeviceKeyAndDerivesNewTrafficKeys()
    {
        var pairingRequest = GoldenRoot<PairingRequest>("handshake/pairing-request.json");
        var request = GoldenRoot<SessionResumeRequest>("handshake/session-resume-request.json");
        var goldenChallenge = GoldenRoot<HandshakeChallenge>("handshake/session-resume-challenge.json");
        var device = new PairedDevice(
            request.DeviceId,
            "Living-room tablet",
            pairingRequest.DeviceKey,
            DeviceAuthorizationRole.Member,
            TabletCapabilities,
            DeviceLifecycleStatus.Active,
            Established,
            Established,
            Established.AddDays(30),
            Established);
        using var signer = TestDesktopSigner.FromVectors();

        var created = SessionResumption.CreateChallenge(
            device,
            request,
            goldenChallenge.ChallengeId,
            goldenChallenge.Assignment,
            CryptoVectors.Ephemeral("desktopEphemeralResume"),
            goldenChallenge.DesktopNonceBase64Url,
            signer,
            ResumeIssued,
            ResumeExpires);
        var established = await SessionResumption.CompleteAsync(
            device,
            request,
            goldenChallenge,
            GoldenRoot<DeviceKeyProof>("handshake/session-resume-proof.json"),
            new ReferenceWebAuthnVerifier(TestAuthenticator.RpId),
            ResumeEstablished);

        Assert.Equal(goldenChallenge.TranscriptHashBase64Url, created.TranscriptHashBase64Url);
        AssertJsonEqual(Golden("handshake/session-resume-established.json"), CompanionProtocolJson.Serialize(established));
        Assert.NotEqual(
            CryptoVectors.Text("pairing", "tabletToDesktopKeyHex"),
            Convert.ToHexStringLower(PairingCryptography.DeriveTrafficKey(
                CryptoVectors.Hex("sessionResume", "sharedSecretHex"),
                established.TranscriptHashBase64Url,
                PairingTrafficDirection.TabletToDesktop)));

        var revoked = DeviceLifecycle.Revoke(device, ResumeIssued, "user-revoked");
        Assert.Throws<UnauthorizedAccessException>(() => SessionResumption.CreateChallenge(
            revoked,
            request,
            goldenChallenge.ChallengeId,
            goldenChallenge.Assignment,
            CryptoVectors.Ephemeral("desktopEphemeralResume"),
            goldenChallenge.DesktopNonceBase64Url,
            signer,
            ResumeIssued,
            ResumeExpires));
        var otherCredential = new SessionResumeRequest(
            request.NegotiatedVersion,
            request.DeviceId,
            request.DeviceKeyId,
            Base64UrlOf("another-credential"u8),
            request.EphemeralKey,
            request.ClientNonceBase64Url);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await SessionResumption.CompleteAsync(
            device,
            otherCredential,
            goldenChallenge,
            GoldenRoot<DeviceKeyProof>("handshake/session-resume-proof.json"),
            new ReferenceWebAuthnVerifier(TestAuthenticator.RpId),
            ResumeEstablished));
    }

    [Fact]
    public void HandshakeLifetimesAndKeyIdentitiesAreBounded()
    {
        var offer = GoldenRoot<PairingOffer>("handshake/pairing-offer.json");
        var resume = GoldenRoot<SessionResumeRequest>("handshake/session-resume-request.json");
        var resumeChallenge = GoldenRoot<HandshakeChallenge>("handshake/session-resume-challenge.json");
        using var signer = TestDesktopSigner.FromVectors();

        Assert.Throws<ArgumentOutOfRangeException>(() => new PairingOffer(
            offer.AttemptId,
            offer.DesktopIdentityKey,
            offer.DesktopEphemeralKey,
            offer.DesktopNonceBase64Url,
            Offered,
            Offered.AddMinutes(11)));
        Assert.Throws<ArgumentOutOfRangeException>(() => HandshakeTranscript
            .ForSessionResume(resume, resumeChallenge.ChallengeId, resumeChallenge.Assignment, signer.PublicKey, resumeChallenge.DesktopEphemeralKey, resumeChallenge.DesktopNonceBase64Url, ResumeIssued, ResumeIssued.AddMinutes(3))
            .Sign(signer));
        Assert.Throws<ArgumentException>(() => new DevicePublicKey(
            new DeviceKeyId(Thumbprint("not-the-cose-key")),
            DeviceKeyAlgorithm.WebAuthnEs256,
            Base64UrlOf("credential"u8),
            GoldenRoot<PairingRequest>("handshake/pairing-request.json").DeviceKey.CosePublicKeyBase64Url));
        Assert.Throws<ArgumentException>(() => new EphemeralPublicKey(EphemeralKeyAlgorithm.EcdhP256, Base64UrlOf(new byte[91])));
        Assert.Throws<ArgumentException>(() => new DesktopIdentityKey(
            new DeviceKeyId(Thumbprint("identity")),
            DesktopIdentityKeyAlgorithm.EcdsaP256Sha256,
            offer.DesktopIdentityKey.SubjectPublicKeyInfoBase64Url));

        // "AB" and "AA" decode to the same byte when trailing bits are ignored; only the canonical spelling is accepted.
        Assert.Throws<ArgumentException>(() => new SealedDeviceName("AB", Base64UrlOf(new byte[16])));
        Assert.Equal("AA", new SealedDeviceName("AA", Base64UrlOf(new byte[16])).CiphertextBase64Url);
    }

    [Fact]
    public void PairingRateLimiterIsPerSourceAndBounded()
    {
        var state = PairingRateState.Empty;
        for (var index = 0; index < ProtocolBounds.MaxPairingAttemptsPerWindow; index++)
        {
            var accepted = PairingRateLimiter.TryConsume(state, "c291cmNlLWE", Now.AddSeconds(index));
            Assert.True(accepted.Accepted);
            state = accepted.State;
        }

        var limited = PairingRateLimiter.TryConsume(state, "c291cmNlLWE", Now.AddSeconds(10));
        var unrelated = PairingRateLimiter.TryConsume(state, "c291cmNlLWI", Now.AddSeconds(10));
        var flood = state;
        for (var index = 0; index < 400; index++)
        {
            flood = PairingRateLimiter.TryConsume(flood, Thumbprint($"source-{index}"), Now.AddSeconds(20)).State;
        }

        Assert.False(limited.Accepted);
        Assert.Equal(Now.Add(ProtocolBounds.PairingRateWindow), limited.RetryAfterUtc);
        Assert.True(unrelated.Accepted);
        Assert.Equal(ProtocolBounds.MaxDevices * ProtocolBounds.MaxPairingAttemptsPerWindow, flood.Observations.Count);
    }

    private static PairingAttempt ApprovedAttempt()
    {
        var offer = GoldenRoot<PairingOffer>("handshake/pairing-offer.json");
        var request = GoldenRoot<PairingRequest>("handshake/pairing-request.json");
        var attempt = PairingStateMachine.Offer(offer.AttemptId, offer.DesktopIdentityKey, offer.DesktopEphemeralKey, offer.DesktopNonceBase64Url, Offered);
        var bound = PairingStateMachine.BindResolvedCode(attempt, request, Bound);
        return PairingStateMachine.Approve(bound, GoldenRoot<HandshakeChallenge>("handshake/pairing-challenge.json"), Issued);
    }

    private sealed class FixedVerifier(bool result) : IDeviceKeyProofVerifier
    {
        public ValueTask<bool> VerifyAsync(
            DevicePublicKey deviceKey,
            HandshakeChallenge challenge,
            DeviceKeyProof proof,
            CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }
}
