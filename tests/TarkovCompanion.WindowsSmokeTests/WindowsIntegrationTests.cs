using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Platform.Windows.Capture;
using TarkovCompanion.Platform.Windows.Discovery;
using TarkovCompanion.Platform.Windows.Displays;
using TarkovCompanion.Platform.Windows.Security;
using TarkovCompanion.Platform.Windows.Watching;

namespace TarkovCompanion.WindowsSmokeTests;

public sealed class WindowsIntegrationTests
{
    /// <summary>
    /// A companion started after the raid began still knows which map the player is on.
    /// </summary>
    /// <remarks>
    /// Tailing alone cannot do this. The notification carrying the map is written when the
    /// raid starts, so a companion launched sixty-nine seconds later never sees it, infers the
    /// raid only from ongoing chatter that names no map, and shows "map none" for the rest of
    /// it. That is what happened on a live machine.
    /// </remarks>
    [Fact]
    public async Task ResumesARaidThatStartedBeforeTheCompanionDid()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-resume-{Guid.NewGuid():N}");
        var session = Path.Combine(root, "log_2026.09.11_21-59-06_1.1.5.0.47242");
        Directory.CreateDirectory(session);
        await File.WriteAllTextAsync(
            Path.Combine(session, "application.log"),
            "2026-09-11 21:59:10.000|1.1.5.0.47242|Info|application|TRACE-NetworkGameCreate " +
            "profileStatus: 'Profileid: P, Status: Busy, RaidMode: Online, Ip: 0.0.0.0, Port: 17009, " +
            "Location: TarkovStreets, Sid: S, GameMode: deathmatch, shortId: I'\n",
            CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource();
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await using var enumerator = new WindowsEftLogWatcher(new EftLogParser())
                .WatchAsync(root, timeout.Token)
                .GetAsyncEnumerator(timeout.Token);

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(RaidLifecycleState.InRaid, enumerator.Current.SuggestedState);
            Assert.Equal("streets-of-tarkov", enumerator.Current.MapId);
            Assert.Contains("already running", enumerator.Current.Summary, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A finished session is not replayed as though the player were still in it.
    /// </summary>
    [Fact]
    public async Task DoesNotResumeARaidThatHasAlreadyEnded()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-resume-{Guid.NewGuid():N}");
        var session = Path.Combine(root, "log_2026.09.11_21-59-06_1.1.5.0.47242");
        Directory.CreateDirectory(session);
        await File.WriteAllTextAsync(
            Path.Combine(session, "application.log"),
            "2026-09-11 21:59:10.000|1.1.5.0.47242|Info|application|TRACE-NetworkGameCreate " +
            "profileStatus: 'Profileid: P, Status: Busy, Location: TarkovStreets'\n" +
            "2026-09-11 22:20:10.000|1.1.5.0.47242|Info|application|TRACE-NetworkGameCreate " +
            "profileStatus: 'Profileid: P, Status: Free, Location: TarkovStreets'\n",
            CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource();
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await using var enumerator = new WindowsEftLogWatcher(new EftLogParser())
                .WatchAsync(root, timeout.Token)
                .GetAsyncEnumerator(timeout.Token);

            // Nothing is resumed, so the watcher has nothing to yield and waits for a line
            // that never comes. Whether the deadline ends that by cancelling or by simply
            // completing the sequence is the watcher's business; what this asserts is that no
            // evidence was produced.
            var produced = false;
            try
            {
                produced = await enumerator.MoveNextAsync();
            }
            catch (OperationCanceledException)
            {
            }

            Assert.False(produced);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The live session is chosen by the name the game stamped on the folder, not by mtime.
    /// </summary>
    /// <remarks>
    /// On a machine whose clock stepped back four hours mid-session, the previous session's
    /// files carried timestamps in the future, so a newest-by-time sort picked the dead folder
    /// over the live one. The folder's name is written once at creation and cannot drift.
    /// </remarks>
    [Fact]
    public async Task ChoosesTheLiveSessionWhenAStaleOneHasNewerFileTimes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-resume-{Guid.NewGuid():N}");
        var stale = Path.Combine(root, "log_2026.09.11_21-29-04_1.1.5.0.47242");
        var live = Path.Combine(root, "log_2026.09.11_21-59-06_1.1.5.0.47242");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(live);
        var stalePath = Path.Combine(stale, "application.log");
        await File.WriteAllTextAsync(
            stalePath,
            "2026-09-11 21:29:10.000|1.1.5.0.47242|Info|application|TRACE-NetworkGameCreate " +
            "profileStatus: 'Profileid: P, Status: Busy, Location: Woods'\n",
            CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(live, "application.log"),
            "2026-09-11 21:59:10.000|1.1.5.0.47242|Info|application|TRACE-NetworkGameCreate " +
            "profileStatus: 'Profileid: P, Status: Busy, Location: TarkovStreets'\n",
            CancellationToken.None);
        // The dead session's file is stamped four hours into the future, as the clock step did.
        File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddHours(4));
        try
        {
            using var timeout = new CancellationTokenSource();
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await using var enumerator = new WindowsEftLogWatcher(new EftLogParser())
                .WatchAsync(root, timeout.Token)
                .GetAsyncEnumerator(timeout.Token);

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("streets-of-tarkov", enumerator.Current.MapId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProductionWindowDiscoveryIgnoresSimulator()
    {
        var catalog = new StubWindowCatalog(
        [
            new WindowCandidate(20, "TarkovCompanion.EftSimulator", "Tarkov Companion EFT Simulator", new PixelRect(0, 0, 800, 600), false),
        ]);
        var locator = new WindowsGameWindowLocator(catalog);

        var production = await locator.FindAsync(developerMode: false, CancellationToken.None);
        var development = await locator.FindAsync(developerMode: true, CancellationToken.None);

        Assert.Null(production);
        Assert.NotNull(development);
        Assert.True(development.IsSimulator);
    }

    [Fact]
    public async Task ActualGameWindowWinsOverSimulatorInDeveloperMode()
    {
        var catalog = new StubWindowCatalog(
        [
            new WindowCandidate(20, "TarkovCompanion.EftSimulator", "Tarkov Companion EFT Simulator", new PixelRect(0, 0, 800, 600), false),
            new WindowCandidate(42, "EscapeFromTarkov", "Escape from Tarkov", new PixelRect(0, 0, 1920, 1080), false),
        ]);

        var result = await new WindowsGameWindowLocator(catalog)
            .FindAsync(developerMode: true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("EscapeFromTarkov", result.ProcessName);
        Assert.False(result.IsSimulator);
    }

    [Fact]
    public async Task PathDiscoveryReportsOnlyExistingCandidates()
    {
        var probe = new StubPathProbe(
            new(["missing-install"], ["logs"], ["shots"]),
            new HashSet<string> { "logs", "shots" });

        var paths = await new WindowsEftPathLocator(probe).FindAsync(CancellationToken.None);

        Assert.Null(paths.InstallRoot);
        Assert.Equal("logs", paths.LogRoot);
        Assert.Equal("shots", paths.ScreenshotRoot);
        Assert.Equal(0.8, paths.Confidence.Value, 3);
    }

    [Fact]
    public async Task ScreenshotWatcherEmitsOnlySupportedNewImages()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-screenshot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var timeout = new CancellationTokenSource();
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await using var enumerator = new WindowsScreenshotWatcher()
                .WatchAsync(root, timeout.Token)
                .GetAsyncEnumerator(timeout.Token);
            var next = enumerator.MoveNextAsync().AsTask();
            var expected = Path.Combine(root, "2026-09-04[18-33]_1, 2, 3_0, 0, 0, 1.png");
            await File.WriteAllBytesAsync(expected, [1], timeout.Token);

            Assert.True(await next.WaitAsync(timeout.Token));
            Assert.Equal(expected, enumerator.Current);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LogWatcherTailsNewFixtureLineThroughParser()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-log-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "application.log");
        await File.WriteAllTextAsync(path, string.Empty, CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource();
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await using var enumerator = new WindowsEftLogWatcher(new EftLogParser())
                .WatchAsync(root, timeout.Token)
                .GetAsyncEnumerator(timeout.Token);
            var next = enumerator.MoveNextAsync().AsTask();
            // A real line: the map arrives on the profileStatus notification, not on prose.
            await File.AppendAllTextAsync(
                path,
                "2026-09-11 00:42:46.453|1.1.5.0.47242|Info|output|application|TRACE-NetworkGameCreate " +
                "profileStatus: 'Profileid: P, Status: Busy, RaidMode: Online, Ip: 0.0.0.0, Port: 17009, " +
                "Location: Woods, Sid: S, GameMode: deathmatch, shortId: I'\n",
                timeout.Token);

            Assert.True(await next.WaitAsync(timeout.Token));
            Assert.Equal(RaidLifecycleState.InRaid, enumerator.Current.SuggestedState);
            Assert.Equal("woods", enumerator.Current.MapId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    
    private sealed class StubWindowCatalog(IReadOnlyList<WindowCandidate> windows) : IWindowCatalog
    {
        public IReadOnlyList<WindowCandidate> GetWindows() => windows;
    }

    private sealed class StubPathProbe(EftPathCandidates candidates, IReadOnlySet<string> existing) : IEftPathProbe
    {
        public EftPathCandidates GetCandidates() => candidates;

        public bool DirectoryExists(string path) => existing.Contains(path);
    }

    private sealed class StubGameWindowLocator : IGameWindowLocator
    {
        public Task<WindowDescriptor?> FindAsync(bool developerMode, CancellationToken cancellationToken) =>
            Task.FromResult<WindowDescriptor?>(null);
    }
}
