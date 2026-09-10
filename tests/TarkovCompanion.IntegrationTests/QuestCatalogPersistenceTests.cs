using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.IntegrationTests;

public sealed class QuestCatalogPersistenceTests
{
    private static readonly DateTimeOffset FetchedUtc = new(2026, 9, 10, 3, 23, 23, TimeSpan.Zero);
    private static readonly DateTimeOffset ValidatedUtc = new(2026, 9, 10, 5, 14, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    [Fact]
    public async Task ContractCatalogRoundTripsFidelityAndIdenticalRefreshIsIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var json = await FixtureJson.ReadAsync("tasks-contract.json");
        var catalog = ParseAndNormalize(json, GameMode.Regular);
        var refresh = new SqliteDataRefreshRepository(database.Factory);

        await refresh.RefreshTasksAsync(catalog, TestContext.Current.CancellationToken);
        await refresh.RefreshTasksAsync(catalog, TestContext.Current.CancellationToken);

        var loaded = await new SqliteQuestCatalog(database.Factory).GetAsync(
            GameMode.Regular,
            "EN",
            TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal(json, loaded.RawSourceJson);
        Assert.Equal("\"contract-v1\"", loaded.Provenance.ETag);
        var task = Assert.Single(loaded.Tasks);
        Assert.Equal(["complete", "active", "future-status"], Assert.Single(task.Requirements).RequiredStatuses);
        Assert.Equal(2, task.FailureConditions.Count);
        Assert.Single(task.Objectives, objective => objective.IsUnsupported);

        var mark = Assert.Single(task.Objectives, objective => objective.Kind == QuestObjectiveKind.Mark);
        var zone = Assert.Single(mark.Zones);
        Assert.Equal("map-one", zone.MapId);
        Assert.Equal(10.5, zone.Position?.X);
        Assert.Equal(20.25, zone.TerrainElevation);
        Assert.Equal(3, zone.Outline.Count);
        Assert.Contains("futureGeometryField", zone.RawSourceJson, StringComparison.Ordinal);

        var questItem = Assert.Single(task.Objectives, objective => objective.Kind == QuestObjectiveKind.FindQuestItem);
        Assert.Equal(2, questItem.Zones.Count);
        Assert.Equal(["map-one", "map-two"], questItem.Zones.Select(value => value.MapId));

        var itemObjective = Assert.Single(task.Objectives, objective => objective.Kind == QuestObjectiveKind.FindItem);
        Assert.Equal(2, itemObjective.ItemTargets.Count);
        Assert.All(itemObjective.ItemTargets, target => Assert.Equal(3, target.TargetCount));
        Assert.All(itemObjective.ItemTargets, target => Assert.True(target.FoundInRaidRequired));
        Assert.Equal(1, await CountAsync(database.Factory, "quest_catalog_snapshots"));
        Assert.Equal(1, await CountAsync(database.Factory, "quest_catalog_tasks"));
        Assert.Equal(23, await CountAsync(database.Factory, "quest_catalog_objectives"));
    }

    [Fact]
    public async Task TranslatedCatalogKeepsTheUntranslatedRawSourceDocument()
    {
        var client = new TarkovDevJsonClient(
            new HttpClient(new FixtureApiHandler()),
            new InMemoryTarkovDevResponseCache(),
            new DataTranslationService(),
            new()
            {
                BaseAddress = new("https://fixture.invalid/"),
                InitialRetryDelay = TimeSpan.Zero,
                MaxAttempts = 1,
            },
            new ManualTimeProvider(FetchedUtc));

        var response = await client.GetTasksAsync(
            GameMode.Regular,
            "en",
            TestContext.Current.CancellationToken);
        var catalog = new TarkovDevQuestCatalogNormalizer().Normalize(
            response,
            GameMode.Regular,
            "en",
            ValidatedUtc);

        Assert.Equal("Shortage", Assert.Single(catalog.Tasks).Name);
        Assert.Contains("task-001 Name", catalog.RawSourceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Shortage", catalog.RawSourceJson, StringComparison.Ordinal);
        Assert.Contains("Shortage", catalog.TranslatedSourceJson, StringComparison.Ordinal);
        Assert.NotEqual(catalog.Provenance.PayloadSha256, catalog.Provenance.TranslatedPayloadSha256);
    }

    [Fact]
    public async Task ModeCatalogsCoexistAndFailedReplacementRollsBack()
    {
        await using var database = await TestDatabase.CreateAsync();
        var json = await FixtureJson.ReadAsync("tasks-contract.json");
        var regular = ParseAndNormalize(json, GameMode.Regular);
        var pve = ParseAndNormalize(json, GameMode.Pve);
        var refresh = new SqliteDataRefreshRepository(database.Factory);
        await refresh.RefreshTasksAsync(regular, TestContext.Current.CancellationToken);
        await refresh.RefreshTasksAsync(pve, TestContext.Current.CancellationToken);

        Assert.Equal(2, await CountAsync(database.Factory, "quest_catalog_snapshots"));
        Assert.Equal(2, await CountAsync(database.Factory, "quest_catalog_tasks"));
        Assert.NotNull(await new SqliteQuestCatalog(database.Factory).GetAsync(
            GameMode.Regular,
            "en",
            TestContext.Current.CancellationToken));
        Assert.NotNull(await new SqliteQuestCatalog(database.Factory).GetAsync(
            GameMode.Pve,
            "en",
            TestContext.Current.CancellationToken));

        var originalTask = Assert.Single(regular.Tasks);
        var invalid = regular with
        {
            Tasks = [originalTask, originalTask with { Name = "duplicate" }],
        };
        await Assert.ThrowsAsync<SqliteException>(
            () => refresh.RefreshTasksAsync(invalid, TestContext.Current.CancellationToken));

        var loaded = await new SqliteQuestCatalog(database.Factory).GetAsync(
            GameMode.Regular,
            "en",
            TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal("Quest catalog contract", Assert.Single(loaded.Tasks).Name);
    }

    [Fact]
    public async Task CatalogRefreshTracksOrphansWithoutChangingProgressAndResolvesThemLater()
    {
        await using var database = await TestDatabase.CreateAsync();
        var json = await FixtureJson.ReadAsync("tasks-contract.json");
        var refresh = new SqliteDataRefreshRepository(database.Factory);
        await InsertProgressAsync(database.Factory);
        await refresh.RefreshTasksAsync(
            ParseAndNormalize(json, GameMode.Regular),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, await CountAsync(database.Factory, "quest_catalog_orphans"));
        Assert.Equal(2, await CountAsync(database.Factory, "profile_task_progress"));
        Assert.Equal(2, await CountAsync(database.Factory, "profile_objective_progress"));

        var expanded = AddLaterTask(json);
        await refresh.RefreshTasksAsync(
            ParseAndNormalize(expanded, GameMode.Regular),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, await CountAsync(database.Factory, "quest_catalog_orphans"));
        Assert.Equal(2, await CountAsync(database.Factory, "profile_task_progress"));
        Assert.Equal(2, await CountAsync(database.Factory, "profile_objective_progress"));
    }

    [Fact]
    public async Task SyntheticFullSizeFixtureImportsObservedTaskScaleDeterministically()
    {
        await using var database = await TestDatabase.CreateAsync();
        var contract = await FixtureJson.ReadAsync("tasks-contract.json");
        var fullSizeJson = BuildFullSizeFixture(contract, 515);
        var catalog = ParseAndNormalize(fullSizeJson, GameMode.Regular);

        await new SqliteDataRefreshRepository(database.Factory).RefreshTasksAsync(
            catalog,
            TestContext.Current.CancellationToken);
        var loaded = await new SqliteQuestCatalog(database.Factory).GetAsync(
            GameMode.Regular,
            "en",
            TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(515, loaded.Tasks.Count);
        Assert.Equal(515, await CountAsync(database.Factory, "quest_catalog_tasks"));
        Assert.Equal(
            Enum.GetValues<QuestObjectiveKind>().Where(kind => kind != QuestObjectiveKind.Unsupported).ToHashSet(),
            loaded.Tasks.SelectMany(task => task.Objectives).Select(objective => objective.Kind).ToHashSet());
        Assert.Equal("task-000", loaded.Tasks[0].Id);
        Assert.Equal("task-514", loaded.Tasks[^1].Id);
    }

    private static QuestCatalogSnapshot ParseAndNormalize(string json, GameMode gameMode)
    {
        var envelope = JsonSerializer.Deserialize<TarkovDevEnvelope<TarkovDevTasksData>>(json, SerializerOptions)
            ?? throw new InvalidDataException("The test task envelope was null.");
        var response = new TarkovDevResponse<TarkovDevTasksData>(
            envelope.Data,
            json,
            FetchedUtc,
            false,
            false,
            "\"contract-v1\"",
            FetchedUtc,
            json);
        return new TarkovDevQuestCatalogNormalizer().Normalize(response, gameMode, "en", ValidatedUtc);
    }

    private static string AddLaterTask(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidDataException("The task contract was not an object.");
        var tasks = root["data"]?["tasks"]?.AsObject()
            ?? throw new InvalidDataException("The task contract did not contain data.tasks.");
        tasks["task-later"] = new JsonObject
        {
            ["id"] = "task-later",
            ["name"] = "Later task",
            ["objectives"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "objective-later",
                    ["type"] = "visit",
                    ["description"] = "Later objective",
                    ["optional"] = false,
                    ["maps"] = new JsonArray(),
                    ["zones"] = new JsonArray(),
                },
            },
            ["failConditions"] = new JsonArray(),
        };
        return root.ToJsonString();
    }

    private static string BuildFullSizeFixture(string contractJson, int taskCount)
    {
        var source = JsonNode.Parse(contractJson)?.AsObject()
            ?? throw new InvalidDataException("The task contract was not an object.");
        var contractTasks = source["data"]?["tasks"]?.AsObject()
            ?? throw new InvalidDataException("The task contract did not contain data.tasks.");
        var templateTask = contractTasks["task-contract"]?.AsObject()
            ?? throw new InvalidDataException("The task contract template was missing.");
        var objectiveTemplates = templateTask["objectives"]?.AsArray()
            ?.Where(node => node?["type"]?.GetValue<string>() != "futureObjective")
            .Select(node => node?.DeepClone())
            .ToArray()
            ?? throw new InvalidDataException("The task contract objectives were missing.");
        var tasks = new JsonObject();
        for (var index = 0; index < taskCount; index++)
        {
            var id = $"task-{index:D3}";
            var objective = objectiveTemplates[index % objectiveTemplates.Length]?.DeepClone().AsObject()
                ?? throw new InvalidDataException("An objective template was null.");
            objective["id"] = $"objective-{index:D3}";
            tasks[id] = new JsonObject
            {
                ["id"] = id,
                ["name"] = $"Synthetic task {index:D3}",
                ["restartable"] = index % 2 == 0,
                ["taskRequirements"] = new JsonArray(),
                ["objectives"] = new JsonArray { objective },
                ["failConditions"] = new JsonArray(),
            };
        }

        return new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["tasks"] = tasks,
                ["questItems"] = new JsonObject(),
            },
            ["translations"] = new JsonArray(),
        }.ToJsonString();
    }

    private static async Task InsertProgressAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO player_profiles(
                id, name, game_mode, faction, level, created_utc, updated_utc)
            VALUES (
                'profile-001', 'Fixture profile', 'Regular', 'Usec', 17,
                '2026-09-10T00:00:00Z', '2026-09-10T00:00:00Z');
            INSERT INTO profile_task_progress(profile_id, task_id, status)
            VALUES ('profile-001', 'task-contract', 'active'),
                   ('profile-001', 'task-later', 'active');
            INSERT INTO profile_objective_progress(profile_id, objective_id, count)
            VALUES ('profile-001', 'objective-find-item', 1),
                   ('profile-001', 'objective-later', 1);
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> CountAsync(SqliteConnectionFactory factory, string table)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "quest_catalog_snapshots",
            "quest_catalog_tasks",
            "quest_catalog_objectives",
            "quest_catalog_orphans",
            "profile_task_progress",
            "profile_objective_progress",
        };
        if (!allowed.Contains(table))
        {
            throw new ArgumentException("Unexpected test table.", nameof(table));
        }

        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) ?? 0L);
    }

    private sealed class TestDatabase(string path, SqliteConnectionFactory factory) : IAsyncDisposable
    {
        public SqliteConnectionFactory Factory { get; } = factory;

        public static async Task<TestDatabase> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"tarkov-quest-catalog-{Guid.NewGuid():N}.db");
            var factory = new SqliteConnectionFactory(new(path));
            await new SqliteMigrationRunner(factory).ApplyAsync(TestContext.Current.CancellationToken);
            return new(path, factory);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-shm");
            File.Delete(path + "-wal");
            return ValueTask.CompletedTask;
        }
    }
}
