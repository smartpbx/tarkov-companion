using System.Diagnostics;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Platform.Windows.Watching;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.CaptureSessions;

/// <summary>
/// How often the screenshot folder is looked at, and what looking at it costs.
/// </summary>
/// <remarks>
/// With the name reported on sight, the folder poll became the largest single term in how long
/// a squadmate waits for a marker. Shortening it is only defensible with a number for what a
/// listing costs, and a bound for the case where that number turns out to be large.
/// </remarks>
public sealed class ScreenshotWatchPaceTests(ITestOutputHelper output)
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Theory]
    [InlineData(RaidLifecycleState.InRaid, true, ScreenshotWatchPace.Attentive)]
    [InlineData(RaidLifecycleState.InRaid, false, ScreenshotWatchPace.Idle)]
    [InlineData(RaidLifecycleState.Menu, true, ScreenshotWatchPace.Idle)]
    [InlineData(RaidLifecycleState.PostRaid, true, ScreenshotWatchPace.Idle)]
    [InlineData(RaidLifecycleState.LoadingRaid, true, ScreenshotWatchPace.Idle)]
    public void TheFolderIsWatchedCloselyOnlyWhenSomebodyElseIsWaiting(
        RaidLifecycleState state,
        bool sharing,
        ScreenshotWatchPace expected)
    {
        var store = new RuntimeStateStore(Options);
        store.Update(current => current with
        {
            Raid = current.Raid with { State = state },
            Group = sharing
                ? new GroupSnapshot(true, [], "Sharing", DateTimeOffset.UtcNow)
                : GroupSnapshot.Off,
        });

        Assert.Equal(expected, new ScreenshotWatchPacer(store).Current);
    }

    /// <summary>
    /// The attentive pace is taken while a raid is on, and given up when it ends.
    /// </summary>
    /// <remarks>
    /// Read on every poll rather than latched, because a raid ending is the moment the fast poll
    /// stops being worth anything and there is nothing else to notice it.
    /// </remarks>
    [Fact]
    public async Task TheWatcherSpeedsUpForARaidAndSlowsDownWhenItEnds()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var pacer = new SwitchablePacer();
            var watcher = new WindowsScreenshotWatcher(
                pollInterval: TimeSpan.FromMilliseconds(400),
                attentivePollInterval: TimeSpan.FromMilliseconds(50),
                pacer: pacer);
            await using var enumerator = watcher.WatchAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);
            var idle = enumerator.MoveNextAsync().AsTask();

            await UntilAsync(() => watcher.PollInterval == TimeSpan.FromMilliseconds(400), stopping.Token);
            Assert.Equal(TimeSpan.FromMilliseconds(400), watcher.PollInterval);

            pacer.Pace = ScreenshotWatchPace.Attentive;
            await UntilAsync(() => watcher.PollInterval == TimeSpan.FromMilliseconds(50), stopping.Token);
            Assert.Equal(TimeSpan.FromMilliseconds(50), watcher.PollInterval);

            pacer.Pace = ScreenshotWatchPace.Idle;
            await UntilAsync(() => watcher.PollInterval == TimeSpan.FromMilliseconds(400), stopping.Token);
            Assert.Equal(TimeSpan.FromMilliseconds(400), watcher.PollInterval);

            await stopping.CancelAsync();
            Assert.False(await idle);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>An attentive interval can only ever be shorter than the idle one.</summary>
    [Fact]
    public async Task AnAttentivePaceNeverSlowsTheWatcherDown()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var watcher = new WindowsScreenshotWatcher(
                pollInterval: TimeSpan.FromMilliseconds(20),
                attentivePollInterval: TimeSpan.FromSeconds(5),
                pacer: new SwitchablePacer { Pace = ScreenshotWatchPace.Attentive });
            await using var enumerator = watcher.WatchAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);
            var idle = enumerator.MoveNextAsync().AsTask();

            await UntilAsync(() => watcher.PollInterval > TimeSpan.Zero, stopping.Token);

            Assert.Equal(TimeSpan.FromMilliseconds(20), watcher.PollInterval);
            await stopping.CancelAsync();
            Assert.False(await idle);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// What a listing costs on a folder holding a few thousand screenshots.
    /// </summary>
    /// <remarks>
    /// The number the fast poll has to be justified against. A real screenshot folder holds
    /// dozens; three thousand is a player who has never deleted one, and is the case where four
    /// listings a second would be worth arguing about.
    /// </remarks>
    [Fact]
    public async Task AListingOfThousandsOfScreenshotsIsStillCheapEnoughToPollQuickly()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var written = DateTime.UtcNow.AddDays(-1);
            for (var file = 0; file < 3_000; file++)
            {
                var path = Path.Combine(root, $"shot-{file:D5}.png");
                await File.WriteAllBytesAsync(path, Png, stopping.Token);
                File.SetLastWriteTimeUtc(path, written.AddSeconds(file));
            }

            var watcher = new WindowsScreenshotWatcher(
                pollInterval: TimeSpan.FromMilliseconds(250),
                maximumTrackedFiles: 16_384,
                pacer: new SwitchablePacer { Pace = ScreenshotWatchPace.Attentive });
            await using var enumerator = watcher.WatchAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);
            var idle = enumerator.MoveNextAsync().AsTask();

            // The duty cycle is what is being asserted, and it is reached rather than true on
            // the first reading: the first listing runs against cold caches, and a listing that
            // turns out to be expensive only backs the interval off on the poll that follows it.
            // So this waits for the invariant instead of judging one sample — which is what made
            // it fail on a loaded CI runner while passing on an idle box, with nothing wrong
            // either time.
            var listings = new List<TimeSpan>();
            var held = false;
            for (var pass = 0; pass < 12 && !held; pass++)
            {
                await UntilAsync(() => watcher.LastListing > TimeSpan.Zero, stopping.Token);
                var listing = watcher.LastListing;
                listings.Add(listing);
                held = listing <= TimeSpan.FromMilliseconds(25) || watcher.PollInterval >= listing * 10;
                if (!held)
                {
                    await Task.Delay(watcher.PollInterval + TimeSpan.FromMilliseconds(50), stopping.Token);
                }
            }

            output.WriteLine(
                $"3,000 screenshots: listings {string.Join(", ", listings.Select(one => $"{one.TotalMilliseconds:0.0}ms"))}; "
                + $"poll interval {watcher.PollInterval.TotalMilliseconds:0}ms.");

            // Whichever way the measurement goes: either the listing is cheap and the folder is
            // polled at the asked-for rate, or it is not and the interval has backed off to at
            // least ten times what it costs.
            Assert.True(
                held,
                $"Listings of {string.Join(", ", listings.Select(one => $"{one.TotalMilliseconds:0.0}ms"))} "
                + $"were still polled every {watcher.PollInterval.TotalMilliseconds:0}ms.");
            await stopping.CancelAsync();
            Assert.False(await idle);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A folder slow enough to matter is looked at less often, not more.
    /// </summary>
    /// <remarks>
    /// The arithmetic rather than a pathological folder: the bound is one line of policy, and a
    /// test that had to build a directory slow enough to trip it would be a minute long and
    /// would measure the machine rather than the rule.
    /// </remarks>
    [Theory]
    // A listing too cheap to care about leaves the asked-for pace alone.
    [InlineData(250, 0, 250)]
    [InlineData(250, 4, 250)]
    // Past that, the listing may cost at most a tenth of the watcher.
    [InlineData(250, 6, 250)]
    [InlineData(250, 40, 400)]
    [InlineData(250, 500, 5_000)]
    // However bad the folder, it is still looked at twice a minute.
    [InlineData(250, 20_000, 30_000)]
    // And a caller that asked for a slow poll gets the slow poll it asked for.
    [InlineData(60_000, 40, 60_000)]
    public void ASlowListingBacksTheWatcherOffRatherThanCostingACore(
        int wantedMilliseconds,
        int listingMilliseconds,
        int expectedMilliseconds) =>
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedMilliseconds),
            WindowsScreenshotWatcher.IntervalFor(
                TimeSpan.FromMilliseconds(wantedMilliseconds),
                TimeSpan.FromMilliseconds(listingMilliseconds)));

    private static async Task UntilAsync(Func<bool> ready, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (!ready() && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15))
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string NewDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-watch-pace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static RuntimeOptions Options { get; } = new(
        false,
        Offline: true,
        GameMode.Regular,
        "en",
        TimeSpan.FromHours(9),
        TimeSpan.FromMinutes(5));

    private sealed class SwitchablePacer : IScreenshotWatchPacer
    {
        private int _pace;

        public ScreenshotWatchPace Pace
        {
            get => (ScreenshotWatchPace)Volatile.Read(ref _pace);
            set => Volatile.Write(ref _pace, (int)value);
        }

        public ScreenshotWatchPace Current => Pace;
    }
}
