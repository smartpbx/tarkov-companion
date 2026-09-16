namespace TarkovCompanion.CompanionProtocol.Tests;

public sealed class PairingAndCompatibilityTests
{
    [Fact]
    public async Task PairingIsSingleUseDesktopApprovedAndDeviceKeyBound()
    {
        var offer = ProtocolTestData.GoldenRoot<PairingOffer>("handshake/pairing-offer.json");
        var request = ProtocolTestData.GoldenRoot<PairingRequest>("handshake/pairing-request.json");
        var reveal = ProtocolTestData.GoldenRoot<PairingNonceReveal>("handshake/pairing-nonce-reveal.json");
        var challenge = ProtocolTestData.GoldenRoot<HandshakeChallenge>("handshake/pairing-challenge.json");
        var proof = ProtocolTestData.GoldenRoot<DeviceKeyProof>("handshake/pairing-proof.json");
        var offered = PairingStateMachine.Offer(
            offer.AttemptId,
            offer.DesktopIdentityKey,
            offer.DesktopEphemeralKey,
            reveal.DesktopNonceBase64Url,
            ProtocolTestData.Now);
        var bound = PairingStateMachine.BindResolvedCode(
            offered,
            request,
            CompanionProtocolVersion.Current,
            ProtocolTestData.Now.AddSeconds(10));

        Assert.Throws<InvalidOperationException>(() =>
            PairingStateMachine.BindResolvedCode(
                bound,
                request,
                CompanionProtocolVersion.Current,
                ProtocolTestData.Now.AddSeconds(11)));

        var approved = PairingStateMachine.Approve(bound, challenge, challenge.IssuedUtc);
        var completedUtc = challenge.IssuedUtc.AddSeconds(10);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await PairingStateMachine.CompleteAsync(
                approved,
                proof,
                new ProofVerifier(false),
                completedUtc));

        var completed = await PairingStateMachine.CompleteAsync(
            approved,
            proof,
            new ProofVerifier(true),
            completedUtc);

        Assert.Equal(PairingAttemptStage.Completed, completed.Stage);
        Assert.Equal(request.DeviceKey.KeyId, completed.Challenge!.DeviceKeyId);
        Assert.Null(typeof(PairingRequest).GetProperty("ShortCode"));
    }

    [Fact]
    public void PairingOfferExpiresWithinTenMinutes()
    {
        var offer = ProtocolTestData.GoldenRoot<PairingOffer>("handshake/pairing-offer.json");
        var reveal = ProtocolTestData.GoldenRoot<PairingNonceReveal>("handshake/pairing-nonce-reveal.json");
        var attempt = PairingStateMachine.Offer(
            offer.AttemptId,
            offer.DesktopIdentityKey,
            offer.DesktopEphemeralKey,
            reveal.DesktopNonceBase64Url,
            ProtocolTestData.Now);

        Assert.Equal(ProtocolTestData.Now.Add(ProtocolBounds.PairingLifetime), attempt.ExpiresUtc);
        Assert.True(attempt.ExpiresUtc - attempt.OfferedUtc <= TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void PairingRateLimiterIsPerSourceAndBounded()
    {
        var state = PairingRateState.Empty;
        for (var index = 0; index < ProtocolBounds.MaxPairingAttemptsPerWindow; index++)
        {
            var accepted = PairingRateLimiter.TryConsume(state, "c291cmNlLWE", ProtocolTestData.Now.AddSeconds(index));
            Assert.True(accepted.Accepted);
            state = accepted.State;
        }

        var limited = PairingRateLimiter.TryConsume(state, "c291cmNlLWE", ProtocolTestData.Now.AddSeconds(10));
        var unrelated = PairingRateLimiter.TryConsume(state, "c291cmNlLWI", ProtocolTestData.Now.AddSeconds(10));

        Assert.False(limited.Accepted);
        Assert.NotNull(limited.RetryAfterUtc);
        Assert.True(unrelated.Accepted);
    }

    [Fact]
    public void NegotiationUsesHighestSharedMinorAndGivesRecovery()
    {
        var shared = ProtocolCompatibility.Negotiate(
            new ClientHello(
                new ProtocolVersionRange(new CompanionProtocolVersion(2, 0), new CompanionProtocolVersion(2, 3)),
                "tablet-a",
                []),
            new ProtocolVersionRange(new CompanionProtocolVersion(2, 0), new CompanionProtocolVersion(2, 1)),
            ProtocolTestData.Now);
        var incompatible = ProtocolCompatibility.Negotiate(
            new ClientHello(
                new ProtocolVersionRange(new CompanionProtocolVersion(1, 0), new CompanionProtocolVersion(1, 9)),
                "old-tablet",
                []),
            ProtocolVersionRange.Current,
            ProtocolTestData.Now);

        Assert.Equal(new CompanionProtocolVersion(2, 1), shared.NegotiatedVersion);
        Assert.Equal(CompatibilityDisposition.NoSharedMajor, incompatible.Disposition);
        Assert.Equal(CompatibilityRecoveryAction.UpdateTablet, incompatible.RecoveryAction);
    }

    [Fact]
    public void DeviceAndSessionTerminalStatesAreExplicitAndFinal()
    {
        var key = ProtocolTestData.GoldenRoot<PairingRequest>("handshake/pairing-request.json").DeviceKey;
        var device = new PairedDevice(
            ProtocolTestData.TabletDevice,
            "Tablet",
            key,
            DeviceAuthorizationRole.Member,
            ProtocolTestData.TabletCapabilities,
            DeviceLifecycleStatus.Active,
            ProtocolTestData.Now,
            ProtocolTestData.Now,
            1,
            ProtocolTestData.Now.AddDays(30),
            ProtocolTestData.Now);
        var revoked = DeviceLifecycle.Revoke(device, ProtocolTestData.Now.AddMinutes(1), "user-revoked");

        Assert.Equal(DeviceLifecycleStatus.Revoked, revoked.Status);
        Assert.Throws<InvalidOperationException>(() =>
            DeviceLifecycle.Expire(revoked, ProtocolTestData.Now.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(() =>
            DeviceLifecycle.Expire(device, ProtocolTestData.Now.AddMinutes(1)));
        Assert.Equal(
            DeviceLifecycleStatus.Expired,
            DeviceLifecycle.Expire(device, ProtocolTestData.Now.Add(ProtocolBounds.DeviceInactivityExpiry)).Status);
    }

    private sealed class ProofVerifier(bool result) : IDeviceKeyProofVerifier
    {
        public ValueTask<bool> VerifyAsync(
            DevicePublicKey deviceKey,
            HandshakeChallenge challenge,
            DeviceKeyProof proof,
            CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }
}
