using System.Globalization;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.App.ViewModels;

/// <summary>
/// The raid that has just finished, rendered as display-ready values.
/// </summary>
/// <remarks>
/// Every member is a value or a short phrase rather than a domain type, so the view holds no
/// formatting logic and the summary can be asserted in tests as the player will read it.
///
/// The fields are limited to what Escape from Tarkov actually writes down.
/// docs/research/EFT_LOG_FACTS.md records, against a live installation, that the logs carry no
/// raid outcome, no kills, no loot value and no experience, so no field is offered for any of
/// it and nothing is inferred from the duration: a short raid is not evidence of a death.
///
/// This used to explain itself in sentences: why the duration was exact, how the side came to
/// be known, that a screenshot is the only thing that can establish a position. None of that
/// changed what the player did next, and it filled the panel beside the map.
/// </remarks>
/// <param name="Headline">Map and duration together, for the one line worth reading first.</param>
/// <param name="Map">The map the raid was played on, by display name where one is known.</param>
/// <param name="Started">Local time the game confirmed the raid.</param>
/// <param name="Ended">Local time the game reported the raid over.</param>
/// <param name="Duration">Elapsed time between those two notifications.</param>
/// <param name="Mode">The game mode the local profile is set to, which is not the PMC/scav side.</param>
/// <param name="Side">PMC or scav, where either route could tell.</param>
/// <param name="Outcome">That survival is not recorded anywhere.</param>
/// <param name="Scans">What the player scanned while this raid was open.</param>
/// <param name="LastKnownPosition">The last screenshot-derived position, if any was taken.</param>
/// <param name="History">Whether the raid reached the local history database.</param>
public sealed record RaidSummaryViewModel(
    string Headline,
    string Map,
    string Started,
    string Ended,
    string Duration,
    string Mode,
    string Side,
    string Outcome,
    string Scans,
    string LastKnownPosition,
    string History)
{
    /// <summary>
    /// The quests the game announced during this raid, where any were.
    /// </summary>
    /// <remarks>
    /// Read back out of the raid's own record rather than counted as they went past. Counting
    /// works only while the application that saw them is still running; a raid opened from
    /// History a week later has to ask the database.
    ///
    /// An init property rather than two more positional parameters on a record that already
    /// takes eleven.
    /// </remarks>
    public string Quests { get; init; } = string.Empty;

    /// <summary>What sold on the flea while this raid was open.</summary>
    public string Sales { get; init; } = string.Empty;

    public bool HasQuests => Quests.Length > 0;

    public bool HasSales => Sales.Length > 0;

    /// <summary>
    /// What the summary says about how the raid went.
    /// </summary>
    /// <remarks>
    /// The game holds the exit status and never writes it down, so every other tool that shows
    /// survived or died is reading something this companion does not have. Saying nothing at
    /// all would read as a field that failed to load.
    /// </remarks>
    public const string OutcomeNotRecorded = "Not logged";

    /// <summary>Neither route could tell which side ran the raid.</summary>
    public const string SideNotCarried = "Not known";

    /// <summary>Nothing was observed, rather than a value of zero.</summary>
    public const string NotSeen = "Not seen";

    private const int MaximumScansListed = 8;

    /// <summary>
    /// Builds the summary from the last observed in-raid state and the end of the raid.
    /// </summary>
    /// <remarks>
    /// The finished raid has to be supplied by the caller rather than read from the snapshot
    /// that ends it: returning to the menu clears the map, the start time and the last-known
    /// position out of the raid state, so by the time the end is visible the details of the
    /// raid are already gone.
    /// </remarks>
    /// <param name="mapName">Display name of the map, or a stand-in when it was never observed.</param>
    /// <param name="startedUtc">When the game confirmed the raid, if that was observed.</param>
    /// <param name="endedUtc">When the game reported the raid over.</param>
    /// <param name="gameMode">The local profile's game mode, or null when no profile is loaded.</param>
    /// <param name="side">PMC or scav, where it was established.</param>
    /// <param name="scannedItems">Items scanned while this raid was open, in the order scanned.</param>
    /// <param name="lastKnownPosition">The last screenshot-derived position of the raid, if any.</param>
    /// <param name="history">What is known so far about the local history row for this raid.</param>
    public static RaidSummaryViewModel Create(
        string mapName,
        DateTimeOffset? startedUtc,
        DateTimeOffset endedUtc,
        string? gameMode,
        string? side,
        IReadOnlyList<string> scannedItems,
        ScreenshotPosition? lastKnownPosition,
        string history)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapName);
        ArgumentNullException.ThrowIfNull(scannedItems);
        ArgumentException.ThrowIfNullOrWhiteSpace(history);

        // A start that is missing or later than the end means the pair of notifications was
        // not seen in full. Reporting no duration is correct; a negative or invented one is
        // not.
        TimeSpan? duration = startedUtc is { } started && endedUtc >= started
            ? endedUtc - started
            : null;

        return new RaidSummaryViewModel(
            duration is { } headlineDuration
                ? $"{mapName} · {FormatDuration(headlineDuration)}"
                : mapName,
            mapName,
            startedUtc is { } startedValue ? FormatMoment(startedValue) : NotSeen,
            FormatMoment(endedUtc),
            duration is { } durationValue ? FormatDuration(durationValue) : NotSeen,
            string.IsNullOrWhiteSpace(gameMode) ? "Unknown" : gameMode,
            string.IsNullOrWhiteSpace(side) ? SideNotCarried : side,
            OutcomeNotRecorded,
            DescribeScans(scannedItems),
            DescribePosition(lastKnownPosition),
            history);
    }

    private static string FormatMoment(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>
    /// Renders an elapsed time the way a player reads a raid: minutes and seconds.
    /// </summary>
    private static string FormatDuration(TimeSpan duration)
    {
        var totalSeconds = (int)Math.Round(duration.TotalSeconds, MidpointRounding.AwayFromZero);
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        return hours > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{hours}h {minutes}m {seconds}s")
            : minutes > 0
                ? string.Create(CultureInfo.CurrentCulture, $"{minutes}m {seconds}s")
                : string.Create(CultureInfo.CurrentCulture, $"{seconds}s");
    }

    private static string DescribeScans(IReadOnlyList<string> scannedItems)
    {
        if (scannedItems.Count == 0)
        {
            return "Nothing scanned";
        }

        var listed = string.Join(", ", scannedItems.Take(MaximumScansListed));
        var remainder = scannedItems.Count - Math.Min(scannedItems.Count, MaximumScansListed);
        return remainder == 0
            ? listed
            : string.Create(CultureInfo.CurrentCulture, $"{listed}, +{remainder} more");
    }

    /// <summary>
    /// Where the player last was, which only a screenshot can establish.
    /// </summary>
    /// <remarks>
    /// X and Z are the two the map draws. Y is height and is kept because it is the floor a
    /// building was on, which is the thing worth going back for.
    /// </remarks>
    private static string DescribePosition(ScreenshotPosition? lastKnownPosition) => lastKnownPosition is null
        ? "No screenshot taken"
        : string.Create(
            CultureInfo.CurrentCulture,
            $"{lastKnownPosition.Position.X:F0}, {lastKnownPosition.Position.Y:F0}, {lastKnownPosition.Position.Z:F0} · {lastKnownPosition.Timestamp.ToLocalTime():T}");
}
