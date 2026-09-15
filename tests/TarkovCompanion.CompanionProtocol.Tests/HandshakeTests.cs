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
        var reveal = GoldenRoot<PairingNonceReveal>("handshake/pairing-nonce-reveal.json");
        var goldenChallenge = GoldenRoot<HandshakeChallenge>("handshake/pairing-challenge.json");
        var attempt = PairingStateMachine.Offer(
            offer.AttemptId,
            CryptoVectors.DesktopIdentity(),
            CryptoVectors.Ephemeral("desktopEphemeralPairing"),
            reveal.DesktopNonceBase64Url,
            Offered);

        AssertJsonEqual(Golden("handshake/pairing-offer.json"), CompanionProtocolJson.Serialize(attempt.Offer));
        Assert.Throws<InvalidOperationException>(() => PairingStateMachine.RevealNonce(attempt, Bound));

        var bound = PairingStateMachine.BindResolvedCode(attempt, request, CompanionProtocolVersion.Current, Bound);
        Assert.Throws<InvalidOperationException>(() => PairingStateMachine.BindResolvedCode(bound, request, CompanionProtocolVersion.Current, Bound.AddSeconds(1)));
        AssertJsonEqual(Golden("handshake/pairing-nonce-reveal.json"), CompanionProtocolJson.Serialize(PairingStateMachine.RevealNonce(bound, Bound)));
        Assert.Equal(CryptoVectors.Text("pairing", "verificationCode"), PairingStateMachine.VerificationCode(bound));
        Assert.Equal(CryptoVectors.Text("pairing", "verificationCode"), PairingCryptography.ComputeVerificationCode(offer, request, reveal));

        using (var desktopEphemeral = CryptoVectors.AgreementKey("desktopEphemeralPairing"))
        {
            var secret = PairingCryptography.DeriveP256SharedSecret(desktopEphemeral, request.EphemeralKey);
            Assert.Equal(CryptoVectors.Text("pairing", "deviceName"), PairingCryptography.OpenDeviceName(secret, offer, request));
        }

        using var signer = TestDesktopSigner.FromVectors();
        var created = PairingStateMachine.CreateChallenge(bound, goldenChallenge.ChallengeId, goldenChallenge.Assignment, signer, Issued, ChallengeExpires);
        Assert.Equal(goldenChallenge.TranscriptHashBase64Url, created.TranscriptHashBase64Url);
        Assert.True(HandshakeTranscript.FromChallenge(created, offer, request, reveal).Matches(goldenChallenge));

        var approved = PairingStateMachine.Approve(bound, goldenChallenge, Issued);
        var proof = GoldenRoot<DeviceKeyProof>("handshake/pairing-proof.json");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await PairingStateMachine.CompleteAsync(approved, proof, new FixedVerifier(false), Established));

        var verifier = new ReferenceWebAuthnVerifier(TestAuthenticator.RpId);
        var completed = await PairingStateMachine.CompleteAsync(approved, proof, verifier, Established);
        var device = DeviceLifecycle.Pair(completed, "Living-room tablet", DeviceAuthorizationRole.Member, TabletCapabilities, Established.AddDays(30));

        Assert.Equal(PairingAttemptStage.Completed, completed.Stage);
        Assert.Equal(1, verifier.Calls);
        AssertJsonEqual(Golden("handshake/pairing-established.json"), CompanionProtocolJson.Serialize(completed.Establishment!));
        Assert.True(completed.Establishment!.Answers(goldenChallenge));
        Assert.Equal(request.DeviceKey, device.DeviceKey);
        Assert.Equal(goldenChallenge.Assignment.DeviceId, device.DeviceId);
        Assert.Equal(goldenChallenge.Assignment.KeyEpoch, device.LastKeyEpoch);
        Assert.Throws<InvalidOperationException>(() => DeviceLifecycle.Pair(approved, "Tablet", DeviceAuthorizationRole.Member, TabletCapabilities, Established.AddDays(30)));
    }

    [Fact]
    public void TheVerificationCodeIsCommitThenRevealSoARequestCannotBeChosenAfterTheNonceIsKnown()
    {
        var offer = GoldenRoot<PairingOffer>("handshake/pairing-offer.json");
        var request = GoldenRoot<PairingRequest>("handshake/pairing-request.json");
        var reveal = GoldenRoot<PairingNonceReveal>("handshake/pairing-nonce-reveal.json");
        var otherNonce = new PairingNonceReveal(offer.AttemptId, Base64UrlOf(SHA256.HashData("relay-chosen-nonce"u8)));
        var attempt = PairingStateMachine.Offer(offer.AttemptId, offer.DesktopIdentityKey, offer.DesktopEphemeralKey, reveal.DesktopNonceBase64Url, Offered);

        // The offer a relay can observe holds only the commitment; no code is computable from it.
        Assert.DoesNotContain(reveal.DesktopNonceBase64Url, Encoding.UTF8.GetString(Golden("handshake/pairing-offer.json")), StringComparison.Ordinal);
        Assert.Equal(offer.DesktopNonceCommitmentBase64Url, PairingCryptography.ComputeDesktopNonceCommitment(reveal.DesktopNonceBase64Url));
        Assert.True(PairingCryptography.IsRevealOf(offer, reveal));
        Assert.False(PairingCryptography.IsRevealOf(offer, otherNonce));
        Assert.Throws<ArgumentException>(() => PairingCryptography.ComputeVerificationCode(offer, request, otherNonce));
        Assert.Throws<ArgumentException>(() => new PairingAttempt(offer, otherNonce.DesktopNonceBase64Url, PairingAttemptStage.Offered));

        // The nonce is released only for the one bound request, and a bound attempt accepts no other request.
        Assert.Throws<InvalidOperationException>(() => PairingStateMachine.RevealNonce(attempt, Bound));
        var bound = PairingStateMachine.BindResolvedCode(attempt, request, CompanionProtocolVersion.Current, Bound);
        var relayRequest = new PairingRequest(
            request.AttemptId,
            request.NegotiatedVersion,
            request.DeviceKey,
            request.EphemeralKey,
            Base64UrlOf(SHA256.HashData("relay-ground-client-nonce"u8)),
            request.RequestedDeviceName);
        Assert.Throws<InvalidOperationException>(() => PairingStateMachine.BindResolvedCode(bound, relayRequest, CompanionProtocolVersion.Current, Bound));
        Assert.Equal(reveal, PairingStateMachine.RevealNonce(bound, Bound));
        Assert.Throws<InvalidOperationException>(() => PairingStateMachine.RevealNonce(bound, offer.ExpiresUtc));
    }

    [Fact]
    public void ATabletRejectsARelaySubstitutedDesktopIdentityOrTranscript()
    {
        var offer = GoldenRoot<PairingOffer>("handshake/pairing-offer.json");
        var request = GoldenRoot<PairingRequest>("handshake/pairing-request.json");
        var reveal = GoldenRoot<PairingNonceReveal>("handshake/pairing-nonce-reveal.json");
        var challenge = GoldenRoot<HandshakeChallenge>("handshake/pairing-challenge.json");
        using var impostor = TestDesktopSigner.Random();
        var impostorOffer = new PairingOffer(offer.AttemptId, impostor.PublicKey, offer.DesktopEphemeralKey, offer.DesktopNonceCommitmentBase64Url, offer.OfferedUtc, offer.ExpiresUtc);
        var forged = HandshakeTranscript.ForPairing(impostorOffer, request, reveal, challenge.ChallengeId, challenge.Assignment, Issued, ChallengeExpires).Sign(impostor);

        Assert.Throws<ArgumentException>(() => HandshakeTranscript.FromChallenge(forged, offer, request, reveal));

        var rerouted = GoldenNode("handshake/pairing-challenge.json");
        rerouted["assignment"]!["relayChannelId"]!["value"] = "90000000-0000-4000-8000-0000000000ff";
        var reroutedChallenge = CompanionProtocolJson.Deserialize<HandshakeChallenge>(Encoding.UTF8.GetBytes(rerouted.ToJsonString()));
        Assert.False(HandshakeTranscript.FromChallenge(reroutedChallenge, offer, request, reveal).Matches(reroutedChallenge));

        var attempt = PairingStateMachine.Offer(offer.AttemptId, offer.DesktopIdentityKey, offer.DesktopEphemeralKey, reveal.DesktopNonceBase64Url, Offered);
        var bound = PairingStateMachine.BindResolvedCode(attempt, request, CompanionProtocolVersion.Current, Bound);
        Assert.Throws<ArgumentException>(() => PairingStateMachine.Approve(bound, reroutedChallenge, Issued));

        var unsigned = GoldenNode("handshake/pairing-challenge.json");
        unsigned["transcriptHashBase64Url"] = Base64UrlOf(SHA256.HashData("another transcript"u8));
        Assert.Throws<System.Text.Json.JsonException>(() =>
            CompanionProtocolJson.Deserialize<HandshakeChallenge>(Encoding.UTF8.GetBytes(unsigned.ToJsonString())));

        var established = GoldenRoot<SessionEstablished>("handshake/pairing-established.json");
        var redirected = new SessionEstablished(
            established.ChallengeId,
            established.Purpose,
            established.DeviceKeyId,
            reroutedChallenge.Assignment,
            established.TranscriptHashBase64Url,
            established.EstablishedUtc);
        Assert.True(established.Answers(challenge));
        Assert.False(redirected.Answers(challenge));
    }

    [Fact]
    public void ARelaySubstitutedTabletKeyOrNameChangesTheCodeOrFailsToOpen()
    {
        var offer = GoldenRoot<PairingOffer>("handshake/pairing-offer.json");
        var request = GoldenRoot<PairingRequest>("handshake/pairing-request.json");
        var reveal = GoldenRoot<PairingNonceReveal>("handshake/pairing-nonce-reveal.json");
        using var relayEphemeral = CryptoVectors.AgreementKey("tabletEphemeralResume");
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
            PairingCryptography.ComputePairingCommitment(offer, request, reveal),
            PairingCryptography.ComputePairingCommitment(offer, substituted, reveal));
        using var desktopEphemeral = CryptoVectors.AgreementKey("desktopEphemeralPairing");
        var desktopSecret = PairingCryptography.DeriveP256SharedSecret(desktopEphemeral, request.EphemeralKey);
        Assert.ThrowsAny<CryptographicException>(() => PairingCryptography.OpenDeviceName(desktopSecret, offer, renamed));
    }

    [Theory]
    [InlineData("Tab\u200Blet")]
    [InlineData("Tab\u2060let")]
    [InlineData("Tab\uFEFFlet")]
    [InlineData("Tab\u061Clet")]
    [InlineData("Tab\u202Elet")]
    [InlineData("Tab\u2028let")]
    [InlineData("Tab\uE000let")]
    [InlineData("Tab\u0007let")]
    [InlineData("Tab\U000E0001let")]
    [InlineData("Tab\U000E0041let")]
    [InlineData("Tab\U0001BCA0let")]
    [InlineData("Tab\U000F0000let")]
    public void DeviceNamesCannotHideCharactersFromTheApprovalPrompt(string name)
    {
        var secret = SHA256.HashData("name-secret"u8);
        var context = SHA256.HashData("name-context"u8);

        Assert.Throws<ArgumentException>(() => PairingCryptography.SealDeviceName(secret, context, name));
        Assert.NotNull(PairingCryptography.SealDeviceName(secret, context, "Living-room tablet \u00E9\U0001F4F1"));
    }

    [Fact]
    public void APairingRequestCannotReflectTheDesktopNonceOrEphemeralKeyOrUseAnotherVersion()
    {
        var offer = GoldenRoot<PairingOffer>("handshake/pairing-offer.json");
        var reveal = GoldenRoot<PairingNonceReveal>("handshake/pairing-nonce-reveal.json");
        var attempt = PairingStateMachine.Offer(offer.AttemptId, offer.DesktopIdentityKey, offer.DesktopEphemeralKey, reveal.DesktopNonceBase64Url, Offered);
        var reflectedNonce = GoldenNode("handshake/pairing-request.json");
        reflectedNonce["clientNonceBase64Url"] = reveal.DesktopNonceBase64Url;
        var reflectedKey = GoldenNode("handshake/pairing-request.json");
        reflectedKey["ephemeralKey"]!["subjectPublicKeyInfoBase64Url"] = offer.DesktopEphemeralKey.SubjectPublicKeyInfoBase64Url;

        foreach (var node in new[] { reflectedNonce, reflectedKey })
        {
            var request = CompanionProtocolJson.Deserialize<PairingRequest>(Encoding.UTF8.GetBytes(node.ToJsonString()));
            Assert.Throws<ArgumentException>(() => PairingStateMachine.BindResolvedCode(attempt, request, CompanionProtocolVersion.Current, Bound));
        }

        Assert.Throws<ArgumentException>(() => PairingStateMachine.BindResolvedCode(
            attempt,
            GoldenRoot<PairingRequest>("handshake/pairing-request.json"),
            new CompanionProtocolVersion(2, 1),
            Bound));
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
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PairingStateMachine.CompleteAsync(completed, valid, verifier, Established));
    }

    [Fact]
    public async Task SessionResumeIsSingleUseReprovesTheDeviceKeyAndDerivesNewTrafficKeys()
    {
        var request = GoldenRoot<SessionResumeRequest>("handshake/session-resume-request.json");
        var goldenChallenge = GoldenRoot<HandshakeChallenge>("handshake/session-resume-challenge.json");
        var proof = GoldenRoot<DeviceKeyProof>("handshake/session-resume-proof.json");
        var device = PairedDeviceFromPairing();
        using var signer = TestDesktopSigner.FromVectors();
        var verifier = new ReferenceWebAuthnVerifier(TestAuthenticator.RpId);

        var attempt = Begin(device, request, goldenChallenge, signer);
        var completed = await SessionResumption.CompleteAsync(attempt, device, proof, verifier, ResumeEstablished);
        var established = completed.Establishment!;

        Assert.Equal(goldenChallenge.TranscriptHashBase64Url, attempt.Challenge.TranscriptHashBase64Url);
        Assert.Equal(SessionResumeStage.Completed, completed.Stage);
        AssertJsonEqual(Golden("handshake/session-resume-established.json"), CompanionProtocolJson.Serialize(established));
        Assert.True(established.Answers(goldenChallenge));
        Assert.True(HandshakeTranscript.FromChallenge(goldenChallenge, request, CryptoVectors.DesktopIdentity()).Matches(goldenChallenge));
        using (var impostor = TestDesktopSigner.Random())
        {
            Assert.Throws<ArgumentException>(() => HandshakeTranscript.FromChallenge(goldenChallenge, request, impostor.PublicKey));
        }

        Assert.NotEqual(
            CryptoVectors.Text("pairing", "tabletToDesktopKeyHex"),
            Convert.ToHexStringLower(PairingCryptography.DeriveTrafficKey(
                CryptoVectors.Hex("sessionResume", "sharedSecretHex"),
                established.TranscriptHashBase64Url,
                PairingTrafficDirection.TabletToDesktop)));

        // A completed challenge never establishes again, and once the session is recorded no captured
        // proof for that or any earlier key epoch can establish a session either.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await SessionResumption.CompleteAsync(completed, device, proof, verifier, ResumeEstablished));
        var recorded = DeviceLifecycle.RecordSession(device, established);
        Assert.Equal(2, recorded.LastKeyEpoch);
        Assert.Equal(ResumeEstablished, recorded.LastUsedUtc);
        Assert.Throws<InvalidOperationException>(() => DeviceLifecycle.RecordSession(recorded, established));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await SessionResumption.CompleteAsync(attempt, recorded, proof, verifier, ResumeEstablished));
        Assert.Throws<ArgumentException>(() => Begin(recorded, request, goldenChallenge, signer));

        var expired = SessionResumption.Expire(Begin(device, request, goldenChallenge, signer), ResumeExpires);
        Assert.Equal(SessionResumeStage.Expired, expired.Stage);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await SessionResumption.CompleteAsync(expired, device, proof, verifier, ResumeEstablished));

        var revoked = DeviceLifecycle.Revoke(device, ResumeIssued, "user-revoked");
        Assert.Throws<UnauthorizedAccessException>(() => Begin(revoked, request, goldenChallenge, signer));
        var otherCredential = new SessionResumeRequest(
            request.NegotiatedVersion,
            request.DeviceId,
            request.DeviceKeyId,
            Base64UrlOf("another-credential"u8),
            request.EphemeralKey,
            request.ClientNonceBase64Url);
        Assert.Throws<UnauthorizedAccessException>(() => Begin(device, otherCredential, goldenChallenge, signer));
        Assert.Throws<ArgumentException>(() => new SessionResumeAttempt(otherCredential, goldenChallenge, SessionResumeStage.AwaitingDeviceProof));
        Assert.Throws<ArgumentException>(() => SessionResumption.Begin(
            device,
            request,
            new CompanionProtocolVersion(2, 1),
            goldenChallenge.ChallengeId,
            goldenChallenge.Assignment,
            CryptoVectors.Ephemeral("desktopEphemeralResume"),
            goldenChallenge.DesktopNonceBase64Url,
            signer,
            ResumeIssued,
            ResumeExpires));
    }

    [Fact]
    public void RecordedUseKeepsAConnectedDeviceLiveButOnlyForALiveSession()
    {
        var device = PairedTablet();
        var session = ActiveSession(device, TabletSession);
        var later = Now.AddMinutes(90);

        var (used, usedSession) = DeviceLifecycle.RecordUse(device, session, later);

        Assert.Equal(later, used.LastUsedUtc);
        Assert.Equal(later, usedSession.LastUsedUtc);
        Assert.True(DeviceLifecycle.IsLive(used, Now.AddMinutes(200)));
        Assert.False(DeviceLifecycle.IsLive(device, Now.AddMinutes(200)));
        var ended = DeviceLifecycle.EndSession(session, DeviceSessionStatus.Closed, later, "closed");
        Assert.Throws<UnauthorizedAccessException>(() => DeviceLifecycle.RecordUse(device, ended, later));
    }

    [Fact]
    public void HandshakeLifetimesKeyIdentitiesAndCurvePointsAreBounded()
    {
        var offer = GoldenRoot<PairingOffer>("handshake/pairing-offer.json");
        var resume = GoldenRoot<SessionResumeRequest>("handshake/session-resume-request.json");
        var resumeChallenge = GoldenRoot<HandshakeChallenge>("handshake/session-resume-challenge.json");
        var deviceKey = GoldenRoot<PairingRequest>("handshake/pairing-request.json").DeviceKey;
        using var signer = TestDesktopSigner.FromVectors();

        Assert.Throws<ArgumentOutOfRangeException>(() => new PairingOffer(
            offer.AttemptId,
            offer.DesktopIdentityKey,
            offer.DesktopEphemeralKey,
            offer.DesktopNonceCommitmentBase64Url,
            Offered,
            Offered.AddMinutes(11)));
        Assert.Throws<ArgumentOutOfRangeException>(() => HandshakeTranscript
            .ForSessionResume(resume, resumeChallenge.ChallengeId, resumeChallenge.Assignment, signer.PublicKey, resumeChallenge.DesktopEphemeralKey, resumeChallenge.DesktopNonceBase64Url, ResumeIssued, ResumeIssued.AddMinutes(3))
            .Sign(signer));
        Assert.Throws<ArgumentException>(() => new DevicePublicKey(
            new DeviceKeyId(Thumbprint("not-the-cose-key")),
            DeviceKeyAlgorithm.WebAuthnEs256,
            Base64UrlOf("credential"u8),
            deviceKey.CosePublicKeyBase64Url));
        Assert.Throws<ArgumentException>(() => new EphemeralPublicKey(EphemeralKeyAlgorithm.EcdhP256, Base64UrlOf(new byte[91])));
        Assert.Throws<ArgumentException>(() => new DesktopIdentityKey(
            new DeviceKeyId(Thumbprint("identity")),
            DesktopIdentityKeyAlgorithm.EcdsaP256Sha256,
            offer.DesktopIdentityKey.SubjectPublicKeyInfoBase64Url));

        // A well-formed SPKI or COSE key whose point is off the curve, or a COSE key for another
        // algorithm or curve, is refused even when its thumbprint matches.
        var offCurveSpki = FromBase64Url(offer.DesktopEphemeralKey.SubjectPublicKeyInfoBase64Url);
        offCurveSpki[^1] ^= 0x01;
        Assert.Throws<ArgumentException>(() => new EphemeralPublicKey(EphemeralKeyAlgorithm.EcdhP256, Base64UrlOf(offCurveSpki)));
        var cose = FromBase64Url(deviceKey.CosePublicKeyBase64Url);
        foreach (var (offset, value) in new[] { (76, (byte)(cose[76] ^ 0x01)), (4, (byte)0x38), (6, (byte)0x02) })
        {
            var altered = cose.ToArray();
            altered[offset] = value;
            Assert.Throws<ArgumentException>(() => new DevicePublicKey(
                new DeviceKeyId(Base64UrlOf(SHA256.HashData(altered))),
                DeviceKeyAlgorithm.WebAuthnEs256,
                deviceKey.CredentialIdBase64Url,
                Base64UrlOf(altered)));
        }

        // "AB" and "AA" decode to the same byte when trailing bits are ignored; only the canonical spelling is accepted.
        Assert.Throws<ArgumentException>(() => new SealedDeviceName("AB", Base64UrlOf(new byte[16])));
        Assert.Equal("AA", new SealedDeviceName("AA", Base64UrlOf(new byte[16])).CiphertextBase64Url);
    }

    [Fact]
    public void PairingRateLimiterIsPerSourceAndFailsClosedWhenItsWindowIsFull()
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
        var refused = 0;
        for (var index = 0; index < 400; index++)
        {
            var decision = PairingRateLimiter.TryConsume(flood, Thumbprint($"source-{index}"), Now.AddSeconds(20));
            refused += decision.Accepted ? 0 : 1;
            flood = decision.State;
        }

        var limitedAfterFlood = PairingRateLimiter.TryConsume(flood, "c291cmNlLWE", Now.AddSeconds(30));
        var newAfterFlood = PairingRateLimiter.TryConsume(flood, "c291cmNlLWM", Now.AddSeconds(30));
        var afterWindow = PairingRateLimiter.TryConsume(flood, "c291cmNlLWM", Now.AddSeconds(21).Add(ProtocolBounds.PairingRateWindow));

        Assert.False(limited.Accepted);
        Assert.Equal(Now.Add(ProtocolBounds.PairingRateWindow), limited.RetryAfterUtc);
        Assert.True(unrelated.Accepted);
        Assert.Equal(PairingRateLimiter.MaxObservations, flood.Observations.Count);
        Assert.Equal(400 - (PairingRateLimiter.MaxObservations - ProtocolBounds.MaxPairingAttemptsPerWindow), refused);
        Assert.False(limitedAfterFlood.Accepted);
        Assert.False(newAfterFlood.Accepted);
        Assert.Equal(Now.Add(ProtocolBounds.PairingRateWindow), newAfterFlood.RetryAfterUtc);
        Assert.True(afterWindow.Accepted);
    }

    private static SessionResumeAttempt Begin(
        PairedDevice device,
        SessionResumeRequest request,
        HandshakeChallenge goldenChallenge,
        TestDesktopSigner signer) =>
        SessionResumption.Begin(
            device,
            request,
            CompanionProtocolVersion.Current,
            goldenChallenge.ChallengeId,
            goldenChallenge.Assignment,
            CryptoVectors.Ephemeral("desktopEphemeralResume"),
            goldenChallenge.DesktopNonceBase64Url,
            signer,
            ResumeIssued,
            ResumeExpires);

    private static PairedDevice PairedDeviceFromPairing()
    {
        var request = GoldenRoot<PairingRequest>("handshake/pairing-request.json");
        var established = GoldenRoot<SessionEstablished>("handshake/pairing-established.json");
        return new PairedDevice(
            established.Assignment.DeviceId,
            "Living-room tablet",
            request.DeviceKey,
            DeviceAuthorizationRole.Member,
            TabletCapabilities,
            DeviceLifecycleStatus.Active,
            Established,
            Established,
            established.Assignment.KeyEpoch,
            Established.AddDays(30),
            Established);
    }

    private static PairingAttempt ApprovedAttempt()
    {
        var offer = GoldenRoot<PairingOffer>("handshake/pairing-offer.json");
        var request = GoldenRoot<PairingRequest>("handshake/pairing-request.json");
        var reveal = GoldenRoot<PairingNonceReveal>("handshake/pairing-nonce-reveal.json");
        var attempt = PairingStateMachine.Offer(offer.AttemptId, offer.DesktopIdentityKey, offer.DesktopEphemeralKey, reveal.DesktopNonceBase64Url, Offered);
        var bound = PairingStateMachine.BindResolvedCode(attempt, request, CompanionProtocolVersion.Current, Bound);
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
