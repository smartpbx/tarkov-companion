using System.Globalization;
using TarkovCompanion.App.Services.V2.SelfTest;
using TarkovCompanion.UnitTests.Runtime;

namespace TarkovCompanion.UnitTests.V2Setup;

/// <summary>
/// One press of the self-test, and what the run as a whole then says.
/// </summary>
public sealed class SelfTestSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ARunReportsEveryCapabilityInTheOrderTheReportShowsThem()
    {
        var reported = new List<string>();
        var session = new SelfTestSession(new StubReadings(), new ManualTimeProvider(Now), CultureInfo.InvariantCulture);

        var summary = await session.RunAsync(capability => reported.Add(capability.Id), CancellationToken.None);

        Assert.Equal(
            [.. SelfTestSession.Capabilities.Select(capability => capability.Id)],
            summary.Capabilities.Select(capability => capability.Id));
        Assert.Equal(SelfTestSession.Capabilities.Count, reported.Count);
    }

    /// <summary>
    /// A probe that throws must not take the run with it.
    /// </summary>
    /// <remarks>
    /// A self-test that falls over is the exact thing that leaves somebody guessing again, which
    /// is what this page exists to stop. The failure becomes that capability's unknown, carrying
    /// the reason, and the other six still report.
    /// </remarks>
    [Fact]
    public async Task AProbeThatThrowsBecomesThatCapabilitysUnknownAndTheRestStillReport()
    {
        var readings = new StubReadings { RelayFails = new InvalidOperationException("the relay client is missing") };
        var session = new SelfTestSession(readings, new ManualTimeProvider(Now), CultureInfo.InvariantCulture);

        var summary = await session.RunAsync(null, CancellationToken.None);

        var relay = Assert.Single(summary.Capabilities, capability => capability.Id == SelfTestProbes.RelayId);
        Assert.Equal(SelfTestOutcome.Unknown, relay.Outcome);
        Assert.Contains("the relay client is missing", relay.Headline, StringComparison.Ordinal);
        Assert.Equal(6, summary.Capabilities.Count(capability => capability.Outcome != SelfTestOutcome.Unknown));
    }

    [Fact]
    public async Task StoppingMidRunLeavesTheUnfinishedCapabilitiesUnknownRatherThanPassing()
    {
        using var cancellation = new CancellationTokenSource();
        var readings = new StubReadings { BlockScreenshot = true };
        var session = new SelfTestSession(readings, new ManualTimeProvider(Now), CultureInfo.InvariantCulture);

        var running = session.RunAsync(null, cancellation.Token);
        await cancellation.CancelAsync();
        var summary = await running;

        var screenshots = Assert.Single(summary.Capabilities, capability => capability.Id == SelfTestProbes.ScreenshotsId);
        Assert.Equal(SelfTestOutcome.Unknown, screenshots.Outcome);
        Assert.Contains("Stopped", screenshots.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void OneFailureFailsTheRunAndAnUntestedCapabilityIsNotAPass()
    {
        var pass = new SelfTestCapability("a", "A", SelfTestOutcome.Pass, "fine", [], TimeSpan.Zero);
        var unknown = new SelfTestCapability("b", "B", SelfTestOutcome.Unknown, "not measurable", [], TimeSpan.Zero);
        var fail = new SelfTestCapability("c", "C", SelfTestOutcome.Fail, "broken", [], TimeSpan.Zero);

        Assert.Equal(SelfTestOutcome.Pass, new SelfTestSummary(Now, TimeSpan.Zero, [pass]).Outcome);
        Assert.Equal(SelfTestOutcome.Unknown, new SelfTestSummary(Now, TimeSpan.Zero, [pass, unknown]).Outcome);
        Assert.Equal(SelfTestOutcome.Fail, new SelfTestSummary(Now, TimeSpan.Zero, [pass, unknown, fail]).Outcome);
    }

    [Fact]
    public async Task TheCopyableTextCarriesEveryFactAndItsSource()
    {
        var session = new SelfTestSession(new StubReadings(), new ManualTimeProvider(Now), CultureInfo.InvariantCulture);
        var summary = await session.RunAsync(null, CancellationToken.None);

        var text = summary.ToText(CultureInfo.InvariantCulture);

        Assert.Contains("## Tarkov Companion self-test", text, StringComparison.Ordinal);
        foreach (var capability in summary.Capabilities)
        {
            Assert.Contains(capability.Title, text, StringComparison.Ordinal);
            foreach (var fact in capability.Facts)
            {
                Assert.Contains(fact.Text, text, StringComparison.Ordinal);
                Assert.Contains(fact.Source, text, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// What may leave the machine is the verdict, never the evidence.
    /// </summary>
    [Fact]
    public async Task TheOutboundProjectionCarriesVerdictsAndNoPathsOrNames()
    {
        var session = new SelfTestSession(new StubReadings(), new ManualTimeProvider(Now), CultureInfo.InvariantCulture);
        var summary = await session.RunAsync(null, CancellationToken.None);

        var facts = summary.ToSupportFacts(CultureInfo.InvariantCulture);

        Assert.Equal(SelfTestSession.Capabilities.Count + 1, facts.Count);
        foreach (var (key, value) in facts)
        {
            Assert.DoesNotContain(@"D:\", value, StringComparison.Ordinal);
            Assert.DoesNotContain("relay.example", value, StringComparison.Ordinal);
            Assert.DoesNotContain("Clayton", value, StringComparison.Ordinal);
            Assert.DoesNotContain("iPad", value, StringComparison.Ordinal);
            Assert.DoesNotContain("140.2", value, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(key));
        }
    }

    /// <summary>
    /// [V2 rough package 43a] A screenshot already on disk settles the probe inside the run, so
    /// nothing waits and nothing is asked of anybody. This is the usual case.
    /// </summary>
    [Fact]
    public async Task AScreenshotAlreadyOnDiskSettlesTheProbeWithoutWaiting()
    {
        var session = new SelfTestSession(new StubReadings(), new ManualTimeProvider(Now), CultureInfo.InvariantCulture);

        var summary = await session.RunAsync(null, CancellationToken.None);

        Assert.Null(session.Settling);
        var screenshots = Assert.Single(summary.Capabilities, capability => capability.Id == SelfTestProbes.ScreenshotsId);
        Assert.Equal(SelfTestOutcome.Pass, screenshots.Outcome);
    }

    /// <summary>
    /// With nothing on disk the run still finishes; the screenshot alone stays open and says so.
    /// </summary>
    [Fact]
    public async Task WithNoRecentScreenshotTheRunFinishesAndOnlyThatOneKeepsWaiting()
    {
        var readings = new StubReadings { NothingRecent = true };
        var session = new SelfTestSession(readings, new ManualTimeProvider(Now), CultureInfo.InvariantCulture);
        var reported = new List<SelfTestCapability>();

        var summary = await session.RunAsync(reported.Add, CancellationToken.None);

        var screenshots = Assert.Single(summary.Capabilities, capability => capability.Id == SelfTestProbes.ScreenshotsId);
        Assert.Equal(SelfTestOutcome.Waiting, screenshots.Outcome);
        Assert.Contains("take one in a raid", screenshots.Headline, StringComparison.OrdinalIgnoreCase);
        // Everything else has already answered, which is the point of not blocking on this one.
        Assert.All(
            summary.Capabilities.Where(capability => capability.Id != SelfTestProbes.ScreenshotsId),
            capability => Assert.NotEqual(SelfTestOutcome.Waiting, capability.Outcome));
        Assert.NotNull(session.Settling);

        var settled = await session.Settling!;

        Assert.Equal(SelfTestOutcome.Pass, settled.Outcome);
        Assert.Contains(reported, capability => capability.Outcome == SelfTestOutcome.Waiting);
    }

    /// <summary>A run still waiting on a screenshot has found nothing wrong, and says that.</summary>
    [Fact]
    public async Task ARunThatIsStillWaitingIsNotAFailure()
    {
        var session = new SelfTestSession(
            new StubReadings { NothingRecent = true },
            new ManualTimeProvider(Now),
            CultureInfo.InvariantCulture);

        var summary = await session.RunAsync(null, CancellationToken.None);

        Assert.NotEqual(SelfTestOutcome.Fail, summary.Outcome);
        Assert.Equal(SelfTestOutcome.Waiting, summary.Outcome);
        Assert.Contains("waiting for you", summary.Headline(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.Equal(0, summary.FailCount);
    }

    /// <summary>Readings that answer instantly, so the session's own behaviour is what is measured.</summary>
    private sealed class StubReadings : ISelfTestReadings
    {
        public Exception? RelayFails { get; init; }

        public bool BlockScreenshot { get; init; }

        /// <summary>Nothing on disk, so the probe has to wait for one.</summary>
        public bool NothingRecent { get; init; }

        public Task<SelfTestFolders> ReadFoldersAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SelfTestProbeTests.Folders(new DateTimeOffset(2026, 9, 18, 20, 58, 0, TimeSpan.Zero)));

        public Task<SelfTestLogs> ReadLogsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SelfTestProbeTests.Logs());

        public Task<SelfTestScreenshot> WatchScreenshotAsync(TimeSpan patience, CancellationToken cancellationToken) =>
            Task.FromResult(SelfTestProbeTests.Screenshot());

        /// <summary>
        /// The half that runs inside the session, so blocking here is what stalls a run.
        /// </summary>
        public async Task<SelfTestScreenshot> RecentScreenshotAsync(TimeSpan lookBack, CancellationToken cancellationToken)
        {
            if (BlockScreenshot)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return NothingRecent ? SelfTestProbeTests.NoScreenshot() : SelfTestProbeTests.Screenshot();
        }

        public Task<SelfTestGameData> ReadGameDataAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SelfTestProbeTests.GameData());

        public Task<SelfTestDatabase> ReadDatabaseAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SelfTestProbeTests.Database());

        public Task<SelfTestRelay> ReadRelayAsync(CancellationToken cancellationToken) =>
            RelayFails is null
                ? Task.FromResult(SelfTestProbeTests.Relay())
                : Task.FromException<SelfTestRelay>(RelayFails);

        public Task<SelfTestTablet> ReadTabletAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SelfTestProbeTests.Tablet());
    }
}
