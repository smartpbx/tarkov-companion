using System.Net;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// The desktop, the relay and a tablet, end to end over real HTTP: claim, restart the desktop,
/// still claimed, and the tablet picks its session back up (#289, #290).
/// </summary>
/// <remarks>
/// The report was "you have to re-claim the relay on every restart". The owner session and every
/// paired session's keys lived in one process's memory, so a restart needed the relay's admin key
/// typed again and then every tablet paired again from scratch. A restart here is what it is on a
/// real machine: every object of the first run is disposed, and a second run is built over nothing
/// but what the first one wrote — the authority's file, and protected storage.
/// </remarks>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class RelayLinkSurvivesRestartTests
{
    [Fact]
    public async Task AClaimSurvivesADesktopRestartAndTheTabletReconnectsWithoutPairingAgain()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);

        await using (var firstRun = await DesktopRun.StartAsync(disk, relay.Origin, clock))
        {
            // [#553] Nothing has been claimed yet. This harness desktop has no group key and
            // speaks the older claim protocol below; what no longer holds is that pairing is
            // blocked until somebody claims: the button registers the desktop when pressed.
            Assert.True(firstRun.Panel.NeedsClaim);
            Assert.True(firstRun.Panel.CanStartPairing);
            await firstRun.ClaimAsync();
            Assert.True(firstRun.Panel.IsClaimedByThisDesktop, firstRun.Panel.RelayClaimMessage);
            Assert.Equal(RelayOwnerLinkState.Verified, firstRun.Bridge.OwnerLink);

            Assert.Equal("Paired \"Raid tablet\".", await firstRun.PairAsync(tablet, "Raid tablet"));
            Assert.True(tablet.HasRelayCredential);
            await tablet.ReadAsync();
            Assert.NotNull(tablet.AuthorityEpoch);
        }

        // Inside the protocol's own bounds: a session lives twelve hours and a device two idle.
        clock.Advance(TimeSpan.FromMinutes(45));
        tablet.Reload();

        await using var secondRun = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        // No admin key anywhere below this line. The panel reads claimed as soon as the run is
        // up, and the relay agrees the first time it is asked.
        Assert.True(secondRun.Panel.IsClaimedByThisDesktop);
        Assert.False(secondRun.Panel.NeedsClaim);
        Assert.True(secondRun.Panel.CanStartPairing);
        await secondRun.Bridge.PollOnceAsync(CancellationToken.None);
        Assert.Equal(RelayOwnerLinkState.Verified, secondRun.Bridge.OwnerLink);
        Assert.True(secondRun.Panel.IsClaimedByThisDesktop);
        Assert.Single(secondRun.Panel.Devices);

        // The reloaded tablet holds no canonical state, so it asks; the restarted desktop can only
        // answer if it still has this session's traffic keys, and the relay only carries the
        // answer if its sender sequence is above everything the first run sent.
        var requestId = await tablet.RequestResyncAsync();
        await secondRun.Bridge.PollOnceAsync(CancellationToken.None);
        var answers = await tablet.ReadAsync();
        var plan = Assert.Single(
            answers.Where(payload => payload.Kind == RelayPayloadKind.ReconnectPlan)
                .Select(payload => CompanionProtocolJson.Deserialize<ReconnectPlan>(payload.Json.Span)),
            candidate => candidate.RequestId == requestId);
        Assert.Equal(ReconnectDisposition.FullSnapshot, plan.Disposition);
        Assert.NotNull(plan.Snapshot);

        // And the link is live in the other direction too: a waypoint dropped on the tablet
        // after the restart lands on the desktop's map.
        tablet.AdoptSnapshot(plan.Snapshot!);
        using var dropped = await tablet.DropWaypointAsync(12, 34, "After restart");
        Assert.Equal(HttpStatusCode.OK, dropped.StatusCode);
        await secondRun.Bridge.PollOnceAsync(CancellationToken.None);
        Assert.Equal("After restart", Assert.Single(disk.Marks.Marks).State.Label);
    }

    [Fact]
    public async Task WithoutProtectedStorageTheRestartForgetsTheClaim()
    {
        // The behaviour this package replaced, kept as the control: the same two runs with
        // nowhere protected to keep anything come back unclaimed.
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        await using (var firstRun = await DesktopRun.StartAsync(disk, relay.Origin, clock, protectedStorage: false))
        {
            await firstRun.ClaimAsync();
            Assert.True(firstRun.Panel.IsClaimedByThisDesktop, firstRun.Panel.RelayClaimMessage);
        }

        await using var secondRun = await DesktopRun.StartAsync(disk, relay.Origin, clock, protectedStorage: false);
        // [#553] Nothing was kept, and nothing needed to be: the desktop comes back on its
        // identity key at startup with nothing typed. This used to end at the admin-key prompt.
        Assert.True(secondRun.Panel.IsClaimedByThisDesktop, secondRun.Panel.RelayClaimMessage);
        Assert.False(secondRun.Panel.NeedsClaim);
        Assert.Equal(0, disk.Secrets.Count);
    }

    [Fact]
    public async Task RevokingOnTheDesktopCutsTheTabletOffAtTheRelayAndForgetsItsKeys()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        await using var desktop = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        await desktop.ClaimAsync();
        await desktop.PairAsync(tablet, "Raid tablet");
        using (var before = await tablet.ReadFramesRawAsync())
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        Assert.Contains(IntegrationSecretKind.PairedDeviceSession, disk.Secrets.Kinds);

        // Revoke, then Confirm revoke: the row asks first because it cannot be undone.
        var row = Assert.Single(desktop.Panel.Devices);
        await ((AsyncDelegateCommand)row.RevokeCommand).ExecuteAsync();
        await ((AsyncDelegateCommand)row.RevokeCommand).ExecuteAsync();
        Assert.Equal(DeviceLifecycleStatus.Revoked, Assert.Single(desktop.Authority.Snapshot.Devices).Status);

        // The tablet's kept credential is the thing a revoke has to kill: it is what a reloaded
        // page would come back with.
        using var after = await tablet.ReadFramesRawAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.DoesNotContain(IntegrationSecretKind.PairedDeviceSession, disk.Secrets.Kinds);
    }

    [Fact]
    public async Task TheSameTabletCanBePairedAgainAndReplacesItsOldRecord()
    {
        // A browser keeps one device key for good. The second pairing of the same tablet used to
        // throw on the desktop ("identity uniqueness") and be refused by the relay as a duplicate,
        // so a tablet that had lost its page, expired or been revoked could never come back.
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        await using var desktop = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        await desktop.ClaimAsync();
        await desktop.PairAsync(tablet, "Raid tablet");
        var firstDeviceId = tablet.DeviceId;

        clock.Advance(TimeSpan.FromMinutes(1));
        var again = await desktop.PairAsync(tablet, "Raid tablet");

        Assert.Equal("Paired \"Raid tablet\".", again);
        Assert.True(tablet.HasRelayCredential);
        var device = Assert.Single(desktop.Authority.Snapshot.Devices);
        Assert.NotEqual(firstDeviceId, device.DeviceId);
        Assert.Equal(DeviceLifecycleStatus.Active, device.Status);
        await tablet.ReadAsync();
        Assert.NotNull(tablet.AuthorityEpoch);
    }

    [Fact]
    public async Task ForgettingTheRelayClearsTheKeptClaimAndTheSameDesktopCanClaimAgainAtOnce()
    {
        // [#289] This test used to end with the relay refusing this same desktop for two hours
        // ("owner-already-live"): forgetting dropped the session here while the relay still counted
        // its owner live, and the relay had no way to tell that owner coming back from a stranger
        // with the admin key. It has one now — the key the desktop first claimed with — so the
        // refusal this pinned is gone for the key holder and only for the key holder
        // (RelayLinkNextDayTests.AnotherDesktopsKeyIsRefused... holds the other half).
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        await using (var firstRun = await DesktopRun.StartAsync(disk, relay.Origin, clock))
        {
            await firstRun.ClaimAsync();
            await firstRun.PairAsync(tablet, "Raid tablet");
            // [#553] There is no "Forget this relay" button now that there is no claim to drop;
            // the kept session is dropped where the button used to drop it.
            await firstRun.Bridge.ForgetOwnerAsync();
            Assert.DoesNotContain(IntegrationSecretKind.RelayOwnerSession, disk.Secrets.Kinds);
        }

        clock.Advance(TimeSpan.FromMinutes(5));
        await using var secondRun = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        // [#553] Forgetting drops the kept session and nothing else. There is no claim for the
        // next run to wait to be asked for: it is back on its key at startup, five minutes after
        // forgetting, with nothing typed.
        Assert.True(secondRun.Panel.IsClaimedByThisDesktop, secondRun.Panel.RelayClaimMessage);
        Assert.False(secondRun.Panel.NeedsClaim);
        await secondRun.Bridge.PollOnceAsync(CancellationToken.None);
        Assert.Equal(RelayOwnerLinkState.Verified, secondRun.Bridge.OwnerLink);

        // And the tablet paired before the forget was not swept away by claiming again.
        using var still = await tablet.ReadFramesRawAsync();
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }
}
