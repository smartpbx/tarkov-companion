using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Setup;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.App.Services.Windowing;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.UnitTests.V2Setup;

/// <summary>#292: the parts of Setup that say what the app is doing and hold back what it should not show.</summary>
public sealed class SetupAdminTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly RuntimeOptions Options = new(false, false, GameMode.Regular, "en", TimeSpan.FromHours(9), TimeSpan.FromMinutes(5));

    [Theory]
    [InlineData(@"C:\Users\Riley\Documents\Escape from Tarkov\Screenshots", @"C:\Users\Riley", @"%USERPROFILE%\…\Escape from Tarkov\Screenshots")]
    [InlineData(@"C:\Users\Riley\Docs", @"C:\Users\Riley", @"%USERPROFILE%\Docs")]
    [InlineData(@"D:\Games\Battlestate Games\EFT\Logs", @"C:\Users\Riley", @"D:\…\EFT\Logs")]
    [InlineData("/home/riley/.local/share/tarkov/db/tarkov.db", "/home/riley", "%USERPROFILE%/…/db/tarkov.db")]
    [InlineData("/tmp/render-1/Demo/Database/tarkov-companion.db", "", "/…/Database/tarkov-companion.db")]
    [InlineData(@"\\nas\share\Users\Riley\Shots", "", @"\…\Riley\Shots")]
    public void APathLosesTheWordsThatNameAPersonAndKeepsWhereItLeads(string path, string profile, string expected) =>
        Assert.Equal(expected, SetupPathMask.Mask(path, profile));

    [Fact]
    public void EveryPathInALineIsMaskedAndTheWordsAroundThemAreNot()
    {
        var line = @"Screenshots C:\Users\Riley\Pictures\EFT\Shots · logs D:\Games\EFT\Logs · not found yet";

        var masked = SetupPathMask.Mask(line, @"C:\Users\Riley");

        Assert.Equal(@"Screenshots %USERPROFILE%\…\EFT\Shots · logs D:\…\EFT\Logs · not found yet", masked);
        Assert.DoesNotContain("Riley", masked, StringComparison.Ordinal);
        Assert.Equal("Nothing found yet", SetupPathMask.Mask("Nothing found yet"));
        // Not paths: a slash between two words, a date, and a web address.
        Assert.Equal("Screenshots and/or logs, 12/05, https://tarkov.dev/x", SetupPathMask.Mask("Screenshots and/or logs, 12/05, https://tarkov.dev/x"));
        Assert.Equal(string.Empty, SetupPathMask.Mask(null));
    }

    [Fact]
    public void PathsAreShownInFullAndFollowTheSourceAndCanStillBeMasked()
    {
        var source = new FakeSource { Value = @"Database C:\Users\Riley\AppData\Local\TarkovCompanion\db\tarkov.db" };
        var gate = new SetupPathDisclosureViewModel(@"C:\Users\Riley");
        using var text = new GatedPathText(() => source.Value, gate, source, nameof(FakeSource.Value));

        Assert.True(gate.IsRevealed);
        Assert.Contains("Riley", text.Text, StringComparison.Ordinal);

        source.Value = @"Database D:\Data\Games\EFT\db\other.db";
        Assert.Equal(source.Value, text.Text);

        gate.ToggleCommand.Execute(null);
        Assert.Equal(@"Database D:\…\db\other.db", text.Text);
    }

    [Fact]
    public void ADataRefreshThatWorkedSaysWhenAndWhereFromAndWhenItWillNextTry()
    {
        var snapshot = Snapshot(Availability(DataAvailability.Current, updated: Now.AddMinutes(-20)), Operation(BackgroundWorkState.Succeeded, Now.AddMinutes(-20)));

        var detail = SetupDataDetail.Describe(snapshot, "PvE · de", TimeSpan.FromHours(9), Now, CultureInfo.InvariantCulture);

        Assert.Equal("json.tarkov.dev · PvE · de", Fact(detail, "Source"));
        Assert.Equal("5,442 items · 7 of 7 endpoints", Fact(detail, "Coverage"));
        Assert.Equal("Succeeded, 20m ago", Fact(detail, "Last attempt"));
        Assert.Equal("20m ago", Fact(detail, "Last success"));
        Assert.Equal("At launch if the data is over 9 h old, or press Sync now", Fact(detail, "Next refresh"));
        Assert.Null(detail.Reason);
        Assert.False(detail.NeedsRetry);
    }

    [Fact]
    public void AFailedRefreshOverOldDataNamesTheReasonAndOffersARetry()
    {
        var snapshot = Snapshot(
            Availability(DataAvailability.Cached, updated: Now.AddHours(-30), detail: "2 endpoint refresh(es) failed · local data stands"),
            Operation(BackgroundWorkState.Faulted, Now.AddMinutes(-3)));

        var detail = SetupDataDetail.Describe(snapshot, "PvP · en", TimeSpan.FromHours(9), Now, CultureInfo.InvariantCulture);

        Assert.Equal("Failed, 3m ago", Fact(detail, "Last attempt"));
        Assert.Equal("30h ago", Fact(detail, "Last success"));
        Assert.Equal("2 endpoint refresh(es) failed · local data stands", detail.Reason);
        Assert.True(detail.NeedsRetry);
    }

    [Fact]
    public void OfflineIsSaidAsOfflineAndIsNotARetryableFailure()
    {
        var snapshot = Snapshot(Availability(DataAvailability.Cached, updated: Now.AddDays(-1), detail: "Offline mode is enabled; using the local game-data cache."), isOffline: true);

        var detail = SetupDataDetail.Describe(snapshot, "PvP · en", TimeSpan.FromHours(9), Now, CultureInfo.InvariantCulture);

        Assert.Equal("Offline, so none was tried", Fact(detail, "Last attempt"));
        Assert.Equal("When you are back online", Fact(detail, "Next refresh"));
        Assert.Equal("Offline mode is enabled; using the local game-data cache.", detail.Reason);
        Assert.False(detail.NeedsRetry);
    }

    [Fact]
    public void NoDataAtAllIsSaidPlainlyAndOffersARetry()
    {
        var snapshot = Snapshot(new RuntimeDataState(DataAvailability.Unavailable, 0, 0, null, "No local game data"));

        var detail = SetupDataDetail.Describe(snapshot, "PvP · en", TimeSpan.FromHours(9), Now, CultureInfo.InvariantCulture);

        Assert.Equal("No game data yet", Fact(detail, "Coverage"));
        Assert.Equal("Never", Fact(detail, "Last success"));
        Assert.Equal("None yet this launch", Fact(detail, "Last attempt"));
        Assert.True(detail.NeedsRetry);
    }

    [Fact]
    public void ReleaseNotesLoseTheirMarkdownAndAreCut()
    {
        var notes = SetupUpdateNotes.Plain("## What's new\n\n- **Profiles** can be switched\n- Fixed [a crash](https://example.invalid/x) on `Plan`\n\n\n\nThanks");

        Assert.Equal("What's new\n\n• Profiles can be switched\n• Fixed a crash on Plan\n\nThanks", notes);
        Assert.Equal(string.Empty, SetupUpdateNotes.Plain("  "));
        Assert.True(SetupUpdateNotes.Plain(string.Join('\n', Enumerable.Repeat("A line of release notes that goes on and on.", 80))).Length <= SetupUpdateNotes.MaximumLength + 2);
    }

    [Fact]
    public void ADeepLinkOpensThePageAtOneItemExpandedAndNoOtherOpen()
    {
        var page = new SetupInfoPageViewModel("Data & Privacy", SetupPageContent.DataPrivacy);
        page.Items[0].IsExpanded = true;

        Assert.True(page.Open(SetupAnchors.SharingScope));

        Assert.Equal([SetupAnchors.SharingScope], page.Items.Where(item => item.IsExpanded).Select(item => item.Id));
        Assert.False(page.Open("no-such-item"));
    }

    [Fact]
    public void EveryTopicTheIssueNamesHasAnItemToLinkTo()
    {
        var anchors = SetupPageContent.About.Concat(SetupPageContent.DataPrivacy).Select(item => item.Id).ToArray();

        Assert.Equal(anchors.Length, anchors.Distinct().Count());
        // Safety, anti-cheat, data methodology, privacy, capture retention and sharing scope.
        Assert.Contains(SetupAnchors.AntiCheat, anchors);
        Assert.Contains(SetupAnchors.Methodology, anchors);
        Assert.Contains(SetupAnchors.Stored, anchors);
        Assert.Contains(SetupAnchors.CaptureRetention, anchors);
        Assert.Contains(SetupAnchors.SharingScope, anchors);
        Assert.All(SetupPageContent.About.Concat(SetupPageContent.DataPrivacy), item =>
        {
            Assert.InRange(item.Summary.Length, 10, 120);
            Assert.NotEmpty(item.Detail);
        });
    }

    [Fact]
    public async Task DisplaysListEachMonitorAndSayWhichHoldsTheGameWindow()
    {
        var monitors = new FakeMonitors(
            new DisplayDescriptor("1", "Display 1", new(0, 0, 1920, 1080), true, 1.0),
            new DisplayDescriptor("2", "Display 2", new(1920, 0, 3840, 1080), false, 1.25));
        var windows = new FakeWindows(new WindowDescriptor(1, "EscapeFromTarkov", "EFT", new(2400, 0, 3840, 1080), false, false));
        var placement = new FakePlacement("2");
        var view = new SetupDisplaysViewModel(monitors, windows, placement);

        await view.RefreshAsync(default);

        Assert.True(view.IsAvailable);
        Assert.Equal(2, view.Displays.Count);
        Assert.Equal("Primary", view.Displays[0].Badges);
        Assert.Equal("Companion here · Game window here", view.Displays[1].Badges);
        Assert.Equal("Game window, 3840×1080, on Display 2", view.CaptureTarget);

        await Assert.IsType<AsyncDelegateCommand>(view.Displays[0].MoveCommand).ExecuteAsync();

        Assert.Equal("1", placement.CurrentDisplayId);
        Assert.Contains("Companion here", view.Displays[0].Badges, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisplaysSayWhenTheGameWindowIsMissingMinimizedOrThePlatformHasNoDisplays()
    {
        var monitors = new FakeMonitors(new DisplayDescriptor("1", "Display 1", new(0, 0, 1920, 1080), true, 1.0));

        var missing = new SetupDisplaysViewModel(monitors, new FakeWindows(null));
        await missing.RefreshAsync(default);
        Assert.Contains("not found", missing.CaptureTarget, StringComparison.Ordinal);

        var minimized = new SetupDisplaysViewModel(monitors, new FakeWindows(new WindowDescriptor(1, "p", "t", new(0, 0, 1920, 1080), true, false)));
        await minimized.RefreshAsync(default);
        Assert.Contains("minimized", minimized.CaptureTarget, StringComparison.Ordinal);

        var none = new SetupDisplaysViewModel(null, null);
        await none.RefreshAsync(default);
        Assert.True(none.IsUnavailable);
    }

    [Fact]
    public async Task TheRealCompositionAttachesSetupAdminAndTheDeepLinkOpensTheAnswer()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-setup-admin-{Guid.NewGuid():N}");
        try
        {
            await using var services = AppComposition.Build(
                new AppCommandLine(false, true, false, false, null, null, null) { UiShell = V2ShellMode.VariantA },
                new(DataRoot: root, Offline: true));
            var legacy = services.GetRequiredService<MainWindowViewModel>();
            var shell = services.GetRequiredService<V2ShellViewModel>();
            legacy.PreviewShell = shell;
            await legacy.InitializeAsync();

            var setup = shell.SetupWorkspace!;

            Assert.True(setup.HasAdmin);
            Assert.True(setup.Paths.IsRevealed);
            Assert.Contains(setup.Sections, tab => tab.Section == V2SetupSection.About);
            Assert.Contains(setup.Sections, tab => tab.Section == V2SetupSection.DataPrivacy);
            Assert.True(setup.OpenSection(V2SetupSection.DataPrivacy, SetupAnchors.SharingScope));
            Assert.True(setup.IsDataPrivacySelected);
            Assert.True(setup.Admin!.DataPrivacy.Items.Single(item => item.Id == SetupAnchors.SharingScope).IsExpanded);
            setup.Select(V2SetupSection.Data);
            Assert.Equal("Source", setup.Admin.Data.Facts[0].Label);
            // The fixture composition is the demo one, which says so instead of promising a refresh.
            Assert.Equal("Demo data, so none", setup.Admin.Data.Facts.Single(fact => fact.Label == "Next refresh").Value);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static string Fact(SetupDataDetail detail, string label) => detail.Facts.Single(fact => fact.Label == label).Value;

    private static RuntimeDataState Availability(DataAvailability availability, DateTimeOffset updated, string detail = "Refreshed from 7 endpoints") =>
        new(availability, 5_442, 7, updated, detail);

    private static BackgroundWorkSnapshot Operation(BackgroundWorkState state, DateTimeOffset completed) => new(
        new("data-refresh"),
        OperationId.New(),
        CorrelationId.New(),
        new("catalog-refresh"),
        WorkloadClass.IO,
        WorkPriority.Background,
        state,
        1,
        0,
        completed.AddSeconds(-30),
        completed.AddSeconds(-29),
        completed,
        null);

    private static ApplicationRuntimeSnapshot Snapshot(
        RuntimeDataState data,
        BackgroundWorkSnapshot? operation = null,
        bool isOffline = false)
    {
        var store = new RuntimeStateStore(Options with { Offline = isOffline }, new FixedClock(Now));
        store.Update(current => current with
        {
            IsOffline = isOffline,
            Data = data,
            Supervisor = operation is null
                ? current.Supervisor
                : current.Supervisor with { Operations = ImmutableArray.Create(operation) },
        });
        return store.Current;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeSource : BindableViewModel
    {
        private string _value = string.Empty;

        public string Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }
    }

    private sealed class FakeMonitors(params DisplayDescriptor[] displays) : IMonitorService
    {
        public Task<IReadOnlyList<DisplayDescriptor>> GetDisplaysAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DisplayDescriptor>>(displays);
    }

    private sealed class FakeWindows(WindowDescriptor? window) : IGameWindowLocator
    {
        public Task<WindowDescriptor?> FindAsync(bool developerMode, CancellationToken cancellationToken) =>
            Task.FromResult(window);
    }

    private sealed class FakePlacement(string currentDisplayId) : IDesktopWindowPlacementController
    {
        public event EventHandler? CurrentDisplayChanged;

        public string? CurrentDisplayId { get; private set; } = currentDisplayId;

        public Task MoveToAsync(string displayId, CancellationToken cancellationToken = default)
        {
            CurrentDisplayId = displayId;
            CurrentDisplayChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }
}
