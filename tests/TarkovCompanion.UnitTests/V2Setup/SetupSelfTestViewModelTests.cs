using System.Globalization;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.SelfTest;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.UnitTests.V2Setup;

/// <summary>
/// The panel in Setup: what it shows, what it copies, and what it hands the problem report.
/// </summary>
public sealed class SetupSelfTestViewModelTests
{
    [Fact]
    public async Task RunningFillsOneRowPerCapabilityWithItsVerdictAndItsFacts()
    {
        var view = new SetupSelfTestViewModel(() => new PassingReadings(), new SelfTestJournal());

        await view.RunAsync();

        Assert.Equal(SelfTestSession.Capabilities.Count, view.Rows.Count);
        Assert.All(view.Rows, row => Assert.NotEqual(SelfTestOutcome.Pending, row.Outcome));
        var folders = Assert.Single(view.Rows, row => row.Id == SelfTestProbes.FoldersId);
        Assert.Equal("working", folders.Status);
        Assert.NotEmpty(folders.Facts);
        Assert.All(folders.Facts, fact => Assert.False(string.IsNullOrWhiteSpace(fact.Source)));
    }

    [Fact]
    public async Task TheHeadlineCountsWhatWorkedWhatDidNotAndWhatCouldNotBeTested()
    {
        var view = new SetupSelfTestViewModel(() => new PassingReadings(), new SelfTestJournal());

        await view.RunAsync();

        Assert.Contains("working", view.Status, StringComparison.Ordinal);
        Assert.Contains("could not be tested", view.Status, StringComparison.Ordinal);
        Assert.False(view.IsRunning);
        Assert.True(view.CanCopy);
    }

    [Fact]
    public async Task NothingCanBeCopiedBeforeARun()
    {
        var view = new SetupSelfTestViewModel(() => new PassingReadings(), new SelfTestJournal());

        Assert.False(view.CanCopy);
        await view.CopyAsync();

        Assert.Contains("nothing to copy", view.CopyStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheResultGoesToTheClipboardAsTextWithItsSources()
    {
        var copied = string.Empty;
        var view = new SetupSelfTestViewModel(() => new PassingReadings(), new SelfTestJournal())
        {
            Clipboard = text =>
            {
                copied = text;
                return Task.CompletedTask;
            },
        };

        await view.RunAsync();
        await view.CopyAsync();

        Assert.Contains("Tarkov Companion self-test", copied, StringComparison.Ordinal);
        Assert.Contains("Game folders", copied, StringComparison.Ordinal);
        Assert.Contains("read from", copied, StringComparison.Ordinal);
        Assert.Contains("Copied", view.CopyStatus, StringComparison.Ordinal);
    }

    /// <summary>
    /// The result outlives the panel, so a problem report can carry it.
    /// </summary>
    [Fact]
    public async Task TheRunIsKeptSoCopyDiagnosticsCanCarryIt()
    {
        var journal = new SelfTestJournal();
        var view = new SetupSelfTestViewModel(() => new PassingReadings(), journal);

        Assert.Null(journal.Last);
        await view.RunAsync();

        Assert.NotNull(journal.Last);
        Assert.Equal(SelfTestSession.Capabilities.Count, journal.Last.Capabilities.Count);
    }

    /// <summary>The one thing the self-test asks for, said while it is waiting rather than after.</summary>
    [Fact]
    public async Task ThePanelAsksForAScreenshotWhileItIsWaitingForOne()
    {
        var gate = new TaskCompletionSource();
        var view = new SetupSelfTestViewModel(() => new PassingReadings { ScreenshotGate = gate.Task }, new SelfTestJournal());

        // [V2 rough package 43a] The run finishes while this one capability is still open. That is
        // the whole change: the other six are already answered and copyable, and nobody is being
        // asked to alt-tab against a clock.
        await view.RunAsync();
        Assert.False(view.IsRunning);
        Assert.True(view.AsksForScreenshot);
        var waiting = Assert.Single(view.Rows, row => row.Id == SelfTestProbes.ScreenshotsId);
        Assert.Equal(SelfTestOutcome.Waiting, waiting.Outcome);

        gate.SetResult();
        await view.Settling!;

        Assert.False(view.AsksForScreenshot);
        Assert.Equal(SelfTestOutcome.Pass, Assert.Single(view.Rows, row => row.Id == SelfTestProbes.ScreenshotsId).Outcome);
    }

    [Fact]
    public async Task StopEndsTheRunAndSaysSo()
    {
        var gate = new TaskCompletionSource();
        var view = new SetupSelfTestViewModel(() => new PassingReadings { ScreenshotGate = gate.Task }, new SelfTestJournal());

        // The run itself no longer waits for a screenshot, so Stop is about the background wait
        // that outlives it: pressing it settles the open capability rather than leaving it open.
        await view.RunAsync();
        Assert.True(view.AsksForScreenshot);

        view.Stop();
        await view.Settling!;

        Assert.False(view.IsRunning);
        var screenshots = Assert.Single(view.Rows, row => row.Id == SelfTestProbes.ScreenshotsId);
        Assert.Equal(SelfTestOutcome.Unknown, screenshots.Outcome);
        Assert.Contains("Stopped", screenshots.Headline, StringComparison.Ordinal);
    }

    /// <summary>
    /// What Copy diagnostics adds, and what Report a problem may not.
    /// </summary>
    /// <remarks>
    /// docs/SAFETY.md lets the self-test's own output name local paths because it is local. The
    /// shareable bundle's schema is closed, so what reaches it is the capability and its verdict
    /// and nothing else.
    /// </remarks>
    [Fact]
    public async Task TheSupportBundleCarriesTheVerdictsAndNoneOfTheEvidence()
    {
        var journal = new SelfTestJournal();
        await new SetupSelfTestViewModel(() => new PassingReadings(), journal).RunAsync();

        var report = SupportBundle.Describe(
            Snapshot(),
            [],
            "/ignored/application.log",
            journal.Last!.ToSupportFacts(CultureInfo.InvariantCulture));

        Assert.Contains("### Self-test", report, StringComparison.Ordinal);
        Assert.Contains("- game-folders:", report, StringComparison.Ordinal);
        Assert.DoesNotContain(@"D:\", report, StringComparison.Ordinal);
        Assert.DoesNotContain("relay.example", report, StringComparison.Ordinal);
        Assert.DoesNotContain("iPad", report, StringComparison.Ordinal);
    }

    private static ApplicationRuntimeSnapshot Snapshot() => new RuntimeStateStore(new(
        false,
        Offline: true,
        GameMode.Regular,
        "en",
        TimeSpan.FromHours(9),
        TimeSpan.FromMinutes(5))).Current;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 3_000 && !condition(); attempt++)
        {
            await Task.Delay(1);
        }
    }

    private sealed class PassingReadings : ISelfTestReadings
    {
        public Task? ScreenshotGate { get; init; }

        public Task<SelfTestFolders> ReadFoldersAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SelfTestProbeTests.Folders(new DateTimeOffset(2026, 9, 18, 20, 58, 0, TimeSpan.Zero)));

        public Task<SelfTestLogs> ReadLogsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SelfTestProbeTests.Logs());

        public async Task<SelfTestScreenshot> WatchScreenshotAsync(TimeSpan patience, CancellationToken cancellationToken)
        {
            if (ScreenshotGate is not null)
            {
                await ScreenshotGate.WaitAsync(cancellationToken);
            }

            return SelfTestProbeTests.Screenshot();
        }

        /// <summary>
        /// Nothing already on disk when a gate is set, so the probe goes on waiting in the
        /// background — which is the state these tests are about.
        /// </summary>
        public Task<SelfTestScreenshot> RecentScreenshotAsync(TimeSpan lookBack, CancellationToken cancellationToken) =>
            Task.FromResult(ScreenshotGate is null
                ? SelfTestProbeTests.Screenshot()
                : SelfTestProbeTests.NoScreenshot());

        public Task<SelfTestGameData> ReadGameDataAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SelfTestProbeTests.GameData());

        public Task<SelfTestDatabase> ReadDatabaseAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SelfTestProbeTests.Database());

        // Not configured, so the run always has one capability that could not be tested — which
        // is the state the headline has to distinguish from everything working.
        public Task<SelfTestRelay> ReadRelayAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SelfTestProbeTests.Relay() with { Configured = false, Origin = null, Reachable = false });

        public Task<SelfTestTablet> ReadTabletAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SelfTestProbeTests.Tablet());
    }
}
