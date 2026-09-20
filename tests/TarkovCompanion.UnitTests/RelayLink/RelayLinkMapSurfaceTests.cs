using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// What a tablet is shown of the desktop's map, over a real in-process relay (#407): whether the
/// desktop is there, and whether the relay holds what the desktop last published.
/// </summary>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class RelayLinkMapSurfaceTests
{
    private static readonly byte[] Surface = Encoding.UTF8.GetBytes("{\"mapName\":\"Customs\",\"publishedUtc\":\"2026-01-01T00:00:00Z\"}");

    // The relay checks the picture against the hash it is declared under and nothing else about it.
    private static readonly TabletMapArtworkBytes Artwork = ArtworkOf([0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5, 6, 7, 8]);

    [Fact]
    public async Task ADesktopSittingStillOnOneMapIsNotCalledOffline()
    {
        // The tablet called a map older than twenty seconds "The desktop is offline", and a desktop
        // publishes only when something changes. What says the desktop is there is that it reads
        // its queue every two seconds, so the relay reports how long ago it last did.
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        await using var desktop = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        await desktop.ClaimAsync();
        await desktop.PairAsync(tablet, "Raid tablet");
        Assert.True(await desktop.Bridge.PublishMapSurfaceAsync(Surface, null));

        // Ten quiet minutes: nothing published, the queue still read.
        clock.Advance(TimeSpan.FromMinutes(10));
        await desktop.Bridge.PollOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        using (var quiet = await tablet.ReadMapRawAsync())
        {
            Assert.Equal(HttpStatusCode.OK, quiet.StatusCode);
            Assert.Equal(1000, OwnerSeenMs(quiet));
        }

        // And a desktop that really has gone: same map, no reads.
        clock.Advance(TimeSpan.FromSeconds(44));
        using var gone = await tablet.ReadMapRawAsync();
        Assert.Equal(45_000, OwnerSeenMs(gone));
    }

    [Fact]
    public async Task TheRelaySaysTheDesktopIsThereEvenBeforeItHasAMapOpen()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        await using var desktop = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        await desktop.ClaimAsync();
        await desktop.PairAsync(tablet, "Raid tablet");
        await desktop.Bridge.PollOnceAsync(CancellationToken.None);

        using var nothingYet = await tablet.ReadMapRawAsync();

        Assert.Equal(HttpStatusCode.NotFound, nothingYet.StatusCode);
        Assert.Equal(0, OwnerSeenMs(nothingYet));
    }

    [Fact]
    public async Task AMapPublishedBeforeTheRelayWasClaimedReachesTheTabletOnceItIs()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        await using var desktop = await DesktopRun.StartAsync(disk, relay.Origin, clock);

        // The player opened a map first and claimed afterwards. Nothing could carry it, and the
        // publisher is told so rather than being left to believe it went.
        Assert.False(await desktop.Bridge.PublishMapSurfaceAsync(Surface, Artwork));

        await desktop.ClaimAsync();
        await desktop.PairAsync(tablet, "Raid tablet");
        await desktop.Bridge.PollOnceAsync(CancellationToken.None);

        using var map = await tablet.ReadMapRawAsync();
        Assert.Equal(HttpStatusCode.OK, map.StatusCode);
        Assert.Equal(Surface, await map.Content.ReadAsByteArrayAsync());
        using var picture = await tablet.ReadArtworkRawAsync();
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Equal(Artwork.Bytes, await picture.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AfterARelayRestartTheMapAndItsPictureAreUploadedAgain()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        await using var desktop = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        await desktop.ClaimAsync();
        await desktop.PairAsync(tablet, "Raid tablet");
        Assert.True(await desktop.Bridge.PublishMapSurfaceAsync(Surface, Artwork));
        using (var before = await tablet.ReadArtworkRawAsync())
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        await relay.RestartAsync();
        using (var emptied = await tablet.ReadMapRawAsync())
        {
            Assert.Equal(HttpStatusCode.NotFound, emptied.StatusCode); // the relay keeps maps in memory
        }

        // Nothing changed on the desktop, so nothing is published. Its next read of its queue is
        // where it learns the relay holds nothing, and it puts back the map and the picture.
        await desktop.Bridge.PollOnceAsync(CancellationToken.None);

        using var map = await tablet.ReadMapRawAsync();
        Assert.Equal(HttpStatusCode.OK, map.StatusCode);
        Assert.Equal(Surface, await map.Content.ReadAsByteArrayAsync());
        using var picture = await tablet.ReadArtworkRawAsync();
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Equal(Artwork.Bytes, await picture.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AQueueThatOverflowedStopsSayingReconnectOnceItsReaderHasSeenTheGap()
    {
        // The hub could always clear a queue that had dropped frames, and no route asked it to: the
        // relay then answered every read with "reconnect" for as long as the session lived, and a
        // tablet met each one with a request for a full resync.
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        await using var desktop = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        await desktop.ClaimAsync();
        await desktop.PairAsync(tablet, "Raid tablet");
        await tablet.ReadAsync();

        // More than the desktop's queue holds (64), with the desktop not reading.
        for (var index = 0; index < 70; index++)
        {
            using var sent = await tablet.DropWaypointAsync(index, index, "Flood " + index);
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        }

        var owner = await new RelayLinkVault(disk.Secrets)
            .LoadOwnerAsync(new CompanionDeviceId(disk.DesktopDeviceId), relay.Origin, CancellationToken.None);
        Assert.NotNull(owner);
        Assert.True(await RequiresReconnectAsync(relay.Origin, owner!), "the overflow is what sets the flag");

        // Reads what is left, then sees an empty batch still saying "reconnect" and answers it.
        for (var reads = 0; reads < 5; reads++)
        {
            await desktop.Bridge.PollOnceAsync(CancellationToken.None);
        }

        Assert.False(await RequiresReconnectAsync(relay.Origin, owner!));

        // And the link still carries, both ways: the tablet asks where things are and is told.
        var requestId = await tablet.RequestResyncAsync();
        await desktop.Bridge.PollOnceAsync(CancellationToken.None);
        // The tablet's own queue is as full as the desktop's was (every refused mark was answered),
        // and a read hands over thirty-two at a time.
        var answers = new List<RelayPayload>();
        for (var reads = 0; reads < 4; reads++)
        {
            answers.AddRange(await tablet.ReadAsync());
        }

        Assert.Contains(
            answers.Where(payload => payload.Kind == RelayPayloadKind.ReconnectPlan)
                .Select(payload => CompanionProtocolJson.Deserialize<ReconnectPlan>(payload.Json.Span)),
            plan => plan.RequestId == requestId);
    }

    private static async Task<bool> RequiresReconnectAsync(Uri origin, StoredRelayOwnerSession owner)
    {
        using var http = new HttpClient { BaseAddress = origin };
        // Exactly where a reader that has seen all seventy stands, so the answer is about the
        // queue's state and not about its frames or the cursor.
        using var request = new HttpRequestMessage(HttpMethod.Get, "v2/companion/relay/frames?after=70");
        request.Headers.Add("X-Relay-Session", owner.SessionId.ToString("D"));
        request.Headers.Add("X-Relay-Credential", owner.Credential);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var batch = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return batch.RootElement.GetProperty("requiresReconnect").GetBoolean();
    }

    private static long OwnerSeenMs(HttpResponseMessage response) =>
        long.Parse(Assert.Single(response.Headers.GetValues("X-Relay-Owner-Seen-Ms")), System.Globalization.CultureInfo.InvariantCulture);

    private static TabletMapArtworkBytes ArtworkOf(byte[] bytes) =>
        new("image/png", Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes);
}
