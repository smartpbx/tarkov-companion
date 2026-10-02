using Microsoft.Data.Sqlite;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Confirm sync through the real view model, service, command service and SQLite store (#989).
/// </summary>
/// <remarks>
/// The older tests recorded the command call and never reached the store, so a store guard that
/// refused every screenshot write passed them all, and Clayton's Confirm sync failed with the
/// store's rule text on screen.
/// </remarks>
public sealed class QuestScreenshotSyncConfirmTests
{
    private static readonly string[] FirstWords =
        ["Amber", "Copper", "Silent", "Northern", "Broken", "Hollow", "Crimson", "Frozen"];

    private static readonly string[] SecondWords =
        ["Falcon", "Harbor", "Ledger", "Quarry", "Signal", "Lantern", "Thicket", "Furnace"];

    private static readonly string[] SeriesStems =
        ["Rook Line", "Vesper Trail", "Gravel Oath", "Static Choir", "Marrow Gate",
         "Pylon Debt", "Cinder Vow", "Ashen Tide", "Wicker Toll", "Ochre Drift"];

    [Fact]
    public async Task ConfirmAppliesTheSixtyFourMatchedAndTheTenConfirmedAndProgressShowsThem()
    {
        await using var database = await TempDatabase.CreateAsync();
        var fixture = Fixture(database);
        var single = FirstWords.SelectMany(first => SecondWords.Select(second => $"{first} {second}")).ToArray();
        var viewModel = new QuestScreenshotSyncViewModel(
            fixture.Service, new NoImages(), () => null, TimeProvider.System);

        await viewModel.LoadFixtureAsync(single.Concat(SeriesStems.Select(stem => $"{stem} Part")));

        Assert.Equal(64, viewModel.Matched.Count);
        Assert.Equal(10, viewModel.Ambiguous.Count);
        Assert.Empty(viewModel.Unmatched);
        foreach (var row in viewModel.Ambiguous)
        {
            await Assert.IsType<AsyncDelegateCommand>(row.ConfirmCommand).ExecuteAsync();
        }

        await Assert.IsType<AsyncDelegateCommand>(viewModel.ApplyCommand).ExecuteAsync();

        Assert.Equal("Quest progress synced · 74 changes", viewModel.Status);
        var progress = await fixture.Store.GetAsync(Scope, CancellationToken.None);
        Assert.Equal(74, progress.Tasks.Count);
        Assert.All(progress.Tasks.Values, task =>
        {
            Assert.Equal(RecordedTaskState.Active, task.State);
            Assert.Equal(QuestProgressSources.Screenshot, task.Source);
        });
        Assert.Contains("rook-line-1", progress.Tasks.Keys);
        Assert.All(
            await fixture.Store.GetJournalAsync(Scope, CancellationToken.None),
            change => Assert.Equal(QuestProgressActor.User, change.Actor));

        await viewModel.RefreshOfferAsync();
        Assert.False(viewModel.ShowEmptyOffer);
    }

    [Fact]
    public async Task AnImportStillCannotWriteProgressDirectly()
    {
        await using var database = await TempDatabase.CreateAsync();
        var store = new SqliteQuestProgressStore(database.Factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyAsync(
            new SetTaskStateMutation(
                Scope, "Local", "amber-falcon", RecordedTaskState.Active,
                QuestProgressActor.Import, QuestProgressSources.Screenshot, Guid.NewGuid(), DateTimeOffset.UnixEpoch),
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyAsync(
            new SetTaskStateMutation(
                Scope, "Local", "amber-falcon", RecordedTaskState.Active,
                QuestProgressActor.GameLog, QuestProgressSources.Screenshot, Guid.NewGuid(), DateTimeOffset.UnixEpoch),
            CancellationToken.None));
        Assert.Empty((await store.GetAsync(Scope, CancellationToken.None)).Tasks);
    }

    [Fact]
    public async Task ARefusedWriteShowsPlainWordsNotTheStoreRule()
    {
        await using var database = await TempDatabase.CreateAsync();
        var fixture = Fixture(database, new RefusingStore(new SqliteQuestProgressStore(database.Factory)));
        var viewModel = new QuestScreenshotSyncViewModel(
            fixture.Service, new NoImages(), () => null, TimeProvider.System);
        await viewModel.LoadFixtureAsync(["Amber Falcon"]);

        await Assert.IsType<AsyncDelegateCommand>(viewModel.ApplyCommand).ExecuteAsync();

        Assert.Equal("Quest progress was not synced: something went wrong; try again", viewModel.Status);
        Assert.DoesNotContain("Stage 2", viewModel.Status, StringComparison.Ordinal);
    }

    private static readonly Guid ProfileId = Guid.Parse("8c2b8f63-3f2f-4c8b-9b55-0d7f3cc8b9a1");
    private static readonly QuestProfileScope Scope = new(ProfileId, GameMode.Regular, "legacy");

    private static (QuestScreenshotSyncService Service, IQuestProgressStore Store) Fixture(
        TempDatabase database,
        IQuestProgressStore? store = null)
    {
        var tasks = FirstWords
            .SelectMany(first => SecondWords.Select(second => Quest($"{first} {second}")))
            .Concat(SeriesStems.SelectMany(stem => new[]
            {
                Quest($"{stem} - Part 1", $"{Slug(stem)}-1"),
                Quest($"{stem} - Part 2", $"{Slug(stem)}-2"),
            }))
            .ToArray();
        var catalog = new StubCatalog(new QuestCatalogSnapshot(
            new(
                "fixture", "https://example.invalid/tasks", GameMode.Regular, "regular", "en",
                new('a', 64), new('b', 64), null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            tasks,
            "{}",
            "{}"));
        var profiles = new StubProfiles();
        var options = new QuestTrackingOptions();
        store ??= new SqliteQuestProgressStore(database.Factory);
        var commands = new QuestProgressCommandService(profiles, catalog, store, options);
        var service = new QuestScreenshotSyncService(
            profiles, catalog, store, commands, new NoOcr(), new NoColumn(),
            new QuestListMatcher(), new QuestListMatchMerger(), new QuestHistoryInference(), options);
        return (service, store);
    }

    private static string Slug(string name) => name.ToLowerInvariant().Replace(' ', '-');

    private static QuestTaskDefinition Quest(string name, string? id = null) => new(
        id ?? Slug(name), name, null, null, null, null, null, null, null, null, null, null, null,
        [], [], [], [], "{}");

    private sealed class RefusingStore(IQuestProgressStore inner) : IQuestProgressStore
    {
        public Task<QuestProgressSnapshot> GetAsync(QuestProfileScope scope, CancellationToken cancellationToken) =>
            inner.GetAsync(scope, cancellationToken);

        public Task<QuestProgressCommandResult> ApplyAsync(QuestProgressMutation mutation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Stage 2 progress accepts manual commands and the game's own log observations only; " +
                "imports cannot mutate it, and an actor must match its source.");

        public Task<IReadOnlyList<QuestProgressChange>> GetJournalAsync(QuestProfileScope scope, CancellationToken cancellationToken) =>
            inner.GetJournalAsync(scope, cancellationToken);
    }

    private sealed class StubCatalog(QuestCatalogSnapshot value) : IQuestCatalog
    {
        public Task<QuestCatalogSnapshot?> GetAsync(GameMode gameMode, string language, CancellationToken cancellationToken) =>
            Task.FromResult<QuestCatalogSnapshot?>(value);
    }

    private sealed class StubProfiles : IPlayerProfileService
    {
        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PlayerProfile(
                ProfileId, "Local", GameMode.Regular, 1, Faction.Unknown, null,
                new Dictionary<string, int>(), new HashSet<string>(), new Dictionary<string, int>(),
                new Dictionary<string, int>(), new HashSet<string>(), new Dictionary<string, int>(),
                new Dictionary<string, EventItemState>(), new Dictionary<string, string>(), DateTimeOffset.UnixEpoch));

        public Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoImages : IQuestScreenshotImageSource
    {
        public Task<QuestScreenshotImageLoad> LoadFilesAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<QuestScreenshotImageLoad> LoadRecentAsync(string? screenshotRoot, DateTimeOffset takenSinceUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoOcr : IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(CapturedImage image, OcrRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoColumn : IQuestTaskColumnRegionDetector
    {
        public QuestScreenshotTextRegion Detect(CapturedImage image) => throw new NotSupportedException();
    }

    private sealed class TempDatabase : IAsyncDisposable
    {
        private readonly string _path;

        private TempDatabase(string path)
        {
            _path = path;
            Factory = new SqliteConnectionFactory(new(path));
        }

        public SqliteConnectionFactory Factory { get; }

        public static async Task<TempDatabase> CreateAsync()
        {
            var database = new TempDatabase(Path.Combine(Path.GetTempPath(), $"quest-sync-{Guid.NewGuid():N}.db"));
            await new SqliteMigrationRunner(database.Factory).ApplyAsync(CancellationToken.None);
            return database;
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { _path, _path + "-shm", _path + "-wal" })
            {
                File.Delete(file);
            }

            return ValueTask.CompletedTask;
        }
    }
}
