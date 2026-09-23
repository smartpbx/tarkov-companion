using System.Net;
using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;
using TarkovCompanion.UnitTests.Updates;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// [#693] A paired tablet whose desktop is not on the relay. On 2026-09-23 the live relay handed
/// such a tablet a resume ticket every minute for twenty minutes: the ticket landed with the
/// desktop the tablet had pinned, that desktop had not had a session since the day before, and
/// the tablet said "Reconnecting to the desktop" throughout while the desktop logged nothing.
/// </summary>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class RelayLinkDesktopOfflineTests
{
    [Fact]
    public async Task ATabletWhoseDesktopIsOffTheRelayIsToldSoAndComesBackOnceTheDesktopIsOn()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        await using (var firstRun = await DesktopRun.StartAsync(disk, relay.Origin, clock))
        {
            await firstRun.ClaimAsync();
            await firstRun.PairAsync(tablet, "Raid tablet");
        }

        // The next day, the desktop is not running. Nobody can answer a ticket, so none is opened.
        clock.Advance(TimeSpan.FromDays(1));
        tablet.Reload();
        Assert.Equal((HttpStatusCode.Conflict, "desktop-offline"), await tablet.ResumeAsync("Raid tablet"));

        // Once it is, the same knock is answered with nothing typed, and the desktop says so.
        var log = new ListLogger();
        await using var secondRun = await DesktopRun.StartAsync(disk, relay.Origin, clock, logger: log);
        await secondRun.Bridge.PollOnceAsync(CancellationToken.None);
        await secondRun.Bridge.PollOnceAsync(CancellationToken.None);
        Assert.Equal(RelayOwnerLinkState.Verified, secondRun.Bridge.OwnerLink);

        var resumed = await tablet.ResumeAsync("Raid tablet", () => secondRun.Bridge.PollOnceAsync(CancellationToken.None));
        Assert.Equal((HttpStatusCode.OK, "resumed"), resumed);
        await secondRun.Panel.ResumesSettled;

        var lines = log.Entries.Select(entry => entry.Message).ToArray();
        Assert.Contains(lines, line => line.Contains("owner session verified", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("resume ticket seen", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("resume offer opened", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("resume answer posted", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("returning device resumed", StringComparison.Ordinal));
        // Never the tablet's name.
        Assert.DoesNotContain(lines, line => line.Contains("Raid tablet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADesktopUnheardForMoreThanTwoMinutesIsOfflineEvenWithALiveSession()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        await using var desktop = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        await desktop.ClaimAsync();
        await desktop.PairAsync(tablet, "Raid tablet");

        // Well inside every session bound; the desktop has simply stopped reading (closed, asleep).
        clock.Advance(RelayCompanionRoutesOfflineWindow + TimeSpan.FromSeconds(1));
        Assert.Equal((HttpStatusCode.Conflict, "desktop-offline"), await tablet.ResumeAsync("Raid tablet"));

        await desktop.Bridge.PollOnceAsync(CancellationToken.None);
        var resumed = await tablet.ResumeAsync("Raid tablet", () => desktop.Bridge.PollOnceAsync(CancellationToken.None));
        Assert.Equal((HttpStatusCode.OK, "resumed"), resumed);
    }

    [Fact]
    public async Task NoMapIsSentWhileNoTabletIsPairedAndTheFirstTabletThatPairsGetsIt()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        using var tablet = new TabletSimulator(relay.Origin, clock);
        await using var desktop = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        await desktop.ClaimAsync();

        // A raid republishes the map several times a second; with no tablet, none of it is sent.
        for (var tick = 1; tick <= 5; tick++)
        {
            Assert.True(await desktop.Bridge.PublishMapSurfaceAsync(
                TabletMapSurfaceJson.Serialize(Surface(clock.GetUtcNow()) with { Revision = tick }),
                artwork: null));
            await desktop.Bridge.PollOnceAsync(CancellationToken.None);
        }

        var maps = relay.Desktops.Legacy.Maps!;
        Assert.Equal(0, maps.Revision);

        await desktop.PairAsync(tablet, "Raid tablet");
        using var read = await tablet.ReadMapRawAsync();
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var surface = TabletMapSurfaceJson.Deserialize(await read.Content.ReadAsByteArrayAsync());
        Assert.Equal(5, surface!.Revision);

        // And once there is a tablet, a change is sent as it always was.
        var before = maps.Revision;
        Assert.True(await desktop.Bridge.PublishMapSurfaceAsync(
            TabletMapSurfaceJson.Serialize(Surface(clock.GetUtcNow()) with { Revision = 6, View = new TabletMapView("1F", 10, 10, 2, null, null) }),
            artwork: null));
        Assert.Equal(before + 1, maps.Revision);
    }

    [Fact]
    public void ARebuildThatOnlyMovedTheRevisionOrTheStampIsNotAChange()
    {
        var now = RelaySecurityTestFactory.Now;
        var first = Surface(now);
        var rebuilt = first with { Revision = 2, PublishedUtc = now.AddMilliseconds(250) };
        var moved = rebuilt with { View = new TabletMapView("1F", 400, 500, 1, null, null) };

        Assert.True(TabletMapSurfaceJson.SerializeVisible(first).AsSpan().SequenceEqual(TabletMapSurfaceJson.SerializeVisible(rebuilt)));
        Assert.False(TabletMapSurfaceJson.SerializeVisible(first).AsSpan().SequenceEqual(TabletMapSurfaceJson.SerializeVisible(moved)));
    }

    [Fact]
    public void TheRelayLinkLogWritesEachKindOfLineAtMostOnceAMinute()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        var logger = new ListLogger();
        var log = new RelayLinkLog(logger, clock);

        log.Write("resume-ticket", "resume ticket seen.");
        log.Write("resume-ticket", "resume ticket seen.");
        log.Write("resume-ticket", "resume ticket seen.");
        log.Write("resume-answer:posted", "resume answer posted.");
        Assert.Equal(2, logger.Entries.Count);

        clock.Advance(RelayLinkLog.Window);
        log.Write("resume-ticket", "resume ticket seen.", LogLevel.Warning);
        var last = logger.Entries.Last();
        Assert.Equal(3, logger.Entries.Count);
        Assert.Equal(LogLevel.Warning, last.Level);
        Assert.Equal("Relay link: resume ticket seen. (2 more like it in the last minute.)", last.Message);
    }

    private static readonly TimeSpan RelayCompanionRoutesOfflineWindow = TarkovCompanion.GroupServer.RelayCompanionRoutes.DesktopOfflineAfter;

    private static TabletMapSurface Surface(DateTimeOffset now) => new(
        Revision: 1,
        MapId: "customs",
        MapName: "Customs",
        VariantKey: "default",
        TransformVersion: "v1",
        Plan: new TabletMapPlan(0, 0, 1000, 1000),
        Artwork: null,
        Attribution: [],
        FloorIds: ["1F"],
        Layers: [new TabletMapLayer("landmarks", "Landmarks", 0, true)],
        Objects: [],
        View: new TabletMapView("1F", 500, 500, 1, null, null),
        Search: null,
        Message: null,
        PublishedUtc: now);
}
