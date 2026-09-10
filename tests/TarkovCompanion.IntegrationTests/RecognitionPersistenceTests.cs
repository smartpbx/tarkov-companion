using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.IntegrationTests;

public sealed class RecognitionPersistenceTests
{
    [Fact]
    public async Task CanonicalCatalogAndScanMetadataRoundTripWithoutCapturedPixels()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "tarkov-recognition-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var factory = new SqliteConnectionFactory(new(databasePath));
            await new SqliteMigrationRunner(factory).ApplyAsync(TestContext.Current.CancellationToken);
            await InsertItemAsync(factory);
            var catalog = await new SqliteRecognitionCatalogRepository(factory)
                .LoadAsync(TestContext.Current.CancellationToken);

            var item = Assert.Single(catalog);
            Assert.Equal("item-1", item.Id);
            Assert.Equal(["GPU"], item.Aliases);

            var scanId = Guid.NewGuid();
            var observedUtc = new DateTimeOffset(2026, 9, 10, 15, 0, 0, TimeSpan.Zero);
            await new SqliteScanEventRepository(factory).SaveAsync(
                new(
                    scanId,
                    observedUtc,
                    ScanContext.SingleItem,
                    "item-1",
                    new Confidence(0.96),
                    [new("item-1", "Graphics Card", new Confidence(0.96), "ocr", new(10, 20, 100, 30))],
                    "Keep",
                    new(0, 0, 1920, 1080),
                    "fixture_evidence"),
                TestContext.Current.CancellationToken);

            await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT timestamp_utc, resolved_item_id, confidence, candidate_json,
                       source_geometry_json, diagnostic_code
                FROM scan_history
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", scanId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(observedUtc, DateTimeOffset.Parse(reader.GetString(0)));
            Assert.Equal("item-1", reader.GetString(1));
            Assert.Equal(0.96, reader.GetDouble(2), 3);
            Assert.Contains("\"canonicalId\":\"item-1\"", reader.GetString(3), StringComparison.Ordinal);
            Assert.Contains("\"width\":1920", reader.GetString(4), StringComparison.Ordinal);
            Assert.Equal("fixture_evidence", reader.GetString(5));

            await reader.DisposeAsync();
            command.CommandText = """
                SELECT COUNT(*)
                FROM pragma_table_info('scan_history')
                WHERE lower(name) LIKE '%pixel%' OR lower(name) LIKE '%image%';
                """;
            command.Parameters.Clear();
            Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    private static async Task InsertItemAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO items(
                id, name, short_name, normalized_name, normalized_short_name, description,
                category_type, width, height, slots, flea_eligible, source_updated_utc)
            VALUES (
                'item-1', 'Graphics Card', 'GPU', 'graphics card', 'gpu', '',
                'Barter', 2, 1, 2, 1, '2026-09-10T15:00:00Z');
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
