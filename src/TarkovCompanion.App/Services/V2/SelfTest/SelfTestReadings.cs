using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services;

namespace TarkovCompanion.App.Services.V2.SelfTest;

/// <summary>One folder the companion chose, why it chose it, and when it last changed.</summary>
/// <param name="Purpose">"Install", "Logs" or "Screenshots".</param>
/// <param name="Why">How this path was arrived at, in one clause.</param>
/// <param name="ChangedUtc">The newest write anywhere inside it, or null when it holds nothing.</param>
public sealed record SelfTestFolderReading(
    string Purpose,
    string? Path,
    string Why,
    bool Exists,
    DateTimeOffset? ChangedUtc,
    int Entries,
    string? Problem = null);

/// <summary>What discovery settled on, and what is actually in those folders now.</summary>
public sealed record SelfTestFolders(
    bool Supported,
    string Detail,
    DateTimeOffset CheckedUtc,
    IReadOnlyList<SelfTestFolderReading> Folders,
    string? Problem = null);

/// <summary>What one read of the newest game session understood.</summary>
/// <remarks>
/// Counts rather than contents. The probe replays the session's own lines through the same
/// parsers the watcher uses, so "understood nothing" here means the same thing it would mean
/// during a raid — and that is precisely the state that used to be invisible.
/// </remarks>
/// <summary>One of the session's log files, and what came out of it.</summary>
/// <param name="Name">The file's own name.</param>
/// <param name="Mode">Whether every line was parsed, or only the chat notifications.</param>
/// <param name="Bytes">How large the file is.</param>
/// <param name="LinesRead">How many of its lines were read.</param>
/// <param name="QuestEvents">Quest notifications found in this file.</param>
/// <param name="FleaSales">Flea sales found in this file.</param>
/// <param name="Problem">Why this file could not be read, where it could not.</param>
public sealed record SelfTestLogFile(
    string Name,
    LogReadMode Mode,
    long Bytes,
    int LinesRead,
    int QuestEvents,
    int FleaSales,
    string? Problem = null);

/// <summary>
/// What this build understood of the newest game session.
/// </summary>
/// <remarks>
/// The totals are across every file of the session that the watcher would open, and
/// <see cref="Files"/> breaks them down per file. Both matter: a build that read seven hundred
/// lines and found no quest in them is reporting something very different depending on whether
/// it looked at one file or at six, and an earlier version of this reading looked at one and
/// said neither.
/// </remarks>
public sealed record SelfTestLogs(
    string? SessionFolder,
    DateTimeOffset? SessionStartedUtc,
    string? FileName,
    long Bytes,
    int LinesRead,
    int RaidsSeen,
    string? LastRaidMap,
    string? LastRaidState,
    DateTimeOffset? LastRaidAtUtc,
    TimeSpan? QueueTime,
    int QuestEvents,
    int FleaSales,
    DateTimeOffset ReadUtc,
    string? Problem = null)
{
    /// <summary>
    /// Every file of the session that was read, and what each yielded.
    /// </summary>
    /// <remarks>
    /// An init property rather than a positional parameter, so the call sites and test doubles
    /// that build this by hand are unaffected by its arrival.
    /// </remarks>
    public IReadOnlyList<SelfTestLogFile> Files { get; init; } = [];

    /// <summary>Files of the session that hold no line this companion has a use for.</summary>
    public IReadOnlyList<string> SkippedFiles { get; init; } = [];
}

/// <summary>A screenshot that arrived while the self-test was watching, and what came out of it.</summary>
/// <param name="Clock">Which clock the time came from: the file's own, or the one in the name.</param>
/// <param name="EndToEnd">From the game writing the file to this application parsing it.</param>
public sealed record SelfTestScreenshot(
    string? Root,
    string? FileName,
    DateTimeOffset? WrittenUtc,
    DateTimeOffset? NoticedUtc,
    bool Parsed,
    double? X,
    double? Y,
    double? Z,
    string Clock,
    TimeSpan Waited,
    TimeSpan? EndToEnd,
    string? Problem = null)
{
    /// <summary>
    /// True when this screenshot was already on disk when the probe started.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 43a] Reported by Clayton: the probe failed "only because i cant alt tab
    /// back to the game and screenshot fast enough". A shot taken in the last few minutes is
    /// evidence of exactly the same thing and one almost always exists, so the probe uses it —
    /// and says which file, because "it worked" about an unnamed file is the kind of claim this
    /// page exists to replace.
    /// </remarks>
    public bool WasAlreadyThere { get; init; }

    /// <summary>
    /// What kind of name the file carries, which decides whether not parsing is a fault.
    /// </summary>
    /// <remarks>
    /// A screenshot taken in the menu or after a raid carries no position by design, so refusing
    /// to read one is correct behaviour and must never be reported as broken.
    /// </remarks>
    public ScreenshotNameKind NameKind { get; init; } = ScreenshotNameKind.Unrecognized;

    /// <summary>How old the file was when it was read, for one that was already there.</summary>
    public TimeSpan? Age { get; init; }
}

/// <summary>One game-data endpoint as the local database last recorded it.</summary>
public sealed record SelfTestEndpoint(
    string Name,
    long Bytes,
    DateTimeOffset? RefreshedUtc,
    int? Rows,
    string Status,
    string? Error = null);

public sealed record SelfTestGameData(
    string GameMode,
    string Language,
    IReadOnlyList<SelfTestEndpoint> Endpoints,
    DateTimeOffset ReadUtc,
    string? Problem = null);

public sealed record SelfTestTable(string Name, long Rows);

public sealed record SelfTestDatabase(
    string? Path,
    long Bytes,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Expected,
    IReadOnlyList<SelfTestTable> Tables,
    DateTimeOffset ReadUtc,
    string? Problem = null);

/// <summary>One other member of the room, and how far behind their marker is.</summary>
/// <param name="PositionAge">How old the screenshot their position came from is.</param>
/// <param name="Since">How long since the relay heard from them at all.</param>
public sealed record SelfTestSquadmate(string Name, string? MapId, TimeSpan? PositionAge, TimeSpan? Since);

/// <summary>
/// What squadmate positions have actually taken to arrive, as package 31 times them.
/// </summary>
/// <remarks>
/// Deliveries, not a single age. One marker's age says how old that screenshot is; this says
/// what the last few screenshots took to reach this map, which is the number the claim "about
/// five seconds" was wrong about.
/// </remarks>
public sealed record SelfTestPositionLatency(
    long Delivered,
    int SampleCount,
    TimeSpan Median,
    TimeSpan Slowest95,
    TimeSpan Last)
{
    public static SelfTestPositionLatency None { get; } = new(0, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

    public bool HasSamples => SampleCount > 0;
}

public sealed record SelfTestRelay(
    bool Configured,
    string? Origin,
    bool Reachable,
    string? Version,
    string? Commit,
    int? Protocol,
    int? Rooms,
    int? Members,
    TimeSpan? RoundTrip,
    bool Sharing,
    string? MyName,
    IReadOnlyList<SelfTestSquadmate> Others,
    DateTimeOffset? StaleSinceUtc,
    DateTimeOffset ReadUtc,
    string? Problem = null)
{
    /// <summary>What the last few squadmate positions took to arrive, when any have.</summary>
    public SelfTestPositionLatency PositionLatency { get; init; } = SelfTestPositionLatency.None;
}

public sealed record SelfTestDevice(string Name, string Role, string Status, DateTimeOffset LastSeenUtc);

public sealed record SelfTestTablet(
    bool Supported,
    string? Origin,
    IReadOnlyList<SelfTestDevice> Devices,
    bool Publishing,
    DateTimeOffset? PublishedUtc,
    string? MapName,
    int Objects,
    DateTimeOffset ReadUtc,
    string? Problem = null);

/// <summary>
/// Everything the self-test reads, as one seam.
/// </summary>
/// <remarks>
/// Deliberately one interface rather than seven. Each method returns measured facts and nothing
/// else — no verdicts, no sentences — so the probes that turn facts into a report can be driven
/// from a fake on any platform, while the adapter that reads the real services stays thin enough
/// to see through.
///
/// Every method here must be read-only and bounded. This is pressed mid-raid.
/// </remarks>
public interface ISelfTestReadings
{
    Task<SelfTestFolders> ReadFoldersAsync(CancellationToken cancellationToken);

    Task<SelfTestLogs> ReadLogsAsync(CancellationToken cancellationToken);

    /// <summary>Waits for the player to take a screenshot, up to <paramref name="patience"/>.</summary>
    Task<SelfTestScreenshot> WatchScreenshotAsync(TimeSpan patience, CancellationToken cancellationToken);

    /// <summary>
    /// A screenshot already on disk from the last <paramref name="lookBack"/>, if there is one.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 43a] The probe's first question, and usually its last: a shot taken a
    /// couple of minutes ago proves the same path as one taken now and costs nobody an alt-tab.
    /// </remarks>
    Task<SelfTestScreenshot> RecentScreenshotAsync(TimeSpan lookBack, CancellationToken cancellationToken);

    Task<SelfTestGameData> ReadGameDataAsync(CancellationToken cancellationToken);

    Task<SelfTestDatabase> ReadDatabaseAsync(CancellationToken cancellationToken);

    Task<SelfTestRelay> ReadRelayAsync(CancellationToken cancellationToken);

    Task<SelfTestTablet> ReadTabletAsync(CancellationToken cancellationToken);
}
