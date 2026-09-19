using TarkovCompanion.GroupServer.Diagnostics;

namespace TarkovCompanion.GroupServer;

/// <summary>What the relay will hold of the reports players send, and for how long.</summary>
/// <remarks>
/// Every limit here exists because a report is text from the network kept on a small box: the per-room
/// hourly limit alone stops one client looping and does nothing about many rooms, a full disk, or
/// reports that outlive any reason to keep them. Environment overrides are clamped rather than
/// trusted, so a typo cannot turn a limit into "keep everything for ever".
/// </remarks>
public sealed record ProblemReportLimits(
    TimeSpan TimeToLive,
    int MaximumHeld,
    long MaximumHeldBytes,
    int MaximumHeldPerRoom,
    int MaximumPerHour,
    int MinimumFreeMegabytes,
    int MaximumFilingAttempts)
{
    public const string TimeToLiveDaysVariable = "TARKOV_RELAY_REPORT_TTL_DAYS";
    public const string MaximumHeldVariable = "TARKOV_RELAY_REPORT_MAX_HELD";
    public const string MaximumMegabytesVariable = "TARKOV_RELAY_REPORT_MAX_MEGABYTES";
    public const string MinimumFreeMegabytesVariable = "TARKOV_RELAY_REPORT_MIN_FREE_MB";

    public static ProblemReportLimits Default { get; } = new(
        TimeSpan.FromDays(30),
        MaximumHeld: 200,
        MaximumHeldBytes: 16L * 1024 * 1024,
        MaximumHeldPerRoom: 10,
        MaximumPerHour: 60,
        MinimumFreeMegabytes: RelayReadiness.MinimumFreeDiskMegabytes,
        MaximumFilingAttempts: 5);

    public static ProblemReportLimits FromEnvironment(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return Default with
        {
            TimeToLive = TimeSpan.FromDays(Clamped(read(TimeToLiveDaysVariable), 1, 365, 30)),
            MaximumHeld = Clamped(read(MaximumHeldVariable), 1, 5_000, Default.MaximumHeld),
            MaximumHeldBytes = Clamped(read(MaximumMegabytesVariable), 1, 1_024, 16) * 1024L * 1024,
            MinimumFreeMegabytes = Clamped(read(MinimumFreeMegabytesVariable), 0, 100_000_000, Default.MinimumFreeMegabytes),
        };
    }

    // Anything that is not a whole number in range is the default, not the nearest bound: a value that
    // could not be read is not evidence of what the operator meant.
    private static int Clamped(string? text, int minimum, int maximum, int fallback) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) &&
        value >= minimum && value <= maximum
            ? value
            : fallback;
}
