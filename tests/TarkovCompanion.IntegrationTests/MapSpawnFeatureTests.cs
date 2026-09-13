using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.IntegrationTests;

/// <summary>
/// What a spawn point is called on the map, and whose it is.
/// </summary>
/// <remarks>
/// Both were wrong and both were visible. Of 3018 spawn points across every map, 1403 carry a
/// GUID where the zone name should be and were drawn on the map exactly as written, so a player
/// on Customs saw a marker labelled "0246436c-7d69-4036-999d-ebcb956970b5". And the commonest
/// side value in the feed is "all", which the faction reader did not recognise, so the largest
/// group of player spawns had no side at all.
/// </remarks>
[Collection(SqliteCollection.Name)]
public sealed class MapSpawnFeatureTests
{
    [Fact]
    public async Task SpawnsAreNamedForWhatTheyAreRatherThanByTheirZoneIdentifier()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "tarkov-spawns-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var factory = new SqliteConnectionFactory(new(databasePath));
            await new SqliteMigrationRunner(factory).ApplyAsync(TestContext.Current.CancellationToken);
            await SeedAsync(factory);

            var features = await new SqliteMapFeatureCatalog(factory)
                .GetAsync("ground-zero", TestContext.Current.CancellationToken);
            var spawns = features
                .Where(feature => feature.Kind == MapFeatureKind.Spawn)
                .OrderBy(feature => feature.Position.X)
                .ToArray();

            Assert.Equal(4, spawns.Length);

            // A GUID is not a place, so it is dropped and what the point is stands alone.
            Assert.Equal("Spawn", spawns[0].Name);
            Assert.Equal(MapFeatureFaction.Shared, spawns[0].Side);

            // A real zone name survives, with its prefix stripped and its capitals split.
            Assert.Equal("Spawn · Red House", spawns[1].Name);
            Assert.Equal(MapFeatureFaction.Pmc, spawns[1].Side);

            Assert.Equal("Boss spawn · Floor 1", spawns[2].Name);
            Assert.Equal(MapFeatureFaction.Scav, spawns[2].Side);

            Assert.Equal("Bot spawn", spawns[3].Name);
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
        var payload = JsonSerializer.Serialize(new
        {
            id = "map-1",
            name = "Ground Zero",
            normalizedName = "ground-zero",
            spawns = new object[]
            {
                new
                {
                    position = new { x = 1.0, y = 0.0, z = 0.0 },
                    sides = new[] { "all" },
                    categories = new[] { "player" },
                    zoneName = "0246436c-7d69-4036-999d-ebcb956970b5",
                },
                new
                {
                    position = new { x = 2.0, y = 0.0, z = 0.0 },
                    sides = new[] { "pmc" },
                    categories = new[] { "player" },
                    zoneName = "ZoneRedHouse",
                },
                new
                {
                    position = new { x = 3.0, y = 0.0, z = 0.0 },
                    sides = new[] { "scav" },
                    categories = new[] { "boss", "bot" },
                    zoneName = "BotZoneFloor1",
                },
                new
                {
                    position = new { x = 4.0, y = 0.0, z = 0.0 },
                    sides = new[] { "scav" },
                    categories = new[] { "bot" },
                    zoneName = "",
                },
            },
        });

        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO maps(id, name, normalized_name, pmc_raid_duration_seconds, scav_raid_duration_seconds, source_json)
            VALUES ('map-1', 'Ground Zero', 'ground zero', 2100, NULL, $payload);
            """;
        command.Parameters.AddWithValue("$payload", payload);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
