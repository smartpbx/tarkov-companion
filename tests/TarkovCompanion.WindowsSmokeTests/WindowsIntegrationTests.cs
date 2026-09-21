using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Raids;
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
            // Six minutes into the raid, with the game running. The clock is fixed because the
            // replay now asks how old the raid is: against the real clock this fixture's raid is
            // older than any raid lasts, and is rightly not resumed (#568).
            await using var enumerator = new WindowsEftLogWatcher(
                    new EftLogParser(),
                    timeProvider: new FixedClock(SixMinutesIn),
                    gameLocator: new RunningGameLocator())
                .WatchAsync(root, timeout.Token)
                .GetAsyncEnumerator(timeout.Token);

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(RaidLifecycleState.InRaid, enumerator.Current.SuggestedState);
            Assert.Equal("streets-of-tarkov", enumerator.Current.MapId);
            Assert.Contains("already running", enumerator.Current.Summary, StringComparison.Ordinal);
            Assert.Equal(new DateTimeOffset(2026, 9, 11, 21, 59, 10, TimeSpan.Zero), enumerator.Current.RaidStartedUtc);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A raid nobody reported over is not resumed when there is no game to be in it (#568).
    /// </summary>
    /// <remarks>
    /// The game writes no shutdown marker, so the folder of a game that died mid-raid looks exactly
    /// like this one. What comes back names no state and no map; it only asks for the row a
    /// previous run left open to be closed as not reported.
    /// </remarks>
    [Fact]
    public async Task DoesNotResumeAnUnreportedRaidWhenTheGameIsNotRunning()
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
            await using var enumerator = new WindowsEftLogWatcher(
                    new EftLogParser(),
                    timeProvider: new FixedClock(SixMinutesIn),
                    gameLocator: new StubGameWindowLocator())
                .WatchAsync(root, timeout.Token)
                .GetAsyncEnumerator(timeout.Token);

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Null(enumerator.Current.SuggestedState);
            Assert.Null(enumerator.Current.MapId);
            Assert.True(enumerator.Current.EndsUnreported);
            Assert.True(enumerator.Current.ResumesSession);
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
            await using var enumerator = new WindowsEftLogWatcher(
                    new EftLogParser(),
                    timeProvider: new FixedClock(SixMinutesIn),
                    gameLocator: new RunningGameLocator())
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

    /// <summary>
    /// A folder the player named beats everything discovery found.
    /// </summary>
    /// <remarks>
    /// Reported by the first person to install this who does not use OneDrive: no screenshots
    /// detected, and no way at all to say where they were.
    /// </remarks>
    [Fact]
    public async Task AFolderThePlayerNamedWins()
    {
        var probe = new StubPathProbe(
            new(["install"], ["guessed-logs"], ["guessed-shots"]),
            new HashSet<string> { "install", "guessed-logs", "guessed-shots", "D:\\EFT\\Screenshots" });

        var paths = await new WindowsEftPathLocator(probe, new StubOverrides(new("D:\\EFT\\Screenshots", null)))
            .FindAsync(CancellationToken.None);

        Assert.Equal("D:\\EFT\\Screenshots", paths.ScreenshotRoot);
        Assert.Equal("guessed-logs", paths.LogRoot);
    }

    /// <summary>
    /// A typo leaves the guessing working rather than leaving the companion watching nothing.
    /// </summary>
    [Fact]
    public async Task AFolderThatIsNotThereIsIgnored()
    {
        var probe = new StubPathProbe(
            new([], [], ["guessed-shots"]),
            new HashSet<string> { "guessed-shots" });

        var paths = await new WindowsEftPathLocator(probe, new StubOverrides(new("D:\\typo", null)))
            .FindAsync(CancellationToken.None);

        Assert.Equal("guessed-shots", paths.ScreenshotRoot);
    }

    private sealed class StubOverrides(EftPathOverrides overrides) : IEftPathOverrideStore
    {
        public Task<EftPathOverrides> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(overrides);

        public Task SaveAsync(EftPathOverrides value, CancellationToken cancellationToken) =>
            Task.CompletedTask;
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
                .WatchSettledAsync(root, timeout.Token)
                .GetAsyncEnumerator(timeout.Token);
            var next = enumerator.MoveNextAsync().AsTask();
            var expected = Path.Combine(root, "2026-09-04[18-33]_1, 2, 3_0, 0, 0, 1.png");
            var completePng = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            await File.WriteAllBytesAsync(expected, completePng, timeout.Token);

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

    /// <summary>Six minutes after the raid in the resume fixtures began, in a zone with no offset.</summary>
    private static readonly DateTimeOffset SixMinutesIn = new(2026, 9, 11, 22, 5, 10, TimeSpan.Zero);

    /// <summary>A clock that stands still, in UTC, so a fixture's local stamps mean one thing on any machine.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class RunningGameLocator : IGameWindowLocator
    {
        public Task<WindowDescriptor?> FindAsync(bool developerMode, CancellationToken cancellationToken) =>
            Task.FromResult<WindowDescriptor?>(new WindowDescriptor(
                1, "EscapeFromTarkov", "EscapeFromTarkov", new PixelRect(0, 0, 1920, 1080), false, false));
    }

    private sealed class StubGameWindowLocator : IGameWindowLocator
    {
        public Task<WindowDescriptor?> FindAsync(bool developerMode, CancellationToken cancellationToken) =>
            Task.FromResult<WindowDescriptor?>(null);
    }
}
