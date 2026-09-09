using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Application.Services.Raids;
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
using TarkovCompanion.Infrastructure.Profile;
using TarkovCompanion.Infrastructure.TarkovDevJson;
using TarkovCompanion.Platform.Windows.Capture;
using TarkovCompanion.Platform.Windows.Discovery;
using TarkovCompanion.Platform.Windows.Displays;
using TarkovCompanion.Platform.Windows.Hotkeys;
using TarkovCompanion.Platform.Windows.Security;
using TarkovCompanion.Platform.Windows.Watching;

namespace TarkovCompanion.App.Services;

public sealed record AppCompositionSettings(
    string? DataRoot = null,
    bool? Offline = null,
    TimeProvider? TimeProvider = null,
    HttpMessageHandler? HttpMessageHandler = null,
    IScanAdapter? ScanAdapter = null);

public static class AppComposition
{
    public const string OfflineEnvironmentVariable = "TARKOV_COMPANION_OFFLINE";

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
            TimeSpan.FromSeconds(45));
        var databaseOptions = new SqliteDatabaseOptions(Path.Combine(paths.Database, "tarkov-companion.db"));
        var profileOptions = new JsonProfileOptions(Path.Combine(paths.Config, "profile.json"));

        var services = new ServiceCollection();
        services.AddSingleton(commandLine);
        services.AddSingleton(paths);
        services.AddSingleton(runtimeOptions);
        services.AddSingleton(databaseOptions);
        services.AddSingleton(profileOptions);
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

        services.AddSingleton<DataTranslationService>();
        services.AddSingleton(provider => new HttpClient(settings.HttpMessageHandler ?? new HttpClientHandler()));
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

        services.AddSingleton<IPlayerProfileService, JsonFilePlayerProfileService>();
        services.AddSingleton<ProfileNeedAggregationService>();
        services.AddSingleton<IQuestProgressService, ProfileQuestProgressService>();
        services.AddSingleton<IHideoutProgressService, ProfileHideoutProgressService>();
        services.AddSingleton<RecommendationContextService>();
        services.AddSingleton<IEventTrackerService, ProfileEventTrackerService>();
        services.AddSingleton<IAmmoIntelligenceService, AmmoIntelligenceService>();
        services.AddSingleton<IKeyIntelligenceService, KeyIntelligenceService>();
        services.AddSingleton<ILoadoutService, LoadoutIntelligenceService>();
        services.AddSingleton<IRecommendationEngine, RecommendationEngine>();

        services.AddSingleton<IMapDefinitionCache, InMemoryMapDefinitionCache>();
        services.AddSingleton<IMapDataService, MapDataService>();
        services.AddSingleton<IMapTransformService, MapTransformService>();
        services.AddSingleton<IStrategyModel, StrategyModel>();
        services.AddSingleton<IRoutePlanner, RoutePlanner>();
        services.AddSingleton<IScreenshotFilenameParser, ScreenshotFilenameParser>();
        services.AddSingleton<EftLogParser>();
        services.AddSingleton<IRaidStateService>(_ => new RaidStateService(commandLine.DeveloperMode || commandLine.Demo));

        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IGameWindowLocator, WindowsGameWindowLocator>();
            services.AddSingleton<IMonitorService, WindowsMonitorService>();
            services.AddSingleton<IEftPathLocator, WindowsEftPathLocator>();
            services.AddSingleton<IEftLogWatcher, WindowsEftLogWatcher>();
            services.AddSingleton<IScreenshotWatcher>(_ => new WindowsScreenshotWatcher(commandLine.DeveloperMode));
            services.AddSingleton<IGlobalHotkeyService, WindowsGlobalHotkeyService>();
            services.AddSingleton<IScreenCaptureService, GdiScreenCaptureService>();
            services.AddSingleton<ISecretStore>(_ => new WindowsDpapiSecretStore(Path.Combine(paths.Config, "Secrets")));
        }

        services.AddSingleton<IRuntimeStateStore, RuntimeStateStore>();
        services.AddSingleton<RaidActivityCoordinator>();
        services.AddSingleton<ApplicationStartupCoordinator>();
        services.AddSingleton<IScanAdapter>(_ => settings.ScanAdapter
            ?? (commandLine.Demo
                ? new FixtureScanAdapter(
                    new("demo-graphics-card"),
                    _.GetRequiredService<IItemRepository>(),
                    _.GetRequiredService<IRecommendationEngine>(),
                    timeProvider)
                : new UnavailableScanAdapter(timeProvider)));
        services.AddSingleton<IScanUseCase, ScanUseCase>();
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
}
