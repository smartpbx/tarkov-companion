using TarkovCompanion.App.Services.V2.Notifications;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.UnitTests.V2Shell;

namespace TarkovCompanion.UnitTests.Notifications;

/// <summary>
/// #314: the flea-sold notification and quiet hours. The one thing the flea rule must never do is
/// announce this morning's sales again because the startup replay read them "now".
/// </summary>
public sealed class FleaSoldAndQuietHoursTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 22, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_sale_written_after_the_companion_started_is_announced_once()
    {
        var coordinator = new NotificationCoordinator();
        coordinator.Observe(Menu(Start), NotificationSettings.Default);

        var raised = coordinator.Observe(Menu(Start.AddMinutes(1), Sale("A", 2, Start.AddSeconds(50))), NotificationSettings.Default);
        var again = coordinator.Observe(Menu(Start.AddMinutes(2), Sale("A", 2, Start.AddSeconds(50))), NotificationSettings.Default);

        var notification = Assert.Single(raised);
        Assert.Equal(NotificationKind.FleaSold, notification.Kind);
        Assert.Equal("Flea offer sold", notification.Title);
        Assert.Equal("2 items. Intel › Flea lists them.", notification.Body);
        Assert.Empty(again);
    }

    [Fact]
    public void A_replayed_sale_from_before_the_start_is_never_announced()
    {
        var coordinator = new NotificationCoordinator();

        // Read "now" by the startup replay, but the game wrote it this morning.
        var raised = coordinator.Observe(
            Menu(Start, Sale("OLD", 1, Start.AddHours(-9))),
            NotificationSettings.Default);
        var later = coordinator.Observe(
            Menu(Start.AddMinutes(5), Sale("OLD", 1, Start.AddHours(-9))),
            NotificationSettings.Default);

        Assert.Empty(raised);
        Assert.Empty(later);
    }

    [Fact]
    public void A_sale_whose_time_could_not_be_read_stays_silent()
    {
        var coordinator = new NotificationCoordinator();
        coordinator.Observe(Menu(Start), NotificationSettings.Default);

        Assert.Empty(coordinator.Observe(Menu(Start.AddMinutes(1), Sale("A", 1, null)), NotificationSettings.Default));
    }

    [Fact]
    public void Sales_during_a_raid_are_said_together_when_it_ends()
    {
        var coordinator = new NotificationCoordinator();
        coordinator.Observe(Menu(Start), NotificationSettings.Default);
        FleaSaleInput[] sales = [Sale("A", 1, Start.AddMinutes(10)), Sale("B", 3, Start.AddMinutes(20))];

        var inRaid = coordinator.Observe(
            new NotificationInputs { NowUtc = Start.AddMinutes(21), RaidState = RaidLifecycleState.InRaid, FleaSales = sales },
            NotificationSettings.Default);
        var after = coordinator.Observe(Menu(Start.AddMinutes(40), sales), NotificationSettings.Default);

        Assert.Empty(inRaid);
        var notification = Assert.Single(after);
        Assert.Equal("2 flea offers sold", notification.Title);
        Assert.Equal("4 items. Intel › Flea lists them.", notification.Body);
        Assert.Equal(2, notification.Count);
    }

    [Fact]
    public void A_sale_seen_while_switched_off_is_not_announced_when_switched_back_on()
    {
        var coordinator = new NotificationCoordinator();
        var off = NotificationSettings.Default.With(NotificationKind.FleaSold, false);
        coordinator.Observe(Menu(Start), off);

        Assert.Empty(coordinator.Observe(Menu(Start.AddMinutes(1), Sale("A", 1, Start.AddSeconds(30))), off));
        Assert.Empty(coordinator.Observe(Menu(Start.AddMinutes(2), Sale("A", 1, Start.AddSeconds(30))), NotificationSettings.Default));
    }

    [Theory]
    [InlineData(23, 8, 23, true)]
    [InlineData(23, 8, 2, true)]
    [InlineData(23, 8, 7, true)]
    [InlineData(23, 8, 8, false)]
    [InlineData(23, 8, 22, false)]
    [InlineData(1, 6, 3, true)]
    [InlineData(1, 6, 6, false)]
    [InlineData(1, 6, 0, false)]
    [InlineData(5, 5, 5, false)]
    public void Quiet_hours_cover_the_start_hour_up_to_the_end_hour_across_midnight(int from, int to, int hour, bool quiet)
    {
        var settings = NotificationSettings.Default with { QuietHours = true, QuietFromHour = from, QuietToHour = to };

        Assert.Equal(quiet, settings.IsQuietAt(new TimeOnly(hour, 30)));
    }

    [Fact]
    public void Quiet_hours_switched_off_are_never_quiet()
    {
        Assert.False(NotificationSettings.Default.IsQuietAt(new TimeOnly(2, 0)));
    }

    [Fact]
    public async Task Quiet_hours_hold_back_the_popup_but_the_tray_still_counts()
    {
        var quiet = new RecordingChannel();
        var popup = new RecordingChannel();
        // 02:00 UTC, read in UTC: inside 23-8.
        var store = new FakeRuntimeStore(V2ShellTestData.Snapshot());
        using var bridge = Build(quiet, popup, new FixedTime(new DateTimeOffset(2026, 9, 22, 2, 0, 0, TimeSpan.Zero)), store);
        await bridge.SetPopupAsync(true, CancellationToken.None);
        await bridge.SetQuietHoursAsync(true, 23, 8, CancellationToken.None);

        Assert.True(bridge.IsQuietNow());
        await bridge.SetQuietHoursAsync(false, 23, 8, CancellationToken.None);
        Assert.False(bridge.IsQuietNow());
        await bridge.SetQuietHoursAsync(true, 23, 8, CancellationToken.None);

        // A raid ends at 02:00: the tray counts it, the pop-up keeps quiet.
        store.Update(current => current with
        {
            Raid = new RaidSnapshot(Guid.NewGuid(), RaidLifecycleState.PostRaid, "customs", Start, Start, Confidence.Unknown, null, [], false),
        });
        Assert.Equal(NotificationKind.DebriefReady, Assert.Single(quiet.Sent).Kind);
        Assert.Empty(popup.Sent);
        quiet.Sent.Clear();

        // A test press is somebody asking to see it, quiet hours or not.
        bridge.Test(NotificationKind.FleaSold);
        Assert.Single(quiet.Sent);
        Assert.Single(popup.Sent);
    }

    [Fact]
    public void The_bridge_hands_the_rules_each_sale_with_the_time_the_game_wrote_it()
    {
        using var bridge = Build(new RecordingChannel(), new RecordingChannel(), new FixedTime(Start));
        var snapshot = V2ShellTestData.Snapshot() with
        {
            FleaSales = new FleaSalesSnapshot(
                [new FleaSaleObservation("OFFER", "ITEM", 3, Start, Start.AddMinutes(-1))],
                Start),
        };

        var sale = Assert.Single(bridge.BuildInputs(snapshot).FleaSales);

        Assert.Equal(new FleaSaleInput("OFFER", 3, Start.AddMinutes(-1)), sale);
    }

    [Fact]
    public void The_parser_reads_the_time_the_game_wrote_the_sale()
    {
        const string line = "2026-09-11 23:00:00.000|1.1.5.0.47242|Info|backend|NOTIFICATION [EVENTID] RagfairOfferSold "
            + """[{"type":"RagfairOfferSold","offerId":"OFFER_1","handbookId":"ITEM_1","count":3}]""";

        var sale = FleaSaleParser.ParseLine(line, Start, TimeZoneInfo.Utc);
        var withoutZone = FleaSaleParser.ParseLine(line, Start);

        Assert.Equal(new DateTimeOffset(2026, 9, 11, 23, 0, 0, TimeSpan.Zero), sale?.WrittenUtc);
        Assert.Equal(Start, sale?.ObservedUtc);
        Assert.Null(withoutZone?.WrittenUtc);
    }

    private static FleaSaleInput Sale(string id, int count, DateTimeOffset? written) => new(id, count, written);

    private static NotificationInputs Menu(DateTimeOffset at, params FleaSaleInput[] sales) => new()
    {
        NowUtc = at,
        RaidState = RaidLifecycleState.Menu,
        FleaSales = sales,
    };

    private static NotificationBridge Build(
        RecordingChannel quiet,
        RecordingChannel popup,
        TimeProvider time,
        FakeRuntimeStore? store = null) => new(
        store ?? new FakeRuntimeStore(V2ShellTestData.Snapshot()),
        new NullSettingsStore(),
        new FakeGroupSettingsStore(),
        [quiet],
        () => popup,
        settingsPage: null,
        timeProvider: time);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class RecordingChannel : INotificationChannel
    {
        public List<NotificationRequest> Sent { get; } = [];

        public void Show(NotificationRequest request) => Sent.Add(request);
    }

    private sealed class NullSettingsStore : INotificationSettingsStore
    {
        public Task<NotificationSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(NotificationSettings.Default);

        public Task SaveAsync(NotificationSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeGroupSettingsStore : IGroupSettingsStore
    {
        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GroupSharingSettings(true, "https://relay.test", "Clayton", "key", false, false));

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeRuntimeStore(ApplicationRuntimeSnapshot current) : IRuntimeStateStore
    {
        public event EventHandler? Changed;

        public ApplicationRuntimeSnapshot Current { get; private set; } = current;

        public void Update(Func<ApplicationRuntimeSnapshot, ApplicationRuntimeSnapshot> update)
        {
            Current = update(Current);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
