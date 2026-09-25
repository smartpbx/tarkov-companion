using System.Net;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// [#891] A PC clock hours out is not the player's problem: the desktop measures it from the
/// relay's own Date header and signs at the relay's time, from the first request.
/// </summary>
/// <remarks>
/// The owner's PC dual-boots and starts four hours fast after most boots. The relay refused its
/// registration for whole evenings (7 h on 2026-09-25), so the tablet was off the relay while the
/// app showed the measured offset in Setup and did nothing with it.
/// </remarks>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class RelayLinkPcClockErrorTests
{
    private const string GroupKeyOfTheSquad = "the-squads-own-group-key";
    private static readonly string[] Loopback = ["http://127.0.0.1:0"];

    [Theory]
    [InlineData(4)]
    [InlineData(-4)]
    public async Task ADesktopWhoseClockIsHoursOutRegistersAndPairsWithNobodyTouchingIt(int hours)
    {
        // Whole seconds, because the relay's Date header is: the relay's clock and Kestrel's
        // header then describe the same instant.
        var relayNow = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var relayClock = new RelayTestClock(relayNow);
        var desktopClock = new RelayTestClock(relayNow.AddHours(hours));
        var offset = new RelayClockOffsetTracker(clock: desktopClock);
        await using var relay = await LinkRelay.StartAsync(relayClock, Loopback, certificate: null, GroupKeyOfTheSquad);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, relayClock);
        await using var desktop = await DesktopRun.StartAsync(
            disk,
            relay.Origin,
            desktopClock,
            groupKey: GroupKeyOfTheSquad,
            clockOffset: offset);

        // Registered on the first attempt, with no refusal to learn from first.
        Assert.True(desktop.Panel.IsClaimedByThisDesktop, desktop.Panel.RelayClaimMessage);
        Assert.False(desktop.Panel.NeedsClockFix);
        Assert.Equal(
            hours > 0
                ? "PC clock 4 h ahead of the relay. Corrected automatically."
                : "PC clock 4 h behind the relay. Corrected automatically.",
            desktop.Panel.ClockSkewNotice);

        // The pairing ceremony's invitation, grant and session are all checked by the relay and a
        // tablet on the right clock, and all of them go through.
        await desktop.PairAsync(tablet, "Raid tablet");
        Assert.Single(desktop.Authority.Snapshot.Devices);
        using var frames = await tablet.ReadFramesRawAsync();
        Assert.Equal(HttpStatusCode.OK, frames.StatusCode);
    }

    [Fact]
    public async Task ADesktopStaysOnTheRelayWhenWindowsSetsItsClockMidSession()
    {
        var relayNow = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var relayClock = new RelayTestClock(relayNow);
        var desktopClock = new RelayTestClock(relayNow.AddHours(4));
        var offset = new RelayClockOffsetTracker(clock: desktopClock);
        await using var relay = await LinkRelay.StartAsync(relayClock, Loopback, certificate: null, GroupKeyOfTheSquad);
        using var disk = new DesktopDisk();
        await using var desktop = await DesktopRun.StartAsync(
            disk,
            relay.Origin,
            desktopClock,
            groupKey: GroupKeyOfTheSquad,
            clockOffset: offset);
        Assert.True(desktop.Panel.IsClaimedByThisDesktop, desktop.Panel.RelayClaimMessage);

        // Windows syncs: the wall clock steps back four hours; the monotonic clock does not.
        desktopClock.Advance(TimeSpan.FromHours(-4));

        // The old measurement no longer describes this clock, so it is not applied twice.
        Assert.Equal(TimeSpan.Zero, offset.CorrectionAt(desktopClock.GetUtcNow()));
        await desktop.Bridge.PollOnceAsync(CancellationToken.None);
        Assert.Equal(RelayOwnerLinkState.Verified, desktop.Bridge.OwnerLink);
        Assert.False(offset.Current?.IsSkewed);
        Assert.False(desktop.Panel.HasClockSkewNotice);

        // And a registration made after the step goes through on the clock as it now is.
        await desktop.ClaimAsync(adminKey: string.Empty);
        Assert.Equal(RelayClaimOutcome.Claimed, desktop.LastClaim?.Outcome);
    }
}
