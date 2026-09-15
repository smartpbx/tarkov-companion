using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.IntegrationTests;

/// <summary>The reviewed extract repair is present in both consumers of a synced map.</summary>
[Collection(SqliteCollection.Name)]
public sealed class ReviewedExtractPersistenceTests
{
    [Fact]
    public async Task Lighthouse_gaps_reach_recognition_definitions_and_desktop_markers()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            "tarkov-reviewed-extracts-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var factory = new SqliteConnectionFactory(new(databasePath));
            await new SqliteMigrationRunner(factory).ApplyAsync(TestContext.Current.CancellationToken);
            await SeedLighthouseAsync(factory);

            var definition = await new SqliteMapDefinitionCache(factory)
                .GetAsync("lighthouse", TestContext.Current.CancellationToken);
            var features = await new SqliteMapFeatureCatalog(factory)
                .GetAsync("lighthouse", TestContext.Current.CancellationToken);

            Assert.NotNull(definition);
            Assert.Equal(6, definition.Extracts.Count);
            Assert.Equal(6, features.Count(feature => feature.Kind == MapFeatureKind.Extract));

            var definitionStage = Assert.Single(definition.Extracts, extract =>
                extract.Name == "Hideout Under the Landing Stage");
            var featureStage = Assert.Single(features, feature =>
                feature.Name == "Hideout Under the Landing Stage");
            Assert.Equal("Scav only", definitionStage.Conditions);
            Assert.Equal(133.068, definitionStage.Position!.Value.X, 3);
            Assert.Equal(286.842, definitionStage.Position!.Value.Y, 3);
            Assert.Equal(MapFeatureFaction.Scav, featureStage.Side);
            Assert.Equal(133.068, featureStage.Position.X, 3);
            Assert.Equal(-0.467, featureStage.Position.Y, 3);
            Assert.Equal(286.842, featureStage.Position.Z, 3);
            Assert.Equal("reviewed extract supplement", featureStage.Provenance?.Source);
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
    public async Task Positionless_terminal_gap_reaches_recognition_but_not_desktop_markers()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            "tarkov-reviewed-extracts-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var factory = new SqliteConnectionFactory(new(databasePath));
            await new SqliteMigrationRunner(factory).ApplyAsync(TestContext.Current.CancellationToken);
            await SeedTerminalAsync(factory);

            var definition = await new SqliteMapDefinitionCache(factory)
                .GetAsync("terminal", TestContext.Current.CancellationToken);
            var features = await new SqliteMapFeatureCatalog(factory)
                .GetAsync("terminal", TestContext.Current.CancellationToken);

            Assert.NotNull(definition);
            var boat = Assert.Single(definition.Extracts);
            Assert.Equal("Zubr Boat", boat.Name);
            Assert.Null(boat.Position);
            Assert.Equal("PMC only", boat.Conditions);
            Assert.DoesNotContain(features, feature => feature.Kind == MapFeatureKind.Extract);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    private static async Task SeedLighthouseAsync(SqliteConnectionFactory factory)
    {
        var primaryExtract = new
        {
            id = "grotto",
            name = "Scav Hideout at the Grotto",
            faction = "scav",
            position = new { x = 1.0, y = 2.0, z = 3.0 },
        };
        var payload = JsonSerializer.Serialize(new
        {
            id = "lighthouse-id",
            name = "Lighthouse",
            normalizedName = "lighthouse",
            extracts = new[] { primaryExtract },
        });
        var extractPayload = JsonSerializer.Serialize(primaryExtract);

        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO maps(id, name, normalized_name, pmc_raid_duration_seconds, scav_raid_duration_seconds, source_json)
            VALUES ('lighthouse-id', 'Lighthouse', 'lighthouse', 2400, 2100, $payload);
            INSERT INTO map_extracts(id, map_id, name, x, y, z, conditions, source_json)
            VALUES ('grotto', 'lighthouse-id', 'Scav Hideout at the Grotto', 1.0, 2.0, 3.0, '{}', $extractPayload);
            """;
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$extractPayload", extractPayload);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task SeedTerminalAsync(SqliteConnectionFactory factory)
    {
        var payload = JsonSerializer.Serialize(new
        {
            id = "terminal-id",
            name = "Terminal",
            normalizedName = "terminal",
            extracts = Array.Empty<object>(),
        });

        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO maps(id, name, normalized_name, pmc_raid_duration_seconds, scav_raid_duration_seconds, source_json)
            VALUES ('terminal-id', 'Terminal', 'terminal', 2400, 2100, $payload);
            """;
        command.Parameters.AddWithValue("$payload", payload);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
