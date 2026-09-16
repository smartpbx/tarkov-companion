using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.GroupServer.Pairing;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.Storage;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

public sealed class RelayPairingInvitationTests
{
    [Fact]
    public async Task InvitationIsOwnerCreatedSingleUseApprovedAndDeviceProofBound()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var invitations = new RelayPairingInvitations(context.Clock);
        var issued = invitations.Create(owner);
        var credential = Assert.IsType<PairingInvitationCredential>(issued.Value);
        var request = Request(credential.AttemptId, "tablet");
        var source = RelayRateLimiter.HashSource("198.51.100.8");

        var resolved = invitations.Resolve(credential.ShortCode, source, request);
        var replay = invitations.Resolve(credential.ShortCode, source, request);
        var pending = Assert.Single(invitations.PendingFor(owner));
        var challenge = RelaySecurityTestFactory.Challenge(
            new PairingAttempt(
                pending.AttemptId,
                pending.Stage,
                pending.OfferedUtc,
                pending.ExpiresUtc,
                context.Clock.UtcNow,
                request),
            context.Clock.UtcNow);
        var approved = invitations.Approve(owner, challenge);
        var completed = await invitations.CompleteAsync(
            credential.AttemptId,
            new PairingProof(challenge.ChallengeId, RelaySecurityTestFactory.Base64Url("valid-proof")),
            new AcceptingProofVerifier());

        Assert.True(resolved.Succeeded);
        Assert.False(replay.Succeeded);
        Assert.Equal("pairing-rejected", replay.Code);
        Assert.True(approved.Succeeded);
        Assert.Equal(PairingAttemptStage.Completed, completed.Value?.Stage);
        Assert.Equal(request.DeviceKey.KeyId, completed.Value?.Request?.DeviceKey.KeyId);
    }

    [Fact]
    public async Task ObserverCannotCreateOrApproveInvitations()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var completed = await RelaySecurityTestFactory.CompletedPairingAsync("observer", context.Clock.UtcNow);
        var paired = await context.Registry.AddPairedDeviceAsync(
            owner,
            completed,
            DeviceAuthorizationRole.Observer,
            CompanionSurfaceKind.NarrowPhone);
        var credential = Assert.IsType<RelaySessionCredential>(paired.Value);
        var observer = Assert.IsType<RelayPrincipal>((await context.Registry.AuthenticateAsync(
            credential.SessionId,
            credential.Secret)).Principal);
        var invitations = new RelayPairingInvitations(context.Clock);

        var attempt = invitations.Create(observer);

        Assert.False(attempt.Succeeded);
        Assert.Equal("not-authorized", attempt.Code);
    }

    [Fact]
    public async Task ExpiredInvitationAndClockSkewedCompletionFailClosed()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var invitations = new RelayPairingInvitations(context.Clock);
        var issued = Assert.IsType<PairingInvitationCredential>(invitations.Create(owner).Value);
        context.Clock.Advance(ProtocolBounds.PairingLifetime);

        var expired = invitations.Resolve(
            issued.ShortCode,
            RelayRateLimiter.HashSource("198.51.100.9"),
            Request(issued.AttemptId, "late"));

        Assert.False(expired.Succeeded);
        Assert.Equal("pairing-rejected", expired.Code);
        Assert.Empty(invitations.PendingFor(owner));
    }

    [Fact]
    public async Task PairingResolutionIsRateLimitedPerSource()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var invitations = new RelayPairingInvitations(context.Clock);
        var issued = Assert.IsType<PairingInvitationCredential>(invitations.Create(owner).Value);
        var source = RelayRateLimiter.HashSource("203.0.113.42");
        for (var attempt = 0; attempt < ProtocolBounds.MaxPairingAttemptsPerWindow; attempt++)
        {
            _ = invitations.Resolve("wrong-code", source, Request(issued.AttemptId, $"wrong-{attempt}"));
        }

        var limited = invitations.Resolve(
            issued.ShortCode,
            source,
            Request(issued.AttemptId, "actual"));
        var otherSource = invitations.Resolve(
            issued.ShortCode,
            RelayRateLimiter.HashSource("203.0.113.43"),
            Request(issued.AttemptId, "actual"));

        Assert.False(limited.Succeeded);
        Assert.Equal("rate-limited", limited.Code);
        Assert.NotNull(limited.RetryAfterUtc);
        Assert.True(otherSource.Succeeded);
    }

    private static PairingRequest Request(PairingAttemptId attemptId, string suffix) => new(
        attemptId,
        $"Device {suffix}",
        RelaySecurityTestFactory.DeviceKey(suffix),
        new EphemeralPublicKey(
            EphemeralKeyAlgorithm.EcdhP256,
            RelaySecurityTestFactory.Base64Url($"ephemeral-{suffix}")),
        RelaySecurityTestFactory.Base64Url($"nonce-{suffix}"));

    private sealed class AcceptingProofVerifier : IDeviceKeyProofVerifier
    {
        public ValueTask<bool> VerifyAsync(
            DevicePublicKey deviceKey,
            PairingChallenge challenge,
            PairingProof proof,
            CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }
}
