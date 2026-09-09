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

            Assert.Single(first);
            Assert.Empty(second);
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table', 'view') AND name = 'items';";
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
}
