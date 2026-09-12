using System.Globalization;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.App.ViewModels;

/// <summary>
/// The raid that has just finished, rendered as display-ready text.
/// </summary>
/// <remarks>
/// Every member is a finished sentence or a formatted value rather than a domain type, so
/// the view holds no formatting logic and the summary can be asserted in tests as the player
/// will read it.
///
/// The fields are deliberately limited to what Escape from Tarkov actually writes down.
/// docs/research/EFT_LOG_FACTS.md records, against a live installation, that the logs carry
/// no raid outcome, no kills, no loot value and no experience, so there is no honest way to
/// show any of it and no field is offered for it. <see cref="Outcome"/> says so once, and
/// nothing here is inferred from the duration: a short raid is not evidence of a death.
/// </remarks>
/// <param name="Headline">Map and duration together, for the one line worth reading first.</param>
/// <param name="Map">The map the raid was played on, by display name where one is known.</param>
/// <param name="Started">Local time the game confirmed the raid.</param>
/// <param name="Ended">Local time the game reported the raid over.</param>
/// <param name="Duration">Exact elapsed time between those two notifications.</param>
/// <param name="Timing">Why the duration is exact rather than estimated.</param>
/// <param name="Mode">The game mode the local profile is set to, which is not the PMC/scav side.</param>
/// <param name="Side">Which side ran the raid and how that was established.</param>
/// <param name="Outcome">The plain statement that survival is not recorded anywhere.</param>
/// <param name="Scans">What the player scanned while this raid was open.</param>
/// <param name="LastKnownPosition">The last screenshot-derived position, if any was taken.</param>
/// <param name="History">Whether the raid reached the local history database.</param>
public sealed record RaidSummaryViewModel(
    string Headline,
    string Map,
    string Started,
    string Ended,
    string Duration,
    string Timing,
    string Mode,
    string Side,
    string Outcome,
    string Scans,
    string LastKnownPosition,
    string History)
{
    /// <summary>
    /// The one statement made about how the raid went.
    /// </summary>
    /// <remarks>
    /// Stated once and then left alone. The game holds the exit status and never logs it, so
    /// every other tool that shows survived or died is reading something this companion does
    /// not have. Saying nothing at all would be worse: the player would assume the summary
    /// simply failed to load it.
    /// </remarks>
    public const string OutcomeNotRecorded =
        "Outcome is not recorded. The game never writes whether a raid was survived, died in, or run through, "
        + "and it is not guessed here.";

    /// <summary>
    /// Why the summary declines to name the side.
    /// </summary>
    /// <remarks>
    /// Reached when neither route could tell: the profile that ran the raid was not one the
    /// reader recognises, and the raid did not end with a transfer. Naming a side here would
    /// be inventing one.
    /// </remarks>
    public const string SideNotCarried =
        "PMC or scav was not established for this raid.";

    /// <summary>Describes the side, together with how it came to be known.</summary>
    /// <remarks>
    /// The two ways of knowing are not equally strong. Which profile ran the raid is an
    /// inference from an asymmetry in the logs; a transfer on the ending notification is
    /// proof, having never once appeared on a PMC raid. This used to describe every side as
    /// inferred from the profile, which stopped being true the moment the second route
    /// existed, so the basis is carried here rather than assumed.
    /// </remarks>
    private static string DescribeSide(string? side, string? basis) => string.IsNullOrWhiteSpace(side)
        ? SideNotCarried
        : string.IsNullOrWhiteSpace(basis)
            ? $"{side} raid. How that was established was not recorded."
            : $"{side} raid. {basis}";

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
    /// <param name="scannedItems">Items scanned while this raid was open, in the order scanned.</param>
    /// <param name="lastKnownPosition">The last screenshot-derived position of the raid, if any.</param>
    /// <param name="history">What is known so far about the local history row for this raid.</param>
    public static RaidSummaryViewModel Create(
        string mapName,
        DateTimeOffset? startedUtc,
        DateTimeOffset endedUtc,
        string? gameMode,
        string? side,
        string? sideBasis,
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
            startedUtc is { } startedValue
                ? FormatMoment(startedValue)
                : "The start of this raid was not observed.",
            FormatMoment(endedUtc),
            duration is { } durationValue
                ? FormatDuration(durationValue)
                : "Unavailable",
            duration is null
                ? "Only the end of this raid was observed, so no duration can be given."
                : "Start and end are the game's own notifications for this profile, so the duration is exact "
                    + "rather than estimated.",
            string.IsNullOrWhiteSpace(gameMode)
                ? "Game mode is unknown; no local profile is loaded."
                : $"{gameMode} · the mode the local profile is set to.",
            DescribeSide(side, sideBasis),
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
            return "Nothing was scanned during this raid.";
        }

        var listed = string.Join(", ", scannedItems.Take(MaximumScansListed));
        var remainder = scannedItems.Count - Math.Min(scannedItems.Count, MaximumScansListed);
        var counted = scannedItems.Count == 1
            ? "1 scan"
            : string.Create(CultureInfo.CurrentCulture, $"{scannedItems.Count} scans");
        return remainder == 0
            ? $"{counted}: {listed}."
            : string.Create(CultureInfo.CurrentCulture, $"{counted}: {listed}, and {remainder} more.");
    }

    /// <summary>
    /// Describes where the player last was, which only a screenshot can establish.
    /// </summary>
    /// <remarks>
    /// This is historical screenshot evidence and never live tracking, so it is described as
    /// the moment the screenshot was taken rather than as a current position.
    /// </remarks>
    private static string DescribePosition(ScreenshotPosition? lastKnownPosition) => lastKnownPosition is null
        ? "No screenshot was taken during this raid, so there is no last-known position."
        : string.Create(
            CultureInfo.CurrentCulture,
            $"X {lastKnownPosition.Position.X:F1}, Y {lastKnownPosition.Position.Y:F1}, Z {lastKnownPosition.Position.Z:F1} · from a screenshot at {lastKnownPosition.Timestamp.ToLocalTime():T}.");
}
