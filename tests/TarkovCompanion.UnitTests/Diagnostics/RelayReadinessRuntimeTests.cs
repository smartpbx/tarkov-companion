using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TarkovCompanion.GroupServer;
using TarkovCompanion.GroupServer.Diagnostics;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.Diagnostics;

/// <summary>
/// The measurements behind <see cref="RelayReadiness"/>, which the pure model was written to consume
/// and nothing fed it, and the operator route that reports them.
/// </summary>
/// <remarks>
/// <c>RelayReadinessTests</c> proves the verdict for a given probe. These prove the probe is real:
/// each number is measured from something this process can observe and moves when that thing does.
/// The route's wiring in the running relay, and the operator key that guards it, are in
/// <c>RunningRelayReadinessTests</c>, because that key is process-wide state no test here may set.
/// </remarks>
public sealed class RelayReadinessRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay-readiness-" + Guid.NewGuid().ToString("N"));

    public RelayReadinessRuntimeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory the OS will reclaim.
        }
    }

    [Fact]
    public void RateLimitAndRejectedInputAreCountedInTheExactWindowAndAgeOutOfIt()
    {
        var clock = new RelayTestClock(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
        var counters = new RelayOperationalCounters(clock);

        counters.Observe(429);
        counters.Observe(400);
        counters.Observe(413);
        counters.Observe(200);
        clock.Advance(TimeSpan.FromMinutes(4));
        counters.Observe(415);

        var inWindow = counters.Snapshot(clock.GetUtcNow());
        Assert.Equal(1, inWindow.RateLimited);
        Assert.Equal(3, inWindow.RejectedInput);

        // The first three left the window at five minutes; the fourth is still in it.
        clock.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1)));
        var later = counters.Snapshot(clock.GetUtcNow());
        Assert.Equal(0, later.RateLimited);
        Assert.Equal(1, later.RejectedInput);
    }

    [Fact]
    public void OnlyAServerFaultCountsAsAFailureAndAnyOtherAnswerEndsTheRun()
    {
        var counters = new RelayOperationalCounters(new RelayTestClock(DateTimeOffset.UnixEpoch.AddDays(30000)));

        for (var i = 0; i < 10; i++)
        {
            counters.Observe(400);
            counters.Observe(401);
            counters.Observe(429);
        }

        Assert.Equal(0, counters.Snapshot(DateTimeOffset.UnixEpoch.AddDays(30000)).ConsecutiveFailures);
        counters.Observe(500);
        counters.Observe(503);
        Assert.Equal(2, counters.Snapshot(DateTimeOffset.UnixEpoch.AddDays(30000)).ConsecutiveFailures);
        counters.Observe(200);
        Assert.Equal(0, counters.Snapshot(DateTimeOffset.UnixEpoch.AddDays(30000)).ConsecutiveFailures);
    }

    [Fact]
    public void AFloodOfRejectionsCannotGrowTheCountersWithoutBound()
    {
        var clock = new RelayTestClock(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
        var counters = new RelayOperationalCounters(clock);

        for (var i = 0; i < RelayOperationalCounters.MaximumTrackedObservations * 3; i++)
        {
            counters.Observe(429);
        }

        Assert.Equal(RelayOperationalCounters.MaximumTrackedObservations, counters.Snapshot(clock.GetUtcNow()).RateLimited);
    }

    [Fact]
    public void AWallClockThatJumpsUnderTheRelayShowsAsDrift()
    {
        var clock = new RelayTestClock(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
        var counters = new RelayOperationalCounters(clock);
        Assert.InRange(counters.ClockDriftSeconds(), 0, 1);

        // The test clock moves the wall clock and not the monotonic one, which is exactly a jump.
        clock.Advance(TimeSpan.FromHours(1));

        Assert.InRange(counters.ClockDriftSeconds(), 3599, 3601);
    }

    [Fact]
    public void ARelayWithNoStateDirectoryAndNoUpdaterIsReadyRatherThanPermanentlyDegraded()
    {
        var clock = new RelayTestClock(DateTimeOffset.UtcNow);
        var probes = new RelayReadinessProbes(clock, null, new RelayUpdate(null, null), new RelayOperationalCounters(clock), buildKnown: true);

        var report = probes.Capture();

        Assert.Equal(RelayReadinessStatus.Ready, report.Result.Status);
        Assert.False(report.StorageIsPersistent);
        Assert.False(report.UpdaterConfigured);
    }

    [Fact]
    public void StorageIsProbedByWritingAndReadingBackAndLeavesNothingBehind()
    {
        var state = Path.Combine(_root, "state");
        var clock = new RelayTestClock(DateTimeOffset.UtcNow);
        var probes = new RelayReadinessProbes(clock, state, new RelayUpdate(state, null), new RelayOperationalCounters(clock), buildKnown: true);

        var report = probes.Capture();

        Assert.True(Check(report, RelayReadinessCheck.Storage));
        Assert.True(report.StorageIsPersistent);
        Assert.True(report.Probe.FreeDiskMegabytes > 0, "a real volume reports its free space");
        Assert.Empty(Directory.GetFileSystemEntries(state));
    }

    [Fact]
    public void AStateDirectoryThatCannotBeWrittenDegradesStorage()
    {
        // A file where the directory should be: creating it fails the way a read-only volume does.
        var blocker = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(blocker, "x");
        var state = Path.Combine(blocker, "state");
        var clock = new RelayTestClock(DateTimeOffset.UtcNow);
        var probes = new RelayReadinessProbes(clock, state, new RelayUpdate(state, null), new RelayOperationalCounters(clock), buildKnown: true);

        var report = probes.Capture();

        Assert.False(Check(report, RelayReadinessCheck.Storage));
        Assert.Equal(RelayReadinessStatus.Degraded, report.Result.Status);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(89, true)]
    [InlineData(200, false)]
    public void TheUpdaterCheckIsTheAgeOfTheLastAuthenticatedDecisionNotOfTheLastRelease(int ageMinutes, bool ready)
    {
        var now = DateTimeOffset.UtcNow;
        var (state, status) = UpdaterDirectories();
        var stamp = Path.Combine(status, "PUBLISHED_SHA256");
        File.WriteAllText(stamp, new string('a', 64) + "\n");
        File.SetLastWriteTimeUtc(stamp, now.AddMinutes(-ageMinutes).UtcDateTime);
        var clock = new RelayTestClock(now);
        var probes = new RelayReadinessProbes(clock, state, new RelayUpdate(state, status), new RelayOperationalCounters(clock), buildKnown: true);

        var report = probes.Capture();

        Assert.True(report.UpdaterConfigured);
        Assert.Equal(ready, Check(report, RelayReadinessCheck.UpdaterCheck));
    }

    [Fact]
    public void AnUpdaterThatHasNeverRecordedACheckIsNotReadyButNoUpdaterAtAllIsNotApplicable()
    {
        var (state, status) = UpdaterDirectories();
        var clock = new RelayTestClock(DateTimeOffset.UtcNow);
        var configured = new RelayReadinessProbes(clock, state, new RelayUpdate(state, status), new RelayOperationalCounters(clock), buildKnown: true);
        var absent = new RelayReadinessProbes(clock, state, new RelayUpdate(state, null), new RelayOperationalCounters(clock), buildKnown: true);

        Assert.False(Check(configured.Capture(), RelayReadinessCheck.UpdaterCheck));
        Assert.True(Check(absent.Capture(), RelayReadinessCheck.UpdaterCheck));
    }

    [Fact]
    public void AnUnknownBuildAndARunOfServerFaultsEachDegradeTheirOwnCheck()
    {
        var clock = new RelayTestClock(DateTimeOffset.UtcNow);
        var counters = new RelayOperationalCounters(clock);
        for (var i = 0; i < RelayReadiness.MaximumConsecutiveFailures; i++)
        {
            counters.Observe(500);
        }

        var report = new RelayReadinessProbes(clock, null, new RelayUpdate(null, null), counters, buildKnown: false).Capture();

        Assert.False(Check(report, RelayReadinessCheck.Build));
        Assert.False(Check(report, RelayReadinessCheck.Failures));
        Assert.Equal(RelayReadinessStatus.Degraded, report.Result.Status);
    }

    [Fact]
    public async Task TheRouteRefusesAnyoneTheAuthoriserRefusesAndAnswersWithNoPathsForTheOperator()
    {
        var state = Path.Combine(_root, "state");
        var clock = new RelayTestClock(DateTimeOffset.UtcNow);
        var counters = new RelayOperationalCounters(clock);
        var probes = new RelayReadinessProbes(clock, state, new RelayUpdate(state, null), counters, buildKnown: true);
        var context = new RelayOperatorContext(
            1, "2.0.1", "abc1234", clock.GetUtcNow(), TransportEnforced: false, () => new RelayLoad(3, 7, 2, 1));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapRelayReadiness(probes, context, request => request.Headers["X-Test-Operator"] == "yes");
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/") };

            var stranger = await client.GetAsync("admin/readiness");
            var operatorRequest = new HttpRequestMessage(HttpMethod.Get, "admin/readiness");
            operatorRequest.Headers.Add("X-Test-Operator", "yes");
            var accepted = await client.SendAsync(operatorRequest);
            var text = await accepted.Content.ReadAsStringAsync();
            var body = JsonDocument.Parse(text).RootElement;

            Assert.Equal(HttpStatusCode.Unauthorized, stranger.StatusCode);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.Equal("ready", body.GetProperty("status").GetString());
            Assert.Equal(7, body.GetProperty("checks").GetArrayLength());
            Assert.Equal("updaterCheck", body.GetProperty("checks")[4].GetProperty("check").GetString());
            var relay = body.GetProperty("relay");
            Assert.Equal(3, relay.GetProperty("rooms").GetInt32());
            Assert.Equal(7, relay.GetProperty("members").GetInt32());
            Assert.Equal("persistent", relay.GetProperty("storage").GetString());
            Assert.Equal("not-enforced", relay.GetProperty("transportPolicy").GetString());
            Assert.DoesNotContain(_root, text, StringComparison.Ordinal);
            Assert.DoesNotContain("relay-readiness-", text, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private (string State, string Status) UpdaterDirectories()
    {
        var state = Path.Combine(_root, "state");
        var status = Path.Combine(_root, "updater-status");
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(status);
        return (state, status);
    }

    private static bool Check(RelayReadinessReport report, RelayReadinessCheck check) =>
        report.Result.Checks.Single(result => result.Check == check).IsReady;
}
