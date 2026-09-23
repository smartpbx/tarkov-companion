using System.Net;
using System.Text;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Tablet;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// [#553] A relay is a group's. Two squadmates on one relay, in one room, each with a desktop and
/// a tablet: neither claims anything, neither types an admin key, and neither can see or touch
/// what is the other's. Driven through the pairing panel each of them would press.
/// </summary>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class RelayLinkTwoDesktopsTests
{
    private const string GroupKeyOfTheSquad = "the-squads-own-group-key";
    private static readonly string[] Loopback = ["http://127.0.0.1:0"];
    private static readonly byte[] Customs = Encoding.UTF8.GetBytes("{\"mapName\":\"Customs\",\"publishedUtc\":\"2026-01-01T00:00:00Z\"}");
    private static readonly byte[] Woods = Encoding.UTF8.GetBytes("{\"mapName\":\"Woods\",\"publishedUtc\":\"2026-01-01T00:00:00Z\"}");

    [Fact]
    public async Task TwoDesktopsEachPairTheirOwnTabletWithNoClaimAndNeitherReachesTheOthers()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock, Loopback, certificate: null, GroupKeyOfTheSquad);
        using var aliceDisk = new DesktopDisk();
        using var bobDisk = new DesktopDisk { DesktopDeviceId = Guid.Parse("10000000-0000-4000-8000-0000000000bb") };
        using var aliceTablet = new TabletSimulator(relay.Origin, clock);
        using var bobTablet = new TabletSimulator(relay.Origin, clock);
        await using var alice = await DesktopRun.StartAsync(aliceDisk, relay.Origin, clock, groupKey: GroupKeyOfTheSquad);
        await using var bob = await DesktopRun.StartAsync(bobDisk, relay.Origin, clock, groupKey: GroupKeyOfTheSquad);

        // Registered at startup on the group key: nothing typed, nothing claimed.
        Assert.True(alice.Panel.IsClaimedByThisDesktop, alice.Panel.RelayClaimMessage);
        Assert.True(bob.Panel.IsClaimedByThisDesktop, bob.Panel.RelayClaimMessage);
        Assert.False(alice.Panel.NeedsClaim);
        Assert.False(relay.Registry.CanAuthenticate);
        Assert.Equal(3, relay.Desktops.Tenants.Length);

        await alice.PairAsync(aliceTablet, "Alice's tablet");
        await bob.PairAsync(bobTablet, "Bob's tablet");
        Assert.Equal("Alice's tablet", Assert.Single(alice.Panel.Devices).Device.DisplayName);
        Assert.Equal("Bob's tablet", Assert.Single(bob.Panel.Devices).Device.DisplayName);

        // Each tablet reads its own desktop's map and only that.
        Assert.True(await alice.Bridge.PublishMapSurfaceAsync(Customs, null));
        using (var bobsBefore = await bobTablet.ReadMapRawAsync())
        {
            Assert.Equal(HttpStatusCode.NotFound, bobsBefore.StatusCode);
        }

        Assert.True(await bob.Bridge.PublishMapSurfaceAsync(Woods, null));
        using (var alices = await aliceTablet.ReadMapRawAsync())
        using (var bobs = await bobTablet.ReadMapRawAsync())
        {
            Assert.Contains("Customs", await alices.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Contains("Woods", await bobs.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // A waypoint dropped on Alice's tablet reaches Alice's desktop and not Bob's.
        await aliceTablet.ReadAsync();
        using (var dropped = await aliceTablet.DropWaypointAsync(120, 340, "Alice's spot"))
        {
            Assert.Equal(HttpStatusCode.OK, dropped.StatusCode);
        }

        await alice.Bridge.PollOnceAsync(CancellationToken.None);
        await bob.Bridge.PollOnceAsync(CancellationToken.None);
        Assert.Contains(aliceDisk.Marks.Marks, mark => mark.State.Label == "Alice's spot");
        Assert.Empty(bobDisk.Marks.Marks);

        // The relay goes down and comes back: both desktops and both tablets carry on, nothing typed.
        await relay.RestartAsync();
        Assert.Equal(3, relay.Desktops.Tenants.Length);
        await alice.Bridge.PollOnceAsync(CancellationToken.None);
        await bob.Bridge.PollOnceAsync(CancellationToken.None);
        Assert.Equal(RelayOwnerLinkState.Verified, alice.Bridge.OwnerLink);
        Assert.Equal(RelayOwnerLinkState.Verified, bob.Bridge.OwnerLink);
        Assert.True(await bob.Bridge.PublishMapSurfaceAsync(Woods, null));
        using (var aliceFrames = await aliceTablet.ReadFramesRawAsync())
        using (var bobsAgain = await bobTablet.ReadMapRawAsync())
        {
            Assert.Equal(HttpStatusCode.OK, aliceFrames.StatusCode);
            Assert.Equal(HttpStatusCode.OK, bobsAgain.StatusCode);
        }

        // Alice revokes her tablet. Bob's is untouched.
        var row = Assert.Single(alice.Panel.Devices);
        await ((AsyncDelegateCommand)row.RevokeCommand).ExecuteAsync();
        await ((AsyncDelegateCommand)row.RevokeCommand).ExecuteAsync();
        using var revoked = await aliceTablet.ReadFramesRawAsync();
        using var untouched = await bobTablet.ReadFramesRawAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, untouched.StatusCode);
        Assert.Equal(DeviceLifecycleStatus.Active, Assert.Single(bob.Authority.Snapshot.Devices).Status);
    }

    [Fact]
    public async Task ThreeDaysLaterEachTabletComesBackToItsOwnDesktopWithNothingTyped()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock, Loopback, certificate: null, GroupKeyOfTheSquad);
        using var aliceDisk = new DesktopDisk();
        using var bobDisk = new DesktopDisk { DesktopDeviceId = Guid.Parse("10000000-0000-4000-8000-0000000000bb") };
        using var aliceTablet = new TabletSimulator(relay.Origin, clock);
        using var bobTablet = new TabletSimulator(relay.Origin, clock);
        await using var alice = await DesktopRun.StartAsync(aliceDisk, relay.Origin, clock, groupKey: GroupKeyOfTheSquad);
        await using var bob = await DesktopRun.StartAsync(bobDisk, relay.Origin, clock, groupKey: GroupKeyOfTheSquad);
        await alice.PairAsync(aliceTablet, "Alice's tablet");
        await bob.PairAsync(bobTablet, "Bob's tablet");
        var alicesDeviceBefore = Assert.Single(alice.Authority.Snapshot.Devices).DeviceId;

        // Every session on the relay is long dead. Each desktop is refused, and registers again.
        clock.Advance(TimeSpan.FromDays(3));
        await relay.RestartAsync();
        foreach (var desktop in new[] { alice, bob })
        {
            await desktop.Bridge.PollOnceAsync(CancellationToken.None);
            await desktop.Bridge.PollOnceAsync(CancellationToken.None);
            Assert.Equal(RelayOwnerLinkState.Verified, desktop.Bridge.OwnerLink);
        }

        // Bob's tablet knocks, naming the desktop it pinned. Both desktops are reading their
        // queues; only Bob's is shown the knock, and only Bob's answers it.
        var resumed = await bobTablet.ResumeAsync("Bob's tablet", async () =>
        {
            await alice.Bridge.PollOnceAsync(CancellationToken.None);
            await bob.Bridge.PollOnceAsync(CancellationToken.None);
        });
        Assert.Equal((HttpStatusCode.OK, "resumed"), resumed);
        await alice.Panel.ResumesSettled;
        await bob.Panel.ResumesSettled;
        Assert.Equal(alicesDeviceBefore, Assert.Single(alice.Authority.Snapshot.Devices).DeviceId);
        Assert.Equal("Bob's tablet", Assert.Single(bob.Authority.Snapshot.Devices, device => device.Status == DeviceLifecycleStatus.Active).DisplayName);
        Assert.Equal(3, relay.Desktops.Tenants.Length);
        using var frames = await bobTablet.ReadFramesRawAsync();
        Assert.Equal(HttpStatusCode.OK, frames.StatusCode);
    }

    [Fact]
    public async Task ADesktopWithAWrongGroupKeyIsRefusedAndOneWithNoneIsToldToSetOne()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock, Loopback, certificate: null, GroupKeyOfTheSquad);
        using var strangerDisk = new DesktopDisk();
        using var keylessDisk = new DesktopDisk { DesktopDeviceId = Guid.Parse("10000000-0000-4000-8000-0000000000cc") };

        await using var stranger = await DesktopRun.StartAsync(strangerDisk, relay.Origin, clock, groupKey: "some-other-groups-key");
        Assert.False(stranger.Panel.IsClaimedByThisDesktop);
        Assert.Equal("This relay did not accept your group key.", stranger.Panel.RelayClaimMessage);
        await ((AsyncDelegateCommand)stranger.Panel.StartPairingCommand).ExecuteAsync();
        Assert.False(stranger.Panel.IsAwaitingTablet);
        Assert.Equal("This relay did not accept your group key.", stranger.Panel.StatusMessage);

        await using var keyless = await DesktopRun.StartAsync(keylessDisk, relay.Origin, clock);
        Assert.False(keyless.Panel.IsClaimedByThisDesktop);
        Assert.Equal(CompanionPairingViewModel.GroupKeyNeededMessage, keyless.Panel.RelayClaimMessage);

        // Neither of them became anything on the relay.
        Assert.Single(relay.Desktops.Tenants);
    }

    [Fact]
    public async Task ADesktopRetriesImmediatelyWhenItsFourHourClockSkewIsFixed()
    {
        var relayNow = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var relayClock = new RelayTestClock(relayNow);
        var desktopClock = new RelayTestClock(relayNow.AddHours(4));
        var offset = new RelayClockOffsetTracker();
        await using var relay = await LinkRelay.StartAsync(
            relayClock,
            Loopback,
            certificate: null,
            GroupKeyOfTheSquad);
        using var disk = new DesktopDisk();
        await using var desktop = await DesktopRun.StartAsync(
            disk,
            relay.Origin,
            desktopClock,
            groupKey: GroupKeyOfTheSquad,
            clockOffset: offset);

        Assert.False(desktop.Panel.IsClaimedByThisDesktop);
        Assert.Equal(
            "Your PC clock is 4 h ahead of real time. Pairing won't work until it's fixed.",
            desktop.Panel.ClockSkewNotice);

        desktopClock.Advance(TimeSpan.FromHours(-4));
        offset.ObserveOffsetSeconds(0);
        await LinkWait.UntilAsync(
            () => desktop.Panel.IsClaimedByThisDesktop,
            "the corrected clock to interrupt registration back-off");

        Assert.False(desktop.Panel.HasClockSkewNotice);
        Assert.True(desktop.Panel.IsClaimedByThisDesktop, desktop.Panel.RelayClaimMessage);
    }

    [Fact]
    public async Task ADesktopThatClaimedBeforeTenancyKeepsItsTabletWhenItStartsRegistering()
    {
        // Production today: one desktop claimed the relay with the admin key and paired a tablet.
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock, Loopback, certificate: null, GroupKeyOfTheSquad);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        await using (var olderBuild = await DesktopRun.StartAsync(disk, relay.Origin, clock))
        {
            await olderBuild.ClaimAsync();
            await olderBuild.PairAsync(tablet, "Raid tablet");
        }

        // The relay is redeployed, and the desktop updates to a build that registers. The next
        // morning: the owner session it kept has run out, so its first read is refused and it
        // asks to be let back in, which is now a registration carrying its group key.
        await relay.RestartAsync();
        clock.Advance(TimeSpan.FromHours(14));
        await using var newBuild = await DesktopRun.StartAsync(disk, relay.Origin, clock, groupKey: GroupKeyOfTheSquad);
        await newBuild.Bridge.PollOnceAsync(CancellationToken.None);
        await newBuild.Bridge.PollOnceAsync(CancellationToken.None);

        Assert.Equal(RelayOwnerLinkState.Verified, newBuild.Bridge.OwnerLink);
        Assert.True(newBuild.Panel.IsClaimedByThisDesktop, newBuild.Panel.RelayClaimMessage);
        // The same registry, now this desktop's tenant with its room learned; no second tenant.
        Assert.Single(relay.Desktops.Tenants);
        Assert.NotNull(relay.Desktops.Legacy.Room);
        Assert.Equal(DeviceLifecycleStatus.Active, Assert.Single(newBuild.Authority.Snapshot.Devices).Status);

        // Its tablet comes back on its key with nobody pairing anything again.
        var resumed = await tablet.ResumeAsync("Raid tablet", () => newBuild.Bridge.PollOnceAsync(CancellationToken.None));
        Assert.Equal((HttpStatusCode.OK, "resumed"), resumed);
        using var frames = await tablet.ReadFramesRawAsync();
        Assert.Equal(HttpStatusCode.OK, frames.StatusCode);
    }
}
