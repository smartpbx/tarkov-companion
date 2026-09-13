using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.Quests;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Recognition;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.App.Services.Updates;
using TarkovCompanion.Application.Services.Strategy;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Keys;
using TarkovCompanion.Core.Domain.Loadouts;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.Events;
using TarkovCompanion.Infrastructure.Maps;
using TarkovCompanion.Infrastructure.Profile;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Settings;
using TarkovCompanion.Infrastructure.Security;
using TarkovCompanion.Infrastructure.TarkovDevJson;
using TarkovCompanion.Infrastructure.TarkovTracker;
using TarkovCompanion.Platform.Windows.Capture;
using TarkovCompanion.Platform.Windows.Discovery;
using TarkovCompanion.Platform.Windows.Displays;
using TarkovCompanion.Platform.Windows.Security;
using TarkovCompanion.Platform.Windows.Storage;
using TarkovCompanion.Platform.Windows.Watching;
using RecognitionScanContract = TarkovCompanion.Core.Abstractions.IScanUseCase;
using RecognitionScanUseCase = TarkovCompanion.Application.Services.Recognition.ScanUseCase;
using RuntimeScanUseCase = TarkovCompanion.Application.Services.Runtime.ScanUseCase;

namespace TarkovCompanion.App.Services;

public sealed record AppCompositionSettings(
    string? DataRoot = null,
    bool? Offline = null,
    TimeProvider? TimeProvider = null,
    HttpMessageHandler? HttpMessageHandler = null,
    IScanAdapter? ScanAdapter = null,
    TarkovTrackerOptions? TarkovTrackerOptions = null,
    HttpMessageHandler? TarkovTrackerHttpMessageHandler = null,
    IIntegrationSecretStore? IntegrationSecretStore = null);

public static class AppComposition
{
    public const string OfflineEnvironmentVariable = "TARKOV_COMPANION_OFFLINE";
    public const string TarkovTrackerEnvironmentVariable = "TARKOV_COMPANION_TARKOVTRACKER_ENABLED";

    public static ServiceProvider Build(AppCommandLine commandLine, AppCompositionSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        settings ??= new();
        var timeProvider = settings.TimeProvider ?? TimeProvider.System;
        var offline = settings.Offline ?? IsEnabled(Environment.GetEnvironmentVariable(OfflineEnvironmentVariable));
        var paths = AppDataPaths.Resolve(settings.DataRoot, commandLine.Demo);
        var runtimeOptions = new RuntimeOptions(
            commandLine.Demo,
            offline,
            GameMode.Regular,
            "en",
            TimeSpan.FromHours(9),
            // The whole-sync budget must exceed the sum of the per-request budgets it
            // contains. Seven endpoints, each up to three 12-second attempts and some with a
            // second translation request, can legitimately need several minutes on a cold
            // cache. At 45 seconds the first slow endpoint consumed the budget and every
            // later endpoint was cancelled, so a clean install never obtained any game data.
            TimeSpan.FromMinutes(5));
        var databaseOptions = new SqliteDatabaseOptions(Path.Combine(paths.Database, "tarkov-companion.db"));
        var profileOptions = new JsonProfileOptions(Path.Combine(paths.Config, "profile.json"));
        var questExchangeOptions = new ProjectQuestProgressJsonOptions(
            typeof(AppComposition).Assembly.GetName().Version?.ToString() ?? "unknown");
        var questTrackingOptions = new QuestTrackingOptions(runtimeOptions.Language);
        var integrationSecretStore = settings.IntegrationSecretStore ??
            (OperatingSystem.IsWindows()
                ? new WindowsDpapiSecretStore(Path.Combine(paths.Config, "Secrets"))
                : new UnavailableIntegrationSecretStore());
        var requestedTarkovTrackerOptions = settings.TarkovTrackerOptions ?? new TarkovTrackerOptions
        {
            Enabled = OptionalFeatureEnabled(
                Environment.GetEnvironmentVariable(TarkovTrackerEnvironmentVariable),
                integrationSecretStore.IsAvailable),
        };
        var tarkovTrackerOptions = requestedTarkovTrackerOptions with
        {
            NetworkAccessEnabled = requestedTarkovTrackerOptions.NetworkAccessEnabled && !offline,
        };
        tarkovTrackerOptions.Validate();

        var services = new ServiceCollection();
        services.AddSingleton(commandLine);
        services.AddSingleton(paths);
        services.AddSingleton(runtimeOptions);
        services.AddSingleton(databaseOptions);
        services.AddSingleton(profileOptions);
        services.AddSingleton(questExchangeOptions);
        services.AddSingleton(questTrackingOptions);
        services.AddSingleton(tarkovTrackerOptions);
        services.AddSingleton(timeProvider);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new TraceLoggerProvider());
            builder.AddProvider(new FileLoggerProvider());
        });

        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<SqliteMigrationRunner>();
        services.AddSingleton<SqliteItemRepository>();
        services.AddSingleton<IItemRepository>(provider => provider.GetRequiredService<SqliteItemRepository>());
        services.AddSingleton<SqlitePriceHistoryRepository>();
        services.AddSingleton<IPriceHistoryStore>(provider => provider.GetRequiredService<SqlitePriceHistoryRepository>());
        services.AddSingleton<SqliteSyncStateRepository>();
        services.AddSingleton<SqliteDataRefreshRepository>();
        services.AddSingleton<SqliteTarkovDevResponseCache>();
        services.AddSingleton<ITarkovDevResponseCache>(provider => provider.GetRequiredService<SqliteTarkovDevResponseCache>());
        services.AddSingleton<SqliteRuntimeDataStore>();
        services.AddSingleton<IRuntimeDataStore>(provider => provider.GetRequiredService<SqliteRuntimeDataStore>());
        services.AddSingleton<SqliteRaidHistoryService>();
        services.AddSingleton<IRaidHistoryService>(provider => provider.GetRequiredService<SqliteRaidHistoryService>());
        services.AddSingleton<SqliteRecognitionCatalogRepository>();
        services.AddSingleton<IRecognitionCatalogRepository>(provider =>
            provider.GetRequiredService<SqliteRecognitionCatalogRepository>());
        services.AddSingleton<SqliteScanEventRepository>();
        services.AddSingleton<IScanEventRepository>(provider =>
            provider.GetRequiredService<SqliteScanEventRepository>());
        // The read half of scan_history, which was written to for months and never read from.
        services.AddSingleton<SqliteScanHistoryService>();
        services.AddSingleton<IScanHistoryService>(provider =>
            provider.GetRequiredService<SqliteScanHistoryService>());
        services.AddSingleton<SqliteQuestCatalog>();
        services.AddSingleton<IQuestCatalog>(provider => provider.GetRequiredService<SqliteQuestCatalog>());
        services.AddSingleton<SqliteQuestProgressStore>();
        services.AddSingleton<IQuestProgressStore>(provider =>
            provider.GetRequiredService<SqliteQuestProgressStore>());
        // The read half of the import record, which was written to and never read from.
        services.AddSingleton<SqliteQuestProgressImportHistory>();
        services.AddSingleton<IQuestProgressImportHistory>(provider =>
            provider.GetRequiredService<SqliteQuestProgressImportHistory>());
        services.AddSingleton(provider => new SqliteQuestProgressImportStore(
            provider.GetRequiredService<SqliteConnectionFactory>(),
            timeProvider));
        services.AddSingleton<IQuestProgressImportStore>(provider =>
            provider.GetRequiredService<SqliteQuestProgressImportStore>());

        services.AddSingleton<DataTranslationService>();
        services.AddSingleton(_ => new HttpClient(
            offline
                ? new OfflineHttpMessageHandler()
                : settings.HttpMessageHandler ?? CreateDataHandler(),
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        services.AddSingleton(provider => new TarkovDevJsonClient(
            provider.GetRequiredService<HttpClient>(),
            provider.GetRequiredService<ITarkovDevResponseCache>(),
            provider.GetRequiredService<DataTranslationService>(),
            // A group that runs a server can hold the catalog once for everybody instead of
            // five clients pulling the same several megabytes. Read from the group settings
            // file directly rather than through the store, because this is composed before
            // anything has had a chance to await one, and upstream is always still tried
            // afterwards so a wrong or stale answer here costs nothing.
            new TarkovDevJsonClientOptions
            {
                MirrorAddress = ReadCatalogMirror(Path.Combine(paths.Config, "group.json")),
            },
            timeProvider));
        services.AddSingleton<TarkovDevDataRefreshOperation>();
        services.AddSingleton<IDataRefreshOperation>(provider => provider.GetRequiredService<TarkovDevDataRefreshOperation>());
        services.AddSingleton<IDataSyncService, DataSyncService>();
        services.AddSingleton<IItemSearchService, ItemSearchService>();
        services.AddSingleton<IPriceHistoryService, PriceHistoryService>();

        services.AddSingleton(TarkovDevMapCatalogClientOptions.CreateDefault(Path.Combine(paths.Cache, "Maps", "Catalog")));
        services.AddSingleton(MapAssetCacheOptions.CreateDefault(Path.Combine(paths.Cache, "Maps", "Assets")));
        services.AddSingleton<TarkovDevMapCatalogClient>();
        services.AddSingleton<TarkovDevMapAssetCache>();
        services.AddSingleton<IMapVariantPreferenceStore>(_ =>
            new JsonFileMapVariantPreferenceStore(Path.Combine(paths.Config, "map-defaults.json")));
        // Sharing with a group is the only part of this application that sends anything
        // anywhere, so it is composed here explicitly rather than discovered.
        services.AddSingleton<IGroupSettingsStore>(_ =>
            new JsonFileGroupSettingsStore(Path.Combine(paths.Config, "group.json")));
        // What the player is working on, for the group to see. Dead until tonight, because
        // there was no quest progress to send.
        services.AddSingleton<GroupQuestShare>();
        services.AddSingleton<GroupKitShare>();
        services.AddSingleton<GroupSessionService>();
        // Keeping the game's screenshot folder from growing without limit. Composed here
        // rather than discovered because it is the other half of the application that touches
        // files it did not create, and that should be visible in one place.
        services.AddSingleton<IScreenshotRetentionStore>(_ =>
            new JsonFileScreenshotRetentionStore(Path.Combine(paths.Config, "screenshots.json")));
        // Where the game keeps its screenshots and logs, when the guessing is wrong. The first
        // person to install this who does not use OneDrive had no screenshots detected and no
        // way to say where they were.
        services.AddSingleton<IEftPathOverrideStore>(_ =>
            new JsonFileEftPathOverrideStore(Path.Combine(paths.Config, "game-folders.json")));
        services.AddSingleton<ScreenshotRetentionService>();
        // Updating from inside the application, so a fix does not need somebody to download an
        // artifact and swap a folder by hand.
        // Updating from inside the application. Velopack owns the install location and the
        // restart, which is what the hand-written swap script could never do: it had to
        // replace files in a folder a sync client held open, and on a real machine that never
        // once succeeded.
        services.AddSingleton<VelopackUpdateGateway>();
        services.AddSingleton<MapVariantSelectionService>();
        services.AddSingleton<QuestMapProjectionService>();
        services.AddSingleton<MapViewModel>();

        services.AddSingleton<IPlayerProfileService, JsonFilePlayerProfileService>();
        services.AddSingleton(provider => new ProjectQuestProgressJson(
            provider.GetRequiredService<ProjectQuestProgressJsonOptions>(),
            timeProvider));
        services.AddSingleton<IProjectQuestProgressJson>(provider =>
            provider.GetRequiredService<ProjectQuestProgressJson>());
        services.AddSingleton<QuestEligibilityEvaluator>();
        services.AddSingleton<QuestProgressCommandService>();
        services.AddSingleton<IQuestProgressCommandService>(provider =>
            provider.GetRequiredService<QuestProgressCommandService>());
        services.AddSingleton<QuestReadService>();
        services.AddSingleton<IQuestReadService>(provider => provider.GetRequiredService<QuestReadService>());
        services.AddSingleton<QuestProgressExchangeService>();
        services.AddSingleton<IQuestProgressExchangeService>(provider =>
            provider.GetRequiredService<QuestProgressExchangeService>());
        services.AddSingleton<IQuestProgressImportPlanner>(provider =>
            provider.GetRequiredService<QuestProgressExchangeService>());
        services.AddSingleton<IIntegrationSecretStore>(integrationSecretStore);

        services.AddSingleton<TarkovTrackerApiClient>(_ => new(
            settings.TarkovTrackerHttpMessageHandler ??
                (offline
                    ? new OfflineHttpMessageHandler()
                    : new HttpClientHandler { AllowAutoRedirect = false }),
            tarkovTrackerOptions,
            timeProvider));
        services.AddSingleton<ITarkovTrackerApiClient>(provider =>
            provider.GetRequiredService<TarkovTrackerApiClient>());
        services.AddSingleton<TarkovTrackerIntegrationService>();
        services.AddSingleton<ITarkovTrackerIntegrationService>(provider =>
            provider.GetRequiredService<TarkovTrackerIntegrationService>());
        services.AddSingleton<QuestsPageViewModel>();
        services.AddSingleton<SqliteRequirementCatalog>();
        services.AddSingleton<IRequirementCatalog>(provider => provider.GetRequiredService<SqliteRequirementCatalog>());
        services.AddSingleton<SqliteMapAliasCatalog>();
        services.AddSingleton<IMapAliasCatalog>(provider => provider.GetRequiredService<SqliteMapAliasCatalog>());
        services.AddSingleton<SqliteItemFactCatalog>();
        services.AddSingleton<IItemFactCatalog>(provider => provider.GetRequiredService<SqliteItemFactCatalog>());
        services.AddSingleton<IEventCatalog>(_ =>
            new JsonFileEventCatalog(Path.Combine(paths.Config, "Events")));

        // The aggregation service takes its requirements as constructor collections, and
        // nothing ever registered one, so it always answered "0 needed". That silently
        // disabled the scanner's outstanding-quest, found-in-raid and hideout reasons.
        // Reading them here is what brings those back.
        // Starts empty and is filled by the startup coordinator once the requirements have
        // been read. Building it from a blocking catalog read instead captured empty data on
        // a clean install, where the database is still empty at composition time, and the
        // block itself was enough to stall a scan waiting behind it.
        services.AddSingleton(_ => new ProfileNeedAggregationService([], []));
        services.AddSingleton<IQuestProgressService, ProfileQuestProgressService>();
        services.AddSingleton<IHideoutProgressService, ProfileHideoutProgressService>();
        services.AddSingleton<RecommendationContextService>();
        // Built from the event catalog rather than by type. Registered by type it received an
        // empty definition list, and it throws KeyNotFoundException for an unknown event id
        // rather than degrading, so every call failed no matter what the caller passed.
        services.AddSingleton<IEventTrackerService>(provider => new ProfileEventTrackerService(
            provider.GetRequiredService<IPlayerProfileService>(),
            provider.GetRequiredService<IEventCatalog>().GetAsync(CancellationToken.None).GetAwaiter().GetResult(),
            timeProvider));
        services.AddSingleton<IAmmoIntelligenceService, AmmoIntelligenceService>();
        services.AddSingleton<IKeyIntelligenceService, KeyIntelligenceService>();
        services.AddSingleton<ILoadoutService, LoadoutIntelligenceService>();
        services.AddSingleton<IRecommendationEngine, RecommendationEngine>();

        services.AddSingleton<SqliteMapFeatureCatalog>();
        services.AddSingleton<IMapFeatureCatalog>(provider => provider.GetRequiredService<SqliteMapFeatureCatalog>());
        // Reads the map, its extracts and who may take each one out of the synced catalog.
        // The in-memory cache this replaces was filled from a list of maps nothing ever
        // registered, so every lookup returned nothing and the extract screen had nothing to
        // match its lines against.
        services.AddSingleton<SqliteMapDefinitionCache>();
        services.AddSingleton<IMapDefinitionCache>(provider => provider.GetRequiredService<SqliteMapDefinitionCache>());
        services.AddSingleton<IMapDataService, MapDataService>();
        services.AddSingleton<IMapTransformService, MapTransformService>();
        services.AddSingleton<IStrategyModel, StrategyModel>();
        services.AddSingleton<IRoutePlanner, RoutePlanner>();
        services.AddSingleton<IScreenshotFilenameParser, ScreenshotFilenameParser>();
        // The game's own screenshot key drives a scan, so one press gives the position and
        // whatever the picture shows rather than needing a second shortcut.
        services.AddSingleton<IScreenshotImageLoader, SkiaScreenshotImageLoader>();
        services.AddSingleton<EftLogParser>();
        services.AddSingleton<SquadStateService>();
        services.AddSingleton<FleaSaleStateService>();
        // The game announces every quest starting, failing and being handed in, and until now
        // nobody was listening: the page showed five hundred quests all reading Unknown while
        // the answer sat in the same files the flea sales come from.
        services.AddSingleton<QuestLogProgressService>();
        services.AddSingleton<IEftLogObserver, EftLogObservers>();
        services.AddSingleton<IRaidStateService>(_ => new RaidStateService(commandLine.DeveloperMode || commandLine.Demo));

        services.AddSingleton<TesseractOcrEngine>();
#if WINDOWS10_0_19041_0_OR_GREATER
        // The engine Windows already has, preferred where it works. It needs no native binary
        // and no Visual C++ redistributable, and on the extract panel it reads text the other
        // engine's thresholding eats. Never a replacement: where Windows will not start it, the
        // registration below falls through to Tesseract, and a machine where neither works says
        // so on the Settings page rather than failing silently.
        services.AddSingleton<TarkovCompanion.Platform.Windows.Ocr.WindowsMediaOcrEngine>();
        services.AddSingleton<IOcrEngine>(provider =>
        {
            var windows = provider.GetRequiredService<TarkovCompanion.Platform.Windows.Ocr.WindowsMediaOcrEngine>();
            return windows.Availability.IsAvailable
                ? windows
                : provider.GetRequiredService<TesseractOcrEngine>();
        });
        services.AddSingleton<IOcrEngineStatus>(provider =>
        {
            var windows = provider.GetRequiredService<TarkovCompanion.Platform.Windows.Ocr.WindowsMediaOcrEngine>();
            return windows.Availability.IsAvailable
                ? windows
                : provider.GetRequiredService<TesseractOcrEngine>();
        });
#else
        services.AddSingleton<IOcrEngine>(provider => provider.GetRequiredService<TesseractOcrEngine>());
        services.AddSingleton<IOcrEngineStatus>(provider => provider.GetRequiredService<TesseractOcrEngine>());
#endif
        services.AddSingleton<CanonicalItemResolverCache>();
        // The same instance, also offered as something a sync invalidates. Without this it is
        // built from whatever the item table held at startup and kept for the life of the
        // process, so a scan before the first sync returned no_match until a restart.
        services.AddSingleton<IInvalidatableProjection>(provider =>
            provider.GetRequiredService<CanonicalItemResolverCache>());
        services.AddSingleton<ScanContextDetector>();
        services.AddSingleton<OcrCoordinator>();
        services.AddSingleton<RecognitionService>();
        services.AddSingleton<IRecognitionService>(provider => provider.GetRequiredService<RecognitionService>());
        services.AddSingleton<RecognitionSelfTest>();
        services.AddSingleton<IRecognitionSelfTest>(provider => provider.GetRequiredService<RecognitionSelfTest>());

        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IGameWindowLocator, WindowsGameWindowLocator>();
            services.AddSingleton<IMonitorService, WindowsMonitorService>();
            services.AddSingleton<IEftPathLocator>(provider => new WindowsEftPathLocator(
                null,
                provider.GetRequiredService<IEftPathOverrideStore>()));
            services.AddSingleton<IEftLogWatcher, WindowsEftLogWatcher>();
            services.AddSingleton<IScreenshotWatcher>(_ => new WindowsScreenshotWatcher(commandLine.DeveloperMode));
            services.AddSingleton<IRecycleBin, WindowsRecycleBin>();
            services.AddSingleton<IScreenCaptureService, GdiScreenCaptureService>();
            services.AddSingleton<ExtractRecognitionService>();
            services.AddSingleton<IExtractRecognitionService>(provider =>
                provider.GetRequiredService<ExtractRecognitionService>());
            services.AddSingleton<ContainerRecognitionService>();
            services.AddSingleton<IContainerRecognitionService>(provider =>
                provider.GetRequiredService<ContainerRecognitionService>());
            services.AddSingleton<FleaRecognitionService>();
            services.AddSingleton<IFleaRecognitionService>(provider =>
                provider.GetRequiredService<FleaRecognitionService>());
            services.AddSingleton<EvidenceRequiredScanRecommendationContextProvider>();
            services.AddSingleton<IScanRecommendationContextProvider>(provider =>
                provider.GetRequiredService<EvidenceRequiredScanRecommendationContextProvider>());
            services.AddSingleton<LatestScanResultPublisher>();
            services.AddSingleton<IScanResultPublisher>(provider =>
                provider.GetRequiredService<LatestScanResultPublisher>());
            services.AddSingleton<RecognitionScanUseCase>();
            services.AddSingleton<RecognitionScanContract>(provider =>
                provider.GetRequiredService<RecognitionScanUseCase>());
        }

        if (!OperatingSystem.IsWindows())
        {
            services.AddSingleton<IEftPathLocator, UnavailableEftPathLocator>();
            services.AddSingleton<IEftLogWatcher, UnavailableEftLogWatcher>();
            services.AddSingleton<IScreenshotWatcher, UnavailableScreenshotWatcher>();
            services.AddSingleton<IRecycleBin, UnavailableRecycleBin>();
        }

        services.AddSingleton<RaidObservationService>();

        services.AddSingleton<IRuntimeStateStore, RuntimeStateStore>();
        services.AddSingleton<RaidActivityCoordinator>();
        // The same instance, offered as the narrow seam a scan is given. The recognition path
        // used to hold IRaidStateService and mutate raid state itself, which skipped the
        // coordinator and left the extracts event unwritten by anything.
        services.AddSingleton<IRaidActivityRecorder>(provider =>
            provider.GetRequiredService<RaidActivityCoordinator>());
        services.AddSingleton<ApplicationStartupCoordinator>();
        services.AddSingleton<IScanAdapter>(_ => settings.ScanAdapter
            ?? (commandLine.Demo
                ? new FixtureScanAdapter(
                    new("demo-graphics-card"),
                    _.GetRequiredService<IItemRepository>(),
                    _.GetRequiredService<IRecommendationEngine>(),
                    timeProvider)
                : OperatingSystem.IsWindows()
                    ? new RecognitionScanAdapter(_.GetRequiredService<RecognitionScanContract>())
                    : new UnavailableScanAdapter(timeProvider)));
        services.AddSingleton<IRuntimeScanUseCase, RuntimeScanUseCase>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    /// <summary>
    /// Creates the handler used for public game-data requests.
    /// </summary>
    /// <remarks>
    /// json.tarkov.dev payloads are several megabytes of JSON and are buffered whole. Without
    /// negotiated compression a cold first sync moved many times more bytes than it needed to
    /// and regularly exhausted its bounded budget.
    /// </remarks>
    /// <summary>
    /// The group server's catalog mirror, from the group settings file, or nothing.
    /// </summary>
    /// <remarks>
    /// Read from the file rather than through <c>IGroupSettingsStore</c> because composition
    /// cannot await, and read at all only because a group that already runs a server can serve
    /// the catalog once for everybody. Any failure returns nothing, which is the same as not
    /// having a server: upstream is always tried afterwards, so this can only ever save a
    /// download and never cost one.
    ///
    /// It does not follow a change to the setting until the next launch. Said here rather than
    /// worked around, because the alternative is a moving base address inside a client that
    /// caches by path, and a download saved is not worth that.
    /// </remarks>
    private static Uri? ReadCatalogMirror(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return null;
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("serverUri", out var server) ||
                server.ValueKind != System.Text.Json.JsonValueKind.String ||
                !Uri.TryCreate(server.GetString(), UriKind.Absolute, out var uri) ||
                // The same rule the group key follows. The catalog mirror does not carry the
                // key, but it is the same address read from the same file, and accepting an
                // http host here while refusing it there would be a confusing half-measure.
                !GroupSharingSettings.IsTransportAcceptable(uri))
            {
                return null;
            }

            // The trailing slash matters: a relative path is resolved against the last segment
            // of the base, so "catalog" without one would replace the group server's own path
            // rather than sit under it.
            return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/catalog/");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static HttpClientHandler CreateDataHandler() =>
        new() { AutomaticDecompression = DecompressionMethods.All };

    private static bool IsEnabled(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    public static bool OptionalFeatureEnabled(string? configuredValue, bool protectedStorageAvailable) =>
        string.IsNullOrWhiteSpace(configuredValue)
            ? protectedStorageAvailable
            : IsEnabled(configuredValue);

    private sealed class OfflineHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException("Network access is disabled by TARKOV_COMPANION_OFFLINE."));
        }
    }
}
