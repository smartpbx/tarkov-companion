using TarkovCompanion.Application.Services.Shell;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.UnitTests;

public sealed class DesktopWindowPlacementTests
{
    [Fact]
    public void RestoreFitsTheWholeWindowInsideTheWorkArea()
    {
        var display = Display("primary", 0, 0, 1920, 1080, 1, primary: true, workHeight: 1040);
        var saved = new MonitorWindowPlacement(
            DesktopWindowPlacement.MonitorKey(display),
            1920,
            1080,
            0,
            0,
            false);

        var restored = DesktopWindowPlacement.Restore(saved, display);

        Assert.Equal(1920, restored.Width);
        Assert.Equal(1040, restored.Height);
        Assert.Equal(0, restored.Left);
        Assert.Equal(0, restored.Top);
    }

    [Fact]
    public void MissingMonitorFallsBackToThePrimaryAndKeepsThePhysicalSize()
    {
        var present = Display("primary", 0, 0, 1920, 1080, 1.25, primary: true, workHeight: 1040);
        var missing = new MonitorWindowPlacement("gone|2560x1440", 1250, 900, 300, 100, true);

        var target = DesktopWindowPlacement.PreferredDisplay([present], missing.MonitorKey);
        var moved = DesktopWindowPlacement.ForFallback(missing, target!, 1500, 900);
        var restored = DesktopWindowPlacement.Restore(moved, target!);

        Assert.Equal(present.Id, restored.MonitorId);
        Assert.Equal(1000, restored.Width);
        Assert.Equal(720, restored.Height);
        Assert.True(restored.IsMaximized);
    }

    [Fact]
    public void DpiChangePreservesPhysicalWindowSize()
    {
        var at100 = Display("same", 0, 0, 2560, 1440, 1, primary: true, workHeight: 1400);
        var at150 = Display("same", 0, 0, 2560, 1440, 1.5, primary: true, workHeight: 1400);
        var saved = DesktopWindowPlacement.Capture(at100, 1200, 800, 100, 50, false);

        var restored = DesktopWindowPlacement.Restore(saved, at150);

        Assert.Equal(800, restored.Width);
        Assert.Equal(1200, restored.Width * at150.Scale);
        Assert.Equal(800, restored.Height * at150.Scale);
    }

    [Fact]
    public void MonitorKeyIncludesDeviceNameAndResolution()
    {
        var original = Display("device", 0, 0, 1920, 1080, 1, true, 1040);
        var resolutionChanged = Display("device", 0, 0, 2560, 1440, 1, true, 1400);

        Assert.Equal("device|1920x1080", DesktopWindowPlacement.MonitorKey(original));
        Assert.NotEqual(
            DesktopWindowPlacement.MonitorKey(original),
            DesktopWindowPlacement.MonitorKey(resolutionChanged));
    }

    [Fact]
    public async Task JsonStoreKeepsOnePlacementPerMonitor()
    {
        var root = Path.Combine(Path.GetTempPath(), $"placement-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "window-placement.json");
        try
        {
            var store = new JsonFileDesktopWindowPlacementStore(path);
            var state = new DesktopWindowPlacementState(
                "second|2560x1440",
                [
                    new("primary|1920x1080", 1200, 800, 20, 30, false),
                    new("second|2560x1440", 1600, 1000, 90, 40, true),
                ]);

            await store.SaveAsync(state, default);
            var restored = await store.GetAsync(default);

            Assert.Equal(state.ActiveMonitorKey, restored.ActiveMonitorKey);
            Assert.Equal(state.Monitors, restored.Monitors);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(1.0, 16, 39)]
    [InlineData(1.5, 24, 58)]
    public void RestoreFitsTheClientAndItsFrameIntoTheWorkArea(double scale, int framePixelsWide, int framePixelsHigh)
    {
        // [#881 follow-up] The placement kept is the client's size at the frame's position. A
        // window saved at the screen's size came back with its client the work area's full
        // height, so the frame's title bar pushed the bottom — and the rail's gear — under the
        // taskbar. This controller runs after the preview store's fit and undid it on every launch.
        var display = Display("primary", 0, 0, 1920, 1080, scale, primary: true, workHeight: 1032);
        var saved = new MonitorWindowPlacement(DesktopWindowPlacement.MonitorKey(display), 1920, 1080, 0, 0, false);

        var restored = DesktopWindowPlacement.Restore(saved, display, framePixelsWide, framePixelsHigh);

        Assert.Equal((1920 - framePixelsWide) / scale, restored.Width, 3);
        Assert.Equal((1032 - framePixelsHigh) / scale, restored.Height, 3);
        Assert.Equal(0, restored.Top);
        Assert.True(restored.Top + (restored.Height * scale) + framePixelsHigh <= 1032.001);
        Assert.True(restored.Left + (restored.Width * scale) + framePixelsWide <= 1920.001);
    }

    [Fact]
    public void AWindowThatFitsWithItsFrameIsLeftWhereItWas()
    {
        var display = Display("primary", 0, 0, 1920, 1080, 1, primary: true, workHeight: 1032);
        var saved = new MonitorWindowPlacement(DesktopWindowPlacement.MonitorKey(display), 1500, 900, 200, 60, false);

        var restored = DesktopWindowPlacement.Restore(saved, display, 16, 39);

        Assert.Equal((1500d, 900d, 200, 60), (restored.Width, restored.Height, restored.Left, restored.Top));
    }

    private static DisplayDescriptor Display(
        string id,
        int x,
        int y,
        int width,
        int height,
        double scale,
        bool primary,
        int workHeight) =>
        new(id, id, new PixelRect(x, y, width, height), primary, scale, new PixelRect(x, y, width, workHeight));
}
