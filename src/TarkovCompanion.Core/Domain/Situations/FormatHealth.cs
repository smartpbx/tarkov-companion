namespace TarkovCompanion.Core.Domain.Situations;

/// <summary>A shape of the game's output the companion depends on, watched for silent format changes.</summary>
public enum FormatSource
{
    /// <summary>The <c>date time|version|level|source|</c> header every line of the logs opens with.</summary>
    GameLog,

    /// <summary>The <c>NOTIFICATION &lt;event&gt; &lt;type&gt; [{"type":…</c> envelope raid start, end and quests arrive in.</summary>
    Notification,

    /// <summary>The screenshot file name positions are read out of.</summary>
    ScreenshotName,
}

public enum FormatHealthStatus
{
    /// <summary>Too little seen since start or since the game version changed to judge.</summary>
    Unknown,
    Ok,

    /// <summary>Most recent lines or names were not in any shape the parsers know.</summary>
    Degraded,
}

/// <summary>One source's recent recognised and unrecognised counts, and what they add up to.</summary>
/// <param name="GameVersion">The game build the lines came from (the log folder's name), where known.</param>
/// <param name="LastHealthyVersion">The build under which this source was last read as OK.</param>
/// <param name="SinceUtc">When <paramref name="Status"/> last changed.</param>
public sealed record FormatSourceHealth(
    FormatSource Source,
    FormatHealthStatus Status,
    int Recognised,
    int Unrecognised,
    string? GameVersion,
    string? LastHealthyVersion,
    DateTimeOffset? SinceUtc)
{
    /// <summary>Degraded, and the build is not the one this source last read well under: a game update did it.</summary>
    public bool ChangedAfterUpdate =>
        Status == FormatHealthStatus.Degraded
        && GameVersion is not null
        && LastHealthyVersion is not null
        && !string.Equals(GameVersion, LastHealthyVersion, StringComparison.Ordinal);
}

/// <summary>Format health for every watched source at one moment.</summary>
public sealed record FormatHealthReport(IReadOnlyList<FormatSourceHealth> Sources, string? GameVersion)
{
    public static FormatHealthReport Empty { get; } = new(
        [.. Enum.GetValues<FormatSource>().Select(source => new FormatSourceHealth(source, FormatHealthStatus.Unknown, 0, 0, null, null, null))],
        null);

    public FormatSourceHealth For(FormatSource source) =>
        Sources.FirstOrDefault(health => health.Source == source)
        ?? new FormatSourceHealth(source, FormatHealthStatus.Unknown, 0, 0, GameVersion, null, null);

    public bool IsDegraded => Sources.Any(health => health.Status == FormatHealthStatus.Degraded);

    /// <summary>The log side as one answer: degraded if the header or the notification envelope is.</summary>
    public FormatHealthStatus Logs =>
        For(FormatSource.GameLog).Status == FormatHealthStatus.Degraded
        || For(FormatSource.Notification).Status == FormatHealthStatus.Degraded
            ? FormatHealthStatus.Degraded
            : For(FormatSource.GameLog).Status;

    public FormatHealthStatus Screenshots => For(FormatSource.ScreenshotName).Status;
}
