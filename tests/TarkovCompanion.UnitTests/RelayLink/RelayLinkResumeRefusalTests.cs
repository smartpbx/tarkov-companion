using System.Net;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;
using TarkovCompanion.UnitTests.Updates;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// [#846] A desktop that will not let a returning tablet back in says so on the relay. Before,
/// it gave up only on its own side: the ticket stayed unanswered for a minute, or a handshake step
/// sat for 30 s, and the tablet showed "Reconnecting" throughout before it offered the code form.
/// </summary>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class RelayLinkResumeRefusalTests
{
    [Fact]
    public async Task ATabletTheDesktopRevokedOnlyLocallyIsToldAtOnce()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        var log = new ListLogger();
        await using var desktop = await DesktopRun.StartAsync(disk, relay.Origin, clock, logger: log);
        await desktop.ClaimAsync();
        await desktop.PairAsync(tablet, "Raid tablet");

        // Revoked here while the relay never heard (the revoke call failed, or the desktop was
        // offline): the relay still lets the tablet knock, and only the desktop can say no.
        await desktop.Authority.RevokeDeviceAsync(tablet.DeviceId, clock.GetUtcNow(), "test");

        var answer = await tablet.ResumeAsync("Raid tablet", () => desktop.Bridge.PollOnceAsync(CancellationToken.None));

        Assert.Equal((HttpStatusCode.OK, "refused:not-recognised"), answer);
        await desktop.Panel.ResumesSettled;
        Assert.Contains(log.Entries, entry => entry.Message.Contains("resume refusal posted (not-recognised)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHandshakeThatFailsAfterTheTicketWasAnsweredIsReportedToo()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        using var stranger = new TabletSimulator(relay.Origin, clock);
        await using var desktop = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        await desktop.ClaimAsync();
        await desktop.PairAsync(tablet, "Raid tablet");

        var ticketId = await tablet.KnockAsync();
        var code = await UntilAsync(async () =>
        {
            await desktop.Bridge.PollOnceAsync(CancellationToken.None);
            return (await tablet.ReadResumeTicketAsync(ticketId)).Code;
        });

        // Somebody else's key answers the offer opened for this tablet: the desktop stops, and
        // the tablet the ticket belongs to reads why instead of timing out on the next step.
        await stranger.SubmitPairingRequestAsync(code, "Stranger");
        var refused = await UntilAsync(async () => (await tablet.ReadResumeTicketAsync(ticketId)).Refused);

        Assert.Equal("not-recognised", refused);
        await desktop.Panel.ResumesSettled;
    }

    [Fact]
    public async Task AnAttemptGoneAtTheDeadlineIsAFailureTheTabletKeepsItsPairingThrough()
    {
        // [#936] The coordinator throws UnauthorizedAccessException for an attempt it pruned at
        // the offer's deadline, and every one of those was refused as not-recognised: the tablet
        // then deleted a pairing that was still good. Only a wrong device key says that now.
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        await using var desktop = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        await desktop.ClaimAsync();
        await desktop.PairAsync(tablet, "Raid tablet");

        var ticketId = await tablet.KnockAsync();
        var code = await UntilAsync(async () =>
        {
            await desktop.Bridge.PollOnceAsync(CancellationToken.None);
            return (await tablet.ReadResumeTicketAsync(ticketId)).Code;
        });

        // The right tablet answers, but the desktop's attempt is gone by the time it reads it.
        await tablet.SubmitPairingRequestAsync(
            code,
            "Raid tablet",
            // Any coordinator call at the deadline prunes it, as the desktop's own next one would.
            offer => desktop.Coordinator.AcknowledgeRelayResolvedCodeAsync(
                new PairingAttemptId(Guid.NewGuid()), offer.ExpiresUtc).AsTask());
        var refused = await UntilAsync(async () => (await tablet.ReadResumeTicketAsync(ticketId)).Refused);

        Assert.Equal("failed", refused);
        await desktop.Panel.ResumesSettled;
    }

    [Fact]
    public async Task OnlyTheOwnerCanRefuseATicket()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var http = new HttpClient { BaseAddress = relay.Origin };

        using var anonymous = await http.PostAsync($"v2/companion/relay/resume/requests/{Guid.NewGuid():D}/refusal?reason=failed", null);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    private static async Task<string> UntilAsync(Func<Task<string?>> read)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            if (await read() is { } value)
            {
                return value;
            }

            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting on the resume ticket.");
            await Task.Delay(20);
        }
    }
}
