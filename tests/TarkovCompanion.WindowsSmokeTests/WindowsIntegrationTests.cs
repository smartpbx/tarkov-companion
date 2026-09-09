using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Platform.Windows.Capture;
using TarkovCompanion.Platform.Windows.Discovery;
using TarkovCompanion.Platform.Windows.Displays;
using TarkovCompanion.Platform.Windows.Hotkeys;
using TarkovCompanion.Platform.Windows.Security;
using TarkovCompanion.Platform.Windows.Watching;

namespace TarkovCompanion.WindowsSmokeTests;

public sealed class WindowsIntegrationTests
{
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
            await File.AppendAllTextAsync(path, "raid_loading location=woods\n", timeout.Token);

            Assert.True(await next.WaitAsync(timeout.Token));
            Assert.Equal(RaidLifecycleState.LoadingRaid, enumerator.Current.SuggestedState);
            Assert.Equal("woods", enumerator.Current.MapId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NativeServicesAreWindowsGated()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var locator = new StubGameWindowLocator();
        var capture = new GdiScreenCaptureService(locator);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => capture.CaptureAsync(
            new CaptureRequest("eft", null, false, "test"),
            CancellationToken.None));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => new WindowsDpapiSecretStore().GetAsync(
            "fixture",
            CancellationToken.None));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => new WindowsGlobalHotkeyService().RegisterAsync(
            new HotkeyGesture(0, 0x7B, "F12"),
            CancellationToken.None));
        Assert.Empty(await new WindowsMonitorService().GetDisplaysAsync(CancellationToken.None));
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
