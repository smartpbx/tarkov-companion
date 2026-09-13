using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Input;
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

public sealed record ItemNeedSummary(int QuestCount, int FoundInRaidQuestCount, int HideoutCount);

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
    string? DiagnosticCode = null);

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
    string? DiagnosticCode = null);

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

public interface IAmmoIntelligenceService
{
    Task<AmmoIntelligence?> GetAsync(string itemId, PlayerProfile? profile, CancellationToken cancellationToken);

    Task<IReadOnlyList<AmmoIntelligence>> GetCaliberAsync(string caliber, PlayerProfile? profile, CancellationToken cancellationToken);
}

public interface IKeyIntelligenceService
{
    Task<KeyIntelligence?> GetAsync(string itemId, PlayerProfile? profile, CancellationToken cancellationToken);
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

public interface IScreenshotWatcher
{
    IAsyncEnumerable<string> WatchAsync(string screenshotRoot, CancellationToken cancellationToken);
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

    RaidSnapshot ApplyExtracts(IReadOnlyList<ActiveExtract> extracts, DateTimeOffset observedUtc);
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

public interface IRaidHistoryService
{
    Task<Guid> StartAsync(RaidHistoryEntry raid, CancellationToken cancellationToken);

    Task RecordEventAsync(Guid raidId, string type, DateTimeOffset timestampUtc, string payloadJson, CancellationToken cancellationToken);

    Task EndAsync(Guid raidId, DateTimeOffset endUtc, string? outcome, string? notes, CancellationToken cancellationToken);

    Task<IReadOnlyList<RaidHistoryEntry>> ListAsync(CancellationToken cancellationToken);

    Task ExportCsvAsync(Stream destination, CancellationToken cancellationToken);

    Task ExportJsonAsync(Stream destination, CancellationToken cancellationToken);
}

public interface ILoadoutService
{
    Task<LoadoutEvaluation> EvaluateAsync(LoadoutSelection selection, PlayerProfile? profile, CancellationToken cancellationToken);
}
