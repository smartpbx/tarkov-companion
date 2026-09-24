using TarkovCompanion.App.Localization;
namespace TarkovCompanion.App.Services.Updates;

/// <summary>
/// A build that was downloaded and is still not the one running.
/// </summary>
/// <remarks>
/// #599. The updater applies a downloaded build before the application starts, so an application
/// that starts and finds a newer package beside it is looking at an update that was tried and
/// did not work. For four builds the owner was shown "2.0.1337 is available", pressed Update,
/// watched the updater, and came back to 2.0.1324 with the same offer and no word of why. The
/// reason was in the updater's own log the whole time.
/// </remarks>
/// <param name="Version">The downloaded build.</param>
/// <param name="Reason">The updater's last "Apply error", when its log could be read.</param>
public sealed record PendingUpdate(string Version, string? Reason)
{
    /// <summary>The line on Setup > Updates.</summary>
    public string Status => string.IsNullOrWhiteSpace(Reason)
        ? SetupText.UpdateDidNotApply(Version)
        : SetupText.UpdateDidNotApplyBecause(Version, Reason);
}

/// <summary>What Setup > Updates says beside a build that did not apply.</summary>
public static class PendingUpdateText
{
    public static string ApplyNow => SetupText.UpdateApplyNow;

    public static string RunInstaller => SetupText.UpdateRunInstaller;

    /// <remarks>
    /// Not "close the game": that was the first theory and it was refuted. What held the install
    /// folder was a program the companion had started (a browser, a file manager) still standing
    /// in it, and those two things are what end that.
    /// </remarks>
    public static string Advice => SetupText.UpdateAdvice;
}
