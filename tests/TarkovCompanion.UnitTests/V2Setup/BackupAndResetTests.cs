using System.Reflection;
using TarkovCompanion.App.Services.Settings;
using TarkovCompanion.App.Services.V2.Notifications;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Network;
using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Application.Services.Personalization;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Recommendations;
using TarkovCompanion.Application.Services.ReleaseExperience;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Setup;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Domain.Personalization;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Core.Features;
using TarkovCompanion.Core.Network;
using TarkovCompanion.Infrastructure.Maps;
using TarkovCompanion.Infrastructure.Settings;
using TarkovCompanion.Infrastructure.Workspaces;
using TarkovCompanion.UnitTests.V2Shell;

namespace TarkovCompanion.UnitTests.V2Setup;

/// <summary>
/// [#902] Setup's Backup &amp; reset against the real file stores and services behind every
/// <see cref="SettingsRegistry"/> group, and the pages that show them.
/// </summary>
public sealed class BackupAndResetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tc-backup-reset-{Guid.NewGuid():N}");

    public BackupAndResetTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Reset_everything_puts_every_registered_setting_back_and_every_page_follows_without_a_restart()
    {
        using var app = await App.StartAsync(_root);
        var notifications = new SetupNotificationsViewModel(app.Notifications);
        var appearance = new V2AppearanceSettingsViewModel(app.Preferences);
        var network = new SetupNetworkControlsViewModel(app.Network);
        var flags = new SetupFeatureFlagsViewModel(app.Flags);
        var horizons = new RecommendationHorizonSettingsViewModel(app.Horizons);
        await app.ChangeEverythingAsync();
        // Built after the changes, as a page that was open when the player pressed Reset everything.
        var learn = new LearnModeSetting(app.Layout);
        var lootScan = new SetupLootScanViewModel(app.Layout, timeline: null);
        var changed = await app.Admin.CaptureCurrentAsync();
        // The fixture really does move every registered group away from its default.
        foreach (var domain in Enum.GetValues<SettingsDomain>())
        {
            Assert.NotEmpty(SetupSettingsDiff.Compare(changed, SetupSettingsAdminViewModel.WithDefault(changed, domain)));
        }

        Assert.True(learn.IsEnabled);
        Assert.True(network.IsLocalOnly);
        Assert.True(lootScan.TabletOnly);

        // Team and Setup's squad row re-read group.json on this event (#903).
        var groupChanges = 0;
        app.Group.Changed += (_, _) => groupChanges++;

        await ((AsyncDelegateCommand)app.Admin.ResetAllCommand).ExecuteAsync();
        Assert.True(app.Admin.HasPendingChange);
        await ((AsyncDelegateCommand)app.Admin.ConfirmCommand).ExecuteAsync();
        Assert.Equal(1, groupChanges);

        Assert.Empty(SetupSettingsDiff.Compare(await app.Admin.CaptureCurrentAsync(), SetupSettingsSnapshot.Default));

        // Every page shows the default now, in this run.
        Assert.Equal(NotificationSettings.Default.SquadMark, notifications.Rows.Single(row => row.Kind == NotificationKind.SquadMark).IsEnabled);
        Assert.False(notifications.QuietHours);
        Assert.True(appearance.Themes.Single(choice => choice.Id == "theme-dark").IsCurrent);
        Assert.False(network.IsLocalOnly);
        Assert.All(flags.Rows, row => Assert.False(row.IsOverridden));
        Assert.True(horizons.QuestChoices.Single(choice => choice.Horizon == RecommendationHorizon.NextFive).IsCurrent);
        Assert.False(learn.IsEnabled);
        Assert.False(lootScan.TabletOnly);
        Assert.Equal(TimeSpan.FromSeconds(TarkovCompanion.Application.Services.CaptureSessions.LootAutoReturnPolicy.DefaultSeconds), lootScan.Timeout);
        Assert.Equal(1, app.Scale);
        Assert.Equal(ScreenshotRetentionSettings.Default, app.RetentionSeenByWindow);

        // A reset keeps the group's address, name and key; only the three switches move.
        var group = await app.Group.GetAsync(CancellationToken.None);
        Assert.Equal("https://relay.example", group.ServerUri);
        Assert.Equal("a-group-key-long-enough", group.Key);

        // And the files say the same after a restart.
        using var restarted = await App.StartAsync(_root);
        Assert.Empty(SetupSettingsDiff.Compare(await restarted.Admin.CaptureCurrentAsync(), SetupSettingsSnapshot.Default));

        // Nothing left to reset.
        await ((AsyncDelegateCommand)app.Admin.ResetAllCommand).ExecuteAsync();
        Assert.False(app.Admin.HasPendingChange);
    }

    [Fact]
    public async Task Export_then_import_round_trips_every_registered_setting()
    {
        using var app = await App.StartAsync(_root);
        await app.ChangeEverythingAsync();
        var changed = await app.Admin.CaptureCurrentAsync();
        var file = Path.Combine(_root, "exported.json");
        app.Admin.ExchangePath = file;

        await ((AsyncDelegateCommand)app.Admin.ExportCommand).ExecuteAsync();
        var text = await File.ReadAllTextAsync(file);
        Assert.DoesNotContain("a-group-key-long-enough", text, StringComparison.Ordinal);
        Assert.DoesNotContain("relay.example", text, StringComparison.Ordinal);

        await ((AsyncDelegateCommand)app.Admin.ResetAllCommand).ExecuteAsync();
        await ((AsyncDelegateCommand)app.Admin.ConfirmCommand).ExecuteAsync();
        Assert.NotEmpty(SetupSettingsDiff.Compare(await app.Admin.CaptureCurrentAsync(), changed));

        await ((AsyncDelegateCommand)app.Admin.PreviewImportCommand).ExecuteAsync();
        Assert.True(app.Admin.HasPendingChange);
        await ((AsyncDelegateCommand)app.Admin.ConfirmCommand).ExecuteAsync();

        Assert.Empty(SetupSettingsDiff.Compare(await app.Admin.CaptureCurrentAsync(), changed));
    }

    [Fact]
    public async Task A_version_1_file_leaves_what_it_does_not_name_alone()
    {
        using var app = await App.StartAsync(_root);
        await app.ChangeEverythingAsync();
        var file = Path.Combine(_root, "v1.json");
        await File.WriteAllTextAsync(file, """{ "schemaVersion": 1, "theme": "HighContrast" }""");
        app.Admin.ExchangePath = file;

        await ((AsyncDelegateCommand)app.Admin.PreviewImportCommand).ExecuteAsync();
        await ((AsyncDelegateCommand)app.Admin.ConfirmCommand).ExecuteAsync();

        Assert.Equal(AppearanceTheme.HighContrast, app.Preferences.Current.Theme);
        Assert.True(app.Network.Controls.LocalOnly);
        Assert.Equal("on", app.Layout.Get(WorkspaceLayoutKeys.PlanLearnMode));
    }

    [Fact]
    public async Task Reset_this_section_resets_only_what_lives_there()
    {
        using var app = await App.StartAsync(_root);
        await app.ChangeEverythingAsync();
        app.Admin.SetCurrentSection(V2SetupSection.Diagnostics);

        await ((AsyncDelegateCommand)app.Admin.ResetSectionCommand).ExecuteAsync();
        await ((AsyncDelegateCommand)app.Admin.ConfirmCommand).ExecuteAsync();

        // Diagnostics holds the flags and the Loot Scan timing.
        Assert.All(app.Flags.States, state => Assert.Equal(FeatureFlagSource.RingDefault, state.Source));
        Assert.Null(app.Layout.Get(WorkspaceLayoutKeys.LootOnTabletOnly));
        Assert.Null(app.Layout.Get(WorkspaceLayoutKeys.LootAutoReturnSeconds));
        // Not the rest.
        Assert.Equal("on", app.Layout.Get(WorkspaceLayoutKeys.PlanLearnMode));
        Assert.True(app.Network.Controls.LocalOnly);
    }

    [Fact]
    public void Backup_lives_under_About_and_Reset_this_section_wherever_a_setting_lives()
    {
        var preferences = new WorkspacePreferenceService(new JsonFileWorkspacePreferenceStore(Path.Combine(_root, "p.json")));
        var admin = new SetupSettingsAdminViewModel(preferences, new JsonFileScreenshotRetentionStore(Path.Combine(_root, "s.json")));

        admin.SetCurrentSection(V2SetupSection.About);
        Assert.True(admin.ShowsBackup);
        Assert.True(admin.IsVisible);

        foreach (var section in new[] { V2SetupSection.Accessibility, V2SetupSection.Notifications, V2SetupSection.Privacy, V2SetupSection.DataPrivacy, V2SetupSection.Diagnostics, V2SetupSection.Progress, V2SetupSection.TeamDevices })
        {
            admin.SetCurrentSection(section);
            Assert.True(admin.CanResetSection, section.ToString());
            Assert.False(admin.ShowsBackup);
        }

        admin.SetCurrentSection(V2SetupSection.Overview);
        Assert.False(admin.IsVisible);
    }

    /// <summary>
    /// The ratchet: a new file-backed store, or a new workspace-layout key, fails here until it is
    /// either registered with Backup &amp; reset or listed as not being a setting, with the reason.
    /// </summary>
    [Fact]
    public void Every_persisted_store_is_registered_or_named_as_not_a_setting()
    {
        var registered = SettingsRegistry.Domains.Select(entry => entry.Store).ToHashSet();
        var stores = typeof(JsonFileNetworkControlsStore).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.Name.StartsWith("JsonFile", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(stores);

        var missing = new List<string>();
        foreach (var store in stores)
        {
            var interfaces = store.GetInterfaces()
                .Where(contract => contract.Namespace?.StartsWith("TarkovCompanion", StringComparison.Ordinal) == true)
                .ToArray();
            if (interfaces.Length == 0 || interfaces.Any(contract => !registered.Contains(contract) && !SettingsRegistry.NotSettings.ContainsKey(contract)))
            {
                missing.Add(store.Name);
            }
        }

        Assert.True(missing.Count == 0, "Register these in SettingsRegistry.Domains or NotSettings: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_workspace_layout_key_has_a_registered_name()
    {
        var keys = typeof(WorkspaceLayoutKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Concat(typeof(WorkspaceLayoutKeys).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.ReturnType == typeof(string) && method.GetParameters() is [{ ParameterType: var type }] && type == typeof(string))
                .Select(method => (string)method.Invoke(null, ["x"])!))
            .ToArray();
        Assert.NotEmpty(keys);

        Assert.All(keys, key => Assert.True(SettingsRegistry.FindLayoutKey(key) is not null, $"'{key}' has no line in SettingsRegistry.LayoutKeys"));
    }

    [Fact]
    public void Every_snapshot_member_belongs_to_a_registered_group()
    {
        var changed = App.EverythingChanged();
        var domains = SettingsRegistry.Domains.Select(entry => entry.Domain).ToArray();
        Assert.Equal(Enum.GetValues<SettingsDomain>().Order(), domains.Order());

        foreach (var property in typeof(SetupSettingsSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.Name != "EqualityContract"))
        {
            var original = property.GetValue(changed);
            Assert.True(
                domains.Any(domain => !Equals(property.GetValue(SetupSettingsAdminViewModel.WithDefault(changed, domain)), original)),
                $"SetupSettingsSnapshot.{property.Name} is reset by no SettingsDomain");
        }
    }

    /// <summary>The application's settings, composed from real file stores in one folder, as a start of the app would.</summary>
    private sealed class App : IDisposable
    {
        private readonly NotificationBridge _bridge;
        private double _scale;

        private App(string root, NotificationBridge bridge, WorkspacePreferenceService preferences)
        {
            _bridge = bridge;
            Preferences = preferences;
            Retention = new ObservableScreenshotRetentionStore(new JsonFileScreenshotRetentionStore(Path.Combine(root, "screenshots.json")));
            Retention.Changed += (_, saved) => RetentionSeenByWindow = saved;
            ScaleFile = Path.Combine(root, "scale.txt");
            _scale = File.Exists(ScaleFile) ? double.Parse(File.ReadAllText(ScaleFile), System.Globalization.CultureInfo.InvariantCulture) : 1;
            Network = new NetworkPolicyService(new JsonFileNetworkControlsStore(Path.Combine(root, "network.json")));
            Flags = new FeatureFlagService(ReleaseRing.Rough, new JsonFileFeatureFlagOverrideStore(Path.Combine(root, "feature-flags.json")));
            Horizons = new RecommendationPolicyService(new JsonFileRecommendationPolicyStore(Path.Combine(root, "recommendations.json")));
            Group = new JsonFileGroupSettingsStore(Path.Combine(root, "group.json"));
            Layout = new JsonFileWorkspaceLayoutStore(Path.Combine(root, "workspace-layout.json"));
            MapDefaults = new JsonFileMapVariantPreferenceStore(Path.Combine(root, "map-defaults.json"));
            Admin = new SetupSettingsAdminViewModel(
                preferences,
                Retention,
                bridge,
                new SetupSettingsSources
                {
                    InterfaceScale = (() => _scale, value =>
                    {
                        _scale = value;
                        File.WriteAllText(ScaleFile, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }),
                    Network = Network,
                    FeatureFlags = Flags,
                    Horizons = Horizons,
                    SquadSharing = Group,
                    Layout = Layout,
                    MapDefaults = MapDefaults,
                });
        }

        public WorkspacePreferenceService Preferences { get; }

        public NotificationBridge Notifications => _bridge;

        public ObservableScreenshotRetentionStore Retention { get; }

        public ScreenshotRetentionSettings? RetentionSeenByWindow { get; private set; }

        public double Scale => _scale;

        public NetworkPolicyService Network { get; }

        public FeatureFlagService Flags { get; }

        public RecommendationPolicyService Horizons { get; }

        public IGroupSettingsStore Group { get; }

        public IWorkspaceLayoutStore Layout { get; }

        public JsonFileMapVariantPreferenceStore MapDefaults { get; }

        public SetupSettingsAdminViewModel Admin { get; }

        private string ScaleFile { get; }

        public static async Task<App> StartAsync(string root)
        {
            var bridge = new NotificationBridge(
                new RuntimeStore(),
                new JsonFileNotificationSettingsStore(Path.Combine(root, "notifications.json")),
                new NoGroup(),
                [],
                static () => null,
                settingsPage: null);
            await bridge.InitializeAsync(CancellationToken.None);
            var preferences = new WorkspacePreferenceService(new JsonFileWorkspacePreferenceStore(Path.Combine(root, "preferences.json")));
            await preferences.LoadAsync(CancellationToken.None);
            var app = new App(root, bridge, preferences);
            await app.Horizons.LoadAsync(CancellationToken.None);
            return app;
        }

        /// <summary>A snapshot with every registered group away from its default.</summary>
        public static SetupSettingsSnapshot EverythingChanged() =>
            new SetupSettingsSnapshot(
                WorkspacePreferences.Default with { Theme = AppearanceTheme.Light, TextScalePercent = 150 },
                NotificationSettings.Default with { SquadMark = false, QuietHours = true, QuietFromHour = 22, QuietToHour = 7 },
                ScreenshotRetentionSettings.Default with { IsEnabled = true, RetentionHours = 72 })
            {
                InterfaceScale = 1.3,
                Network = NetworkControls.Default with { LocalOnly = true, UpdateChecks = false },
                FeatureFlags = new SortedDictionary<string, bool> { [Flag.DrawMode.Key] = false },
                Horizons = new RecommendationHorizonSettings(RecommendationHorizon.All, RecommendationHorizon.NextOnly),
                SquadSharing = new SquadSharingChoices(true, true, false),
                Layout = new SortedDictionary<string, string>
                {
                    [WorkspaceLayoutKeys.PlanLearnMode] = "on",
                    [WorkspaceLayoutKeys.RaidLayerVisibility] = "objective-route:0,loot:1",
                    [WorkspaceLayoutKeys.LootOnTabletOnly] = "on",
                    [WorkspaceLayoutKeys.LootAutoReturnSeconds] = "30",
                    ["page.intel"] = "kind=ammo",
                },
                MapDefaults = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["factory#artwork"] = "drawing" },
            };

        /// <summary>Makes every change through the same services and stores the pages use.</summary>
        public async Task ChangeEverythingAsync()
        {
            var changed = EverythingChanged();
            await Preferences.UpdateAsync(changed.Appearance, CancellationToken.None);
            await Notifications.ReplaceAsync(changed.Notifications, CancellationToken.None);
            await Retention.SaveAsync(changed.ScreenshotRetention, CancellationToken.None);
            _scale = changed.InterfaceScale;
            Network.Set(changed.Network);
            Flags.Set(Flag.DrawMode, false);
            await Horizons.UpdateAsync(changed.Horizons, CancellationToken.None);
            await Group.SaveAsync(
                new GroupSharingSettings(true, "https://relay.example", "Player", "a-group-key-long-enough", true, false),
                CancellationToken.None);
            foreach (var (key, value) in changed.Layout)
            {
                Layout.Set(key, value);
            }

            await MapDefaults.SetAsync("factory#artwork", "drawing", CancellationToken.None);
        }

        public void Dispose() => _bridge.Dispose();
    }

    private sealed class NoGroup : IGroupSettingsStore
    {
        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(GroupSharingSettings.Off);

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RuntimeStore : IRuntimeStateStore
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public ApplicationRuntimeSnapshot Current { get; } = V2ShellTestData.Snapshot();

        public void Update(Func<ApplicationRuntimeSnapshot, ApplicationRuntimeSnapshot> update)
        {
        }
    }
}
