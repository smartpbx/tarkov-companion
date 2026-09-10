using Microsoft.Data.Sqlite;
using TarkovCompanion.Infrastructure.Persistence;

namespace TarkovCompanion.IntegrationTests;

public sealed class SqliteMigrationTests
{
    [Fact]
    public async Task EmptyDatabaseMigratesIdempotently()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tarkov-companion-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new(databasePath));
            var runner = new SqliteMigrationRunner(factory);

            var first = await runner.ApplyAsync(CancellationToken.None);
            var second = await runner.ApplyAsync(CancellationToken.None);

            Assert.Equal(4, first.Count);
            Assert.Empty(second);
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table', 'view') AND name = 'items';";
            Assert.Equal(1L, await command.ExecuteScalarAsync());

            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'http_response_cache';";
            Assert.Equal(1L, await command.ExecuteScalarAsync());

            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'quest_catalog_snapshots';";
            Assert.Equal(1L, await command.ExecuteScalarAsync());

            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'quest_objective_zones';";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
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
    public async Task ExistingInitialSchemaUpgradesWithoutLosingItems()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tarkov-companion-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new(databasePath));
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            {
                var assembly = typeof(SqliteMigrationRunner).Assembly;
                var resourceName = Assert.Single(
                    assembly.GetManifestResourceNames(),
                    name => name.EndsWith("Persistence.Migrations.0001_initial.sql", StringComparison.Ordinal));
                await using var stream = assembly.GetManifestResourceStream(resourceName);
                Assert.NotNull(stream);
                using var reader = new StreamReader(stream);
                var initialSql = await reader.ReadToEndAsync();

                await using var command = connection.CreateCommand();
                command.CommandText = initialSql;
                await command.ExecuteNonQueryAsync();
                command.CommandText = """
                    CREATE TABLE schema_migrations (version TEXT PRIMARY KEY, applied_utc TEXT NOT NULL);
                    INSERT INTO schema_migrations(version, applied_utc) VALUES ('0001_initial', '2026-09-09T00:00:00Z');
                    INSERT INTO items(
                        id, name, short_name, normalized_name, description, category_type,
                        width, height, slots, flea_eligible, source_updated_utc)
                    VALUES ('existing', 'Existing item', 'Existing', 'existing item', '', 'Barter', 1, 1, 1, 1, '2026-09-09T00:00:00Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var applied = await new SqliteMigrationRunner(factory).ApplyAsync(CancellationToken.None);

            Assert.Equal(
                ["0002_data_cache", "0003_recognition_scan_metadata", "0004_quest_catalog_fidelity"],
                applied);
            await using var verification = await factory.OpenAsync(CancellationToken.None);
            await using var verifyCommand = verification.CreateCommand();
            verifyCommand.CommandText = "SELECT name, normalized_short_name FROM items WHERE id = 'existing';";
            await using var result = await verifyCommand.ExecuteReaderAsync();
            Assert.True(await result.ReadAsync());
            Assert.Equal("Existing item", result.GetString(0));
            Assert.Equal(string.Empty, result.GetString(1));

            await result.DisposeAsync();
            verifyCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('scan_history') WHERE name = 'diagnostic_code';";
            Assert.Equal(1L, await verifyCommand.ExecuteScalarAsync());

            verifyCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'quest_catalog_orphans';";
            Assert.Equal(1L, await verifyCommand.ExecuteScalarAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }
}
