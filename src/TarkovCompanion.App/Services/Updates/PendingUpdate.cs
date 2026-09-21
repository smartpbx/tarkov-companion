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
        ? $"{Version} is downloaded but the last attempt did not apply"
        : $"{Version} is downloaded but the last attempt did not apply · {Reason}";
}

/// <summary>What Setup > Updates says beside a build that did not apply.</summary>
public static class PendingUpdateText
{
    public const string ApplyNow = "Apply now";

    public const string RunInstaller = "Run the installer instead";

    /// <remarks>
    /// Not "close the game": that was the first theory and it was refuted. What held the install
    /// folder was a program the companion had started (a browser, a file manager) still standing
    /// in it, and those two things are what end that.
    /// </remarks>
    public const string Advice = "Restart the PC or close programs opened from the companion, then Apply now.";
}
