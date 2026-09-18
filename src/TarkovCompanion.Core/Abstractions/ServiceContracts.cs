using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Keys;
using TarkovCompanion.Core.Domain.Loadouts;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Core.Domain.Strategy;

namespace TarkovCompanion.Core.Abstractions;

public sealed record SyncRequest(GameMode GameMode, string Language, bool Force = false);

public sealed record SyncEndpointResult(string Endpoint, bool Updated, bool UsedStaleCache, int RecordCount, string? Error);

public sealed record SyncReport(DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, IReadOnlyList<SyncEndpointResult> Endpoints);

public sealed record PriceHistoryPoint(DateTimeOffset TimestampUtc, long? FleaPriceRoubles, long? TraderValueRoubles, string Source);

/// <summary>
/// How much of one item the player's own progress still asks for, and from how many quests.
/// </summary>
/// <remarks>
/// The first three are quantities of the item, not counts of quests, and the names used to say
/// otherwise: <c>QuestCount</c> summed the outstanding amounts across every requirement, so one
/// quest asking for two of something reported two. The scanner reads them as quantities and its
/// sentences are correct — "Outstanding quests still need 2" — but the Keys page read the same
/// field as a number of quests and printed "225 quests you are tracking ask for it" beside a
/// dorm key, on a profile tracking nothing at all.
///
/// So the quantities say they are quantities, and the two counts of quests are their own fields.
/// </remarks>
/// <param name="OutstandingItems">How many of the item uncompleted quests still ask for.</param>
/// <param name="OutstandingFoundInRaidItems">How many of those must be found in raid.</param>
/// <param name="HideoutCount">How many the planned hideout upgrades still ask for.</param>
public sealed record ItemNeedSummary(
    int OutstandingItems,
    int OutstandingFoundInRaidItems,
    int HideoutCount)
{
    /// <summary>
    /// How many uncompleted quests ask for it at all.
    /// </summary>
    /// <remarks>
    /// Distinct quests, so a quest with two objectives wanting the same item counts once. An
    /// init property because every existing caller wants the quantities and this record is
    /// constructed positionally in several places.
    /// </remarks>
    public int QuestsNeedingIt { get; init; }

    /// <summary>
    /// How many of those the player is actually on — active or pinned.
    /// </summary>
    /// <remarks>
    /// The distinction the Keys page needs and could not make. "A quest you are on needs it" and
    /// "a quest ahead of you needs it" are both reasons to keep a key and they are not the same
    /// strength, and on a fresh wipe every quest in the game is ahead of you.
    /// </remarks>
    public int TrackedQuestsNeedingIt { get; init; }
}

public sealed record CaptureRequest(string WindowSelector, PixelRect? Region, bool AllowDesktopFallback, string Reason);

public sealed record WindowDescriptor(nint Handle, string ProcessName, string Title, PixelRect Bounds, bool IsMinimized, bool IsSimulator);

public sealed record DisplayDescriptor(string Id, string Name, PixelRect Bounds, bool IsPrimary, double Scale);

public sealed record EftPaths(string? InstallRoot, string? LogRoot, string? ScreenshotRoot, Confidence Confidence);


public sealed record ExtractRecognitionResult(
    IReadOnlyList<ActiveExtract> Extracts,
    IReadOnlyList<ObservedExtract> Observations,
    IReadOnlyList<string> AmbiguousLines,
    IReadOnlyList<string> UnmatchedLines,
    bool ProviderAvailable,
    string? DiagnosticCode = null)
{
    /// <summary>
    /// The transits the same screen offered, as the screen named them.
    /// </summary>
    /// <remarks>
    /// The extract panel lists ways to another map alongside the exits from this one, each
    /// labelled TRANSIT rather than EXFIL and drawn a different colour. They are not in any
    /// extract catalog and never will be, so matching them against one produces nothing but a
    /// list of lines that failed.
    ///
    /// Carried verbatim because "Transit to Factory" says everything a player needs and there
    /// is nothing to look it up against.
    /// </remarks>
    public IReadOnlyList<string> Transits { get; init; } = [];

    /// <summary>
    /// Every line the screen was read as, before anything was stripped from it.
    /// </summary>
    /// <remarks>
    /// The raid clock is on this screen and is the game's own number, so it outranks anything
    /// the companion counts. It was transported in <see cref="UnmatchedLines"/> and
    /// <see cref="AmbiguousLines"/> — the diagnostics for lines that failed to match an exit —
    /// on the reasoning that a clock is not an extract name so it must land there.
    ///
    /// It does not. The real row reads <c>Find an extraction point 0:12:28</c>: the clock is
    /// stripped as a trailing measure, the remainder is recognised as the panel header, and the
    /// whole line is dropped before either diagnostic list sees it. The clock only ever arrived
    /// when OCR happened to break it onto a line of its own, which is why it worked in a test
    /// that supplied two separate strings and not on a screenshot.
    ///
    /// So the raw lines are carried in their own right. Evidence the player photographed is not
    /// a by-product of failing to match something else.
    /// </remarks>
    public IReadOnlyList<string> RawLines { get; init; } = [];

    /// <summary>
    /// Structurally identified EXFIL rows whose names were absent from this map's catalog.
    /// </summary>
    /// <remarks>
    /// These are preserved as conservative observations instead of disappearing into generic
    /// unmatched text. Keeping the raw rows separately makes an upstream coverage regression
    /// measurable while the observation still lets the player see an exit the game offered.
    /// An unlabelled OCR fragment never enters this list.
    /// </remarks>
    public IReadOnlyList<string> CatalogGapLines { get; init; } = [];
}

public enum ScanCompletionStatus
{
    Complete,
    Partial,
    Unavailable,
}

public sealed record ScanRequest(CaptureRequest Capture);

public sealed record ScanEvidence(
    string Code,
    string Detail,
    Confidence Confidence,
    DateTimeOffset ObservedUtc);

public sealed record ScanOutcome(
    Guid ScanId,
    ScanCompletionStatus Status,
    ScanContext Context,
    DateTimeOffset ObservedUtc,
    RecognitionResult Recognition,
    ExtractRecognitionResult? Extracts,
    ContainerScanResult? Container,
    FleaRecognitionResult? Flea,
    RecommendationResult? Recommendation,
    IReadOnlyList<ScanEvidence> Evidence,
    string? DiagnosticCode = null)
{
    /// <summary>What the recognised item is worth, whether or not advice was given about it.</summary>
    /// <remarks>
    /// Separate from the recommendation on purpose. "What is this worth" and "should you take
    /// it" are different questions, and the first is answerable whenever the item is known.
    /// They used to share a field, so a withheld recommendation hid a price the application
    /// had already fetched and was holding in a local variable.
    /// </remarks>
    public long? EconomicValue { get; init; }

    public long? ValuePerSlot { get; init; }
}

public sealed record ScanEventMetadata(
    Guid ScanId,
    DateTimeOffset TimestampUtc,
    ScanContext Context,
    string? ResolvedItemId,
    Confidence Confidence,
    IReadOnlyList<RecognitionCandidate> Candidates,
    string? Recommendation,
    PixelRect? SourceGeometry,
    string? DiagnosticCode);

public interface IDataSyncService
{
    Task<SyncReport> SyncAsync(SyncRequest request, CancellationToken cancellationToken);
}

public interface IItemRepository
{
    Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken);

    Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken);
}

public interface IItemSearchService
{
    Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}

public interface IPriceHistoryService
{
    Task<IReadOnlyList<PriceHistoryPoint>> GetAsync(string itemId, TimeSpan window, CancellationToken cancellationToken);
}

public interface IRecommendationEngine
{
    RecommendationResult Recommend(
        ItemDefinition item,
        ItemPriceSnapshot price,
        RecommendationContext context,
        ValueTierThresholds thresholds);
}

public interface IPlayerProfileService
{
    Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken);

    Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken);

    Task<string> ExportJsonAsync(CancellationToken cancellationToken);

    Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken);
}

public interface IQuestProgressService
{
    Task<ItemNeedSummary> GetItemNeedsAsync(string itemId, CancellationToken cancellationToken);
}

/// <summary>
/// What the traders are called, which the application has synced and never read.
/// </summary>
/// <remarks>
/// The <c>traders</c> table has held id and name since migration 0001 and is rewritten on every
/// sync, and until now the only query touching it was a sub-select inside the sell-offer join.
/// So the Quests page printed <c>Trader: 54cb50c76803fa8b248b4571</c>, which is what the feed
/// puts in a task's trader field, and the map projection carried the same id to anything that
/// wanted to draw an objective.
///
/// Names rather than the whole trader. What a consumer wants from a trader id is a word a
/// player would say, and a bigger read would be a bigger read for nobody.
/// </remarks>
public interface ITraderCatalog
{
    /// <summary>Every trader the last sync stored, by id.</summary>
    Task<IReadOnlyDictionary<string, string>> GetNamesAsync(CancellationToken cancellationToken);
}

public interface IQuestCatalog
{
    Task<QuestCatalogSnapshot?> GetAsync(
        GameMode gameMode,
        string language,
        CancellationToken cancellationToken);
}

public interface IQuestProgressStore
{
    Task<QuestProgressSnapshot> GetAsync(
        QuestProfileScope scope,
        CancellationToken cancellationToken);

    Task<QuestProgressCommandResult> ApplyAsync(
        QuestProgressMutation mutation,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<QuestProgressChange>> GetJournalAsync(
        QuestProfileScope scope,
        CancellationToken cancellationToken);
}

public interface IProjectQuestProgressJson
{
    Task<ProjectQuestProgressDocument> ReadAsync(string filePath, CancellationToken cancellationToken);

    Task<QuestProgressExportResult> WriteAsync(
        string filePath,
        PlayerProfile profile,
        QuestProgressSnapshot progress,
        CancellationToken cancellationToken);
}

public interface IQuestProgressImportStore
{
    Task<QuestImportApplyResult> ApplyImportAsync(
        QuestProgressImportPreview preview,
        string expectedPreviewSha256,
        IReadOnlyDictionary<string, QuestImportResolution> resolutions,
        CancellationToken cancellationToken);

    Task<QuestImportUndoResult> UndoImportAsync(
        QuestProfileScope scope,
        Guid importId,
        CancellationToken cancellationToken);
}

/// <summary>One quest-progress import that has already been applied, as it was recorded.</summary>
/// <param name="ImportId">The import's own id, which is also what an undo takes.</param>
/// <param name="ImportedUtc">When it was applied.</param>
/// <param name="ProfileName">Whose progress it was, as their own installation named them.</param>
/// <param name="SourceAppVersion">Which build exported it.</param>
/// <param name="AppliedChangeCount">How much of it landed.</param>
/// <param name="KeptLocalCount">How much was refused in favour of what was already here.</param>
/// <param name="Conflicts">Each thing the two sides disagreed about, and which way it went.</param>
/// <param name="Unresolved">Each thing that could not be read or matched at all.</param>
public sealed record QuestImportRecord(
    Guid ImportId,
    DateTimeOffset ImportedUtc,
    string ProfileName,
    string SourceAppVersion,
    int AppliedChangeCount,
    int KeptLocalCount,
    IReadOnlyList<QuestImportConflictRecord> Conflicts,
    IReadOnlyList<QuestImportUnresolvedRecord> Unresolved);

/// <summary>One disagreement between what was here and what arrived.</summary>
/// <param name="EntityKind">A task or an objective.</param>
/// <param name="EntityId">Which one.</param>
/// <param name="LocalValueJson">What this installation had.</param>
/// <param name="IncomingValueJson">What the file said.</param>
/// <param name="Reason">Why it was a conflict rather than a straightforward change.</param>
/// <param name="Resolution">Which way it was settled.</param>
public sealed record QuestImportConflictRecord(
    string EntityKind,
    string EntityId,
    string LocalValueJson,
    string IncomingValueJson,
    string Reason,
    QuestImportResolution Resolution);

/// <summary>One thing in the file that could not be matched to anything here.</summary>
public sealed record QuestImportUnresolvedRecord(
    string EntityKind,
    string EntityId,
    string IncomingValueJson,
    string Reason);

/// <summary>
/// Reads back the imports already applied, and what each one could not do.
/// </summary>
/// <remarks>
/// Every import records exactly which changes it refused and why, in
/// <c>quest_progress_import_conflicts</c> and <c>quest_progress_import_unresolved</c>, and
/// nothing has ever read either table. So an import that half-worked told the player "kept 3
/// local · 2 unresolved" once, in a status line, and could never say which three or which two.
///
/// It is also what makes an undo survive a restart: the id an undo needs was held in memory
/// only, so closing the application between importing and regretting it lost the way back.
/// </remarks>
public interface IQuestProgressImportHistory
{
    Task<IReadOnlyList<QuestImportRecord>> GetRecentAsync(
        QuestProfileScope scope,
        int limit,
        CancellationToken cancellationToken);
}

public interface IQuestProgressImportPlanner
{
    Task<QuestProgressImportPreview> PreviewAsync(
        QuestProfileScope scope,
        QuestProgressImportSnapshot snapshot,
        CancellationToken cancellationToken);
}

public interface IQuestProgressExchangeService
{
    Task<QuestProgressExportResult> ExportAsync(
        QuestProfileScope scope,
        string filePath,
        CancellationToken cancellationToken);

    Task<QuestProgressImportPreview> PreviewImportAsync(
        QuestProfileScope scope,
        string filePath,
        CancellationToken cancellationToken);

    Task<QuestImportApplyResult> ApplyImportAsync(
        QuestProgressImportPreview preview,
        IReadOnlyDictionary<string, QuestImportResolution> resolutions,
        CancellationToken cancellationToken);

    Task<QuestImportUndoResult> UndoImportAsync(
        QuestProfileScope scope,
        Guid importId,
        CancellationToken cancellationToken);
}

public interface IQuestProgressCommandService
{
    Task<QuestProgressCommandResult> SetTaskStateAsync(
        QuestProfileScope scope,
        string taskId,
        RecordedTaskState state,
        CancellationToken cancellationToken);

    Task<QuestProgressCommandResult> SetObjectiveProgressAsync(
        QuestProfileScope scope,
        string objectiveId,
        RecordedObjectiveState state,
        decimal? count,
        CancellationToken cancellationToken);

    Task<QuestProgressCommandResult> SetItemHoldingAsync(
        QuestProfileScope scope,
        string itemId,
        bool foundInRaid,
        int? count,
        CancellationToken cancellationToken);

    Task<QuestProgressCommandResult> SetPinAsync(
        QuestProfileScope scope,
        QuestPinTargetKind targetKind,
        string targetId,
        bool isPinned,
        int sortOrder,
        string? note,
        CancellationToken cancellationToken);
}

public interface IQuestReadService
{
    Task<QuestBoardReadModel> GetQuestBoardAsync(
        QuestProfileScope scope,
        CancellationToken cancellationToken);

    Task<QuestItemNeedsReadModel> GetItemNeedsAsync(
        QuestProfileScope scope,
        string itemId,
        CancellationToken cancellationToken);

    Task<QuestMapObjectivesReadModel> GetActiveMapObjectivesAsync(
        QuestProfileScope scope,
        IReadOnlyCollection<string> mapIds,
        CancellationToken cancellationToken);
}

public interface IHideoutProgressService
{
    Task<int> GetRemainingItemCountAsync(string itemId, CancellationToken cancellationToken);
}

public interface IEventTrackerService
{
    Task<EventItemState> GetItemStateAsync(string eventId, string itemId, CancellationToken cancellationToken);

    Task SetItemStateAsync(string eventId, string itemId, EventItemState state, CancellationToken cancellationToken);

    Task<EventProgress> GetProgressAsync(string eventId, CancellationToken cancellationToken);
}

public interface IRecognitionService
{
    Task<RecognitionResult> RecognizeAsync(CapturedImage image, CancellationToken cancellationToken);
}

public interface IOcrEngine
{
    Task<OcrResult> RecognizeAsync(CapturedImage image, OcrRequest request, CancellationToken cancellationToken);
}

public interface IOcrEngineStatus
{
    OcrEngineAvailability Availability { get; }
}

public interface IRecognitionSelfTest
{
    Task<RecognitionSelfTestResult> RunAsync(CancellationToken cancellationToken);
}

public interface IIconMatcher
{
    Task<IReadOnlyList<RecognitionCandidate>> MatchAsync(CapturedImage image, int limit, CancellationToken cancellationToken);
}

public interface IScreenCaptureService
{
    Task<CapturedImage> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken);
}


public interface IGameWindowLocator
{
    Task<WindowDescriptor?> FindAsync(bool developerMode, CancellationToken cancellationToken);
}

public interface IMonitorService
{
    Task<IReadOnlyList<DisplayDescriptor>> GetDisplaysAsync(CancellationToken cancellationToken);
}

public interface IEftPathLocator
{
    Task<EftPaths> FindAsync(CancellationToken cancellationToken);
}

public interface IEftLogWatcher
{
    IAsyncEnumerable<RaidEvidence> WatchAsync(string logRoot, CancellationToken cancellationToken);
}

/// <summary>How much of a screenshot the watcher can vouch for yet.</summary>
/// <remarks>
/// The game writes the player's coordinates into the filename, so the position is complete the
/// moment the directory entry appears. The picture is not: it is still being written, and the
/// watcher has to see it stop changing before anything may decode it. Reporting both facts on
/// one signal meant the position waited on the picture — two stable probes a second apart, for
/// a number that was already on disk.
/// </remarks>
public enum ScreenshotSightingKind
{
    /// <summary>The entry exists and its name can be read. The pixels may still be arriving.</summary>
    NameSeen,

    /// <summary>The file has stopped changing and opens cleanly, so it may be decoded.</summary>
    Settled,
}

/// <summary>One thing the watcher saw in the screenshot folder.</summary>
public readonly record struct ScreenshotSighting(string Path, ScreenshotSightingKind Kind)
{
    public bool IsSettled => Kind == ScreenshotSightingKind.Settled;
}

public interface IScreenshotWatcher
{
    /// <summary>
    /// Reports every screenshot twice: once on sight, and again once it is safe to read.
    /// </summary>
    IAsyncEnumerable<ScreenshotSighting> WatchAsync(string screenshotRoot, CancellationToken cancellationToken);
}

/// <summary>Reading only the sightings a caller cares about.</summary>
public static class ScreenshotWatcherExtensions
{
    /// <summary>The settled files, for callers that read pixels rather than names.</summary>
    public static async IAsyncEnumerable<string> WatchSettledAsync(
        this IScreenshotWatcher watcher,
        string screenshotRoot,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        await foreach (var sighting in watcher.WatchAsync(screenshotRoot, cancellationToken).ConfigureAwait(false))
        {
            if (sighting.IsSettled)
            {
                yield return sighting.Path;
            }
        }
    }
}

public interface IScreenshotFilenameParser
{
    bool TryParse(string filename, TimeSpan localUtcOffset, out ScreenshotPosition? position);

    /// <summary>
    /// Reads a screenshot that exists on disk, taking its time from the file rather than its name.
    /// </summary>
    /// <remarks>
    /// The coordinates only exist in the name, but the time in the name is not in a zone the
    /// companion can identify: on a live installation it ran hours away from when the file was
    /// actually written, and every position that arrived was then discarded as older than the
    /// raid already on screen. The filesystem knows exactly when the game wrote the file, so
    /// that is the time used whenever there is a real file to ask.
    /// </remarks>
    bool TryParseFile(string path, TimeSpan localUtcOffset, out ScreenshotPosition? position);
}

public interface IMapDataService
{
    Task<MapDefinition?> GetAsync(string mapId, CancellationToken cancellationToken);
}

public interface IMapTransformService
{
    MapPoint Transform(WorldPosition position, MapTransformConfig config);

    MapFloorLayer? SelectFloor(double elevation, IReadOnlyList<MapFloorLayer> floors);
}

public interface IExtractRecognitionService
{
    Task<ExtractRecognitionResult> RecognizeAsync(
        CapturedImage image,
        MapDefinition currentMap,
        CancellationToken cancellationToken);
}

public interface IContainerRecognitionService
{
    Task<ContainerScanResult> RecognizeAsync(CapturedImage image, CancellationToken cancellationToken);
}

public interface IFleaRecognitionService
{
    Task<FleaRecognitionResult> RecognizeAsync(CapturedImage image, CancellationToken cancellationToken);
}

public interface IRecognitionCatalogRepository
{
    Task<IReadOnlyList<CanonicalItemReference>> LoadAsync(CancellationToken cancellationToken);
}

public interface IScanUseCase
{
    Task<ScanOutcome> ScanAsync(ScanRequest request, CancellationToken cancellationToken);

    /// <summary>Reads a picture the player already took, instead of capturing the screen.</summary>
    Task<ScanOutcome> ScanImageAsync(CapturedImage image, CancellationToken cancellationToken);
}

/// <summary>
/// Turns a screenshot on disk into pixels the recogniser can read.
/// </summary>
/// <remarks>
/// The game writes a full-resolution picture of exactly what the player was looking at, which
/// is a better thing to read than a capture taken afterwards from a window that may no longer
/// show it. The file is the player's own; it is read and discarded, never copied or sent.
/// </remarks>
public interface IScreenshotImageLoader
{
    Task<CapturedImage?> LoadAsync(string path, CancellationToken cancellationToken);
}

public interface IScanEventRepository
{
    Task SaveAsync(ScanEventMetadata scanEvent, CancellationToken cancellationToken);
}

/// <summary>One scan the player has already made, as it was recorded at the time.</summary>
/// <param name="ScanId">The scan's own id.</param>
/// <param name="ObservedUtc">When it was read.</param>
/// <param name="Context">What the recogniser decided it was looking at.</param>
/// <param name="Name">The item, named where it resolved and described where it did not.</param>
/// <param name="Confidence">How sure it was.</param>
/// <param name="Recommendation">What it said to do, where it said anything.</param>
/// <param name="DiagnosticCode">Why it produced nothing, where it produced nothing.</param>
public sealed record ScanHistoryEntry(
    Guid ScanId,
    DateTimeOffset ObservedUtc,
    ScanContext Context,
    string Name,
    Confidence Confidence,
    string? Recommendation,
    string? DiagnosticCode);

/// <summary>
/// Reads back the scans already on disk.
/// </summary>
/// <remarks>
/// Every scan a player has ever made has been written to <c>scan_history</c> since the first
/// migration and nothing has ever read one back, so the application could not show a player
/// the thing it had just spent a night recording. This is the read half.
/// </remarks>
public interface IScanHistoryService
{
    Task<IReadOnlyList<ScanHistoryEntry>> GetRecentAsync(int limit, CancellationToken cancellationToken);
}

public interface IScanResultPublisher
{
    Task PublishAsync(ScanOutcome result, CancellationToken cancellationToken);
}

public interface IScanRecommendationContextProvider
{
    Task<RecommendationContext?> GetAsync(
        ItemDefinition item,
        RecognitionCandidate recognition,
        CancellationToken cancellationToken);
}

public interface IRaidStateService
{
    RaidSnapshot Current { get; }

    RaidSnapshot Apply(RaidEvidence evidence);

    RaidSnapshot ApplyPosition(ScreenshotPosition position);

    /// <param name="raidClock">
    /// The remaining time as the same screenshot showed it, where it could be read. The game
    /// draws it on the extract list screen, so photographing that screen hands over the exact
    /// number and nothing else has to be estimated.
    /// </param>
    /// <param name="linesNotMatched">
    /// What the scan read on that screen and could not match to an exit, so a scan that found
    /// one exit out of eight can say so instead of looking like a screen with one exit on it.
    /// </param>
    RaidSnapshot ApplyExtracts(
        IReadOnlyList<ActiveExtract> extracts,
        DateTimeOffset observedUtc,
        TimeSpan? raidClock = null,
        IReadOnlyList<string>? linesNotMatched = null,
        IReadOnlyList<string>? transits = null);

    /// <summary>
    /// Takes over a raid a previous run of the companion was already recording.
    /// </summary>
    /// <remarks>
    /// The raid this service holds is memory, so a companion restarted mid-raid mints a new
    /// identity for a raid that already has one, and the trail it draws starts empty over
    /// screenshots that are on disk. This replaces the identity and the trail with the ones
    /// already recorded, which is the only way the two can be the same raid.
    ///
    /// Refused unless a raid is running, because there is nothing to take over otherwise.
    /// </remarks>
    /// <param name="raidId">The id the recorded raid already has.</param>
    /// <param name="startedUtc">When it actually started, rather than when this run noticed.</param>
    /// <param name="trail">Where the player has already been, oldest first.</param>
    RaidSnapshot Adopt(Guid raidId, DateTimeOffset? startedUtc, IReadOnlyList<ScreenshotPosition> trail);
}

public interface IStrategyModel
{
    TrafficPrediction Predict(
        TimeSpan elapsed,
        TimeSpan raidDuration,
        IReadOnlyList<StrategyZone> zones,
        MapPoint? lastKnownPlayerPosition,
        DateTimeOffset nowUtc);
}

public interface IRoutePlanner
{
    PlannedRoute Plan(RouteGraph graph, string startNodeId, string endNodeId, RouteMode mode, RaidPhase phase);
}

public interface IIntegrationSecretStore
{
    bool IsAvailable { get; }

    Task SaveAsync(
        IntegrationSecretReference reference,
        string secret,
        CancellationToken cancellationToken);

    Task<string?> LoadAsync(
        IntegrationSecretReference reference,
        CancellationToken cancellationToken);

    Task<bool> ExistsAsync(
        IntegrationSecretReference reference,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        IntegrationSecretReference reference,
        CancellationToken cancellationToken);
}

/// <summary>One raid's path across a map, as the screenshots recorded it.</summary>
/// <param name="RaidId">Which raid, so a trail can be opened as a replay from where it is drawn.</param>
/// <param name="StartedUtc">When it began, which is what decides how faintly it is drawn.</param>
/// <param name="Positions">Where the player was, oldest first.</param>
public sealed record RaidTrail(Guid RaidId, DateTimeOffset? StartedUtc, IReadOnlyList<ScreenshotPosition> Positions);

public interface IRaidHistoryService
{
    Task<Guid> StartAsync(RaidHistoryEntry raid, CancellationToken cancellationToken);

    Task RecordEventAsync(Guid raidId, string type, DateTimeOffset timestampUtc, string payloadJson, CancellationToken cancellationToken);

    Task EndAsync(Guid raidId, DateTimeOffset endUtc, string? outcome, string? notes, CancellationToken cancellationToken);

    /// <summary>
    /// Fixes an outcome or notes field the player says is wrong, after the raid already ended.
    /// </summary>
    /// <remarks>
    /// The game never records an outcome (see <see cref="EndAsync"/>'s callers), so most of this
    /// row is the player's own account of the raid, entered or corrected by hand. Unlike a
    /// game-observed event, an explicit correction from Debrief has no delivery-ordering
    /// relationship with the rest of a raid's history, so it does not need the outbox's
    /// durable-replay guarantee and is not part of its closed command set.
    /// </remarks>
    Task CorrectAsync(Guid raidId, string? outcome, string? notes, CancellationToken cancellationToken);

    Task<IReadOnlyList<RaidHistoryEntry>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Every screenshot position recorded during one raid, oldest first.
    /// </summary>
    /// <remarks>
    /// These have been written on every scan since the first raid and nothing has ever read
    /// them back, so a raid's path survived a restart in the database and vanished from the
    /// screen. They are the whole of what a replay needs: the trail is a record of the
    /// player's own screenshots rather than any kind of tracking.
    /// </remarks>
    Task<IReadOnlyList<ScreenshotPosition>> ListPositionsAsync(Guid raidId, CancellationToken cancellationToken);

    /// <summary>
    /// Every screenshot position recorded on one map, newest raid first.
    /// </summary>
    /// <remarks>
    /// The same rows one raid's replay reads, asked for by map instead of by raid. A player who
    /// has run Customs two hundred times has two hundred trails in this table and could only
    /// ever see one of them at a time, from the History page, one raid at a time.
    ///
    /// Grouped by raid rather than flattened, because the points of one raid are a path and the
    /// points of two are not: joining the last position of Tuesday to the first of Wednesday
    /// would draw a line across the map that nobody walked.
    ///
    /// Bounded, because the answer grows without limit and the question does not. The newest
    /// raids are the ones worth drawing.
    /// </remarks>
    /// <summary>
    /// The payloads of one raid's events of one kind, oldest first.
    /// </summary>
    /// <remarks>
    /// The service recorded events and offered no way to read them back, so the raid summary
    /// counted scans as they went past in the runtime snapshot instead of asking — which works
    /// only while the application that saw them is still running. A raid opened from History a
    /// week later had a record nothing could reach.
    ///
    /// Payloads rather than parsed objects, because each kind is a different shape and the
    /// caller is the one that knows which. Returning a union here would make every caller
    /// switch on a type it already knew.
    /// </remarks>
    Task<IReadOnlyList<string>> ListEventPayloadsAsync(
        Guid raidId,
        string type,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RaidTrail>> ListTrailsForMapAsync(
        string mapId,
        int limit,
        CancellationToken cancellationToken);

    Task ExportCsvAsync(Stream destination, CancellationToken cancellationToken);

    Task ExportJsonAsync(Stream destination, CancellationToken cancellationToken);
}
