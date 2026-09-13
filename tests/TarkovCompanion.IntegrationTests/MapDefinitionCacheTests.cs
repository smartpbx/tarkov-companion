using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.IntegrationTests;

/// <summary>
/// The extract screen can only be read against a map that has extracts in it.
/// </summary>
/// <remarks>
/// The cache these replace was filled from a list of maps nothing ever supplied, so every
/// lookup returned nothing, and recognising the extract panel produced a perfect score and no
/// candidates. These pin the two halves that failed: the map is found by the slug the rest of
/// the application uses, not by either of the name columns, and its extracts come back with
/// the positions and factions already stored beside them.
/// </remarks>
[Collection(SqliteCollection.Name)]
public sealed class MapDefinitionCacheTests
{
    [Fact]
    public async Task MapIsFoundBySlugWithItsExtracts()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "tarkov-mapdef-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var factory = new SqliteConnectionFactory(new(databasePath));
            await new SqliteMigrationRunner(factory).ApplyAsync(TestContext.Current.CancellationToken);
            await SeedAsync(factory);

            var cache = new SqliteMapDefinitionCache(factory);
            var map = await cache.GetAsync("ground-zero", TestContext.Current.CancellationToken);

            Assert.NotNull(map);
            Assert.Equal("ground-zero", map.Id);
            Assert.Equal("Ground Zero", map.Name);
            Assert.Equal(TimeSpan.FromMinutes(35), map.PmcRaidDuration);

            var extracts = map.Extracts.OrderBy(extract => extract.Name, StringComparer.Ordinal).ToArray();
            Assert.Equal(2, extracts.Length);

            // World X and world Z are what the map projects; the Y column is height.
            Assert.Equal("Emercom Checkpoint", extracts[0].Name);
            Assert.Equal(12.5, extracts[0].Position!.Value.X, 3);
            Assert.Equal(-44.25, extracts[0].Position!.Value.Y, 3);
            Assert.Equal("PMC only", extracts[0].Conditions);

            Assert.Equal("Mira Ave", extracts[1].Name);
            Assert.Null(extracts[1].Position);
            Assert.Equal("Scav only", extracts[1].Conditions);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    [Fact]
    public async Task MapThatWasNotSyncedIsReportedMissingRatherThanEmpty()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "tarkov-mapdef-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var factory = new SqliteConnectionFactory(new(databasePath));
            await new SqliteMigrationRunner(factory).ApplyAsync(TestContext.Current.CancellationToken);
            await SeedAsync(factory);

            var cache = new SqliteMapDefinitionCache(factory);
            Assert.Null(await cache.GetAsync("streets-of-tarkov", TestContext.Current.CancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    /// <summary>
    /// A miss is remembered like any other answer, so a sync has to be able to drop it.
    /// </summary>
    /// <remarks>
    /// On a fresh install the first map is selected before the catalog finishes downloading.
    /// Without this the session would keep reporting no extracts for that map however long it
    /// ran, which is exactly how the empty cache behaved.
    /// </remarks>
    [Fact]
    public async Task RowsThatLandAfterAMissAreSeenOnceTheCacheIsInvalidated()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "tarkov-mapdef-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var factory = new SqliteConnectionFactory(new(databasePath));
            await new SqliteMigrationRunner(factory).ApplyAsync(TestContext.Current.CancellationToken);

            var cache = new SqliteMapDefinitionCache(factory);
            Assert.Null(await cache.GetAsync("ground-zero", TestContext.Current.CancellationToken));

            await SeedAsync(factory);
            Assert.Null(await cache.GetAsync("ground-zero", TestContext.Current.CancellationToken));

            cache.Invalidate();
            var map = await cache.GetAsync("ground-zero", TestContext.Current.CancellationToken);
            Assert.NotNull(map);
            Assert.Equal(2, map.Extracts.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    private static async Task SeedAsync(SqliteConnectionFactory factory)
    {
        // The name columns hold a display name and a normalized display name. The slug the
        // application uses lives only in the payload, which is why it is seeded there.
        var payload = JsonSerializer.Serialize(new
        {
            id = "map-1",
            name = "Ground Zero",
            normalizedName = "ground-zero",
            nameId = "Sandbox",
        });

        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO maps(id, name, normalized_name, pmc_raid_duration_seconds, scav_raid_duration_seconds, source_json)
            VALUES ('map-1', 'Ground Zero', 'ground zero', 2100, NULL, $payload);
            INSERT INTO map_extracts(id, map_id, name, x, y, z, conditions, source_json)
            VALUES ('map-1:0:e1', 'map-1', 'Emercom Checkpoint', 12.5, 3.0, -44.25, '{}', '{"faction":"pmc"}');
            INSERT INTO map_extracts(id, map_id, name, x, y, z, conditions, source_json)
            VALUES ('map-1:1:e2', 'map-1', 'Mira Ave', NULL, NULL, NULL, '{}', '{"faction":"scav"}');
            """;
        command.Parameters.AddWithValue("$payload", payload);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
