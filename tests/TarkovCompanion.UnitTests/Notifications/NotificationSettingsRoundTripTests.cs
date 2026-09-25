using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.App.Services.V2.Notifications;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Application.Services.Personalization;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Setup;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Personalization;
using TarkovCompanion.UnitTests.V2Shell;

namespace TarkovCompanion.UnitTests.Notifications;

/// <summary>
/// #888: Setup › Notifications against the saved file, settings import/reset against quiet hours,
/// and quiet hours read in the zone the app shows times in.
/// </summary>
public sealed class NotificationSettingsRoundTripTests
{
    private static readonly NotificationSettings Saved = NotificationSettings.Default with
    {
        ShowsDesktopPopup = true,
        QuietHours = true,
        QuietFromHour = 22,
        QuietToHour = 7,
        FleaSold = false,
    };

    [Fact]
    public async Task The_page_built_before_the_file_is_read_shows_the_saved_settings_once_it_is()
    {
        var store = new MemoryStore(Saved);
        using var bridge = Build(store);
        // Composed first, exactly as the shell does, then the bridge reads the file.
        var page = new SetupNotificationsViewModel(bridge);

        await bridge.InitializeAsync(CancellationToken.None);

        Assert.True(page.ShowsPopup);
        Assert.True(page.QuietHours);
        Assert.Equal(22, page.QuietFromHour);
        Assert.Equal(7, page.QuietToHour);
        Assert.False(page.Rows.Single(row => row.Kind == NotificationKind.FleaSold).IsEnabled);
        Assert.True(page.Rows.Single(row => row.Kind == NotificationKind.DebriefReady).IsEnabled);
        // Showing the saved values wrote nothing.
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task Toggling_quiet_hours_keeps_the_saved_window_and_the_popup_toggle_turns_it_off()
    {
        var store = new MemoryStore(Saved);
        using var bridge = Build(store);
        var page = new SetupNotificationsViewModel(bridge);
        await bridge.InitializeAsync(CancellationToken.None);

        page.ToggleQuietHoursCommand.Execute(null);
        page.TogglePopupCommand.Execute(null);

        Assert.False(store.Stored.QuietHours);
        Assert.Equal(22, store.Stored.QuietFromHour);
        Assert.Equal(7, store.Stored.QuietToHour);
        Assert.False(store.Stored.ShowsDesktopPopup);
        Assert.False(page.ShowsPopup);
    }

    [Fact]
    public async Task Reset_all_applies_quiet_hours_and_the_page_follows()
    {
        var store = new MemoryStore(Saved);
        using var bridge = Build(store);
        await bridge.InitializeAsync(CancellationToken.None);
        var page = new SetupNotificationsViewModel(bridge);
        var preferences = new WorkspacePreferenceService(new PreferenceStore());
        await preferences.LoadAsync(CancellationToken.None);
        var admin = new SetupSettingsAdminViewModel(preferences, new RetentionStore(), bridge);

        await ((AsyncDelegateCommand)admin.ResetAllCommand).ExecuteAsync();
        Assert.Contains(admin.PendingDiff, row => row.Field == SetupText.SettingsField(SetupSettingsField.QuietHours));
        await ((AsyncDelegateCommand)admin.ConfirmCommand).ExecuteAsync();

        Assert.Equal(NotificationSettings.Default, store.Stored);
        Assert.Equal(NotificationSettings.Default, bridge.Settings);
        Assert.False(page.QuietHours);
        Assert.Equal(23, page.QuietFromHour);
        Assert.Equal(8, page.QuietToHour);
        Assert.True(page.Rows.Single(row => row.Kind == NotificationKind.FleaSold).IsEnabled);
        // One write for the whole record, not one per switch.
        Assert.Equal(1, store.Saves);

        // Nothing left to reset: the same diff is not offered again.
        await ((AsyncDelegateCommand)admin.ResetAllCommand).ExecuteAsync();
        Assert.False(admin.HasPendingChange);
    }

    /// <summary>
    /// The profile says New York (UTC-4 in September) on a machine set to UTC. At 03:00 UTC it is
    /// 23:00 on the player's clock: quiet under 23-8. The machine's clock says 03:00, which is
    /// quiet too, so the hour that tells them apart is 01:00 UTC: 21:00 for the player, not quiet.
    /// </summary>
    [Fact]
    public async Task Quiet_hours_are_read_in_the_zone_the_app_shows_times_in()
    {
        var fourBehind = TimeZoneInfo.CreateCustomTimeZone("minus4", TimeSpan.FromHours(-4), "minus4", "minus4");
        using var zone = LocalTime.UseZone(fourBehind);
        var time = new SettableTime(new DateTimeOffset(2026, 9, 22, 1, 0, 0, TimeSpan.Zero));
        using var bridge = Build(new MemoryStore(NotificationSettings.Default), time);
        await bridge.SetQuietHoursAsync(true, 23, 8, CancellationToken.None);

        // 01:00 UTC = 21:00 for the player: not quiet (the machine's zone said it was).
        Assert.False(bridge.IsQuietNow());

        // 03:00 UTC = 23:00 for the player: quiet.
        time.Now = new DateTimeOffset(2026, 9, 22, 3, 0, 0, TimeSpan.Zero);
        Assert.True(bridge.IsQuietNow());

        // 11:30 UTC = 07:30 for the player: still quiet (the machine's zone said it was not).
        time.Now = new DateTimeOffset(2026, 9, 22, 11, 30, 0, TimeSpan.Zero);
        Assert.True(bridge.IsQuietNow());
    }

    private static NotificationBridge Build(INotificationSettingsStore store, TimeProvider? time = null) => new(
        new RuntimeStore(),
        store,
        new GroupStore(),
        [],
        static () => null,
        settingsPage: null,
        timeProvider: time ?? new SettableTime(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero)));

    private sealed class SettableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        // The machine's zone. Quiet hours must not use it.
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class MemoryStore(NotificationSettings initial) : INotificationSettingsStore
    {
        public NotificationSettings Stored { get; private set; } = initial;

        public int Saves { get; private set; }

        public Task<NotificationSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Stored);

        public Task SaveAsync(NotificationSettings settings, CancellationToken cancellationToken)
        {
            Stored = settings;
            Saves++;
            return Task.CompletedTask;
        }
    }

    private sealed class GroupStore : IGroupSettingsStore
    {
        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GroupSharingSettings(false, "https://relay.test", "Player", "key", false, false));

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

    private sealed class PreferenceStore : IWorkspacePreferenceStore
    {
        private WorkspacePreferences _stored = WorkspacePreferences.Default;

        public Task<WorkspacePreferences> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_stored);

        public Task SaveAsync(WorkspacePreferences preferences, CancellationToken cancellationToken)
        {
            _stored = preferences;
            return Task.CompletedTask;
        }
    }

    private sealed class RetentionStore : IScreenshotRetentionStore
    {
        private ScreenshotRetentionSettings _stored = ScreenshotRetentionSettings.Default;

        public Task<ScreenshotRetentionSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_stored);

        public Task SaveAsync(ScreenshotRetentionSettings settings, CancellationToken cancellationToken)
        {
            _stored = settings;
            return Task.CompletedTask;
        }
    }
}
