using System.Net;
using System.Net.Http.Headers;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Tablet;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// The day after: the relay's session bounds (twelve hours, two idle) have long run out on both the
/// desktop and the tablet, and neither the admin key nor a pairing code is needed again (#289, #290).
/// </summary>
/// <remarks>
/// The previous attempt at this lengthened the bounds and let the "same desktop" re-claim a live
/// relay, and was reverted: the bounds are what stop an admin-key holder displacing a live owner,
/// and "same desktop" was not something the relay could check. It can check a key. These tests
/// hold both halves: the key holder comes back at any time with nothing typed, and everybody else
/// is treated exactly as before.
/// </remarks>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class RelayLinkNextDayTests
{
    private static readonly TimeSpan ThreeDays = TimeSpan.FromDays(3);

    [Fact]
    public async Task ThreeDaysLaterTheDesktopIsClaimedWithoutTheAdminKeyAndTheTabletResumesWithoutPairing()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);

        await using (var firstRun = await DesktopRun.StartAsync(disk, relay.Origin, clock))
        {
            await firstRun.ClaimAsync();
            Assert.Equal("Paired \"Raid tablet\".", await firstRun.PairAsync(tablet, "Raid tablet"));
            await tablet.ReadAsync();
        }

        var firstDeviceId = tablet.DeviceId;
        clock.Advance(ThreeDays);
        tablet.Reload();

        await using var secondRun = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        // No admin key anywhere below this line. The kept session is three days dead, the relay
        // says so on the first read, and the desktop is let back in on its identity key.
        Assert.True(secondRun.Panel.IsClaimedByThisDesktop);
        await secondRun.Bridge.PollOnceAsync(CancellationToken.None);
        Assert.Equal(RelayOwnerLinkState.Verified, secondRun.Bridge.OwnerLink);
        Assert.True(secondRun.Panel.IsClaimedByThisDesktop, secondRun.Panel.RelayClaimMessage);
        Assert.False(secondRun.Panel.NeedsClaim);
        await secondRun.Bridge.PollOnceAsync(CancellationToken.None);
        Assert.Equal(RelayOwnerLinkState.Verified, secondRun.Bridge.OwnerLink);

        // The tablet's kept session is as dead as the desktop's was.
        using (var stale = await tablet.ReadFramesRawAsync())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        }

        // It proves its key at the relay's door; the desktop sees the ticket on its next read and
        // runs the handshake with nobody at the panel.
        var resumed = await tablet.ResumeAsync("Raid tablet", () => secondRun.Bridge.PollOnceAsync(CancellationToken.None));
        Assert.Equal((HttpStatusCode.OK, "resumed"), resumed);
        Assert.True(tablet.HasRelayCredential);
        await secondRun.Panel.ResumesSettled;
        Assert.True(secondRun.Panel.IsIdle, "Nothing was asked of the player.");
        Assert.Null(secondRun.Panel.PairingCode);

        var device = Assert.Single(secondRun.Authority.Snapshot.Devices);
        Assert.Equal(DeviceLifecycleStatus.Active, device.Status);
        Assert.Equal("Raid tablet", device.DisplayName);
        Assert.NotEqual(firstDeviceId, device.DeviceId);

        // And it is a working link, both ways, on keys that did not exist three days ago.
        await tablet.ReadAsync();
        Assert.NotNull(tablet.AuthorityEpoch);
        using var dropped = await tablet.DropWaypointAsync(12, 34, "Day three");
        Assert.Equal(HttpStatusCode.OK, dropped.StatusCode);
        await secondRun.Bridge.PollOnceAsync(CancellationToken.None);
        Assert.Equal("Day three", Assert.Single(disk.Marks.Marks).State.Label);
    }

    [Fact]
    public async Task TheOwnerComesBackOnItsKeyWhileItsEarlierSessionIsStillLive()
    {
        // "Expired, idle, or still live": the key holder replaces its own session, nobody else's.
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        await using var desktop = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        await desktop.ClaimAsync();
        await desktop.PairAsync(tablet, "Raid tablet");

        clock.Advance(TimeSpan.FromMinutes(1));
        var again = await desktop.ClaimClient.ClaimByKeyAsync(clock.UtcNow, CancellationToken.None);

        Assert.Equal(RelayClaimOutcome.Claimed, again.Outcome);
        await desktop.Bridge.PollOnceAsync(CancellationToken.None);
        Assert.Equal(RelayOwnerLinkState.Verified, desktop.Bridge.OwnerLink);
        // The tablet was not part of that: its session is the one it had.
        using var still = await tablet.ReadFramesRawAsync();
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }

    [Fact]
    public async Task AnotherDesktopsKeyIsRefusedAndIsLeftWithTheAdminKeyRuleAsItWas()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var ownerDisk = new DesktopDisk();
        using var strangerDisk = new DesktopDisk { DesktopDeviceId = Guid.Parse("10000000-0000-4000-8000-0000000000bb") };
        await using var owner = await DesktopRun.StartAsync(ownerDisk, relay.Origin, clock);
        await owner.ClaimAsync();

        await using var stranger = await DesktopRun.StartAsync(strangerDisk, relay.Origin, clock);
        var byKey = await stranger.ClaimClient.ClaimByKeyAsync(clock.UtcNow, CancellationToken.None);
        Assert.Equal(RelayClaimOutcome.KeyNotRecognised, byKey.Outcome);
        Assert.Equal("owner-key-mismatch", byKey.Code);

        // Pressing Claim with nothing typed says what is needed rather than claiming anything.
        await stranger.ClaimAsync(adminKey: string.Empty);
        Assert.False(stranger.Panel.IsClaimedByThisDesktop);
        Assert.Contains("admin key", stranger.Panel.RelayClaimMessage, StringComparison.Ordinal);

        // With the admin key it is where it always was: a live owner is not displaced.
        await stranger.ClaimAsync();
        Assert.False(stranger.Panel.IsClaimedByThisDesktop);
        Assert.Equal(RelayOwnerClaimState.ClaimedByAnotherDesktop, stranger.Panel.RelayClaimState);

        // Three days on the owner is long expired, and the admin key takes the relay, as before.
        clock.Advance(ThreeDays);
        await stranger.ClaimAsync();
        Assert.True(stranger.Panel.IsClaimedByThisDesktop, stranger.Panel.RelayClaimMessage);

        // Which makes the first desktop the stranger: the key on record is no longer its own.
        var displaced = await owner.ClaimClient.ClaimByKeyAsync(clock.UtcNow, CancellationToken.None);
        Assert.Equal(RelayClaimOutcome.KeyNotRecognised, displaced.Outcome);
    }

    [Fact]
    public async Task AClaimThatDoesNotAnswerThisRelaysNonceIsRefused()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        await using var desktop = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        await desktop.ClaimAsync();
        using var http = new HttpClient { BaseAddress = relay.Origin };

        // The right key, signing a nonce it made up itself.
        var selfChosen = DesktopRelayOwnerClaim.Build(disk.Signer, new CompanionDeviceId(disk.DesktopDeviceId), clock.UtcNow);
        using (var refused = await PostClaimAsync(http, selfChosen))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("challenge-rejected", (await refused.Content.ReadAsStringAsync()).Trim('"'));
        }

        // The right key and the relay's nonce, sent twice: the second is a replay.
        using var asked = await http.PostAsync("v2/companion/relay/possession/challenge", null);
        using var issued = System.Text.Json.JsonDocument.Parse(await asked.Content.ReadAsStringAsync());
        var nonce = issued.RootElement.GetProperty("nonceBase64Url").GetString()!;
        var answered = DesktopRelayOwnerClaim.Build(disk.Signer, new CompanionDeviceId(disk.DesktopDeviceId), clock.UtcNow, nonce);
        using (var accepted = await PostClaimAsync(http, answered))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using var replayed = await PostClaimAsync(http, answered);
        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);
        Assert.Equal("challenge-rejected", (await replayed.Content.ReadAsStringAsync()).Trim('"'));
    }

    [Fact]
    public async Task ATabletTheRelayHasNeverSeenIsRefusedAtTheDoor()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var paired = new TabletSimulator(relay.Origin, clock);
        using var stranger = new TabletSimulator(relay.Origin, clock);
        await using var desktop = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        await desktop.ClaimAsync();
        await desktop.PairAsync(paired, "Raid tablet");

        var refused = await stranger.ResumeAsync("Stranger");

        Assert.Equal((HttpStatusCode.Forbidden, "device-unknown"), refused);
        Assert.False(stranger.HasRelayCredential);
    }

    [Fact]
    public async Task ARevokedTabletIsStillRefusedThreeDaysLater()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        await using (var firstRun = await DesktopRun.StartAsync(disk, relay.Origin, clock))
        {
            await firstRun.ClaimAsync();
            await firstRun.PairAsync(tablet, "Raid tablet");
            var row = Assert.Single(firstRun.Panel.Devices);
            await ((AsyncDelegateCommand)row.RevokeCommand).ExecuteAsync();
            await ((AsyncDelegateCommand)row.RevokeCommand).ExecuteAsync();
        }

        clock.Advance(ThreeDays);
        await using var secondRun = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        await secondRun.Bridge.PollOnceAsync(CancellationToken.None);
        Assert.Equal(RelayOwnerLinkState.Verified, secondRun.Bridge.OwnerLink);

        var refused = await tablet.ResumeAsync("Raid tablet", () => secondRun.Bridge.PollOnceAsync(CancellationToken.None));

        Assert.Equal((HttpStatusCode.Forbidden, "device-revoked"), refused);
        Assert.Equal(DeviceLifecycleStatus.Revoked, Assert.Single(secondRun.Authority.Snapshot.Devices).Status);
    }

    [Fact]
    public async Task ATabletRevokedAfterItHadAlreadyGoneQuietIsRefusedToo()
    {
        // The relay used to refuse to revoke a device it no longer counted as live, which was
        // harmless only while such a device could not come back without the owner's approval.
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        await using (var firstRun = await DesktopRun.StartAsync(disk, relay.Origin, clock))
        {
            await firstRun.ClaimAsync();
            await firstRun.PairAsync(tablet, "Raid tablet");
        }

        clock.Advance(ThreeDays);
        await using var secondRun = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        await secondRun.Bridge.PollOnceAsync(CancellationToken.None);
        using (var stale = await tablet.ReadFramesRawAsync())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode); // and the relay marks it expired
        }

        var row = Assert.Single(secondRun.Panel.Devices);
        await secondRun.Bridge.RevokePairedDeviceAsync(row.Device.DeviceId, CancellationToken.None);

        var refused = await tablet.ResumeAsync("Raid tablet", () => secondRun.Bridge.PollOnceAsync(CancellationToken.None));
        Assert.Equal((HttpStatusCode.Forbidden, "device-revoked"), refused);
    }

    private static async Task<HttpResponseMessage> PostClaimAsync(HttpClient relay, DesktopRelayOwnerClaimMaterial material)
    {
        var content = new ByteArrayContent(material.ToJsonBody());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await relay.PostAsync("v2/companion/relay/owner/resume", content);
    }
}
