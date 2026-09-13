using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.IntegrationTests;

[Collection(SqliteCollection.Name)]
public sealed class SqliteDataPersistenceTests
{
    [Fact]
    public async Task OfflineFixtureSyncNormalizesEveryEndpointAndSupportsExactShortAndFuzzySearch()
    {
        await using var database = await TestDatabase.CreateAsync();
        var handler = new FixtureApiHandler();
        var client = CreateClient(handler);
        var refresh = new SqliteDataRefreshRepository(database.Factory);
        var syncState = new SqliteSyncStateRepository(database.Factory);
        var operation = new TarkovDevDataRefreshOperation(client, refresh, syncState);
        var service = new DataSyncService(operation);

        var report = await service.SyncAsync(
            new(GameMode.Regular, "en"),
            TestContext.Current.CancellationToken);

        Assert.Equal(7, report.Endpoints.Count);
        Assert.All(report.Endpoints, endpoint => Assert.Null(endpoint.Error));
        Assert.Equal(2, await CountAsync(database.Factory, "items"));
        Assert.Equal(1, await CountAsync(database.Factory, "maps"));
        // map_spawns, map_transits, map_hazards and map_loot_positions were dropped in 0009:
        // written on every sync and selected by nothing, while the map reads all four out of
        // maps.source_json. Extracts and locks are still tables because they are still read.
        Assert.Equal(1, await CountAsync(database.Factory, "map_extracts"));
        Assert.Equal(1, await CountAsync(database.Factory, "map_locks"));
        Assert.Equal(1, await CountAsync(database.Factory, "tasks"));
        Assert.Equal(1, await CountAsync(database.Factory, "hideout_stations"));
        Assert.Equal(1, await CountAsync(database.Factory, "traders"));
        Assert.Equal(1, await CountAsync(database.Factory, "crafts"));
        Assert.Equal(1, await CountAsync(database.Factory, "barters"));

        var repository = new SqliteItemRepository(database.Factory);
        var exact = await repository.SearchAsync("Salewa first aid kit", 5, TestContext.Current.CancellationToken);
        var shortName = await repository.SearchAsync("Hose", 5, TestContext.Current.CancellationToken);
        var fuzzy = await repository.SearchAsync("Salwa", 5, TestContext.Current.CancellationToken);

        Assert.Equal("item-001", exact[0].Item.Id);
        Assert.Equal(1, exact[0].Score);
        Assert.Equal("item-002", shortName[0].Item.Id);
        Assert.Equal("item-001", fuzzy[0].Item.Id);

        var item = await repository.GetAsync("item-001", TestContext.Current.CancellationToken);
        var price = await repository.GetPriceAsync("item-001", TestContext.Current.CancellationToken);
        Assert.NotNull(item);
        Assert.Equal(2, item.Dimensions.Slots);
        Assert.Contains("category-medical", item.CategoryIds);
        Assert.NotNull(price);
        Assert.Equal(21000, price.FleaPriceRoubles);
        Assert.Equal(20000, price.Low24HourRoubles);
        Assert.Equal(24000, price.High24HourRoubles);
        Assert.Equal("Therapist", price.BestTrader?.TraderName);

        var state = await syncState.GetAsync("items", "regular", "en", TestContext.Current.CancellationToken);
        Assert.NotNull(state);
        Assert.Equal("current", state.Status);
        Assert.NotNull(state.LastSuccessUtc);
    }

    [Fact]
    public async Task PriceHistoryEndpointPersistsUtcPointsAndServiceFiltersWindow()
    {
        await using var database = await TestDatabase.CreateAsync();
        var client = CreateClient(new FixtureApiHandler());
        var refresh = new SqliteDataRefreshRepository(database.Factory);
        var items = await client.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);
        await refresh.RefreshItemsAsync(items.Data, new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);
        var prices = await client.GetPriceHistoryAsync(GameMode.Regular, "item-001", TestContext.Current.CancellationToken);
        await refresh.RefreshPriceHistoryAsync("item-001", prices.Data, TestContext.Current.CancellationToken);

        var clock = new ManualTimeProvider(new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        var service = new PriceHistoryService(new SqlitePriceHistoryRepository(database.Factory), clock);
        var history = await service.GetAsync("item-001", TimeSpan.FromDays(2), TestContext.Current.CancellationToken);

        Assert.True(history.Count >= 2);
        Assert.Contains(history, point => point.Source == "json.tarkov.dev/prices" && point.FleaPriceRoubles == 22000);
        Assert.All(history, point => Assert.Equal(TimeSpan.Zero, point.TimestampUtc.Offset));
    }

    [Fact]
    public async Task RejectedItemSnapshotLeavesPreviousNormalizedDataIntact()
    {
        await using var database = await TestDatabase.CreateAsync();
        var client = CreateClient(new FixtureApiHandler());
        var refresh = new SqliteDataRefreshRepository(database.Factory);
        var original = await client.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);
        await refresh.RefreshItemsAsync(original.Data, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var invalid = new TarkovDevItemsData
        {
            Items = new Dictionary<string, TarkovDevItem>
            {
                ["item-001"] = new()
                {
                    Id = "item-001",
                    Name = "Changed name",
                    ShortName = "Changed",
                    Width = 1,
                    Height = 1,
                },
                ["invalid"] = new()
                {
                    Id = "invalid",
                    Name = "Invalid",
                    ShortName = "Invalid",
                    Width = 0,
                    Height = 1,
                },
            },
        };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => refresh.RefreshItemsAsync(invalid, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));

        var repository = new SqliteItemRepository(database.Factory);
        var item = await repository.GetAsync("item-001", TestContext.Current.CancellationToken);
        Assert.NotNull(item);
        Assert.Equal("Salewa first aid kit", item.Name);
        Assert.Equal(2, await CountAsync(database.Factory, "items"));
    }

    [Fact]
    public async Task SqliteHttpCacheRoundTripsValidators()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteTarkovDevResponseCache(database.Factory);
        var cachedUtc = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var modifiedUtc = cachedUtc.AddHours(-1);

        await repository.PutAsync(
            new("regular/items", "{\"data\":{}}", cachedUtc, "\"etag\"", modifiedUtc),
            TestContext.Current.CancellationToken);
        var value = await repository.GetAsync("regular/items", TestContext.Current.CancellationToken);

        Assert.NotNull(value);
        Assert.Equal(cachedUtc, value.CachedUtc);
        Assert.Equal("\"etag\"", value.ETag);
        Assert.Equal(modifiedUtc, value.LastModified);
    }

    private static TarkovDevJsonClient CreateClient(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new InMemoryTarkovDevResponseCache(),
            new DataTranslationService(),
            new()
            {
                BaseAddress = new("https://fixture.invalid/"),
                InitialRetryDelay = TimeSpan.Zero,
                MaxAttempts = 1,
            });

    private static async Task<long> CountAsync(SqliteConnectionFactory factory, string table)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "items", "maps", "map_extracts", "map_locks",
            "tasks", "hideout_stations", "traders", "crafts", "barters",
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
            var path = Path.Combine(Path.GetTempPath(), $"tarkov-companion-data-{Guid.NewGuid():N}.db");
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
