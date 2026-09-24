using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.GroupServer;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;
using TarkovCompanion.UnitTests.RelayLink;

namespace TarkovCompanion.UnitTests.Compatibility;

/// <summary>
/// [#294] The relay's frame queue and resume tickets across builds: what each released tablet page
/// reads from the current relay's replies, and what the current desktop reads from older relays.
/// </summary>
/// <remarks>
/// The tablet page ships inside the relay, but a browser keeps an open page for as long as the
/// tab lives, so the page a player holds can be older than the relay it polls. The read paths are
/// in <c>tablet-reads.json</c>, taken from index.html and relay-crypto.js at each commit. The
/// replies here come from a real relay over HTTP, so the web JSON options it answers with are the
/// ones under test, not a copy of them.
/// </remarks>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class RelayFramesVersionMatrixTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The frame poll every page makes: v2-rough-11 reads without holding, and 53a3b743 and today's
    /// page hold the read (#604). Both answers carry the delivery id and every frame field the
    /// page's own crypto authenticates, plus the reconnect flag and the reset cursor the newer
    /// pages read.
    /// </summary>
    [Fact]
    public async Task TheCurrentRelaysFramePollCarriesWhatEveryPageReads()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        await using var desktop = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        await desktop.ClaimAsync();
        await desktop.PairAsync(tablet, "Raid tablet");
        var reads = CompatibilityFixtures.Node("tablet-reads.json");

        using var plain = await tablet.ReadFramesRawAsync();
        using var held = await tablet.ReadFramesHeldRawAsync(0);
        using var reset = await tablet.ResetFramesRawAsync();

        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal(HttpStatusCode.OK, held.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var unheld = JsonNode.Parse(await plain.Content.ReadAsStringAsync())!;
        var holding = JsonNode.Parse(await held.Content.ReadAsStringAsync())!;
        var cursor = JsonNode.Parse(await reset.Content.ReadAsStringAsync())!;
        Assert.Empty(CompatibilityFixtures.Missing(unheld, Paths(reads["v2-rough-11"]!, "frames")));
        foreach (var page in new[] { "53a3b743", "today" })
        {
            Assert.Empty(CompatibilityFixtures.Missing(holding, Paths(reads[page]!, "frames")));
            Assert.Empty(CompatibilityFixtures.Missing(cursor, Paths(reads[page]!, "framesReset")));
        }

        // What the pages branch on, not only the keys: the held header only on a held read, a
        // number to continue from, and a frame their crypto can open.
        Assert.False(plain.Headers.Contains("X-Relay-Frames-Held"));
        Assert.Equal("1", Assert.Single(held.Headers.GetValues("X-Relay-Frames-Held")));
        Assert.False(holding["requiresReconnect"]!.GetValue<bool>());
        Assert.True(cursor["after"]!.GetValue<long>() >= 0);
        Assert.True(unheld["frames"]![0]!["deliveryId"]!.GetValue<long>() > 0);
        Assert.All(RelayMarksBridge.ParseFrameBatch(unheld.ToJsonString()), frame => Assert.NotNull(frame.Frame));

        // Today's golden batch still describes what this relay writes.
        Assert.Empty(CompatibilityFixtures.Missing(holding, CompatibilityFixtures.PathsOf(CompatibilityFixtures.Node("relay-frames-today.json"))));
    }

    /// <summary>
    /// #846: a resume ticket still carries what the 53a3b743 page reads, and a refusal only adds a
    /// field. That page never reads it and keeps polling a ticket with no code until its own
    /// deadline, which is what it did before; today's page reads it and shows the code form.
    /// </summary>
    [Fact]
    public async Task AResumeTicketCarriesWhatEveryPageReadsAndARefusalOnlyAddsAField()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        await using var desktop = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        await desktop.ClaimAsync();
        await desktop.PairAsync(tablet, "Raid tablet");
        await desktop.Authority.RevokeDeviceAsync(tablet.DeviceId, clock.GetUtcNow(), "test");
        using var http = new HttpClient { BaseAddress = relay.Origin };
        var reads = CompatibilityFixtures.Node("tablet-reads.json");

        var ticketId = await tablet.KnockAsync();
        var waiting = await ReadTicketAsync(http, ticketId);
        await desktop.Bridge.PollOnceAsync(CancellationToken.None);
        await desktop.Panel.ResumesSettled;
        var refused = await ReadTicketAsync(http, ticketId);

        Assert.Empty(CompatibilityFixtures.Missing(waiting, Paths(reads["53a3b743"]!, "resumeTicket")));
        Assert.Empty(CompatibilityFixtures.Missing(refused, Paths(reads["53a3b743"]!, "resumeTicket")));
        Assert.Empty(CompatibilityFixtures.Missing(refused, Paths(reads["today"]!, "resumeTicket")));
        Assert.Null(waiting["refused"]);
        Assert.Null(refused["pairingCode"]);
        Assert.Equal("not-recognised", refused["refused"]!.GetValue<string>());
        Assert.Empty(CompatibilityFixtures.Missing(refused, CompatibilityFixtures.PathsOf(CompatibilityFixtures.Node("relay-ticket-today.json"))));
    }

    /// <summary>
    /// #846: a relay from before the refusal route answers it 404, as the current relay answers a
    /// ticket it does not hold. The current desktop reports "not delivered" and carries on; the
    /// tablet times out as it always did.
    /// </summary>
    [Fact]
    public async Task ARefusalTheRelayDoesNotTakeIsNotAFailureOfTheDesktop()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        await using var desktop = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        await desktop.ClaimAsync();

        Assert.False(await desktop.Bridge.RefuseResumeTicketAsync(Guid.NewGuid(), "failed"));
        // And the next read goes on as normal.
        await desktop.Bridge.PollOnceAsync(CancellationToken.None);
    }

    /// <summary>
    /// The current desktop reads a frame batch from every relay generation: v2-rough-11's names
    /// no returning devices and no held map, today's names both.
    /// </summary>
    [Fact]
    public void TheCurrentDesktopReadsEveryRelaysFrameBatch()
    {
        var older = CompatibilityFixtures.Read("relay-frames-v2-rough-11.json");
        var golden = CompatibilityFixtures.Node("relay-frames-today.json");
        var ticket = Guid.Parse("7c000000-0000-4000-8000-000000000001");
        var batch = golden.AsObject();
        batch["resumeRequests"] = JsonSerializer.SerializeToNode(new[] { new RelayResumeRequest(ticket, "tablet-key-1") }, Web);
        batch["map"] = JsonSerializer.SerializeToNode(new RelayHeldMap(true, 3, new string('c', 64)), Web);
        var today = batch.ToJsonString();

        var olderFrames = RelayMarksBridge.ParseFrameBatch(older);
        var todayFrames = RelayMarksBridge.ParseFrameBatch(today);

        Assert.NotNull(Assert.Single(olderFrames).Frame);
        Assert.Empty(RelayMarksBridge.ParseResumeRequests(older));
        Assert.Equal(
            CompatibilityFixtures.Node("relay-frames-v2-rough-11.json")["frames"]![0]!["deliveryId"]!.GetValue<long>(),
            olderFrames[0].DeliveryId);
        Assert.All(todayFrames, frame => Assert.NotNull(frame.Frame));
        Assert.Equal(golden["frames"]!.AsArray().Count, todayFrames.Count);
        Assert.Equal(new RelayResumeTicket(ticket, "tablet-key-1"), Assert.Single(RelayMarksBridge.ParseResumeRequests(today)));
    }

    private static async Task<JsonNode> ReadTicketAsync(HttpClient http, Guid ticketId)
    {
        using var response = await http.GetAsync($"v2/companion/relay/resume/requests/{ticketId:D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static IEnumerable<string> Paths(JsonNode reads, string part) =>
        reads[part]!.AsArray().Select(path => path!.GetValue<string>());
}
