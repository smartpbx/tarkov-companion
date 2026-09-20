using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Notifications;

namespace TarkovCompanion.UnitTests.Notifications;

public sealed class CloseToTrayDecisionTests
{
    [Fact]
    public void Ordinary_player_launch_with_a_tray_closes_to_tray()
    {
        var options = AppCommandLine.Parse([]);

        Assert.True(CloseToTrayDecision.ShouldCloseToTray(
            trayIsAvailable: true,
            options,
            warningLogPath: null,
            quitOnClose: null));
    }

    [Fact]
    public void No_tray_never_closes_to_tray()
    {
        Assert.False(CloseToTrayDecision.ShouldCloseToTray(
            trayIsAvailable: false,
            AppCommandLine.Parse([]),
            warningLogPath: null,
            quitOnClose: null));
    }

    [Theory]
    [InlineData("--page", "Raid")]
    [InlineData("--developer-mode", null)]
    [InlineData("--map-renderer-gallery", null)]
    [InlineData("--self-test", null)]
    [InlineData("--demo", null)]
    [InlineData("--headless", null)]
    public void Tool_launches_quit_on_window_close(string flag, string? value)
    {
        var args = value is null ? new[] { flag } : new[] { flag, value };
        // --self-test needs --output; Parse still records SelfTest=true either way.
        if (flag == "--self-test")
        {
            args = ["--self-test", "--output", "out.json"];
        }

        Assert.False(CloseToTrayDecision.ShouldCloseToTray(
            trayIsAvailable: true,
            AppCommandLine.Parse(args),
            warningLogPath: null,
            quitOnClose: null));
    }

    [Fact]
    public void Page_gallery_warning_log_disables_close_to_tray()
    {
        Assert.False(CloseToTrayDecision.ShouldCloseToTray(
            trayIsAvailable: true,
            AppCommandLine.Parse([]),
            warningLogPath: @"D:\a\_temp\warnings.log",
            quitOnClose: null));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("YES")]
    public void Launch_probe_quit_on_close_disables_close_to_tray(string value)
    {
        Assert.False(CloseToTrayDecision.ShouldCloseToTray(
            trayIsAvailable: true,
            AppCommandLine.Parse([]),
            warningLogPath: null,
            quitOnClose: value));
    }
}
