namespace TarkovCompanion.CompanionProtocol.Tests;

public sealed class PairingAndCompatibilityTests
{
    [Fact]
    public async Task PairingIsSingleUseDesktopApprovedAndDeviceKeyBound()
    {
        var attemptId = new PairingAttemptId(Guid.Parse("81000000-0000-0000-0000-000000000001"));
        var offered = PairingStateMachine.Offer(attemptId, ProtocolTestData.Now);
        var request = Request(attemptId);
        var bound = PairingStateMachine.BindResolvedCode(offered, request, ProtocolTestData.Now.AddSeconds(1));

        Assert.Throws<InvalidOperationException>(() =>
            PairingStateMachine.BindResolvedCode(bound, request, ProtocolTestData.Now.AddSeconds(2)));

        var challenge = new PairingChallenge(
            "challenge-1",
            attemptId,
            request.DeviceKey.KeyId,
            "Y2hhbGxlbmdl",
            new EphemeralPublicKey(EphemeralKeyAlgorithm.EcdhP256, "ZGVza3RvcC1lcGhlbWVyYWw"),
            ProtocolTestData.Now.AddSeconds(2),
            ProtocolTestData.Now.AddMinutes(2));
        var approved = PairingStateMachine.Approve(bound, challenge, ProtocolTestData.Now.AddSeconds(2));
        var proof = new PairingProof("challenge-1", "c2lnbmF0dXJl");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await PairingStateMachine.CompleteAsync(
                approved,
                proof,
                new ProofVerifier(false),
                ProtocolTestData.Now.AddSeconds(3)));

        var completed = await PairingStateMachine.CompleteAsync(
            approved,
            proof,
            new ProofVerifier(true),
            ProtocolTestData.Now.AddSeconds(3));

        Assert.Equal(PairingAttemptStage.Completed, completed.Stage);
        Assert.Equal(request.DeviceKey.KeyId, completed.Challenge!.DeviceKeyId);
        Assert.Null(typeof(PairingRequest).GetProperty("ShortCode"));
    }

    [Fact]
    public void PairingOfferExpiresWithinTenMinutes()
    {
        var id = new PairingAttemptId(Guid.NewGuid());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PairingAttempt(
            id,
            PairingAttemptStage.Offered,
            ProtocolTestData.Now,
            ProtocolTestData.Now.AddMinutes(11)));

        Assert.Throws<ArgumentOutOfRangeException>(() => new PairingChallenge(
            "challenge-too-long",
            id,
            new DeviceKeyId("a2V5LXRodW1icHJpbnQ"),
            "Y2hhbGxlbmdl",
            new EphemeralPublicKey(EphemeralKeyAlgorithm.EcdhP256, "ZGVza3RvcC1lcGhlbWVyYWw"),
            ProtocolTestData.Now,
            ProtocolTestData.Now.AddMinutes(11)));
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
            new ProtocolVersionRange(new CompanionProtocolVersion(2, 0), new CompanionProtocolVersion(2, 1)));
        var incompatible = ProtocolCompatibility.Negotiate(
            new ClientHello(
                new ProtocolVersionRange(new CompanionProtocolVersion(1, 0), new CompanionProtocolVersion(1, 9)),
                "old-tablet",
                []),
            ProtocolVersionRange.Current);

        Assert.Equal(new CompanionProtocolVersion(2, 1), shared.NegotiatedVersion);
        Assert.Equal(CompatibilityDisposition.NoSharedMajor, incompatible.Disposition);
        Assert.Equal(CompatibilityRecoveryAction.UpdateTablet, incompatible.RecoveryAction);
    }

    [Fact]
    public void DeviceAndSessionTerminalStatesAreExplicitAndFinal()
    {
        var key = Request(new PairingAttemptId(Guid.NewGuid())).DeviceKey;
        var device = new PairedDevice(
            ProtocolTestData.TabletDevice,
            "Tablet",
            key,
            DeviceAuthorizationRole.Member,
            ProtocolTestData.TabletCapabilities,
            DeviceLifecycleStatus.Active,
            ProtocolTestData.Now,
            ProtocolTestData.Now,
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

    private static PairingRequest Request(PairingAttemptId attemptId) => new(
        attemptId,
        "Tablet",
        new DevicePublicKey(
            new DeviceKeyId("a2V5LXRodW1icHJpbnQ"),
            DeviceKeyAlgorithm.WebAuthnEs256,
            "Y3JlZGVudGlhbC1pZA",
            "Y29zZS1wdWJsaWMta2V5"),
        new EphemeralPublicKey(EphemeralKeyAlgorithm.EcdhP256, "dGFibGV0LWVwaGVtZXJhbA"),
        "Y2xpZW50LW5vbmNl");

    private sealed class ProofVerifier(bool result) : IDeviceKeyProofVerifier
    {
        public ValueTask<bool> VerifyAsync(
            DevicePublicKey deviceKey,
            PairingChallenge challenge,
            PairingProof proof,
            CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }
}
