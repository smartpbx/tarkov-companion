using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.App.Services.V2.Notifications;

/// <summary>
/// Whether closing the main window should hide into the tray instead of ending the process.
/// </summary>
/// <remarks>
/// <para>
/// [V2 rough package 43] Close-to-tray is the product behaviour for a player who clicks the
/// window's X: the companion stays reachable from the tray. It is wrong for every tool that
/// drives the packaged exe and expects <c>CloseMainWindow</c> to end the process — the Windows
/// page gallery and launch probe among them. Those launches used to be reported as "no window"
/// because the gallery only marks a page presented after a graceful exit, and a tray-held
/// process has to be killed.
/// </para>
/// <para>
/// Tool flags (<c>--page</c>, developer mode) and the verification environment
/// variables are enough: an ordinary player launch keeps close-to-tray, everything else closes
/// the way it did before this feature existed.
/// </para>
/// </remarks>
public static class CloseToTrayDecision
{
    /// <summary>
    /// Set by <c>scripts/windows-launch-probe.ps1</c> so an ordinary-looking launch still exits
    /// when the probe closes the window.
    /// </summary>
    public const string QuitOnCloseVariable = "TARKOV_COMPANION_QUIT_ON_CLOSE";

    /// <summary>
    /// Whether this launch should cancel window-close and hide into the tray.
    /// </summary>
    public static bool ShouldCloseToTray(bool trayIsAvailable, AppCommandLine? options) =>
        ShouldCloseToTray(
            trayIsAvailable,
            options,
            Environment.GetEnvironmentVariable(InterfaceWarningLog.PathVariable),
            Environment.GetEnvironmentVariable(QuitOnCloseVariable));

    /// <summary>Internal for direct coverage: the decision with environment already read.</summary>
    internal static bool ShouldCloseToTray(
        bool trayIsAvailable,
        AppCommandLine? options,
        string? warningLogPath,
        string? quitOnClose)
    {
        if (!trayIsAvailable)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(warningLogPath))
        {
            return false;
        }

        if (IsTruthy(quitOnClose))
        {
            return false;
        }

        if (options is null)
        {
            return true;
        }

        // Any launch a script aims at a named page or diagnostic host needs CloseMainWindow to
        // mean quit. The tray icon can still appear; only the close gesture changes. V2 is the
        // ordinary player shell now, so the shell mode itself is not a reason to disable this.
        if (options.StartPage is not null
            || options.DeveloperMode
            || options.MapRendererGallery
            || options.SelfTest
            || options.Headless
            || options.Demo
            || options.OcrProbePath is not null)
        {
            return false;
        }

        return true;
    }

    private static bool IsTruthy(string? value) =>
        value is not null
        && (value == "1"
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
