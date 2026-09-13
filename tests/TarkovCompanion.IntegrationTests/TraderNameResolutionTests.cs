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

/// <summary>
/// Turning a trader id back into a trader's name.
/// </summary>
/// <remarks>
/// The Quests page printed "Trader: 54cb50c76803fa8b248b4571", which is what the feed puts in a
/// task's trader field. The traders table has held id and name since migration 0001 and is
/// rewritten on every sync; the only query that had ever touched it was a sub-select inside the
/// sell-offer join, so every consumer holding a trader id printed the id.
/// </remarks>
[Collection(SqliteCollection.Name)]
public sealed class TraderNameResolutionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-13T12:00:00Z");
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    [Fact]
    public async Task AQuestNamesItsTraderOnceTheTradersAreSynced()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.StoreTraderAsync("trader-001", "Prapor");

        var task = await harness.ReadContractTaskAsync();

        Assert.Equal("Prapor", task.TraderName);
        Assert.Equal("trader-001", task.TraderId);
    }

    /// <summary>
    /// The id survives on the model, because the search box reads both.
    /// </summary>
    /// <remarks>
    /// Nobody will type a trader id. Somebody chasing a figure from upstream will paste one,
    /// and it costs nothing to let that find its quest.
    /// </remarks>
    [Fact]
    public async Task TheIdIsStillThereAfterTheNameIsFound()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.StoreTraderAsync("trader-001", "Prapor");

        Assert.Equal("trader-001", (await harness.ReadContractTaskAsync()).TraderId);
    }

    /// <summary>
    /// A trader the catalog does not know about leaves the name empty, not blank.
    /// </summary>
    /// <remarks>
    /// The page then prints the id. Wrong is worse than ugly: a missing trader is something to
    /// notice rather than something to hide behind an empty label, and this is the state every
    /// installation is in before its first sync.
    /// </remarks>
    [Fact]
    public async Task AnUnknownTraderIsNotNamed()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.StoreTraderAsync("trader-999", "Somebody Else");

        Assert.Null((await harness.ReadContractTaskAsync()).TraderName);
    }

    [Fact]
    public async Task NothingSyncedYetIsNotAFailure()
    {
        await using var harness = await Harness.CreateAsync();

        Assert.Null((await harness.ReadContractTaskAsync()).TraderName);
    }

    /// <summary>The map projection carried the same id, and wanted the same answer.</summary>
    /// <remarks>
    /// Anything drawing a quest objective on the map had no way to say whose quest it was,
    /// which is the other half of what the id being unresolved cost.
    /// </remarks>
    [Fact]
    public async Task AMapObjectiveNamesItsTraderToo()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.StoreTraderAsync("trader-001", "Prapor");
        // The projection draws the quests somebody is on, so the quest has to be one of them.
        await harness.MakeContractActiveAsync();

        var objectives = await harness.ReadMapObjectivesAsync("map-primary");

        Assert.NotEmpty(objectives);
        Assert.All(objectives, objective => Assert.Equal("Prapor", objective.TraderName));
    }

    [Fact]
    public async Task TheCatalogReadsEveryTraderItWasGiven()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.StoreTraderAsync("trader-001", "Prapor");
        await harness.StoreTraderAsync("trader-002", "Therapist");

        var names = await new SqliteTraderCatalog(harness.Factory).GetNamesAsync(CancellationToken.None);

        Assert.Equal(2, names.Count);
        Assert.Equal("Therapist", names["trader-002"]);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly JsonFilePlayerProfileService _profileService;

        private Harness(string directory, SqliteConnectionFactory factory, JsonFilePlayerProfileService profileService)
        {
            _directory = directory;
            _profileService = profileService;
            Factory = factory;
        }

        public SqliteConnectionFactory Factory { get; }

        public static async Task<Harness> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"tarkov-trader-names-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var factory = new SqliteConnectionFactory(new(Path.Combine(directory, "traders.db")));
            await new SqliteMigrationRunner(factory).ApplyAsync(CancellationToken.None);
            await new SqliteDataRefreshRepository(factory).RefreshTasksAsync(
                await CatalogAsync(),
                CancellationToken.None);

            var profileService = new JsonFilePlayerProfileService(
                new(Path.Combine(directory, "profile.json")),
                new ManualTimeProvider(Now));
            await profileService.SaveAsync(
                new PlayerProfile(
                    Guid.Parse("6ec9a0b1-2b1f-4d7c-9d67-3f3ce4c2f0a1"),
                    "Trader reader",
                    GameMode.Regular,
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
                    "generation-a"),
                CancellationToken.None);
            return new(directory, factory, profileService);
        }

        public async Task StoreTraderAsync(string id, string name)
        {
            await using var connection = await Factory.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO traders(id, name, source_json) VALUES ($id, $name, '{}');";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", name);
            await command.ExecuteNonQueryAsync();
        }

        public async Task MakeContractActiveAsync() => await new QuestProgressCommandService(
                _profileService,
                new SqliteQuestCatalog(Factory),
                new SqliteQuestProgressStore(Factory),
                new(),
                new ManualTimeProvider(Now))
            .SetTaskStateAsync(await ScopeAsync(), "task-contract", RecordedTaskState.Active, CancellationToken.None);

        public async Task<QuestSummaryReadModel> ReadContractTaskAsync()
        {
            var board = await Read().GetQuestBoardAsync(await ScopeAsync(), CancellationToken.None);
            return Assert.Single(board.Tasks, task => task.TaskId == "task-contract");
        }

        public async Task<IReadOnlyList<QuestMapObjectiveReadModel>> ReadMapObjectivesAsync(string mapId)
        {
            var projection = await Read()
                .GetActiveMapObjectivesAsync(await ScopeAsync(), [mapId], CancellationToken.None);
            return projection.Objectives;
        }

        public ValueTask DisposeAsync()
        {
            _profileService.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                ScratchDirectory.Remove(_directory);
            }

            return ValueTask.CompletedTask;
        }

        private QuestReadService Read() => new(
            _profileService,
            new SqliteQuestCatalog(Factory),
            new SqliteQuestProgressStore(Factory),
            new(),
            new(),
            new ManualTimeProvider(Now),
            new SqliteTraderCatalog(Factory));

        private async Task<QuestProfileScope> ScopeAsync()
        {
            var profile = await _profileService.GetActiveAsync(CancellationToken.None);
            return new(profile.Id, profile.GameMode, profile.ProfileGeneration);
        }

        private static async Task<QuestCatalogSnapshot> CatalogAsync()
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
                "\"traders\"",
                Now,
                json);
            return new TarkovDevQuestCatalogNormalizer().Normalize(response, GameMode.Regular, "en", Now);
        }
    }
}
