using TarkovCompanion.App.Services.V2.Notifications;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Platform.Windows.Notifications;
using TarkovCompanion.UnitTests.V2Shell;

namespace TarkovCompanion.UnitTests.Notifications;

public sealed class NativeNotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Windows_receives_only_the_lock_screen_projection()
    {
        var request = new NotificationRequest(
            NotificationKind.FleaSold,
            "LEDX sold for 1,200,000 roubles",
            "Your LEDX sold for 1,200,000 roubles.",
            1,
            Now);

        var notification = NotificationPrivacy.ForLockScreen(request);

        Assert.Equal("Flea offer sold", notification.Title);
        Assert.Equal("Open Tarkov Companion for details.", notification.Body);
        Assert.DoesNotContain("LEDX", notification.Title + notification.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1,200,000", notification.Title + notification.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_platform_adapter_is_fixture_testable_without_Windows()
    {
        var sink = new RecordingWindowsSink();
        var channel = new WindowsToastNotificationChannel(sink);

        channel.Show(new LockScreenNotification("Raid over", "The debrief is ready."));

        Assert.Equal(("Raid over", "The debrief is ready."), Assert.Single(sink.Shown));
    }

    [Fact]
    public async Task Quiet_hours_hold_the_native_toast_back_while_the_tray_still_counts()
    {
        var tray = new RecordingChannel();
        var native = new RecordingNativeChannel();
        var runtime = new RuntimeStore(Snapshot(RaidLifecycleState.Menu));
        using var bridge = Build(runtime, tray, native);
        await bridge.InitializeAsync(CancellationToken.None);
        await bridge.SetPopupAsync(true, CancellationToken.None);
        await bridge.SetQuietHoursAsync(true, 23, 8, CancellationToken.None);

        runtime.Set(Snapshot(RaidLifecycleState.PostRaid) with
        {
            Raid = new RaidSnapshot(Guid.NewGuid(), RaidLifecycleState.PostRaid, "customs", Now, Now, Confidence.Unknown, null, [], false),
        });

        Assert.Single(tray.Sent);
        Assert.Empty(native.Sent);
    }

    [Fact]
    public async Task A_native_toast_never_interrupts_loading_or_active_raids()
    {
        foreach (var state in new[] { RaidLifecycleState.LoadingRaid, RaidLifecycleState.InRaid })
        {
            var tray = new RecordingChannel();
            var native = new RecordingNativeChannel();
            using var bridge = Build(Snapshot(state), tray, native);
            await bridge.SetPopupAsync(true, CancellationToken.None);

            bridge.Test(NotificationKind.SquadMark);

            Assert.Single(tray.Sent);
            Assert.Empty(native.Sent);
        }
    }

    [Fact]
    public async Task Outside_a_raid_the_opted_in_popup_uses_the_native_channel()
    {
        var tray = new RecordingChannel();
        var fallback = new RecordingChannel();
        var native = new RecordingNativeChannel();
        using var bridge = Build(Snapshot(RaidLifecycleState.Menu), tray, native, fallback);
        await bridge.SetPopupAsync(true, CancellationToken.None);

        bridge.Test(NotificationKind.DataRefreshFailed);

        Assert.Single(native.Sent);
        Assert.Empty(fallback.Sent);
        Assert.Equal("Game data needs attention", native.Sent[0].Title);
    }

    private static NotificationBridge Build(
        ApplicationRuntimeSnapshot snapshot,
        RecordingChannel tray,
        RecordingNativeChannel native,
        RecordingChannel? fallback = null) => new(
        new RuntimeStore(snapshot),
        new SettingsStore(),
        new GroupStore(),
        [tray],
        () => fallback,
        timeProvider: new FixedTime(Now),
        nativePopupChannel: native);

    private static NotificationBridge Build(
        RuntimeStore runtime,
        RecordingChannel tray,
        RecordingNativeChannel native,
        RecordingChannel? fallback = null) => new(
        runtime,
        new SettingsStore(),
        new GroupStore(),
        [tray],
        () => fallback,
        timeProvider: new FixedTime(Now),
        nativePopupChannel: native);

    private static ApplicationRuntimeSnapshot Snapshot(RaidLifecycleState state)
    {
        var snapshot = V2ShellTestData.Snapshot();
        return snapshot with
        {
            Raid = new RaidSnapshot(null, state, "customs", Now, Now, Confidence.Unknown, null, [], false),
        };
    }

    private sealed class RecordingWindowsSink : IWindowsToastSink
    {
        public bool IsAvailable => true;

        public List<(string Title, string Body)> Shown { get; } = [];

        public void Show(string title, string body) => Shown.Add((title, body));
    }

    private sealed class RecordingNativeChannel : INativeNotificationChannel
    {
        public bool IsAvailable => true;

        public List<LockScreenNotification> Sent { get; } = [];

        public void Show(LockScreenNotification notification) => Sent.Add(notification);
    }

    private sealed class RecordingChannel : INotificationChannel
    {
        public List<NotificationRequest> Sent { get; } = [];

        public void Show(NotificationRequest request) => Sent.Add(request);
    }

    private sealed class SettingsStore : INotificationSettingsStore
    {
        public Task<NotificationSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(NotificationSettings.Default);

        public Task SaveAsync(NotificationSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class GroupStore : IGroupSettingsStore
    {
        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GroupSharingSettings(false, "https://relay.test", "Clayton", "key", false, false));

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RuntimeStore(ApplicationRuntimeSnapshot current) : IRuntimeStateStore
    {
        public event EventHandler? Changed;

        public ApplicationRuntimeSnapshot Current { get; private set; } = current;

        public void Update(Func<ApplicationRuntimeSnapshot, ApplicationRuntimeSnapshot> update) => Set(update(Current));

        public void Set(ApplicationRuntimeSnapshot snapshot)
        {
            Current = snapshot;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
