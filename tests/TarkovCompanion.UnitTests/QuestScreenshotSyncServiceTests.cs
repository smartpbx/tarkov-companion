using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests;

public sealed class QuestScreenshotSyncServiceTests
{
    [Fact]
    public async Task AnalysisKeepsThreeOutcomeGroupsAndWritesNothing()
    {
        var fixture = Fixture();

        var preview = await fixture.Service.AnalyzeAsync([Image("tasks")], CancellationToken.None);

        Assert.Equal("fixture OCR", preview.OcrEngine);
        Assert.Single(preview.Lines, line => line.Kind == QuestListLineKind.Matched);
        Assert.Single(preview.Lines, line => line.Kind == QuestListLineKind.Ambiguous);
        Assert.Single(preview.Lines, line => line.Kind == QuestListLineKind.Unmatched);
        Assert.Equal(1, preview.History.ActiveChanges);
        Assert.Equal(1, preview.History.EarlierQuestChanges);
        Assert.Empty(fixture.Commands.Calls);
    }

    [Fact]
    public async Task OverlappingScreenshotsShowEachQuestAndLineOnce()
    {
        var fixture = Fixture();

        var preview = await fixture.Service.AnalyzeAsync(
            [Image("first-screenful"), Image("overlapping-screenful")],
            CancellationToken.None);

        Assert.Equal(2, preview.ImageCount);
        Assert.Equal(3, preview.Lines.Count);
        Assert.Single(preview.Lines, line => line.Kind == QuestListLineKind.Matched);
        Assert.Single(preview.Lines, line => line.Kind == QuestListLineKind.Ambiguous);
        Assert.Single(preview.Lines, line => line.Kind == QuestListLineKind.Unmatched);
    }

    [Fact]
    public async Task OperationalNamesAreNeverAppliedAgainstTheCatalogByNameAlone()
    {
        var fixture = Fixture(new OperationalFullFrame());

        var preview = await fixture.Service.AnalyzeAsync([Image("operational")], CancellationToken.None);

        Assert.Empty(preview.ConfirmedTaskIds);
        Assert.Equal(3, preview.Lines.Count(line => line.Kind == QuestListLineKind.Unmatched));
        Assert.Empty(preview.History.Changes);
    }

    [Fact]
    public async Task ConfirmationRechecksThenAppliesPrerequisiteBeforeActiveQuest()
    {
        var fixture = Fixture();
        var preview = await fixture.Service.AnalyzeAsync([Image("tasks")], CancellationToken.None);

        var applied = await fixture.Service.ApplyAsync(preview.ConfirmedTaskIds, CancellationToken.None);

        Assert.Equal(2, applied.Changed);
        Assert.Collection(
            fixture.Commands.Calls,
            call => Assert.Equal(("debut", RecordedTaskState.Completed), (call.TaskId, call.State)),
            call => Assert.Equal(("shortage", RecordedTaskState.Active), (call.TaskId, call.State)));
        Assert.All(fixture.Commands.Calls, call =>
        {
            Assert.Equal(QuestProgressActor.Import, call.Actor);
            Assert.Equal("ScreenshotSync", call.Source);
        });
    }

    [Fact]
    public async Task AFinishedQuestRecordedAfterPreviewIsNotMovedBackwardsOnConfirm()
    {
        var fixture = Fixture();
        var preview = await fixture.Service.AnalyzeAsync([Image("tasks")], CancellationToken.None);
        fixture.Store.Snapshot = Progress(("shortage", RecordedTaskState.Completed));

        await fixture.Service.ApplyAsync(preview.ConfirmedTaskIds, CancellationToken.None);

        Assert.DoesNotContain(fixture.Commands.Calls, call => call.TaskId == "shortage");
    }

    [Fact]
    public async Task CancellingTheViewModelPreviewChangesNothing()
    {
        var fixture = Fixture();
        var viewModel = new QuestScreenshotSyncViewModel(
            fixture.Service,
            new StubImages(),
            () => "unused",
            TimeProvider.System)
        {
            ChooseFiles = () => Task.FromResult<IReadOnlyList<string>>(["tasks.png"]),
        };

        await Assert.IsType<AsyncDelegateCommand>(viewModel.PickCommand).ExecuteAsync();
        Assert.True(viewModel.HasPreview);

        viewModel.CancelCommand.Execute(null);

        Assert.False(viewModel.HasPreview);
        Assert.Equal("Sync cancelled. Nothing changed.", viewModel.Status);
        Assert.Empty(fixture.Commands.Calls);
    }

    [Fact]
    public async Task EmptyProgressOffersOnboardingUntilDismissed()
    {
        var fixture = Fixture();
        var viewModel = new QuestScreenshotSyncViewModel(
            fixture.Service,
            new StubImages(),
            () => "unused",
            TimeProvider.System);

        await viewModel.RefreshOfferAsync();
        Assert.True(viewModel.ShowEmptyOffer);

        viewModel.DismissOfferCommand.Execute(null);
        Assert.False(viewModel.ShowEmptyOffer);
    }

    [Fact]
    public async Task PassiveOfferOpensWithTheExactBurstPreloadedAndStillWritesNothing()
    {
        var fixture = Fixture();
        var images = new StubImages();
        var bursts = new QuestScreenshotBurstCollector();
        var runtime = Runtime();
        V2RouteId? navigated = null;
        var viewModel = new QuestScreenshotSyncViewModel(
            fixture.Service,
            images,
            () => "unused",
            TimeProvider.System,
            bursts,
            runtime);
        var workspace = new V2SetupWorkspaceViewModel(null, null, null, route => navigated = route);
        workspace.AttachQuestSync(viewModel);
        bursts.Observe("tasks-1.png", DateTimeOffset.UnixEpoch, raidActive: false);
        bursts.Observe("tasks-2.png", DateTimeOffset.UnixEpoch.AddSeconds(30), raidActive: false);

        Assert.True(viewModel.ShowsPassiveOffer);
        Assert.Equal("Quest list seen · 2 screenshots", viewModel.PassiveOfferText);
        await Assert.IsType<AsyncDelegateCommand>(viewModel.ReviewPassiveOfferCommand).ExecuteAsync();

        Assert.Equal(V2Routes.Setup, navigated);
        Assert.True(workspace.IsProfileProgressSelected);
        Assert.Equal(["tasks-1.png", "tasks-2.png"], images.Paths);
        Assert.True(viewModel.HasPreview);
        Assert.Equal("Read 2 screenshots · fixture OCR", viewModel.Status);
        Assert.False(viewModel.ShowsPassiveOffer);
        Assert.Empty(fixture.Commands.Calls);
    }

    [Fact]
    public void ExistingOfferIsHiddenAsSoonAsARaidStarts()
    {
        var fixture = Fixture();
        var bursts = new QuestScreenshotBurstCollector();
        var runtime = Runtime();
        var viewModel = new QuestScreenshotSyncViewModel(
            fixture.Service,
            new StubImages(),
            () => "unused",
            TimeProvider.System,
            bursts,
            runtime);
        bursts.Observe("tasks.png", DateTimeOffset.UnixEpoch, raidActive: false);
        Assert.True(viewModel.ShowsPassiveOffer);

        runtime.Update(current => current with
        {
            Raid = current.Raid with { State = RaidLifecycleState.InRaid },
        });

        Assert.False(viewModel.ShowsPassiveOffer);
    }

    private static RuntimeStateStore Runtime() => new(new RuntimeOptions(
        DemoMode: false,
        Offline: true,
        GameMode: GameMode.Regular,
        Language: "en",
        DataFreshFor: TimeSpan.FromHours(9),
        RefreshTimeout: TimeSpan.FromMinutes(5)));

    private static TestFixture Fixture(IQuestTaskColumnRegionDetector? detector = null)
    {
        var catalog = Catalog(
            Quest("debut", "Debut"),
            Quest("shortage", "Shortage", new QuestTaskRequirement(0, "debut", ["complete"], "{}")),
            Quest("gunsmith-1", "Gunsmith - Part 1"),
            Quest("gunsmith-2", "Gunsmith - Part 2"));
        var store = new StubStore(Progress());
        var commands = new RecordingCommands();
        var service = new QuestScreenshotSyncService(
            new StubProfiles(),
            new StubCatalog(catalog),
            store,
            commands,
            new FixtureOcr(),
            detector ?? new FullFrameTaskColumn(),
            new QuestListMatcher(),
            new QuestListMatchMerger(),
            new QuestHistoryInference(),
            new QuestTrackingOptions());
        return new(service, store, commands);
    }

    private static CapturedImage Image(string source) => new(
        new byte[4], 1, 1, 4, PixelFormat.Bgra8888, DateTimeOffset.UnixEpoch, source);

    private static QuestCatalogSnapshot Catalog(params QuestTaskDefinition[] tasks) => new(
        new(
            "fixture", "https://example.invalid/tasks", GameMode.Regular, "regular", "en",
            new('a', 64), new('b', 64), null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
        tasks,
        "{}",
        "{}");

    private static QuestProgressSnapshot Progress(params (string Id, RecordedTaskState State)[] tasks)
    {
        var scope = new QuestProfileScope(Guid.Empty, GameMode.Regular, "legacy");
        return new(
            scope,
            0,
            tasks.ToDictionary(
                task => task.Id,
                task => new RecordedTaskProgress(task.Id, task.State, "fixture", 1, DateTimeOffset.UnixEpoch),
                StringComparer.Ordinal),
            new Dictionary<string, RecordedObjectiveProgress>(StringComparer.Ordinal),
            [],
            []);
    }

    private static QuestTaskDefinition Quest(
        string id,
        string name,
        params QuestTaskRequirement[] requirements) => new(
        id, name, null, null, null, null, null, null, null, null, null, null, null,
        [], requirements, [], [], "{}");

    private sealed record TestFixture(
        QuestScreenshotSyncService Service,
        StubStore Store,
        RecordingCommands Commands);

    private sealed class StubImages : IQuestScreenshotImageSource
    {
        public List<string> Paths { get; } = [];

        public Task<QuestScreenshotImageLoad> LoadFilesAsync(
            IReadOnlyCollection<string> paths,
            CancellationToken cancellationToken)
        {
            Paths.AddRange(paths);
            return Task.FromResult(new QuestScreenshotImageLoad(paths.Select(Image).ToArray(), 0));
        }

        public Task<QuestScreenshotImageLoad> LoadRecentAsync(
            string? screenshotRoot,
            DateTimeOffset takenSinceUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixtureOcr : IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(
            CapturedImage image,
            OcrRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new OcrResult(
                [
                    new("Shortage", new(0, 0, 100, 20), null),
                    new("Gunsmith Part", new(0, 20, 100, 20), null),
                    new("CHARACTER TASKS", new(0, 40, 100, 20), null),
                ],
                TimeSpan.FromMilliseconds(10),
                "fixture OCR"));
    }

    private sealed class FullFrameTaskColumn : IQuestTaskColumnRegionDetector
    {
        public QuestScreenshotTextRegion Detect(CapturedImage image) =>
            new(QuestScreenshotLayout.SideTaskList, new PixelRect(0, 0, image.Width, image.Height));
    }

    private sealed class OperationalFullFrame : IQuestTaskColumnRegionDetector
    {
        public QuestScreenshotTextRegion Detect(CapturedImage image) =>
            new(QuestScreenshotLayout.OperationalTaskList, new PixelRect(0, 0, image.Width, image.Height));
    }

    private sealed class StubCatalog(QuestCatalogSnapshot value) : IQuestCatalog
    {
        public Task<QuestCatalogSnapshot?> GetAsync(
            GameMode gameMode,
            string language,
            CancellationToken cancellationToken) => Task.FromResult<QuestCatalogSnapshot?>(value);
    }

    private sealed class StubStore(QuestProgressSnapshot snapshot) : IQuestProgressStore
    {
        public QuestProgressSnapshot Snapshot { get; set; } = snapshot;

        public Task<QuestProgressSnapshot> GetAsync(QuestProfileScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot);

        public Task<QuestProgressCommandResult> ApplyAsync(
            QuestProgressMutation mutation,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<QuestProgressChange>> GetJournalAsync(
            QuestProfileScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuestProgressChange>>([]);
    }

    private sealed class RecordingCommands : IQuestProgressCommandService
    {
        public List<(string TaskId, RecordedTaskState State, QuestProgressActor Actor, string Source)> Calls { get; } = [];

        public Task<QuestProgressCommandResult> SetTaskStateAsync(
            QuestProfileScope scope,
            string taskId,
            RecordedTaskState state,
            CancellationToken cancellationToken) =>
            SetTaskStateAsync(scope, taskId, state, QuestProgressActor.User, "Manual", cancellationToken);

        public Task<QuestProgressCommandResult> SetTaskStateAsync(
            QuestProfileScope scope,
            string taskId,
            RecordedTaskState state,
            QuestProgressActor actor,
            string source,
            CancellationToken cancellationToken)
        {
            Calls.Add((taskId, state, actor, source));
            return Task.FromResult(new QuestProgressCommandResult(Guid.NewGuid(), Calls.Count, true));
        }

        public Task<QuestProgressCommandResult> SetObjectiveProgressAsync(
            QuestProfileScope scope,
            string objectiveId,
            RecordedObjectiveState state,
            decimal? count,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<QuestProgressCommandResult> SetItemHoldingAsync(
            QuestProfileScope scope,
            string itemId,
            bool foundInRaid,
            int? count,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<QuestProgressCommandResult> SetPinAsync(
            QuestProfileScope scope,
            QuestPinTargetKind targetKind,
            string targetId,
            bool isPinned,
            int sortOrder,
            string? note,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubProfiles : IPlayerProfileService
    {
        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PlayerProfile(
                Guid.Empty, "Local", GameMode.Regular, 1, Faction.Unknown, null,
                new Dictionary<string, int>(), new HashSet<string>(), new Dictionary<string, int>(),
                new Dictionary<string, int>(), new HashSet<string>(), new Dictionary<string, int>(),
                new Dictionary<string, EventItemState>(), new Dictionary<string, string>(), DateTimeOffset.UnixEpoch));

        public Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
