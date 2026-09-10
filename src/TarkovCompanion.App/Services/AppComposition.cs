using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.Quests;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Recognition;
using TarkovCompanion.Application.Services.Runtime;
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
using TarkovCompanion.Infrastructure.Maps;
using TarkovCompanion.Infrastructure.Profile;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Security;
using TarkovCompanion.Infrastructure.TarkovDevJson;
using TarkovCompanion.Infrastructure.TarkovTracker;
using TarkovCompanion.Platform.Windows.Capture;
using TarkovCompanion.Platform.Windows.Discovery;
using TarkovCompanion.Platform.Windows.Displays;
using TarkovCompanion.Platform.Windows.Hotkeys;
using TarkovCompanion.Platform.Windows.Security;
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
    public const string DataRootEnvironmentVariable = "TARKOV_COMPANION_DATA_ROOT";
    public const string OfflineEnvironmentVariable = "TARKOV_COMPANION_OFFLINE";
    public const string TarkovTrackerEnvironmentVariable = "TARKOV_COMPANION_TARKOVTRACKER_ENABLED";

    public static ServiceProvider Build(AppCommandLine commandLine, AppCompositionSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        settings ??= new();
        var timeProvider = settings.TimeProvider ?? TimeProvider.System;
        var offline = settings.Offline ?? IsEnabled(Environment.GetEnvironmentVariable(OfflineEnvironmentVariable));
        var dataRoot = settings.DataRoot ?? Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        var paths = AppDataPaths.Resolve(dataRoot, commandLine.Demo);
        var runtimeOptions = new RuntimeOptions(
            commandLine.Demo,
            offline,
            GameMode.Regular,
            "en",
            TimeSpan.FromHours(9),
            TimeSpan.FromSeconds(45));
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
        services.AddSingleton<SqliteQuestCatalog>();
        services.AddSingleton<IQuestCatalog>(provider => provider.GetRequiredService<SqliteQuestCatalog>());
        services.AddSingleton<SqliteQuestProgressStore>();
        services.AddSingleton<IQuestProgressStore>(provider =>
            provider.GetRequiredService<SqliteQuestProgressStore>());
        services.AddSingleton(provider => new SqliteQuestProgressImportStore(
            provider.GetRequiredService<SqliteConnectionFactory>(),
            timeProvider));
        services.AddSingleton<IQuestProgressImportStore>(provider =>
            provider.GetRequiredService<SqliteQuestProgressImportStore>());

        services.AddSingleton<DataTranslationService>();
        services.AddSingleton(_ => new HttpClient(
            offline ? new OfflineHttpMessageHandler() : settings.HttpMessageHandler ?? new HttpClientHandler(),
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        services.AddSingleton(provider => new TarkovDevJsonClient(
            provider.GetRequiredService<HttpClient>(),
            provider.GetRequiredService<ITarkovDevResponseCache>(),
            provider.GetRequiredService<DataTranslationService>(),
            timeProvider: timeProvider));
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
        services.AddSingleton<ProfileNeedAggregationService>();
        services.AddSingleton<IQuestProgressService, ProfileQuestProgressService>();
        services.AddSingleton<IHideoutProgressService, ProfileHideoutProgressService>();
        services.AddSingleton<RecommendationContextService>();
        services.AddSingleton<IEventTrackerService, ProfileEventTrackerService>();
        services.AddSingleton<IAmmoIntelligenceService, AmmoIntelligenceService>();
        services.AddSingleton<IKeyIntelligenceService, KeyIntelligenceService>();
        services.AddSingleton<ILoadoutService, LoadoutIntelligenceService>();
        services.AddSingleton<IRecommendationEngine, RecommendationEngine>();

        services.AddSingleton<IMapDefinitionCache>(_ => new InMemoryMapDefinitionCache(
            commandLine.Demo ? CreateDemoMaps(timeProvider.GetUtcNow()) : []));
        services.AddSingleton<IMapDataService, MapDataService>();
        services.AddSingleton<IMapTransformService, MapTransformService>();
        services.AddSingleton<IStrategyModel, StrategyModel>();
        services.AddSingleton<IRoutePlanner, RoutePlanner>();
        services.AddSingleton<IScreenshotFilenameParser, ScreenshotFilenameParser>();
        services.AddSingleton<EftLogParser>();
        services.AddSingleton<IRaidStateService>(_ => new RaidStateService(commandLine.DeveloperMode || commandLine.Demo));

        services.AddSingleton<TesseractOcrEngine>();
        services.AddSingleton<IOcrEngine>(provider => provider.GetRequiredService<TesseractOcrEngine>());
        services.AddSingleton<IOcrEngineStatus>(provider => provider.GetRequiredService<TesseractOcrEngine>());
        services.AddSingleton<CanonicalItemResolverCache>();
        services.AddSingleton<ScanContextDetector>();
        services.AddSingleton<OcrCoordinator>();
        services.AddSingleton<RecognitionService>();
        services.AddSingleton<IRecognitionService>(provider => provider.GetRequiredService<RecognitionService>());
        services.AddSingleton<RecognitionSelfTest>();
        services.AddSingleton<IRecognitionSelfTest>(provider => provider.GetRequiredService<RecognitionSelfTest>());

        services.AddSingleton<PngFileScreenCaptureService>();
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IGameWindowLocator, WindowsGameWindowLocator>();
            services.AddSingleton<IMonitorService, WindowsMonitorService>();
            services.AddSingleton<IEftPathLocator, WindowsEftPathLocator>();
            services.AddSingleton<IEftLogWatcher, WindowsEftLogWatcher>();
            services.AddSingleton<IScreenshotWatcher>(_ => new WindowsScreenshotWatcher(commandLine.DeveloperMode));
            services.AddSingleton<IGlobalHotkeyService, WindowsGlobalHotkeyService>();
            services.AddSingleton<IScreenCaptureService>(provider =>
            {
                var native = new GdiScreenCaptureService(
                    provider.GetRequiredService<IGameWindowLocator>(),
                    commandLine.DeveloperMode);
                return commandLine.DeveloperMode
                    ? new DeveloperScreenCaptureService(
                        provider.GetRequiredService<PngFileScreenCaptureService>(),
                        native)
                    : native;
            });
        }
        else if (commandLine.DeveloperMode)
        {
            services.AddSingleton<IScreenCaptureService>(provider =>
                provider.GetRequiredService<PngFileScreenCaptureService>());
        }

        if (OperatingSystem.IsWindows() || commandLine.DeveloperMode)
        {
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

        services.AddSingleton<IRuntimeStateStore, RuntimeStateStore>();
        services.AddSingleton<RaidActivityCoordinator>();
        services.AddSingleton<ApplicationStartupCoordinator>();
        services.AddSingleton<IScanAdapter>(_ => settings.ScanAdapter
            ?? (commandLine.DeveloperMode && (OperatingSystem.IsWindows() || _.GetService<IScreenCaptureService>() is not null)
                ? new RecognitionScanAdapter(_.GetRequiredService<RecognitionScanContract>())
                : commandLine.Demo
                ? new FixtureScanAdapter(
                    new("demo-graphics-card"),
                    _.GetRequiredService<IItemRepository>(),
                    _.GetRequiredService<IRecommendationEngine>(),
                    timeProvider)
                : OperatingSystem.IsWindows()
                    ? new RecognitionScanAdapter(_.GetRequiredService<RecognitionScanContract>())
                    : new UnavailableScanAdapter(timeProvider)));
        services.AddSingleton<IRuntimeScanUseCase, RuntimeScanUseCase>();
        services.AddSingleton<DiagnosticScenarioProcessor>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static bool IsEnabled(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    public static bool OptionalFeatureEnabled(string? configuredValue, bool protectedStorageAvailable) =>
        string.IsNullOrWhiteSpace(configuredValue)
            ? protectedStorageAvailable
            : IsEnabled(configuredValue);

    private static IReadOnlyList<MapDefinition> CreateDemoMaps(DateTimeOffset observedUtc)
    {
        var provenance = new DataProvenance("simulator-fixture; live-unvalidated", observedUtc.ToUniversalTime());
        return
        [
            new(
                "customs",
                "Customs",
                TimeSpan.FromMinutes(40),
                TimeSpan.FromMinutes(25),
                [],
                [
                    new("customs-crossroads", "customs", "Crossroads", null, null, provenance),
                    new("customs-trailer-park", "customs", "Trailer Park", null, null, provenance),
                    new("customs-dorms-v-ex", "customs", "Dorms V-Ex", null, "Dynamic status must be observed.", provenance),
                ],
                null,
                provenance),
        ];
    }

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
