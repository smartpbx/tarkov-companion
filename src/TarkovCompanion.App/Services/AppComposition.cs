using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.Services.V2.Profile;
using TarkovCompanion.App.Services.V2.SelfTest;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.Quests;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.Services.V2.Setup;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Infrastructure.Persistence.Inventory;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Infrastructure.Devices;
using TarkovCompanion.Infrastructure.Diagnostics;
using TarkovCompanion.Platform.Windows.Devices;
using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Loadouts;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Application.Services.Personalization;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Recognition;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Shell;
using TarkovCompanion.App.Services.Updates;
using TarkovCompanion.App.ViewModels.V2.Debrief;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.StashScan;
using TarkovCompanion.App.ViewModels.V2.Tablet;
using TarkovCompanion.App.ViewModels.V2.Team;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Application.Services.Strategy;
using TarkovCompanion.Infrastructure.Strategy.Datasets;
using TarkovCompanion.Application.Services.Wiki;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Keys;
using TarkovCompanion.Core.Domain.Loadouts;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Stash;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.Persistence.Stash;
using TarkovCompanion.Infrastructure.Events;
using TarkovCompanion.Infrastructure.GameData.LootSpawns;
using TarkovCompanion.Infrastructure.Maps;
using TarkovCompanion.Infrastructure.Workspaces;
using TarkovCompanion.Infrastructure.Profile;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.App.Services.V2.Notifications;
using TarkovCompanion.App.Services.Windowing;
using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Infrastructure.Settings;
using TarkovCompanion.Infrastructure.Security;
using TarkovCompanion.Infrastructure.TarkovDevJson;
using TarkovCompanion.Infrastructure.TarkovTracker;
using TarkovCompanion.Infrastructure.Wiki;
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
    IIntegrationSecretStore? IntegrationSecretStore = null,
    IMonitorService? MonitorService = null,
    IDesktopWindowPlacementController? WindowPlacementController = null);

public static class AppComposition
{
    public const string OfflineEnvironmentVariable = "TARKOV_COMPANION_OFFLINE";
    public const string TarkovTrackerEnvironmentVariable = "TARKOV_COMPANION_TARKOVTRACKER_ENABLED";

    public static ServiceProvider Build(AppCommandLine commandLine, AppCompositionSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        settings ??= new();
        var timeProvider = settings.TimeProvider ?? TimeProvider.System;
        Func<bool> offlineProbe = settings.Offline is { } configuredOffline
            ? () => configuredOffline
            : () => IsEnabled(Environment.GetEnvironmentVariable(OfflineEnvironmentVariable));
        var offline = offlineProbe();
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
            TimeSpan.FromMinutes(5))
        {
            OfflineProbe = offlineProbe,
        };
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
        services.AddSingleton<SqliteDataPlatformMaintenance>();
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
        services.AddSingleton<SqliteOutboxStore>();
        services.AddSingleton<IOutboxStore>(provider => provider.GetRequiredService<SqliteOutboxStore>());
        // Behind a queue, so a database busy with the hourly catalog refresh cannot stall the
        // watcher reading the game's log. A write that arrives late is a row with the right
        // timestamp; an observation that never happens is gone.
        services.AddSingleton<IRaidHistoryService>(provider => new RaidHistoryOutbox(
            provider.GetRequiredService<SqliteRaidHistoryService>(),
            provider.GetService<ILogger<RaidHistoryOutbox>>(),
            timeProvider,
            provider.GetRequiredService<IOutboxStore>()));
        services.AddSingleton<SqliteProfileWorkspaceStore>();
        services.AddSingleton<IProfileWorkspaceStore>(provider => provider.GetRequiredService<SqliteProfileWorkspaceStore>());
        services.AddSingleton<ProfileContextService>();
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
        // Two columns of a table every sync has rewritten since migration 0001 and nothing has
        // ever read, which is why the Quests page printed a trader's id where it meant Prapor.
        services.AddSingleton<SqliteTraderCatalog>();
        services.AddSingleton<ITraderCatalog>(provider => provider.GetRequiredService<SqliteTraderCatalog>());
        // 789 barters rewritten on every sync, across three tables, with no reader anywhere.
        // Hidden from the unread-table sweep by its DELETE blind spot until that was fixed.
        services.AddSingleton<SqliteBarterCatalog>();
        services.AddSingleton<IBarterCatalog>(provider => provider.GetRequiredService<SqliteBarterCatalog>());
        // Cost and yield history used to have a write API only. Keep the normalized definition
        // and its bounded economics history behind one production read contract.
        services.AddSingleton<SqliteCraftPlanningCatalog>();
        services.AddSingleton<ICraftPlanningCatalog>(provider =>
            provider.GetRequiredService<SqliteCraftPlanningCatalog>());
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
            settings.HttpMessageHandler ?? CreateDataHandler(),
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
                // The environment variable is an operating mode, not a constructor-time choice.
                // Keeping the real handler underneath this probe lets the bounded stale-cache
                // retry observe a later transition back online without restarting the process.
                OfflineProbe = offlineProbe,
            },
            timeProvider));
        services.AddSingleton<TarkovDevDataRefreshOperation>();
        services.AddSingleton<IDataRefreshOperation>(provider => provider.GetRequiredService<TarkovDevDataRefreshOperation>());
        services.AddSingleton<IDataSyncService, DataSyncService>();
        services.AddSingleton<IItemSearchService, ItemSearchService>();
        services.AddSingleton<IPriceHistoryService, PriceHistoryService>();

        // [V2 rough package 46] How the player arranged the window: the navigation rail's width
        // and the Raid context panel's. Chrome preferences, remembered so they are set once.
        services.AddSingleton<IWorkspaceLayoutStore>(_ => new JsonFileWorkspaceLayoutStore(
            Path.Combine(paths.Config, "workspace-layout.json")));

        services.AddSingleton(TarkovDevMapCatalogClientOptions.CreateDefault(Path.Combine(paths.Cache, "Maps", "Catalog")));
        // [P0 stability] Map drawings are rasterised in a child process. A native access
        // violation inside Skia killed the application on 2026-09-19 and cannot be caught, so the
        // draw happens somewhere the application can afford to lose. Null under a test, a tool or
        // `dotnet run`, where the running process is not this application's own host executable
        // and re-launching it would run something else; rasterisation is then in process, as before.
        services.AddSingleton(MapAssetCacheOptions.CreateDefault(Path.Combine(paths.Cache, "Maps", "Assets")) with
        {
            Rasterizer = ResolveRasterizerHost(),
        });
        services.AddSingleton<TarkovDevMapCatalogClient>();
        services.AddSingleton<TarkovDevMapAssetCache>();
        services.AddSingleton<TarkovDevLootSpawnNormalizer>();
        services.AddSingleton(provider => new DurableLootSpawnPublicationStore(
            Path.Combine(paths.Cache, "LootSpawns", "publication.cache"),
            timeProvider));
        services.AddSingleton<ILootSpawnSourcePublicationStore>(provider =>
            provider.GetRequiredService<DurableLootSpawnPublicationStore>());
        services.AddSingleton<IReviewedLootSpawnPublicationReplacementStore>(provider =>
            provider.GetRequiredService<DurableLootSpawnPublicationStore>());
        services.AddSingleton(provider => new TarkovDevLootSpawnRefreshService(
            runtimeOptions.GameMode,
            runtimeOptions.Language,
            provider.GetRequiredService<TarkovDevJsonClient>(),
            provider.GetRequiredService<TarkovDevMapCatalogClient>(),
            provider.GetRequiredService<TarkovDevLootSpawnNormalizer>(),
            provider.GetRequiredService<ILootSpawnSourcePublicationStore>(),
            timeProvider));
        services.AddSingleton<ILootSpawnSourceRefreshService>(provider =>
            provider.GetRequiredService<TarkovDevLootSpawnRefreshService>());
        services.AddSingleton<HighValueLootLayerService>();
        services.AddSingleton<HighValueLootRuntimeSource>();
        services.AddSingleton<IHighValueLootRuntimeSource>(provider =>
            provider.GetRequiredService<HighValueLootRuntimeSource>());
        // V2 Raid cockpit (package 2): the scene adapter, the historical-traffic runtime it
        // registers but does not yet evaluate (see RaidCockpitViewModel's remark on why), and
        // local pings/waypoints kept between runs.
        services.AddSingleton<MapSceneAssembler>();
        services.AddSingleton<HistoricalTrafficRuntimeService>();
        // [Issue 311] The governed traffic snapshot store, which was merged and tested and never
        // constructed. Packages are the five files tools/TrafficModelBuilder writes, left one
        // directory each in Traffic/Inbox; only a key listed in Config/traffic-trusted-keys.json
        // can make one install, and with none listed nothing does.
        services.AddSingleton(provider => new TrafficSnapshotStore(
            new TrafficSnapshotStoreOptions(Path.Combine(paths.Root, "Traffic", "Snapshots")),
            new TrafficModelPackageImporter(TrafficTrustedKeys.Load(
                Path.Combine(paths.Config, "traffic-trusted-keys.json"),
                provider.GetService<ILogger<TrafficSnapshotStore>>())),
            timeProvider));
        services.AddSingleton<ITrafficPublicationSource>(provider => new InstalledTrafficPublicationSource(
            provider.GetRequiredService<TrafficSnapshotStore>(),
            Path.Combine(paths.Root, "Traffic", "Inbox"),
            provider.GetService<ILogger<InstalledTrafficPublicationSource>>()));
        services.AddSingleton<IGameVersionSource>(provider => new EftLogFolderGameVersionSource(
            async cancellationToken => (await provider.GetRequiredService<IEftPathLocator>()
                .FindAsync(cancellationToken).ConfigureAwait(false)).LogRoot,
            timeProvider));
        services.AddSingleton(provider => new HistoricalTrafficSource(
            provider.GetRequiredService<ITrafficPublicationSource>(),
            provider.GetRequiredService<HistoricalTrafficRuntimeService>(),
            provider.GetRequiredService<IGameVersionSource>(),
            provider.GetRequiredService<IProfileRuntimeContextService>(),
            provider.GetRequiredService<IMapDataService>(),
            timeProvider));
        services.AddSingleton<IRaidMarkStore>(_ =>
            new JsonFileRaidMarkStore(Path.Combine(paths.Config, "raid-marks.json"), timeProvider));
        // [Issue 379] Objective markers the player placed themselves, kept apart from the quest
        // catalog because a sync replaces the catalog and a note of theirs must outlive it.
        services.AddSingleton<IUserQuestMarkStore>(_ =>
            new JsonFileUserQuestMarkStore(Path.Combine(paths.Config, "user-quest-markers.json"), timeProvider));
        // [Issue 571] "Done" by hand, on the map or the Objectives list: kept apart from quest
        // progress on purpose (see HandDoneObjectives.cs), so it survives a restart the same way
        // the marker above does.
        services.AddSingleton<IHandDoneObjectiveStore>(_ =>
            new JsonFileHandDoneObjectiveStore(Path.Combine(paths.Config, "hand-done-objectives.json"), timeProvider));
        // [Issue 318/563] The last verified loot-spawn import's per-map coverage plus its last
        // refresh attempt's own error (if any), for Setup > Data.
        services.AddSingleton(provider => new LootCoverageViewModel(
            provider.GetRequiredService<ILootSpawnSourcePublicationStore>(),
            () => provider.GetRequiredService<MapViewModel>().Locations,
            provider.GetRequiredService<IHighValueLootRuntimeSource>()));
        services.AddSingleton(provider => new QuestCoverageViewModel(
            provider.GetRequiredService<IQuestCatalog>(),
            provider.GetRequiredService<IUserQuestMarkStore>(),
            provider.GetRequiredService<IMapDataService>(),
            () => provider.GetRequiredService<MapViewModel>().Locations,
            provider.GetRequiredService<IProfileRuntimeContextService>()));
        services.AddSingleton<IMapVariantPreferenceStore>(_ =>
            new JsonFileMapVariantPreferenceStore(Path.Combine(paths.Config, "map-defaults.json")));
        // Sharing with a group is the only part of this application that sends anything
        // anywhere, so it is composed here explicitly rather than discovered. Decorated so
        // saving a new relay address reconfigures the paired-tablet bridge at once instead of
        // leaving it on whatever group.json said at startup (RelayMarksBridge is registered
        // further down, but DI resolves it lazily on first use, not in registration order).
        services.AddSingleton<IGroupSettingsStore>(provider =>
            new RelayReconfiguringGroupSettingsStore(
                new JsonFileGroupSettingsStore(Path.Combine(paths.Config, "group.json")),
                provider.GetRequiredService<RelayMarksBridge>()));
        // What the player is working on, for the group to see. Dead until tonight, because
        // there was no quest progress to send.
        services.AddSingleton<GroupQuestShare>();
        services.AddSingleton<GroupKitShare>();
        services.AddSingleton<GroupSessionService>();
        // Keeping the game's screenshot folder from growing without limit. Composed here
        // rather than discovered because it is the other half of the application that touches
        // files it did not create, and that should be visible in one place.
        services.AddSingleton(provider => new SqliteScreenshotRetentionStore(
            provider.GetRequiredService<SqliteConnectionFactory>(),
            timeProvider,
            Path.Combine(paths.Config, "screenshots.json")));
        services.AddSingleton<IScreenshotRetentionStore>(provider =>
            provider.GetRequiredService<SqliteScreenshotRetentionStore>());
        // Where the game keeps its screenshots and logs, when the guessing is wrong. The first
        // person to install this who does not use OneDrive had no screenshots detected and no
        // way to say where they were.
        services.AddSingleton<IEftPathOverrideStore>(_ =>
            new JsonFileEftPathOverrideStore(Path.Combine(paths.Config, "game-folders.json")));
        // Where the window was and how the rail was left. The shell opened at 1500 by 900 in
        // whatever place the operating system chose, every launch, and no store had an entry.
        services.AddSingleton<IShellLayoutStore>(_ =>
            new JsonFileShellLayoutStore(Path.Combine(paths.Config, "shell.json")));
        services.AddSingleton<IDesktopWindowPlacementStore>(_ =>
            new JsonFileDesktopWindowPlacementStore(Path.Combine(paths.Config, "window-placement.json")));
        if (settings.WindowPlacementController is not null)
        {
            services.AddSingleton(settings.WindowPlacementController);
        }
        else if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<DesktopWindowPlacementController>();
            services.AddSingleton<IDesktopWindowPlacementController>(provider =>
                provider.GetRequiredService<DesktopWindowPlacementController>());
        }
        // [#309] What the hourly tidy moved, or failed to, kept as counts and reasons across restarts.
        services.AddSingleton<IScreenshotTidyLedger>(_ =>
            new JsonFileScreenshotTidyLedger(Path.Combine(paths.Config, "screenshot-tidy-ledger.json")));
        // [V2 rough package 60 — appearance] #266/#315: the one versioned record that says how
        // the companion looks. Nothing persisted a theme, a text scale, a density or a motion
        // choice before this, so every palette the design system shipped was unreachable.
        services.AddSingleton<IWorkspacePreferenceStore>(_ =>
            new JsonFileWorkspacePreferenceStore(Path.Combine(paths.Config, "preferences.json")));
        services.AddSingleton<WorkspacePreferenceService>();
        // [V2 rough package 60 — Plan] #288: saved kits, so a loadout survives closing the page.
        services.AddSingleton<ILoadoutPresetStore>(_ =>
            new JsonFileLoadoutPresetStore(Path.Combine(paths.Config, "loadouts.json")));
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

        // [#269] profile.json stays the first profile's progress; every other profile gets its own file
        // under profiles/, and IPlayerProfileService hands each caller the active profile's file.
        services.AddSingleton(provider => new JsonFilePlayerProfileService(
            provider.GetRequiredService<JsonProfileOptions>(),
            timeProvider,
            provider.GetRequiredService<SqliteConnectionFactory>()));
        services.AddSingleton<IPlayerProfileService>(provider => new ProfileScopedPlayerProfileService(
            provider.GetRequiredService<JsonFilePlayerProfileService>(),
            provider.GetRequiredService<IProfileRuntimeContextService>(),
            Path.Combine(paths.Config, "profiles"),
            path => new JsonFilePlayerProfileService(
                new JsonProfileOptions(path),
                timeProvider,
                provider.GetRequiredService<SqliteConnectionFactory>()),
            provider.GetService<ILogger<ProfileScopedPlayerProfileService>>()));
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
        services.AddSingleton<IHideoutPrerequisiteCatalog, SqliteHideoutPrerequisiteCatalog>();
        services.AddSingleton<TarkovCompanion.Application.Services.Planning.AllergyWarningService>();
        services.AddSingleton<SqliteMapAliasCatalog>();
        services.AddSingleton<IMapAliasCatalog>(provider => provider.GetRequiredService<SqliteMapAliasCatalog>());
        services.AddSingleton<SqliteItemFactCatalog>();
        services.AddSingleton<IItemFactCatalog>(provider => provider.GetRequiredService<SqliteItemFactCatalog>());
        // Package 5 (Intel workspace + wiki deep links): the fact catalog and quest progress
        // service already exist; this is the first caller to read them together for a single
        // item id instead of a whole legacy page.
        services.AddSingleton<IItemIntelService, ItemIntelService>();
        // Package 33 (#287, the lookup half): the Intel landing page's four real sections need
        // only what is already registered above, plus the catalog's own value ranking.
        services.AddSingleton<IHighValueItemCatalog, SqliteHighValueItemCatalog>();
        services.AddSingleton<IIntelLandingService, IntelLandingService>();
        // #287 (Crafts & barters tab): every craft and barter, priced and cached in memory. Reads
        // ICraftPlanningCatalog/IBarterCatalog/IRequirementCatalog/ITraderCatalog/IItemRepository/
        // IItemMarketFactSource, all already registered elsewhere in this method.
        services.AddSingleton<IIntelTradeCatalogService, IntelTradeCatalogService>();
        services.AddSingleton<IWikiLinkOpener, SystemBrowserWikiLinkOpener>();
        // One instance behind both interfaces, so a definition written through the authoring
        // side drops the cache the reading side is serving from.
        services.AddSingleton(_ => new JsonFileEventCatalog(Path.Combine(paths.Config, "Events")));
        services.AddSingleton<IEventCatalog>(provider => provider.GetRequiredService<JsonFileEventCatalog>());
        services.AddSingleton<IEventAuthoring>(provider => provider.GetRequiredService<JsonFileEventCatalog>());
        // #287 (event state on items): the Events page's Safe/Allergic/Untested result for every
        // item in a running event, read as one map for Intel's chips.
        services.AddSingleton<IIntelEventStateCatalog, IntelEventStateCatalog>();

        // The aggregation service takes its requirements as constructor collections, and
        // nothing ever registered one, so it always answered "0 needed". That silently
        // disabled the scanner's outstanding-quest, found-in-raid and hideout reasons.
        // Reading them here is what brings those back.
        // Starts empty and is filled by the startup coordinator once the requirements have
        // been read. Building it from a blocking catalog read instead captured empty data on
        // a clean install, where the database is still empty at composition time, and the
        // block itself was enough to stall a scan waiting behind it.
        services.AddSingleton(_ => new ProfileNeedAggregationService([], []));
        // Built rather than resolved by type so the quest board comes in, which is the only
        // place the set of quests the player is actually on exists: the profile holds what has
        // been completed and nothing else. Without it the Keys page can say a quest is ahead of
        // you and never that you are on one, which is the weaker half of the answer.
        services.AddSingleton<IQuestProgressService>(provider => new ProfileQuestProgressService(
            provider.GetRequiredService<IPlayerProfileService>(),
            provider.GetRequiredService<ProfileNeedAggregationService>(),
            provider.GetRequiredService<IQuestReadService>(),
            timeProvider));
        services.AddSingleton<IHideoutProgressService, ProfileHideoutProgressService>();
        services.AddSingleton<RecommendationContextService>();
        // Given the catalog rather than a list read from it. Registered by type it received an
        // empty definition list and threw KeyNotFoundException for every id; read once here it
        // knew only the events that existed at startup, which stopped being good enough when the
        // Events page learned to write one.
        services.AddSingleton<IEventTrackerService>(provider => new ProfileEventTrackerService(
            provider.GetRequiredService<IPlayerProfileService>(),
            [],
            timeProvider,
            provider.GetRequiredService<IEventCatalog>()));
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
        // [Issue 571] Attached where the service is made, so a failed or restarted quest clears the
        // player's hand Done marks whatever page happens to be open.
        services.AddSingleton<QuestLogProgressService>(provider =>
        {
            var questLog = ActivatorUtilities.CreateInstance<QuestLogProgressService>(provider);
            QuestLogHandDoneReconciler.Attach(
                questLog,
                provider.GetRequiredService<IHandDoneObjectiveStore>(),
                provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(QuestLogHandDoneReconciler)));
            return questLog;
        });
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
        // [#453] One OCR per screenshot: the always-on scan and the V2 capture session read the same frame.
        services.AddSingleton(provider =>
        {
            var coordinator = ActivatorUtilities.CreateInstance<OcrCoordinator>(provider);
            coordinator.SharesIdenticalFrames = true;
            return coordinator;
        });
        services.AddSingleton<RecognitionService>();
        services.AddSingleton<IRecognitionService>(provider => provider.GetRequiredService<RecognitionService>());
        services.AddSingleton<RecognitionSelfTest>();
        services.AddSingleton<IRecognitionSelfTest>(provider => provider.GetRequiredService<RecognitionSelfTest>());

        if (settings.MonitorService is not null)
        {
            services.AddSingleton(settings.MonitorService);
        }
        else if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IMonitorService, WindowsMonitorService>();
        }

        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IGameWindowLocator, WindowsGameWindowLocator>();
            services.AddSingleton<IEftPathLocator>(provider => new WindowsEftPathLocator(
                null,
                provider.GetRequiredService<IEftPathOverrideStore>()));
            // [V2 rough package 41] The same locator, offered as the discovery source Setup's
            // self-test re-probes for the folders it reports and why each was chosen.
            services.AddSingleton<IEftInstallDiscoverySource>(provider =>
                (IEftInstallDiscoverySource)provider.GetRequiredService<IEftPathLocator>());
            services.AddSingleton<IEftLogWatcher, WindowsEftLogWatcher>();
            // v2r-fast-positions (package 31): the pacer lets the watcher look four times a
            // second while a raid is running and the group is sharing, and once a second
            // otherwise. Without one it keeps the one-second poll it always had.
            services.AddSingleton<IScreenshotWatchPacer>(provider =>
                new ScreenshotWatchPacer(provider.GetRequiredService<IRuntimeStateStore>()));
            services.AddSingleton<IScreenshotWatcher>(provider => new WindowsScreenshotWatcher(
                commandLine.DeveloperMode,
                pacer: provider.GetRequiredService<IScreenshotWatchPacer>()));
            services.AddSingleton<IRecycleBin, WindowsRecycleBin>();
            // [Issue 316] GDI window capture is retired: scans read the screenshots the game writes.
            // The slot stays because the scan use case and the capture-session source take one;
            // both report an unavailable capture instead of failing.
            services.AddSingleton<IScreenCaptureService, UnavailableScreenCaptureService>();
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

        // [f920 capture] Where the player is working when a capture arrives. The raid observer
        // asks it, and the V2 capture bridge binds the router into it once the shell exists.
        services.AddSingleton<ShellCaptureContextSource>();
        services.AddSingleton<ICaptureContextSource>(provider => provider.GetRequiredService<ShellCaptureContextSource>());
        // #572: one shared timeline, file seen to first paint, logged once per scan and kept for
        // Setup > Diagnostics. RaidObservationService, CaptureRecognitionPipeline and
        // LootScanCaptureHandoff each take it as an optional constructor parameter.
        services.AddSingleton<ICaptureStageTimeline, CaptureStageTimeline>();
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
        // V2 rough — package 3 (stash scan workspace + debrief). Refs #283 #291 #377.
        services.AddSingleton<SqliteV2DataStore>();
        services.AddSingleton<StashScanAssembler>();
        services.AddSingleton<StashSnapshotComparer>();
        services.AddSingleton<SqliteStashSnapshotStore>();
        services.AddSingleton<IStashSnapshotStore>(provider => provider.GetRequiredService<SqliteStashSnapshotStore>());
        services.AddSingleton<SqliteStashReviewCommandStore>();
        services.AddSingleton<IStashReviewCommandSink>(provider => provider.GetRequiredService<SqliteStashReviewCommandStore>());
        services.AddSingleton<StashScanWorkflow>();
        // [V2 rough package 40] The guided full-stash scan: several screenshots, one stash, and
        // the owned counts a finished scan feeds. Refs #283 #273.
        services.AddSingleton<StashLayoutAligner>();
        services.AddSingleton<StashReconstructionProjector>();
        services.AddSingleton<StashOwnedCountsApplier>();
        services.AddSingleton<StashScanCaptureStatus>();
        services.AddSingleton<IGuidedStashScanPendingStore>(provider => new JsonFileGuidedStashScanStore(
            Path.Combine(paths.Config, "stash-scan-in-progress.json"),
            provider.GetService<Microsoft.Extensions.Logging.ILogger<JsonFileGuidedStashScanStore>>()));
        services.AddSingleton<GuidedStashScanService>();
        services.AddSingleton<GuidedStashScanArming>();
        services.AddSingleton<StashScanWorkspaceViewModel>();
        services.AddSingleton<DebriefWorkspaceViewModel>();
        // v2r-team (package 9, wave 2): the Team workspace, over the same GroupSessionService and
        // IGroupSettingsStore the V1 Group/Squad pages used, plus CompanionPairingViewModel
        // (registered further down) for its Devices section.
        services.AddSingleton<TeamWorkspaceViewModel>();

        // V2 rough — package 10 (Plan workspace + Hideout section). Refs #288 #307.
        services.AddSingleton<PlanWorkspaceViewModel>();
        services.AddSingleton<HideoutWorkspaceViewModel>();
        // V2 rough package 25 (#402): the Keep list, a Plan section beside Hideout.
        services.AddSingleton<KeepListWorkspaceViewModel>();

        // v2r-pairing-tablet: paired companion device authority (docs/PAIRED_DEVICE_PROTOCOL.md).
        // The desktop is the sole authority over paired-device state, so the authority and its
        // store are always available (list/revoke keeps working even when pairing cannot). The
        // production IDeviceKeyProofVerifier pins the tablet web app's origin, so approving a new
        // device additionally needs a group relay configured with an HTTPS DNS origin (group.json)
        // and, for the desktop identity key, Windows DPAPI.
        services.AddSingleton<IDeviceSignatureCounterStore>(_ => new JsonFileDeviceSignatureCounterStore(
            Path.Combine(paths.Config, "Devices", "signature-counters.json")));
        services.AddSingleton<IDesktopCompanionAuthorityStore>(_ => new JsonFileDesktopCompanionAuthorityStore(
            Path.Combine(paths.Config, "Devices", "companion-authority.json")));
        services.AddSingleton(provider => DesktopCompanionAuthority.OpenAsync(
                provider.GetRequiredService<IDesktopCompanionAuthorityStore>(),
                CreateInitialCompanionState(),
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult());
        // v2r-relay-owner (package 13): the first live paired payload — a tablet's mark add/edit/
        // remove reaching IRaidMarkStore over the now-composed relay registry and frame hub.
        // Always registered (like DesktopCompanionAuthority above); Configure/SetOwnerCredential
        // below are what actually turn it on, once a relay origin and a successful claim exist.
        services.AddSingleton(provider => new RelayMarksBridge(
            provider.GetRequiredService<DesktopCompanionAuthority>(),
            provider.GetRequiredService<IRaidMarkStore>(),
            timeProvider,
            // [#289] The owner session and paired sessions' keys, kept where the TarkovTracker
            // token is, so a restart does not ask for the relay's admin key or a re-pair.
            new RelayLinkVault(provider.GetRequiredService<IIntegrationSecretStore>())));
        // The default every platform/configuration resolves unless the block below overrides it,
        // so V2ShellViewModel has one dependency to take regardless of whether pairing is possible.
        services.AddSingleton(CompanionPairingAvailability.Unavailable);

        var companionOrigin = ReadCompanionRelayOrigin(Path.Combine(paths.Config, "group.json"));
        if (OperatingSystem.IsWindows() && companionOrigin is { } origin)
        {
            RegisterWindowsCompanionPairing(services, paths, origin);
        }

        services.AddSingleton<CompanionPairingViewModel>();

        services.AddSingleton<MainWindowViewModel>();
        // V2 Raid cockpit (package 2): built from the V1 map/raid page a MainWindowViewModel
        // singleton already owns, not from its own copies of them.
        services.AddSingleton(provider => new RaidCockpitViewModel(
            provider.GetRequiredService<MainWindowViewModel>().Map,
            provider.GetRequiredService<MainWindowViewModel>().Raid,
            provider.GetRequiredService<IRuntimeStateStore>(),
            provider.GetRequiredService<MapSceneAssembler>(),
            provider.GetRequiredService<IHighValueLootRuntimeSource>(),
            provider.GetRequiredService<HistoricalTrafficSource>(),
            provider.GetRequiredService<IRaidMarkStore>(),
            provider.GetRequiredService<TarkovDevMapAssetCache>(),
            timeProvider,
            // [V2 rough package 20] so the Raid marks list can remove a group waypoint or ping
            // too, through the same relay call the Team workspace uses.
            provider.GetRequiredService<GroupSessionService>(),
            // [Package 35] The wiki link on a selected quest objective.
            provider.GetRequiredService<IWikiLinkOpener>(),
            // [Issue 379] The player's own objective markers. Named, so another optional parameter
            // added before it cannot quietly take this one's place.
            userMarkers: provider.GetRequiredService<IUserQuestMarkStore>(),
            // [Issue 571] "Done" by hand, and whose profile it belongs to.
            handDone: provider.GetRequiredService<IHandDoneObjectiveStore>(),
            profiles: provider.GetRequiredService<IPlayerProfileService>()));
        services.AddSingleton<V2ShellViewModel>();

        // [V2 rough package 1] #269/#271/#274/#282: register the merged-but-orphaned V2
        // foundations and the small adapters that connect them to the shell and to each other.
        // RaidObservationService already forwards every settled screenshot to ICaptureSessionService
        // when one is registered (additively, alongside the existing V1 scan path); registering it
        // here is what turns that on.
        services.AddSingleton<ProfileRuntimeContextService>();
        services.AddSingleton<IProfileRuntimeContextService>(provider =>
            provider.GetRequiredService<ProfileRuntimeContextService>());
        services.AddSingleton<IBackgroundWorkSupervisor>(_ => new BackgroundWorkSupervisor(timeProvider));
        services.AddSingleton<ICaptureWorkScheduler>(provider =>
            new SupervisedCaptureWorkScheduler(provider.GetRequiredService<IBackgroundWorkSupervisor>()));
        services.AddSingleton(_ => new WorkspaceOrigin(
            new WorkspaceId(Guid.NewGuid()),
            new CompanionDeviceId(Guid.NewGuid()),
            WorkspaceOriginKind.DesktopApplication,
            "desktop"));
        // [V2 rough package 14] #273: pixel-to-grid recognition. The icon evidence cache (#355)
        // is machine-local, keyed by canonical item id and source URI - nothing here fetches or
        // populates it yet, so it starts empty until something feeds it (deferred to polish).
        // [V2 rough package 37] Something feeds it now: IconEvidenceIndexer below. The catalog
        // names 5,320 grid images and the cache's default ceiling is 4,096 entries.
        services.AddSingleton<IIconEvidenceCache>(_ => new FileIconEvidenceCache(
            new FileIconEvidenceCacheOptions(Path.Combine(paths.Cache, "IconEvidence")) { MaximumEntries = 8192 }));
        services.AddSingleton<IconCandidateSeparator>();
        // [V2 rough package 37] The reference icons are read once and kept, and each named item
        // is handed to the decision engine with the catalog facts the companion holds for it.
        services.AddSingleton<IconReferenceIndex>();
        services.AddSingleton<IIconCatalog, SqliteIconCatalog>();
        services.AddSingleton<IIconContentFetcher>(provider => new HttpIconContentFetcher(provider.GetRequiredService<HttpClient>()));
        services.AddSingleton<IconEvidenceIndexer>();
        services.AddSingleton<IInvalidatableProjection>(provider => provider.GetRequiredService<IconEvidenceIndexer>());
        // [fin-recognition] #282: what a Loot Scan decides from. The profile's pins and item
        // rules, outstanding quest and hideout needs, the published flea rates, how readily an
        // item is had, the raid phase and risk, and a scanned stash where one exists.
        services.AddSingleton<IItemMarketFactSource, SqliteItemMarketFactSource>();
        services.AddSingleton(provider => new LootScanNeedSource(
            provider.GetRequiredService<IPlayerProfileService>(),
            provider.GetRequiredService<ProfileNeedAggregationService>(),
            provider.GetRequiredService<IQuestReadService>(),
            provider.GetRequiredService<IRequirementCatalog>()));
        services.AddSingleton<LootScanRaidPreference>();
        services.AddSingleton(provider => new LootScanRaidContextSource(
            provider.GetRequiredService<IRaidStateService>(),
            provider.GetRequiredService<IMapDataService>(),
            provider.GetRequiredService<LootScanRaidPreference>()));
        services.AddSingleton<ObservedInventoryRecognitionProjector>();
        services.AddSingleton<IObservedInventoryEvidenceReader, SqliteObservedInventoryEvidenceReader>();
        services.AddSingleton(provider => new LootScanRecommendationSource(
            provider.GetRequiredService<IItemRepository>(),
            provider.GetRequiredService<IItemMarketFactSource>(),
            provider.GetRequiredService<LootScanNeedSource>(),
            provider.GetRequiredService<LootScanRaidContextSource>(),
            // [f920 capture] The Events page's Safe / Allergic results reach the scan.
            new LootScanEventStateSource(
                provider.GetRequiredService<IPlayerProfileService>(),
                provider.GetRequiredService<IEventCatalog>())));
        services.AddSingleton<GridPixelReconstructionBuilder>();
        services.AddSingleton<CaptureRecognitionPipeline>();
        services.AddSingleton<ICaptureSessionPipeline>(provider =>
            provider.GetRequiredService<CaptureRecognitionPipeline>());
        services.AddSingleton<InventoryGridReconstructor>();
        services.AddSingleton<LootScanDecisionService>();
        services.AddSingleton(provider => new LootScanCaptureHandoff(
            provider.GetRequiredService<IProfileRuntimeContextService>(),
            provider.GetRequiredService<InventoryGridReconstructor>(),
            provider.GetRequiredService<LootScanDecisionService>(),
            timeProvider,
            provider.GetService<Microsoft.Extensions.Logging.ILogger<LootScanCaptureHandoff>>(),
            provider.GetRequiredService<LootScanRecommendationSource>(),
            provider.GetRequiredService<IObservedInventoryEvidenceReader>()));
        services.AddSingleton<StashScanCaptureHandoff>();
        // [V2 rough package 60 — Intel scan] #287: the handoff for a capture whose answer is one
        // item. Every intent but Loot and Stash used to be acknowledged and dropped.
        services.AddSingleton<IntelCaptureHandoff>();
        // [f920 capture] #284: the flea rows the player photographed, priced against the catalog.
        services.AddSingleton(provider => new FleaCaptureHandoff(
            provider.GetRequiredService<IItemRepository>(),
            provider.GetRequiredService<IItemMarketFactSource>(),
            provider.GetService<Microsoft.Extensions.Logging.ILogger<FleaCaptureHandoff>>()));
        services.AddSingleton<CompositeCaptureResultHandoff>();
        services.AddSingleton<ICaptureResultHandoff>(provider =>
            provider.GetRequiredService<CompositeCaptureResultHandoff>());
        services.AddSingleton<ICaptureSessionService>(provider => new CaptureSessionCoordinator(
            provider.GetRequiredService<ICaptureWorkScheduler>(),
            provider.GetRequiredService<ICaptureSessionPipeline>(),
            provider.GetRequiredService<ICaptureResultHandoff>(),
            provider.GetRequiredService<WorkspaceOrigin>(),
            timeProvider));
        // [fin-recognition] #283: the caller StashOrganizationPlanner never had. The stash
        // workspace takes it as an optional dependency and sorts each scan with it.
        services.AddSingleton<StashPlanSource>();
        // [fin-recognition] #282: pin, wishlist, item rule, raid phase and risk, set from the
        // Loot Scan workspace and read back by the scan.
        services.AddSingleton<LootScanWorkspaceControls>();
        services.AddSingleton<ILootScanWorkspaceControls>(provider => provider.GetRequiredService<LootScanWorkspaceControls>());
        // [f920 capture] A picture the player pasted, dropped or picked, on the watcher's intake.
        services.AddSingleton<ManualImageIntake>();
        services.AddSingleton<V2ShellCaptureBridge>();
        // [V2 rough package 24] The desktop's raid map, carried to its paired tablets, and a
        // paired device in Control mode moving it back. Refs #407.
        services.AddSingleton(provider => new TabletMapSurfacePublisher(
            provider.GetRequiredService<RaidCockpitViewModel>(),
            provider.GetRequiredService<RelayMarksBridge>(),
            provider.GetRequiredService<DesktopCompanionAuthority>(),
            provider.GetRequiredService<RelayMarksBridge>(),
            provider.GetRequiredService<IItemSearchService>(),
            provider.GetRequiredService<IItemRepository>(),
            timeProvider,
            // #407: the same Allergic chip Intel draws (#287), now on a tablet's search results.
            provider.GetRequiredService<IIntelEventStateCatalog>()));
        // [V2 rough package 41] Setup's self-test. Refs #292, #281. Every reading is read-only:
        // discovery re-probes folders, the log and screenshot readers open files for reading,
        // game data and the database are SELECTs, and the relay is asked only for its health.
        services.AddSingleton<SqliteSelfTestReader>();
        services.AddSingleton<SelfTestJournal>();
        services.AddSingleton<SelfTestFolderReader>();
        services.AddSingleton(provider => new SelfTestLogReader(provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new SelfTestScreenshotWatch(
            provider.GetRequiredService<IScreenshotFilenameParser>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ISelfTestReadings>(provider => new AppSelfTestReadings(
            provider.GetRequiredService<SqliteSelfTestReader>(),
            provider.GetRequiredService<IGroupSettingsStore>(),
            provider.GetRequiredService<IRuntimeStateStore>(),
            provider.GetRequiredService<HttpClient>(),
            provider.GetRequiredService<SelfTestFolderReader>(),
            provider.GetRequiredService<SelfTestLogReader>(),
            provider.GetRequiredService<SelfTestScreenshotWatch>(),
            TarkovDevDataRefreshOperation.ModeSlug(runtimeOptions.GameMode),
            runtimeOptions.Language.ToLowerInvariant(),
            provider.GetService<IEftInstallDiscoverySource>(),
            provider.GetService<IEftPathOverrideStore>(),
            provider.GetService<DesktopCompanionAuthority>(),
            provider.GetService<TabletMapSurfacePublisher>(),
            provider.GetRequiredService<CompanionPairingAvailability>().RelayOrigin ?? companionOrigin,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<IProfileRuntimeContextService>()));
        services.AddSingleton(provider => new SetupSelfTestViewModel(
            provider.GetRequiredService<ISelfTestReadings>,
            provider.GetRequiredService<SelfTestJournal>(),
            provider.GetRequiredService<TimeProvider>(),
            action => Avalonia.Threading.Dispatcher.UIThread.Post(action)));
        // [V2 rough package 43 (#314)] Tray presence and the five notifications. The tray host is
        // registered here and attached once Avalonia is up; the bridge observes the runtime store
        // for the life of the process and is resolved by the shell that shows Setup.
        services.AddSingleton<TrayPresenceHost>();
        services.AddSingleton<PopupNotificationHost>();
        services.AddSingleton<INotificationSettingsStore>(_ =>
            new JsonFileNotificationSettingsStore(Path.Combine(paths.Config, "notifications.json")));
        services.AddSingleton(provider => new NotificationBridge(
            provider.GetRequiredService<IRuntimeStateStore>(),
            provider.GetRequiredService<INotificationSettingsStore>(),
            provider.GetRequiredService<IGroupSettingsStore>(),
            [provider.GetRequiredService<TrayPresenceHost>()],
            provider.GetRequiredService<PopupNotificationHost>,
            () => provider.GetRequiredService<MainWindowViewModel>().Settings,
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new SetupNotificationsViewModel(
            provider.GetRequiredService<NotificationBridge>(),
            () => provider.GetRequiredService<TrayPresenceHost>().IsAvailable));
        services.AddSingleton<LegacyProfileContextBootstrap>();
        // [#309] Setup > Privacy: preview before turning tidying on, a dry run, and the last-run ledger.
        services.AddSingleton(provider => new SetupCleanupViewModel(
            provider.GetRequiredService<ScreenshotRetentionService>(),
            provider.GetRequiredService<IScreenshotRetentionStore>(),
            provider.GetService<IScreenshotTidyLedger>(),
            () => provider.GetRequiredService<IRuntimeStateStore>().Current.Observation.ScreenshotRoot,
            () => provider.GetRequiredService<MainWindowViewModel>().Settings.ToggleScreenshotTidyingCommand,
            () => provider.GetRequiredService<MainWindowViewModel>().Settings.CanTidyScreenshots,
            provider.GetRequiredService<TimeProvider>()));
        // [#269] What Setup › Game & Profile drives: create, switch, archive, restore. A first profile
        // waits for the V1 one to be seeded, so V1 progress always has a profile to belong to.
        services.AddSingleton(provider => new ProfileManagementService(
            provider.GetRequiredService<ProfileContextService>(),
            provider.GetRequiredService<IProfileRuntimeContextService>(),
            timeProvider,
            provider.GetRequiredService<LegacyProfileContextBootstrap>().EnsureSeededAsync));
        services.AddSingleton(provider => new SetupProfilesViewModel(
            provider.GetRequiredService<ProfileManagementService>(),
            action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            ProfileTransferComposition.Create(provider, paths)));
        // [#292] Setup's data detail, About, Data & Privacy and Displays.
        services.AddSingleton(provider => new SetupDataDetailViewModel(
            provider.GetRequiredService<IRuntimeStateStore>(),
            runtimeOptions,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<IProfileRuntimeContextService>(),
            action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            () => provider.GetRequiredService<ApplicationStartupCoordinator>().RefreshAsync(force: true, CancellationToken.None)));
        services.AddSingleton(provider => new SetupDisplaysViewModel(
            provider.GetService<IMonitorService>(),
            provider.GetService<IGameWindowLocator>(),
            provider.GetService<IDesktopWindowPlacementController>()));
        services.AddSingleton(provider => new SetupAdminViewModel(
            provider.GetRequiredService<SetupDataDetailViewModel>(),
            new SetupInfoPageViewModel("About", SetupPageContent.About, SetupPageFacts.ForAbout),
            new SetupInfoPageViewModel(
                "Data & Privacy",
                SetupPageContent.DataPrivacy,
                anchor => SetupPageFacts.ForDataPrivacy(
                    anchor,
                    provider.GetRequiredService<IRuntimeStateStore>().Current.IsOffline,
                    provider.GetRequiredService<SetupDataDetailViewModel>().Facts.FirstOrDefault()?.Value,
                    provider.GetRequiredService<MainWindowViewModel>().Group)),
            provider.GetRequiredService<SetupDisplaysViewModel>(),
            // [#292/#309] The report a player reads before it is sent, and the send of exactly that text.
            new SetupReportViewModel(
                () => provider.GetRequiredService<MainWindowViewModel>().Settings.BuildReport(),
                (report, token) => provider.GetRequiredService<MainWindowViewModel>().Settings.SendReviewedReportAsync(report, token)),
            new SetupLootScanViewModel(
                provider.GetService<IWorkspaceLayoutStore>(),
                provider.GetService<ICaptureStageTimeline>(),
                action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
                provider.GetRequiredService<TimeProvider>())));
        // [#292 task 2] "Reset this section", "Reset everything", export and import. The same
        // three stores the sections themselves already read/write, never a fourth of its own.
        services.AddSingleton(provider => new SetupSettingsAdminViewModel(
            provider.GetRequiredService<WorkspacePreferenceService>(),
            provider.GetRequiredService<IScreenshotRetentionStore>(),
            provider.GetService<NotificationBridge>()));
        // [#292 task 3] The database's migration state and verified backup, read from the same
        // SqliteMigrationRunner that already makes and verifies one before a destructive migration.
        services.AddSingleton(provider => new SetupDatabaseStatusViewModel(
            provider.GetRequiredService<SqliteMigrationRunner>()));

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

    /// <summary>
    /// Registers the Windows-only half of paired-device pairing: the DPAPI-protected desktop
    /// identity key and everything that needs it.
    /// </summary>
    /// <remarks>
    /// A dedicated, attributed method rather than an inline guarded block: <see
    /// cref="WindowsDpapiDesktopIdentitySigner"/> is <c>[SupportedOSPlatform("windows")]</c> at the
    /// class level, and the platform-compatibility analyzer does not trace a runtime
    /// <c>OperatingSystem.IsWindows()</c> guard through a reference to the type itself as a generic
    /// argument (<c>GetRequiredService&lt;WindowsDpapiDesktopIdentitySigner&gt;()</c>) the way it
    /// does an ordinary guarded call. Attributing the whole method covers every reference inside it.
    /// </remarks>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RegisterWindowsCompanionPairing(IServiceCollection services, AppDataPaths paths, Uri origin)
    {
        services.AddSingleton(provider => WindowsDpapiDesktopIdentitySigner.OpenOrCreateAsync(
                Path.Combine(paths.Config, "Devices", "identity-key.dat"),
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult());
        services.AddSingleton<IDesktopIdentitySigner>(provider =>
            provider.GetRequiredService<WindowsDpapiDesktopIdentitySigner>());
        services.AddSingleton<IDeviceKeyProofVerifier>(provider => new WebAuthnDeviceKeyProofVerifier(
            origin.IdnHost,
            origin.GetLeftPart(UriPartial.Authority),
            provider.GetRequiredService<IDeviceSignatureCounterStore>()));
        services.AddSingleton(provider => new DesktopPairingCoordinator(
            provider.GetRequiredService<DesktopCompanionAuthority>(),
            provider.GetRequiredService<IDesktopIdentitySigner>(),
            provider.GetRequiredService<IDeviceKeyProofVerifier>()));
        services.AddSingleton(provider => new CompanionPairingAvailability(
            provider.GetRequiredService<DesktopPairingCoordinator>(),
            origin,
            provider.GetRequiredService<IDesktopIdentitySigner>(),
            // [#553] The group key registers this desktop on the relay; read when it is needed.
            async token => (await provider.GetRequiredService<IGroupSettingsStore>().GetAsync(token).ConfigureAwait(false)).Key));
    }

    /// <summary>
    /// The first-run paired-companion state: no workspace yet, no devices, nothing pending.
    /// </summary>
    /// <remarks>
    /// Used only when the authority store is empty; every later launch loads the persisted state
    /// instead, so the fresh identifiers minted here never change once a device has paired.
    /// </remarks>
    private static CanonicalCompanionState CreateInitialCompanionState() => new(
        new AuthorityEpoch(Guid.NewGuid()),
        new WorkspaceId(Guid.NewGuid()),
        Environment.MachineName,
        new GlobalRevision(0),
        new CompanionDeviceId(Guid.NewGuid()),
        new DeviceModeAggregate(AggregateCursor.Empty, [], null, null),
        new WorkspaceAggregate(
            AggregateCursor.Empty,
            new WorkspaceProjection(WorkspaceKind.Raid, null, null, null, null, [], [], null, [], [], [], null)),
        new MarkAggregate(AggregateCursor.Empty, []),
        new CaptureIntentAggregate(AggregateCursor.Empty, null),
        ProfilePreferencesAggregate.Empty);

    /// <summary>
    /// The group relay's origin, when it is one a paired tablet's device-key proof can be pinned
    /// to, or null.
    /// </summary>
    /// <remarks>
    /// Read from the file rather than through <c>IGroupSettingsStore</c> for the same reason
    /// <see cref="ReadCatalogMirror"/> is: composition cannot await. <see cref="WebAuthnDeviceKeyProofVerifier"/>
    /// requires an exact HTTPS DNS origin with no path, so an http, IP-address, or unset relay
    /// leaves paired-device pairing unavailable rather than failing composition; the desktop's
    /// existing paired devices keep working either way.
    /// </remarks>
    private static Uri? ReadCompanionRelayOrigin(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return null;
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
            // The shape check itself lives in CompanionRelayOrigin, shared with whatever
            // reconfigures this live after a save (RelayReconfiguringGroupSettingsStore), so
            // startup and a later change can never accept a different notion of "usable".
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                document.RootElement.TryGetProperty("serverUri", out var server) &&
                server.ValueKind == System.Text.Json.JsonValueKind.String
                    ? CompanionRelayOrigin.TryParse(server.GetString())
                    : null;
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

    /// <summary>
    /// How to re-launch this application as a map rasteriser, when it can be re-launched at all.
    /// </summary>
    /// <remarks>
    /// Only when the running process really is this application's own host executable. Under
    /// `dotnet run`, a test host or a tool, <see cref="Environment.ProcessPath"/> is the muxer or
    /// the tool: passing it the rasteriser's options would start something that has never heard of
    /// them. Returning null there is not a degradation — it is the behaviour every build had
    /// before the child process existed.
    ///
    /// Ninety seconds is generous by two orders of magnitude for a drawing that takes about two,
    /// and it is a deadline rather than a budget: its job is to end a child that has hung, not to
    /// hurry one that is working.
    /// </remarks>
    private static SvgRasterizerHost? ResolveRasterizerHost()
    {
        if (Environment.ProcessPath is not { Length: > 0 } path)
        {
            return null;
        }

        return string.Equals(
            Path.GetFileNameWithoutExtension(path),
            "TarkovCompanion",
            StringComparison.OrdinalIgnoreCase)
            ? new(path, [], TimeSpan.FromSeconds(90))
            : null;
    }
}
