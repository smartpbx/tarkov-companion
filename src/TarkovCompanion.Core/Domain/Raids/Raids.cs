using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Core.Domain.Raids;

public enum RaidLifecycleState
{
    Unknown,
    LauncherOrGameDetected,
    Menu,
    LoadingRaid,
    InRaid,
    PostRaid,
}

public enum RaidEvidenceKind
{
    ProcessDetected,
    LogLine,
    ScreenshotFilename,
    ManualOverride,
    Simulator,
}

public sealed record RaidEvidence(
    RaidEvidenceKind Kind,
    DateTimeOffset ObservedUtc,
    string? MapId,
    RaidLifecycleState? SuggestedState,
    Confidence Confidence,
    string Summary)
{
    /// <summary>"PMC" or "scav", where the source could tell them apart.</summary>
    /// <remarks>
    /// Optional so evidence from sources that cannot know stays silent rather than guessing.
    /// How it was established is carried separately in <see cref="SideBasis"/>, because the
    /// two ways of knowing are not equally strong and a reader is owed the difference.
    /// </remarks>
    public string? Side { get; init; }

    /// <summary>
    /// Whether this evidence is the game announcing that a raid is beginning.
    /// </summary>
    /// <remarks>
    /// Set by the game's own confirmation, and by a screenshot whose gap from the last one is
    /// too wide to be the same raid. It exists because a raid can no longer be recognised by
    /// the state changing: loading markers are ignored while a raid is running, which stopped
    /// them flapping, but it also meant a second raid beginning before the first was seen to
    /// end would be treated as a continuation of it and keep the old raid's map, start time
    /// and trail.
    /// </remarks>
    public bool StartsNewRaid { get; init; }

    /// <summary>
    /// The game's own id for the notification this came from, where it had one.
    /// </summary>
    /// <remarks>
    /// Every notification is written into two log files, so the companion sees each one twice.
    /// That was harmless until a confirmation began a raid, at which point the second copy
    /// began a second raid and threw away the first one's identity, start time and trail. The
    /// id is how one event is told from two.
    /// </remarks>
    public string? EventId { get; init; }

    /// <summary>
    /// The game's own short id for the raid this line belongs to (<c>shortId</c>), where the line had one.
    /// </summary>
    /// <remarks>
    /// <c>userConfirmed</c>, <c>userMatchOver</c> and the <c>profileStatus</c> line all carry it, and
    /// it is the only thing in the logs that says whether two lines describe one raid. A reconnect
    /// after the game died writes the same id into a new log folder; the next raid writes a new one.
    /// Measured on 2026-09-20: nine confirmations, eight ends, every id pairing except the raid the
    /// game process died in (#568).
    /// </remarks>
    public string? RaidKey { get; init; }

    /// <summary>The game log folder the line was read from, which is one launch of the game.</summary>
    /// <remarks>
    /// The fallback when a line carries no <see cref="RaidKey"/>: a raid that begins in a later
    /// launch than the open one cannot be the open one. Folder names sort by launch time.
    /// </remarks>
    public string? LogSession { get; init; }

    /// <summary>
    /// Whether this ends a raid whose end the game never reported, rather than reporting one.
    /// </summary>
    public bool EndsUnreported { get; init; }

    /// <summary>When the raid began, where the line that says so was read after the fact.</summary>
    /// <remarks>
    /// Only the startup replay sets it. Every other line is read as it is written, so the moment
    /// it was observed is the moment it happened; a replayed confirmation is minutes old, and
    /// dating the raid to the restart would restart its clock and its time bound with it.
    /// </remarks>
    public DateTimeOffset? RaidStartedUtc { get; init; }

    /// <summary>When a raid found dead by the startup replay last wrote anything.</summary>
    public DateTimeOffset? RaidLastSeenUtc { get; init; }

    /// <summary>
    /// Whether this is the companion catching up on a raid that was already running.
    /// </summary>
    /// <remarks>
    /// Only the startup replay sets it. A raid recovered this way is one the previous run of
    /// the companion may already have been recording, so whatever acts on it has to look for
    /// that row before opening a new one — and no other evidence can be, because every other
    /// piece arrives from a log line written while this process was watching.
    /// </remarks>
    public bool ResumesSession { get; init; }

    /// <summary>How <see cref="Side"/> was established, in the player's own words.</summary>
    /// <remarks>
    /// There are two routes and they differ in kind. Which profile ran the raid is an
    /// inference from an asymmetry in the logs; a transfer on the ending notification is
    /// proof, because it has never once appeared on a PMC raid. Presenting the second as the
    /// first would understate it, and the first as the second would be a lie, so the basis
    /// travels with the value rather than being reconstructed by whoever displays it.
    /// </remarks>
    public string? SideBasis { get; init; }

    /// <summary>
    /// Seconds matchmaking and loading took before this raid began, where the game said so.
    /// </summary>
    /// <remarks>Set only on the evidence that begins a raid, from the game's <c>MatchingCompleted</c> line.</remarks>
    public double? LoadSeconds { get; init; }

    /// <summary>
    /// An end that holds only for a raid the game never gave an id.
    /// </summary>
    /// <remarks>
    /// The game reloads the profile (<c>CompleteSelectedProfile</c>) when a player comes back to
    /// the menu. An offline or transit raid writes no <c>userMatchOver</c>, so that reload is the
    /// only end it has; on 2026-09-23 two offline Labs raids ran together into one without it
    /// (#892). An online raid carries its short id and ends on its own notification, and a game
    /// relaunched mid-raid reloads the profile before it reconnects, so it must not end that.
    /// </remarks>
    public bool EndsOnlyARaidWithoutId { get; init; }
}

/// <summary>One bar of the game's own display, and how long it has ever been.</summary>
/// <remarks>
/// The longest seen is kept per raid rather than for all time. A bar's full length is a fact
/// about this screen at this resolution, and a player who changed either would otherwise be
/// measured for the rest of the wipe against a bar that no longer exists.
/// </remarks>
/// <param name="Kind">Which bar, by the colour a player can see.</param>
/// <param name="Length">How long the drawn part was, in pixels.</param>
/// <param name="LongestSeen">The longest this colour has been this raid.</param>
public sealed record RaidHudBar(string Kind, int Length, int LongestSeen)
{
    /// <summary>
    /// How full it is against the longest seen, or null while that is all there is.
    /// </summary>
    /// <remarks>
    /// Null on the first reading rather than one. A companion that answered "full" the first
    /// time it saw a bar would be right only by accident, and wrong in the one case that
    /// matters: the first screenshot a player takes after running themselves empty.
    /// </remarks>
    public double? Fraction => LongestSeen <= 0 || LongestSeen <= Length ? null : (double)Length / LongestSeen;
}

/// <summary>What the game's display said, and when it said it.</summary>
/// <param name="IsPresent">Whether the display was drawn in that frame at all.</param>
/// <param name="Detail">What was found, or why nothing was.</param>
/// <param name="ReadUtc">When the screenshot it came from was taken.</param>
/// <param name="Bars">The bars, longest first.</param>
public sealed record RaidHudReading(
    bool IsPresent,
    string Detail,
    DateTimeOffset ReadUtc,
    IReadOnlyList<RaidHudBar> Bars);

public sealed record RaidSnapshot(
    Guid? RaidId,
    RaidLifecycleState State,
    string? MapId,
    DateTimeOffset? StartedUtc,
    DateTimeOffset UpdatedUtc,
    Confidence Confidence,
    ScreenshotPosition? LastKnownPosition,
    IReadOnlyList<ActiveExtract> ActiveExtracts,
    bool IsManualMapOverride)
{
    /// <summary>Whether the raid was run as a PMC or a scav, where known.</summary>
    public string? Side { get; init; }

    /// <summary>How that was established, carried through so the summary can say.</summary>
    public string? SideBasis { get; init; }

    /// <summary>The notification that began this raid, so a repeat of it does not begin another.</summary>
    public string? StartedByEventId { get; init; }

    /// <summary>The game's short id for this raid, where a line has named it. See <see cref="RaidEvidence.RaidKey"/>.</summary>
    public string? RaidKey { get; init; }

    /// <summary>The newest game log folder this raid has been seen in.</summary>
    public string? LogSession { get; init; }

    /// <summary>The last moment this raid itself showed activity, which is when an unreported end is dated.</summary>
    /// <remarks>
    /// Not <see cref="UpdatedUtc"/>: a game relaunched after it died writes menu lines for minutes
    /// before the next raid begins, and those are not the dead raid doing anything (#568).
    /// </remarks>
    public DateTimeOffset? LastActivityUtc { get; init; }

    /// <summary>
    /// Every screenshot position of this raid, oldest first.
    /// </summary>
    /// <remarks>
    /// A single point says where the player was; the sequence says where they have been, which
    /// is the more useful thing on a map. It belongs to one raid and is emptied when the next
    /// one begins, so a trail never crosses from a map the player has left.
    ///
    /// These are the same screenshots the player took themselves, so the trail is a record of
    /// their own evidence rather than any kind of tracking.
    /// </remarks>
    public IReadOnlyList<ScreenshotPosition> PositionTrail { get; init; } = [];

    /// <summary>
    /// The raid clock as it last appeared in a screenshot, and when that was read.
    /// </summary>
    /// <remarks>
    /// The game draws the remaining time on the extract list screen, so a player who
    /// photographs that screen has handed over the exact number. It is kept with the moment it
    /// was read because it is a reading rather than a value: three minutes later it is three
    /// minutes less, and without the timestamp it would be a stale claim presented as current.
    /// </remarks>
    public TimeSpan? RaidClock { get; init; }

    public DateTimeOffset? RaidClockReadUtc { get; init; }

    /// <summary>
    /// The two bars the game draws in the corner, as the last screenshot showed them.
    /// </summary>
    /// <remarks>
    /// Read on every frame, turned into one evidence string, and dropped. HudBar.Fraction has
    /// no production caller at all: the whole reading was computed and discarded, so a player
    /// who photographed themselves at a quarter of something had handed over the number and
    /// been told nothing.
    ///
    /// A reading with the moment it was read, like the raid clock above and for the same
    /// reason. Bars move continuously, so a reading four minutes old is a claim about four
    /// minutes ago and has to be shown as one.
    ///
    /// Named by colour throughout, which is not squeamishness. What each bar measures has not
    /// been established, the code that reads them says so, and calling one "stamina" would be a
    /// guess printed as a fact — while a player looking at their own screen knows which bar is
    /// which by looking at it.
    /// </remarks>
    public RaidHudReading? Hud { get; init; }

    /// <summary>
    /// Lines the last extract scan read and could not match to an exit on this map.
    /// </summary>
    /// <remarks>
    /// Read and thrown away until now, which made a scan that matched one exit out of eight
    /// indistinguishable from a screen that had one exit on it. Both look like "Extracts: one".
    /// Showing what was read and not matched turns the next report of this into an answer
    /// rather than an investigation.
    /// </remarks>
    public IReadOnlyList<string> ExtractLinesNotMatched { get; init; } = [];

    /// <summary>
    /// The transits the extract screen offered, as the screen named them.
    /// </summary>
    /// <remarks>
    /// The panel lists ways to another map alongside the exits from this one. They are labelled
    /// TRANSIT rather than EXFIL, drawn a different colour, and absent from every extract
    /// catalog, so matching them against one produced nothing but lines that failed. They are
    /// worth showing: leaving a raid through Factory is a decision somebody makes off this
    /// panel, and the name the screen prints says everything there is to know.
    /// </remarks>
    public IReadOnlyList<string> Transits { get; init; } = [];
}

public sealed record RaidHistoryEntry(
    Guid Id,
    Guid ProfileId,
    string? MapId,
    string Mode,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? EndedUtc,
    string? Outcome,
    string? Notes);
