using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.GroupServer;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.StateSync;
using TarkovCompanion.GroupServer.Storage;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

public sealed class OpaqueRelayFrameHubTests
{
    [Fact]
    public async Task MemberFrameRoutesToOwnerAndReplayIsRejected()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var member = await AddDeviceAsync(context, owner, "member", DeviceAuthorizationRole.Member);
        var hub = new OpaqueRelayFrameHub(context.Registry, context.Clock);
        var frame = Frame(member.Credential.SessionId, member.Credential.ChannelId, context.Clock.UtcNow, 1, 1);

        var first = await hub.PublishAsync(member.Principal, frame);
        var replay = await hub.PublishAsync(member.Principal, frame);
        var batch = hub.Read(owner, 0);
        var acknowledged = hub.Acknowledge(owner, Assert.Single(batch.Frames).DeliveryId);
        var staleAcknowledgement = hub.Acknowledge(owner, batch.Frames[0].DeliveryId);

        Assert.True(first.Accepted);
        Assert.Equal(1, first.RecipientCount);
        Assert.False(replay.Accepted);
        Assert.Equal("replay-rejected", replay.Code);
        Assert.True(acknowledged.Accepted);
        Assert.False(staleAcknowledgement.Accepted);
    }

    [Fact]
    public async Task OwnerCanReplyOnlyToAnActiveDeviceSessionOnItsChannel()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var member = await AddDeviceAsync(context, owner, "target", DeviceAuthorizationRole.Member);
        var hub = new OpaqueRelayFrameHub(context.Registry, context.Clock);

        var sent = await hub.PublishAsync(
            owner,
            Frame(member.Credential.SessionId, owner.ChannelId, context.Clock.UtcNow, 1, 1));
        var received = hub.Read(member.Principal, 0);
        var unknown = await hub.PublishAsync(
            owner,
            Frame(new DeviceSessionId(Guid.NewGuid()), owner.ChannelId, context.Clock.UtcNow, 1, 2));

        Assert.True(sent.Accepted);
        Assert.Single(received.Frames);
        Assert.False(unknown.Accepted);
        Assert.Equal("route-rejected", unknown.Code);
    }

    [Fact]
    public async Task ObserverIsReadOnlyForEveryOpaqueMutation()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var observer = await AddDeviceAsync(context, owner, "observer", DeviceAuthorizationRole.Observer);
        var hub = new OpaqueRelayFrameHub(context.Registry, context.Clock);

        var result = await hub.PublishAsync(
            observer.Principal,
            Frame(observer.Credential.SessionId, owner.ChannelId, context.Clock.UtcNow, 1, 1));

        Assert.False(result.Accepted);
        Assert.Equal("not-authorized", result.Code);
        Assert.Empty(hub.Read(observer.Principal, 0).Frames);
    }

    [Fact]
    public async Task ExpiredFutureAndVersionSkewedFramesFailClosed()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var member = await AddDeviceAsync(context, owner, "skew", DeviceAuthorizationRole.Member);
        var hub = new OpaqueRelayFrameHub(context.Registry, context.Clock);

        var expired = Frame(
            member.Credential.SessionId,
            owner.ChannelId,
            context.Clock.UtcNow.AddMinutes(-5),
            1,
            1,
            expiresUtc: context.Clock.UtcNow);
        var future = Frame(
            member.Credential.SessionId,
            owner.ChannelId,
            context.Clock.UtcNow.Add(RelaySecurityBounds.ClockSkew).AddMilliseconds(1),
            1,
            1);
        var incompatible = Frame(
            member.Credential.SessionId,
            owner.ChannelId,
            context.Clock.UtcNow,
            1,
            1,
            new CompanionProtocolVersion(3, 0));

        Assert.False((await hub.PublishAsync(member.Principal, expired)).Accepted);
        Assert.False((await hub.PublishAsync(member.Principal, future)).Accepted);
        Assert.Equal("unsupported-version", (await hub.PublishAsync(member.Principal, incompatible)).Code);
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
                Frame(member.Credential.SessionId, owner.ChannelId, context.Clock.UtcNow, 1, sequence));
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
    public async Task PairedDeviceNeverCreatesAPhantomGroupMember()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        _ = await AddDeviceAsync(context, owner, "not-a-squad-member", DeviceAuthorizationRole.Member);
        var v1Rooms = new GroupRooms(context.Clock);

        Assert.Equal(2, context.Registry.ActiveRoutes(owner.ChannelId).Count);
        Assert.Equal(0, v1Rooms.MemberCount);
        Assert.DoesNotContain(
            typeof(RelaySessionRoute).GetProperties(),
            property => property.PropertyType == typeof(GroupMemberState));
    }

    private static async ValueTask<(RelaySessionCredential Credential, RelayPrincipal Principal)> AddDeviceAsync(
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
        return (credential, principal);
    }

    private static OpaqueRelayFrame Frame(
        DeviceSessionId sessionId,
        RelayChannelId channelId,
        DateTimeOffset issuedUtc,
        long keyEpoch,
        long senderSequence,
        CompanionProtocolVersion? version = null,
        DateTimeOffset? expiresUtc = null) => new(
            version ?? CompanionProtocolVersion.Current,
            channelId,
            sessionId,
            keyEpoch,
            senderSequence,
            RelayCipherSuite.P256HkdfSha256Aes256Gcm,
            Encode(new byte[12]),
            [Encode([1, 2, 3, 4])],
            Encode(new byte[16]),
            issuedUtc,
            expiresUtc ?? issuedUtc.AddMinutes(1));

    private static string Encode(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
