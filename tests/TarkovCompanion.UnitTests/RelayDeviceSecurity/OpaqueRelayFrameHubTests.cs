using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.GroupServer;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.StateSync;
using TarkovCompanion.GroupServer.Storage;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

public sealed class OpaqueRelayFrameHubTests
{
    [Fact]
    public async Task MemberFrameRoutesToOwnerAndReplayAndStaleAckAreRejected()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var member = await AddDeviceAsync(context, owner, "member", DeviceAuthorizationRole.Member);
        var hub = new OpaqueRelayFrameHub(context.Registry, context.Clock);
        var frame = RelaySecurityTestFactory.Frame(member.Principal, context.Clock.UtcNow, 1);

        var first = await hub.PublishAsync(member.Principal, frame);
        var replay = await hub.PublishAsync(member.Principal, frame);
        var batch = hub.Read(owner, 0);
        var delivery = Assert.Single(batch.Frames).DeliveryId;
        var acknowledged = hub.Acknowledge(owner, delivery);
        var staleAcknowledgement = hub.Acknowledge(owner, delivery);

        Assert.True(first.Accepted);
        Assert.Equal(1, first.RecipientCount);
        Assert.False(replay.Accepted);
        Assert.Equal("replay-rejected", replay.Code);
        Assert.True(acknowledged.Accepted);
        Assert.False(staleAcknowledgement.Accepted);
    }

    [Fact]
    public async Task ReplayStateIsPerTargetSessionAndPerDirection()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var first = await AddDeviceAsync(context, owner, "first", DeviceAuthorizationRole.Member);
        var second = await AddDeviceAsync(context, owner, "second", DeviceAuthorizationRole.Observer);
        var hub = new OpaqueRelayFrameHub(context.Registry, context.Clock);

        var firstUp = await hub.PublishAsync(
            first.Principal,
            RelaySecurityTestFactory.Frame(first.Principal, context.Clock.UtcNow, 1));
        var firstDown = await hub.PublishAsync(
            owner,
            RelaySecurityTestFactory.Frame(first.Principal, context.Clock.UtcNow, 1));
        var secondDown = await hub.PublishAsync(
            owner,
            RelaySecurityTestFactory.Frame(second.Principal, context.Clock.UtcNow, 1));
        var secondUp = await hub.PublishAsync(
            second.Principal,
            RelaySecurityTestFactory.Frame(second.Principal, context.Clock.UtcNow, 1));

        Assert.True(firstUp.Accepted);
        Assert.True(firstDown.Accepted);
        Assert.True(secondDown.Accepted);
        Assert.True(secondUp.Accepted);
        Assert.Single(hub.Read(first.Principal, 0).Frames);
        Assert.Single(hub.Read(second.Principal, 0).Frames);
        Assert.Equal(2, hub.Read(owner, 0).Frames.Count);
    }

    [Fact]
    public async Task OwnerCannotSpoofTargetChannelEpochOrUnknownSession()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var member = await AddDeviceAsync(context, owner, "target", DeviceAuthorizationRole.Member);
        var hub = new OpaqueRelayFrameHub(context.Registry, context.Clock);

        var wrongChannel = await hub.PublishAsync(
            owner,
            RelaySecurityTestFactory.Frame(
                member.Principal,
                context.Clock.UtcNow,
                1,
                channelId: owner.ChannelId));
        var wrongEpoch = await hub.PublishAsync(
            owner,
            RelaySecurityTestFactory.Frame(
                member.Principal,
                context.Clock.UtcNow,
                1,
                keyEpoch: member.Principal.KeyEpoch + 1));
        var valid = await hub.PublishAsync(
            owner,
            RelaySecurityTestFactory.Frame(member.Principal, context.Clock.UtcNow, 1));

        Assert.False(wrongChannel.Accepted);
        Assert.False(wrongEpoch.Accepted);
        Assert.True(valid.Accepted);
        Assert.Single(hub.Read(member.Principal, 0).Frames);
    }

    [Fact]
    public async Task ObserverCanSendEncryptedAcknowledgementTrafficButCannotGainAdminAuthority()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var observer = await AddDeviceAsync(context, owner, "observer", DeviceAuthorizationRole.Observer);
        var hub = new OpaqueRelayFrameHub(context.Registry, context.Clock);

        var result = await hub.PublishAsync(
            observer.Principal,
            RelaySecurityTestFactory.Frame(observer.Principal, context.Clock.UtcNow, 1));
        var revoked = await context.Registry.RevokeDeviceAsync(
            observer.Principal,
            owner.DeviceId,
            "observer-forgery");

        Assert.True(result.Accepted);
        Assert.Single(hub.Read(owner, 0).Frames);
        Assert.False(revoked.Succeeded);
    }

    [Fact]
    public async Task ExpiredFutureVersionAndStaleEpochFramesFailClosed()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var member = await AddDeviceAsync(context, owner, "skew", DeviceAuthorizationRole.Member);
        var hub = new OpaqueRelayFrameHub(context.Registry, context.Clock);

        var expired = RelaySecurityTestFactory.Frame(
            member.Principal,
            context.Clock.UtcNow.AddMinutes(-1),
            1,
            expiresUtc: context.Clock.UtcNow);
        var future = RelaySecurityTestFactory.Frame(
            member.Principal,
            context.Clock.UtcNow.Add(ProtocolBounds.MaxClientClockSkew).AddMilliseconds(1),
            1);
        var incompatible = RelaySecurityTestFactory.Frame(
            member.Principal,
            context.Clock.UtcNow,
            1,
            new CompanionProtocolVersion(3, 0));
        var staleEpoch = RelaySecurityTestFactory.Frame(
            member.Principal,
            context.Clock.UtcNow,
            1,
            keyEpoch: member.Principal.KeyEpoch + 1);

        Assert.False((await hub.PublishAsync(member.Principal, expired)).Accepted);
        Assert.False((await hub.PublishAsync(member.Principal, future)).Accepted);
        Assert.Equal("unsupported-version", (await hub.PublishAsync(member.Principal, incompatible)).Code);
        Assert.False((await hub.PublishAsync(member.Principal, staleEpoch)).Accepted);
    }

    [Fact]
    public async Task PerRecipientQueueIsBoundedAndSignalsReconnectAfterOverflow()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var member = await AddDeviceAsync(context, owner, "flood", DeviceAuthorizationRole.Member);
        var hub = new OpaqueRelayFrameHub(context.Registry, context.Clock);

        for (var sequence = 1; sequence <= RelaySecurityBounds.MaximumQueuedFramesPerParticipant + 1; sequence++)
        {
            var result = await hub.PublishAsync(
                member.Principal,
                RelaySecurityTestFactory.Frame(member.Principal, context.Clock.UtcNow, sequence));
            Assert.True(result.Accepted);
        }

        var batch = hub.Read(owner, 0, RelaySecurityBounds.MaximumQueuedFramesPerParticipant);

        Assert.Equal(RelaySecurityBounds.MaximumQueuedFramesPerParticipant, batch.Frames.Count);
        Assert.True(batch.RequiresReconnect);
        Assert.Equal(2, batch.Frames[0].DeliveryId);
        Assert.False(hub.Acknowledge(owner, batch.Frames[^1].DeliveryId).Accepted);
        Assert.True(hub.ResetAfterReconnect(owner).Accepted);
        Assert.Empty(hub.Read(owner, batch.Frames[^1].DeliveryId).Frames);
    }

    [Fact]
    public async Task MissingOrFutureDeliveryCursorRequiresReconnect()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var member = await AddDeviceAsync(context, owner, "cursor", DeviceAuthorizationRole.Member);
        var hub = new OpaqueRelayFrameHub(context.Registry, context.Clock);
        Assert.True((await hub.PublishAsync(
            member.Principal,
            RelaySecurityTestFactory.Frame(member.Principal, context.Clock.UtcNow, 1))).Accepted);

        var future = hub.Read(owner, 99);
        var afterRestart = new OpaqueRelayFrameHub(context.Registry, context.Clock).Read(owner, 1);

        Assert.True(future.RequiresReconnect);
        Assert.True(afterRestart.RequiresReconnect);
    }

    [Fact]
    public async Task PairedDeviceNeverCreatesAPhantomV1GroupMember()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var member = await AddDeviceAsync(context, owner, "not-a-squad-member", DeviceAuthorizationRole.Member);
        var v1Rooms = new GroupRooms(context.Clock);

        Assert.NotNull(context.Registry.ActiveRoute(member.Principal.SessionId));
        Assert.Equal(0, v1Rooms.MemberCount);
        Assert.DoesNotContain(
            typeof(RelaySessionRoute).GetProperties(),
            property => property.PropertyType == typeof(GroupMemberState));
    }

    private static async ValueTask<AddedDevice> AddDeviceAsync(
        RelayTestContext context,
        RelayPrincipal owner,
        string suffix,
        DeviceAuthorizationRole role)
    {
        var completed = await RelaySecurityTestFactory.CompletedPairingAsync(suffix, context.Clock.UtcNow);
        var paired = await context.Registry.AddPairedDeviceAsync(
            owner,
            completed,
            role,
            CompanionSurfaceKind.TabletLandscape);
        var credential = Assert.IsType<RelaySessionCredential>(paired.Value);
        var principal = Assert.IsType<RelayPrincipal>((await context.Registry.AuthenticateAsync(
            credential.SessionId,
            credential.Secret)).Principal);
        return new AddedDevice(credential, principal, completed);
    }

    private sealed record AddedDevice(
        RelaySessionCredential Credential,
        RelayPrincipal Principal,
        PairingAttempt Pairing);
}
