using TarkovCompanion.Infrastructure.Diagnostics;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.IntegrationTests;

/// <summary>
/// What Setup's self-test reads back about game data and the database.
/// </summary>
/// <remarks>
/// Written over the real writers rather than over hand-made rows: an endpoint's answer is spread
/// across its sync state, the publication its head points at, and the body still in the response
/// cache, and a fixture that inserted those by hand would prove the query matches the fixture
/// rather than the schema.
///
/// The case that matters is the failed one. "2 endpoint refresh(es) failed" with no names cost a
/// day; the reason was already recorded here the whole time and nothing read it back.
/// </remarks>
[Collection(SqliteCollection.Name)]
public sealed class SelfTestDatabaseReadTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 21, 0, 0, TimeSpan.Zero);
    private static readonly string[] Endpoints = ["items", "tasks"];

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"tarkov-companion-selftest-{Guid.NewGuid():N}.db");

    private SqliteConnectionFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(new(_databasePath));
        await new SqliteMigrationRunner(_factory).ApplyAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        try
        {
            File.Delete(_databasePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A temporary database that outlives the test is not a test failure.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task EveryEndpointComesBackWithItsRowsItsAgeAndTheSizeOfItsCachedBody()
    {
        var syncState = new SqliteSyncStateRepository(_factory);
        var run = await syncState.BeginRunAsync("regular", "en", Endpoints, Now, CancellationToken.None);
        await syncState.RecordAsync(
            new("items", "regular", "en", Now.AddHours(-3), Now.AddHours(-3), null, null, "current", null),
            new string('a', 64),
            CancellationToken.None,
            run,
            5_321);
        var cache = new SqliteTarkovDevResponseCache(_factory);
        await cache.PutAsync(new("regular/items", new string('x', 4_096), Now, null, null), CancellationToken.None);
        await cache.PutAsync(new("regular/items_en", new string('y', 2_048), Now, null, null), CancellationToken.None);
        // Another endpoint's body, so the cache-key match is proved to be exact rather than a prefix.
        await cache.PutAsync(new("regular/tasks", new string('z', 512), Now, null, null), CancellationToken.None);

        var rows = await new SqliteSelfTestReader(_factory).ReadEndpointsAsync("regular", "en", CancellationToken.None);

        var items = Assert.Single(rows, row => row.SourceKey == "items");
        Assert.Equal(5_321, items.RecordCount);
        Assert.Equal(Now.AddHours(-3), items.LastSuccessUtc);
        Assert.Equal("current", items.Status);
        Assert.Null(items.ErrorSummary);
        Assert.Equal(4_096 + 2_048, items.Bytes);
    }

    [Fact]
    public async Task AFailedEndpointComesBackNamedWithItsReasonRatherThanAsACount()
    {
        var syncState = new SqliteSyncStateRepository(_factory);
        var run = await syncState.BeginRunAsync("regular", "en", Endpoints, Now, CancellationToken.None);
        await syncState.RecordAsync(
            new("tasks", "regular", "en", null, Now, null, null, "refused", "502 from json.tarkov.dev"),
            null,
            CancellationToken.None,
            run,
            0);

        var rows = await new SqliteSelfTestReader(_factory).ReadEndpointsAsync("regular", "en", CancellationToken.None);

        var tasks = Assert.Single(rows, row => row.SourceKey == "tasks");
        Assert.Equal("refused", tasks.Status);
        Assert.Equal("502 from json.tarkov.dev", tasks.ErrorSummary);
        Assert.Null(tasks.LastSuccessUtc);
        Assert.Equal(0, tasks.Bytes);
    }

    [Fact]
    public async Task AnEmptySyncRecordComesBackEmptyRatherThanThrowing()
    {
        var rows = await new SqliteSelfTestReader(_factory).ReadEndpointsAsync("regular", "en", CancellationToken.None);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task TheDatabaseReportsEveryAppliedMigrationItsSizeAndTheTablesThatMatter()
    {
        var reading = await new SqliteSelfTestReader(_factory).ReadDatabaseAsync(CancellationToken.None);

        Assert.Equal(_databasePath, reading.Path);
        Assert.True(reading.Bytes > 0);
        Assert.Equal(
            SqliteMigrationLedger.Entries.Select(entry => entry.Id),
            reading.Applied);
        Assert.Contains(reading.Tables, table => table.Name == "items" && table.Rows == 0);
        Assert.Contains(reading.Tables, table => table.Name == "raids");
    }

    /// <summary>
    /// A counted table that a later migration dropped would otherwise throw on every press.
    /// </summary>
    [Fact]
    public async Task EveryCountedTableIsOneThisSchemaStillHas()
    {
        var reading = await new SqliteSelfTestReader(_factory).ReadDatabaseAsync(CancellationToken.None);

        Assert.Equal(
            SqliteSelfTestReader.CountedTables,
            reading.Tables.Select(table => table.Name));
    }
}
