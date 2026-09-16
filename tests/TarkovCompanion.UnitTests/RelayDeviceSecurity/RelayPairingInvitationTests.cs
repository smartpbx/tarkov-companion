using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.GroupServer.Pairing;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.Storage;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

public sealed class RelayPairingInvitationTests
{
    [Fact]
    public async Task RelayConsumesCodeAndRoutesOfferWithoutDrivingHandshake()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        using var invitations = new RelayPairingInvitations(context.Registry, context.Clock);
        var offer = RelaySecurityTestFactory.Offer("tablet", context.Clock.UtcNow);
        var issued = invitations.Register(owner, offer, "01234-56789");
        var credential = Assert.IsType<PairingInvitationCredential>(issued.Value);

        var resolved = invitations.Resolve(
            credential.ShortCode,
            RelaySecurityTestFactory.SourceHash("198.51.100.8"));
        var replay = invitations.Resolve(
            credential.ShortCode,
            RelaySecurityTestFactory.SourceHash("198.51.100.8"));
        var route = invitations.RouteFor(offer.AttemptId);

        Assert.Equal(offer, resolved.Value);
        Assert.False(replay.Succeeded);
        Assert.True(route.Succeeded);
        Assert.Equal(owner.SessionId, Assert.IsType<RelayPairingRoute>(route.Value).OwnerSessionId);
        Assert.DoesNotContain(
            typeof(RelayPairingInvitations).GetMethods(),
            method => method.Name is "Approve" or "CompleteAsync");
        Assert.DoesNotContain(
            typeof(PairingInvitationView).GetProperties(),
            property => property.Name.Contains("Name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InvitationAndOwnerSessionAreIndependentlyRevocable()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        using var invitations = new RelayPairingInvitations(context.Registry, context.Clock);
        var firstOffer = RelaySecurityTestFactory.Offer("first", context.Clock.UtcNow);
        var first = Assert.IsType<PairingInvitationCredential>(
            invitations.Register(owner, firstOffer, "AAAAAAAAAA").Value);
        Assert.True(invitations.Resolve(
            first.ShortCode,
            RelaySecurityTestFactory.SourceHash("198.51.100.9")).Succeeded);
        Assert.True(invitations.Revoke(owner, firstOffer.AttemptId).Succeeded);
        Assert.False(invitations.RouteFor(firstOffer.AttemptId).Succeeded);

        var secondOffer = RelaySecurityTestFactory.Offer("second", context.Clock.UtcNow);
        var second = Assert.IsType<PairingInvitationCredential>(
            invitations.Register(owner, secondOffer, "BBBBBBBBBB").Value);
        Assert.True(invitations.Resolve(
            second.ShortCode,
            RelaySecurityTestFactory.SourceHash("198.51.100.10")).Succeeded);
        var resume = await RelaySecurityTestFactory.CompletedResumeAsync(
            owner,
            context.OwnerKey,
            context.OwnerPairing.Establishment!.EstablishedUtc,
            context.Clock.UtcNow);
        Assert.True((await context.Registry.RotateSessionAsync(
            owner,
            resume,
            CompanionSurfaceKind.Desktop)).Succeeded);

        Assert.False(invitations.RouteFor(secondOffer.AttemptId).Succeeded);
    }

    [Fact]
    public async Task ObserverCannotRegisterInvitations()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var completed = await RelaySecurityTestFactory.CompletedPairingAsync("observer-invite", context.Clock.UtcNow);
        var paired = await context.Registry.AddPairedDeviceAsync(
            owner,
            completed,
            DeviceAuthorizationRole.Observer,
            CompanionSurfaceKind.NarrowPhone);
        var credential = Assert.IsType<RelaySessionCredential>(paired.Value);
        var observer = Assert.IsType<RelayPrincipal>((await context.Registry.AuthenticateAsync(
            credential.SessionId,
            credential.Secret)).Principal);
        using var invitations = new RelayPairingInvitations(context.Registry, context.Clock);

        var result = invitations.Register(
            observer,
            RelaySecurityTestFactory.Offer("denied", context.Clock.UtcNow),
            "CCCCCCCCCC");

        Assert.False(result.Succeeded);
        Assert.Equal("not-authorized", result.Code);
    }

    [Fact]
    public async Task ExpiredOrMalformedInvitationFailsClosed()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        using var invitations = new RelayPairingInvitations(context.Registry, context.Clock);
        var offer = RelaySecurityTestFactory.Offer("expiry", context.Clock.UtcNow);
        var malformed = invitations.Register(owner, offer, "not a code!");
        var issued = Assert.IsType<PairingInvitationCredential>(
            invitations.Register(owner, offer, "DDDDDDDDDD").Value);
        context.Clock.Advance(ProtocolBounds.PairingLifetime);

        var expired = invitations.Resolve(
            issued.ShortCode,
            RelaySecurityTestFactory.SourceHash("198.51.100.11"));

        Assert.False(malformed.Succeeded);
        Assert.False(expired.Succeeded);
        Assert.Empty(invitations.PendingFor(owner));
    }

    [Fact]
    public async Task PairingResolutionUsesCanonicalBoundedPerSourceRateState()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        using var invitations = new RelayPairingInvitations(context.Registry, context.Clock);
        var offer = RelaySecurityTestFactory.Offer("rate", context.Clock.UtcNow);
        var issued = Assert.IsType<PairingInvitationCredential>(
            invitations.Register(owner, offer, "EEEEEEEEEE").Value);
        var source = RelaySecurityTestFactory.SourceHash("203.0.113.42");
        for (var attempt = 0; attempt < ProtocolBounds.MaxPairingAttemptsPerWindow; attempt++)
        {
            _ = invitations.Resolve("FFFFFFFFFF", source);
        }

        var limited = invitations.Resolve(issued.ShortCode, source);
        var otherSource = invitations.Resolve(
            issued.ShortCode,
            RelaySecurityTestFactory.SourceHash("203.0.113.43"));

        Assert.False(limited.Succeeded);
        Assert.Equal("rate-limited", limited.Code);
        Assert.NotNull(limited.RetryAfterUtc);
        Assert.True(otherSource.Succeeded);
    }
}
