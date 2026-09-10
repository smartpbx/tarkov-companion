using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.Profile;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.IntegrationTests;

public sealed class QuestProgressPersistenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-10T12:00:00Z");
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    [Fact]
    public async Task ManualProgressAndReadModelsSurviveOfflineRestart()
    {
        await using var database = await TestDatabase.CreateAsync();
        var profilePath = Path.Combine(database.Directory, "profile.json");
        var profile = Profile(GameMode.Regular, "generation-a");
        using (var profileWriter = new JsonFilePlayerProfileService(new(profilePath)))
        {
            await profileWriter.SaveAsync(profile, CancellationToken.None);
        }

        await new SqliteDataRefreshRepository(database.Factory).RefreshTasksAsync(
            await CatalogAsync(GameMode.Regular),
            CancellationToken.None);
        var clock = new ManualTimeProvider(Now);
        using (var profileService = new JsonFilePlayerProfileService(new(profilePath), clock))
        {
            var store = new SqliteQuestProgressStore(database.Factory);
            var commands = new QuestProgressCommandService(
                profileService,
                new SqliteQuestCatalog(database.Factory),
                store,
                new(),
                clock);
            var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
            await commands.SetTaskStateAsync(scope, "task-contract", RecordedTaskState.Active, CancellationToken.None);
            await commands.SetObjectiveProgressAsync(
                scope,
                "objective-find-item",
                RecordedObjectiveState.InProgress,
                1,
                CancellationToken.None);
            await commands.SetItemHoldingAsync(scope, "item-a", false, 7, CancellationToken.None);
            await commands.SetItemHoldingAsync(scope, "item-a", true, 2, CancellationToken.None);
            await commands.SetPinAsync(
                scope,
                QuestPinTargetKind.Task,
                "task-contract",
                true,
                10,
                "Focus next",
                CancellationToken.None);
        }

        using var reloadedProfile = new JsonFilePlayerProfileService(new(profilePath), new ManualTimeProvider(Now));
        var reloadedStore = new SqliteQuestProgressStore(database.Factory);
        var read = new QuestReadService(
            reloadedProfile,
            new SqliteQuestCatalog(database.Factory),
            reloadedStore,
            new(),
            new(),
            new ManualTimeProvider(Now));
        var reloaded = await reloadedProfile.GetActiveAsync(CancellationToken.None);
        var reloadedScope = new QuestProfileScope(reloaded.Id, reloaded.GameMode, reloaded.ProfileGeneration);

        var board = await read.GetQuestBoardAsync(reloadedScope, CancellationToken.None);
        var task = Assert.Single(board.Tasks, value => value.TaskId == "task-contract");
        Assert.Equal(RecordedTaskState.Active, task.RecordedState);
        Assert.True(task.IsPinned);
        Assert.Equal(RecordedObjectivesSatisfaction.Indeterminate, task.RecordedObjectivesSatisfied);

        var needs = await read.GetItemNeedsAsync(reloadedScope, "item-a", CancellationToken.None);
        var need = Assert.Single(needs.Requirements, value => value.ObjectiveId == "objective-find-item");
        Assert.Equal(2, need.RemainingCount);
        Assert.True(need.FoundInRaidRequired);
        Assert.Equal(2, needs.FoundInRaidHeldCount);
        Assert.Equal(7, needs.NonFoundInRaidHeldCount);

        var journal = await reloadedStore.GetJournalAsync(reloadedScope, CancellationToken.None);
        Assert.Equal(5, journal.Count);
        Assert.All(journal, change => Assert.Equal(QuestProgressActor.User, change.Actor));
        Assert.All(journal, change => Assert.Equal(change.PreviousValueJson, change.InverseValueJson));
    }

    [Fact]
    public async Task StateAndJournalRollBackTogetherWhenJournalInsertFails()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var connection = await database.Factory.OpenAsync(CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER test_reject_quest_journal
                BEFORE INSERT ON quest_progress_journal
                BEGIN
                    SELECT RAISE(ABORT, 'forced journal failure');
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var profile = Profile(GameMode.Regular, "generation-a");
        var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
        var store = new SqliteQuestProgressStore(database.Factory);

        await Assert.ThrowsAsync<SqliteException>(() => store.ApplyAsync(
            TaskMutation(scope, RecordedTaskState.Active),
            CancellationToken.None));

        var snapshot = await store.GetAsync(scope, CancellationToken.None);
        Assert.Equal(0, snapshot.Revision);
        Assert.Empty(snapshot.Tasks);
        Assert.Empty(await store.GetJournalAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task JournalIsAppendOnlyAndSecondCommandStoresItsInverse()
    {
        await using var database = await TestDatabase.CreateAsync();
        var profile = Profile(GameMode.Regular, "generation-a");
        var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
        var store = new SqliteQuestProgressStore(database.Factory);
        await store.ApplyAsync(TaskMutation(scope, RecordedTaskState.Active), CancellationToken.None);
        await store.ApplyAsync(TaskMutation(scope, RecordedTaskState.Completed), CancellationToken.None);

        var journal = await store.GetJournalAsync(scope, CancellationToken.None);
        Assert.Equal(2, journal.Count);
        Assert.Equal(journal[0].NewValueJson, journal[1].InverseValueJson);

        await using var connection = await database.Factory.OpenAsync(CancellationToken.None);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "UPDATE quest_progress_journal SET assertion_source = 'changed' WHERE id = 1;"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "DELETE FROM quest_progress_journal WHERE id = 1;"));
    }

    [Fact]
    public async Task ExactModeAndGenerationScopesNeverShareProgress()
    {
        await using var database = await TestDatabase.CreateAsync();
        var profileId = Guid.Parse("c32f4924-e167-43d5-a418-66f9c4d29aa4");
        var scopes = new[]
        {
            new QuestProfileScope(profileId, GameMode.Regular, "wipe-a"),
            new QuestProfileScope(profileId, GameMode.Pve, "persistent-a"),
            new QuestProfileScope(profileId, GameMode.PvpSeason, "season-1"),
            new QuestProfileScope(profileId, GameMode.Regular, "wipe-b"),
        };
        var states = new[]
        {
            RecordedTaskState.Active,
            RecordedTaskState.Completed,
            RecordedTaskState.Failed,
            RecordedTaskState.NotStarted,
        };
        var store = new SqliteQuestProgressStore(database.Factory);
        for (var index = 0; index < scopes.Length; index++)
        {
            await store.ApplyAsync(TaskMutation(scopes[index], states[index]), CancellationToken.None);
            await store.ApplyAsync(new SetObjectiveProgressMutation(
                scopes[index],
                "Quest profile",
                "objective-find-item",
                RecordedObjectiveState.InProgress,
                index,
                QuestProgressActor.User,
                "Manual",
                Guid.NewGuid(),
                Now), CancellationToken.None);
            await store.ApplyAsync(new SetItemHoldingMutation(
                scopes[index],
                "Quest profile",
                "item-a",
                true,
                index,
                QuestProgressActor.User,
                "Manual",
                Guid.NewGuid(),
                Now), CancellationToken.None);
            await store.ApplyAsync(new SetQuestPinMutation(
                scopes[index],
                "Quest profile",
                QuestPinTargetKind.Task,
                "task-contract",
                true,
                index,
                null,
                QuestProgressActor.User,
                "Manual",
                Guid.NewGuid(),
                Now), CancellationToken.None);
        }

        for (var index = 0; index < scopes.Length; index++)
        {
            var snapshot = await store.GetAsync(scopes[index], CancellationToken.None);
            Assert.Equal(states[index], Assert.Single(snapshot.Tasks).Value.State);
            Assert.Equal(index, Assert.Single(snapshot.Objectives).Value.Count);
            Assert.Equal(index, Assert.Single(snapshot.ItemHoldings).Count);
            Assert.Equal(index, Assert.Single(snapshot.Pins).SortOrder);
            Assert.Equal(4, (await store.GetJournalAsync(scopes[index], CancellationToken.None)).Count);
        }
    }

    [Theory]
    [InlineData(QuestProgressActor.Import, "Project import")]
    [InlineData(QuestProgressActor.User, "Screenshot observation")]
    public async Task NonManualSourcesCannotMutateProgress(QuestProgressActor actor, string source)
    {
        await using var database = await TestDatabase.CreateAsync();
        var profile = Profile(GameMode.Regular, "generation-a");
        var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
        var store = new SqliteQuestProgressStore(database.Factory);
        var mutation = new SetTaskStateMutation(
            scope,
            profile.Name,
            "task-contract",
            RecordedTaskState.Active,
            actor,
            source,
            Guid.NewGuid(),
            Now);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ApplyAsync(mutation, CancellationToken.None));
        Assert.Empty((await store.GetAsync(scope, CancellationToken.None)).Tasks);
    }

    [Fact]
    public async Task CommandRejectsAStaleGenerationBeforeCatalogOrProgressAccess()
    {
        await using var database = await TestDatabase.CreateAsync();
        var profilePath = Path.Combine(database.Directory, "profile.json");
        var profile = Profile(GameMode.Regular, "current-generation");
        using var profileService = new JsonFilePlayerProfileService(new(profilePath));
        await profileService.SaveAsync(profile, CancellationToken.None);
        var store = new SqliteQuestProgressStore(database.Factory);
        var commands = new QuestProgressCommandService(
            profileService,
            new SqliteQuestCatalog(database.Factory),
            store,
            new());
        var staleScope = new QuestProfileScope(profile.Id, profile.GameMode, "previous-generation");

        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.SetTaskStateAsync(
            staleScope,
            "task-contract",
            RecordedTaskState.Active,
            CancellationToken.None));

        Assert.Empty((await store.GetAsync(staleScope, CancellationToken.None)).Tasks);
        Assert.Empty(await store.GetJournalAsync(staleScope, CancellationToken.None));
    }

    [Fact]
    public async Task NegativeObjectiveAndHoldingCountsLeaveNoProgress()
    {
        await using var database = await TestDatabase.CreateAsync();
        var profile = Profile(GameMode.Regular, "generation-a");
        var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
        var store = new SqliteQuestProgressStore(database.Factory);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ApplyAsync(
            new SetObjectiveProgressMutation(
                scope,
                profile.Name,
                "objective-find-item",
                RecordedObjectiveState.InProgress,
                -1,
                QuestProgressActor.User,
                "Manual",
                Guid.NewGuid(),
                Now),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ApplyAsync(
            new SetItemHoldingMutation(
                scope,
                profile.Name,
                "item-a",
                true,
                -1,
                QuestProgressActor.User,
                "Manual",
                Guid.NewGuid(),
                Now),
            CancellationToken.None));

        var snapshot = await store.GetAsync(scope, CancellationToken.None);
        Assert.Empty(snapshot.Objectives);
        Assert.Empty(snapshot.ItemHoldings);
        Assert.Empty(await store.GetJournalAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task CatalogRemovalProducesOrphansWithoutRewritingProgressAndLaterResolvesThem()
    {
        await using var database = await TestDatabase.CreateAsync();
        var profilePath = Path.Combine(database.Directory, "profile.json");
        var profile = Profile(GameMode.Regular, "generation-a");
        using var profileService = new JsonFilePlayerProfileService(new(profilePath));
        await profileService.SaveAsync(profile, CancellationToken.None);
        var catalog = await CatalogAsync(GameMode.Regular);
        var refresh = new SqliteDataRefreshRepository(database.Factory);
        await refresh.RefreshTasksAsync(catalog, CancellationToken.None);
        var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
        var store = new SqliteQuestProgressStore(database.Factory);
        await store.ApplyAsync(TaskMutation(scope, RecordedTaskState.Active), CancellationToken.None);
        await store.ApplyAsync(new SetObjectiveProgressMutation(
            scope,
            profile.Name,
            "objective-find-item",
            RecordedObjectiveState.InProgress,
            1,
            QuestProgressActor.User,
            "Manual",
            Guid.NewGuid(),
            Now), CancellationToken.None);

        await refresh.RefreshTasksAsync(
            catalog with
            {
                Provenance = catalog.Provenance with
                {
                    PayloadSha256 = "empty-hash",
                    TranslatedPayloadSha256 = "empty-translated-hash",
                    FetchedUtc = Now.AddMinutes(1),
                    ValidatedUtc = Now.AddMinutes(1),
                },
                Tasks = [],
                RawSourceJson = "{}",
                TranslatedSourceJson = "{}",
            },
            CancellationToken.None);
        var read = new QuestReadService(
            profileService,
            new SqliteQuestCatalog(database.Factory),
            store,
            new(),
            new());
        var orphaned = await read.GetQuestBoardAsync(scope, CancellationToken.None);
        Assert.Empty(orphaned.Tasks);
        Assert.Contains(orphaned.OrphanedProgress, value =>
            value.EntityKind == QuestProgressEntityKind.Task && value.ExternalId == "task-contract");
        Assert.Contains(orphaned.OrphanedProgress, value =>
            value.EntityKind == QuestProgressEntityKind.Objective && value.ExternalId == "objective-find-item");

        await refresh.RefreshTasksAsync(catalog, CancellationToken.None);
        var resolved = await read.GetQuestBoardAsync(scope, CancellationToken.None);
        Assert.DoesNotContain(resolved.OrphanedProgress, value =>
            value.ExternalId is "task-contract" or "objective-find-item");
        Assert.Equal(RecordedTaskState.Active, Assert.Single(resolved.Tasks).RecordedState);
        Assert.Equal(2, (await store.GetAsync(scope, CancellationToken.None)).Revision);
    }

    private static SetTaskStateMutation TaskMutation(
        QuestProfileScope scope,
        RecordedTaskState state) => new(
        scope,
        "Quest profile",
        "task-contract",
        state,
        QuestProgressActor.User,
        "Manual",
        Guid.NewGuid(),
        Now);

    private static PlayerProfile Profile(GameMode mode, string generation) => new(
        Guid.Parse("940d35d5-47a2-4a25-afb9-94145166d65b"),
        "Quest profile",
        mode,
        25,
        Faction.Usec,
        null,
        new Dictionary<string, int>(),
        new HashSet<string>(),
        new Dictionary<string, int>(),
        new Dictionary<string, int>(),
        new HashSet<string>(),
        new Dictionary<string, int>(),
        new Dictionary<string, EventItemState>(),
        new Dictionary<string, string>(),
        Now,
        generation);

    private static async Task<QuestCatalogSnapshot> CatalogAsync(GameMode mode)
    {
        var json = await FixtureJson.ReadAsync("tasks-contract.json");
        var envelope = JsonSerializer.Deserialize<TarkovDevEnvelope<TarkovDevTasksData>>(json, SerializerOptions)
            ?? throw new InvalidDataException("Quest fixture was null.");
        var response = new TarkovDevResponse<TarkovDevTasksData>(
            envelope.Data,
            json,
            Now,
            false,
            false,
            "\"stage-2\"",
            Now,
            json);
        return new TarkovDevQuestCatalogNormalizer().Normalize(response, mode, "en", Now);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _directory;

        private TestDatabase(string directory, SqliteConnectionFactory factory)
        {
            _directory = directory;
            Factory = factory;
        }

        public string Directory => _directory;

        public SqliteConnectionFactory Factory { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"tarkov-quest-progress-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "progress.db");
            var factory = new SqliteConnectionFactory(new(path));
            await new SqliteMigrationRunner(factory).ApplyAsync(CancellationToken.None);
            return new(directory, factory);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(_directory))
            {
                System.IO.Directory.Delete(_directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
