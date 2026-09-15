using Microsoft.Data.Sqlite;
using TarkovCompanion.Infrastructure.Persistence;

namespace TarkovCompanion.IntegrationTests.DataV2;

[Collection(SqliteCollection.Name)]
public sealed class MigrationRecoveryTests
{
    [Fact]
    public async Task LedgerHasPairedUpgradeAndRollbackFixturesAndFreshDatabaseAppliesAll()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(11, SqliteMigrationLedger.Entries.Count);
        Assert.Equal("0011_v2_data_platform", SqliteMigrationLedger.Entries[^1].Id);
        Assert.All(SqliteMigrationLedger.Entries, entry =>
        {
            var fixture = SqliteMigrationRunner.ReadFixture(entry.Id);
            Assert.False(string.IsNullOrWhiteSpace(fixture.UpgradeSql));
            Assert.False(string.IsNullOrWhiteSpace(fixture.RollbackSql));
            Assert.EndsWith(";", fixture.UpgradeSql.TrimEnd(), StringComparison.Ordinal);
            Assert.EndsWith(";", fixture.RollbackSql.TrimEnd(), StringComparison.Ordinal);
        });
        Assert.Equal(11, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM schema_migrations;"));

        var rollback = SqliteMigrationRunner.ReadFixture("0011_v2_data_platform").RollbackSql;
        await using var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = rollback;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('http_response_cache') WHERE name = 'body_json';";
        Assert.Equal(1L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DestructiveFailureRestoresVerifiedV10DatabaseAndLegacyCache()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tarkov-companion-v2-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new(path));
            await CreateAtV10Async(factory);
            await using (var connection = await factory.OpenAsync(TestContext.Current.CancellationToken))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO http_response_cache(cache_key, body_json, cached_utc) VALUES ('legacy', '{\"data\":{}}', '2026-09-15T00:00:00Z');";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var runner = new SqliteMigrationRunner(factory, new ThrowAtFault(SqliteMigrationFaultPoint.AfterMigrationSql, new IOException("disk-full")));
            var failure = await Assert.ThrowsAsync<SqliteMigrationException>(() => runner.ApplyAsync(TestContext.Current.CancellationToken));
            Assert.Equal(MigrationRecoveryState.RestoredVerifiedBackup, failure.RecoveryState);
            Assert.NotNull(failure.LastRecoverableBackupPath);
            Assert.Equal(10, await V2TestDatabase.ScalarAsync(factory, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.Equal(1, await V2TestDatabase.ScalarAsync(factory, "SELECT COUNT(*) FROM http_response_cache WHERE cache_key = 'legacy' AND body_json = '{\"data\":{}}';"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}*")) File.Delete(file);
        }
    }

    [Theory]
    [InlineData(SqliteMigrationFaultPoint.BeforeBackup, "disk-full")]
    [InlineData(SqliteMigrationFaultPoint.BeforeMigration, "database-locked")]
    [InlineData(SqliteMigrationFaultPoint.BeforeMigrationCommit, "interrupted")]
    public async Task BackupMigrationAndRollbackFaultsAlwaysReportRecoverableState(SqliteMigrationFaultPoint point, string code)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tarkov-companion-v2-fault-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new(path));
            await CreateAtV10Async(factory);
            Exception injected = code == "interrupted" ? new OperationCanceledException(code) : new IOException(code);
            var failure = await Assert.ThrowsAsync<SqliteMigrationException>(() => new SqliteMigrationRunner(factory, new ThrowAtFault(point, injected)).ApplyAsync(TestContext.Current.CancellationToken));
            Assert.Contains(failure.RecoveryState, new[] { MigrationRecoveryState.OriginalIntact, MigrationRecoveryState.RestoredVerifiedBackup });
            Assert.Equal(10, await V2TestDatabase.ScalarAsync(factory, "SELECT COUNT(*) FROM schema_migrations;"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}*")) File.Delete(file);
        }
    }

    [Fact]
    public async Task RestoreFailureLeavesVerifiedBackupPathForManualRecovery()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tarkov-companion-v2-restore-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new(path));
            await CreateAtV10Async(factory);
            var injector = new MultiFault(
                (SqliteMigrationFaultPoint.AfterMigrationSql, new IOException("migration-failed")),
                (SqliteMigrationFaultPoint.AfterRestoreCopy, new IOException("restore-failed")));
            var failure = await Assert.ThrowsAsync<SqliteMigrationException>(() => new SqliteMigrationRunner(factory, injector).ApplyAsync(TestContext.Current.CancellationToken));
            Assert.Equal(MigrationRecoveryState.RecoverableBackupAvailable, failure.RecoveryState);
            Assert.True(File.Exists(failure.LastRecoverableBackupPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}*")) File.Delete(file);
        }
    }

    [Fact]
    public async Task BackupCorruptionIsDetectedBeforeAnyDestructiveSqlRuns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tarkov-companion-v2-corrupt-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new(path));
            await CreateAtV10Async(factory);
            var failure = await Assert.ThrowsAsync<SqliteMigrationException>(() =>
                new SqliteMigrationRunner(factory, new CorruptVerifiedBackup()).ApplyAsync(TestContext.Current.CancellationToken));
            Assert.Equal(MigrationRecoveryState.OriginalIntact, failure.RecoveryState);
            Assert.Equal(10, await V2TestDatabase.ScalarAsync(factory, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.Equal(1, await V2TestDatabase.ScalarAsync(factory, "SELECT COUNT(*) FROM pragma_table_info('http_response_cache') WHERE name = 'body_json';"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}*")) File.Delete(file);
        }
    }

    private static async Task CreateAtV10Async(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE schema_migrations(version TEXT PRIMARY KEY, applied_utc TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        foreach (var entry in SqliteMigrationLedger.Entries.Take(10))
        {
            command.CommandText = SqliteMigrationRunner.ReadFixture(entry.Id).UpgradeSql;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            command.CommandText = "INSERT INTO schema_migrations(version, applied_utc) VALUES ($id, '2026-09-15T00:00:00Z');";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", entry.Id);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            command.Parameters.Clear();
        }
    }

    private sealed class ThrowAtFault(SqliteMigrationFaultPoint point, Exception exception) : ISqliteMigrationFaultInjector
    {
        public ValueTask InjectAsync(SqliteMigrationFaultPoint actual, string? migrationId, string databasePath, CancellationToken cancellationToken)
        {
            if (actual == point && (migrationId is null || migrationId == "0011_v2_data_platform")) throw exception;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MultiFault(params (SqliteMigrationFaultPoint Point, Exception Exception)[] faults) : ISqliteMigrationFaultInjector
    {
        public ValueTask InjectAsync(SqliteMigrationFaultPoint actual, string? migrationId, string databasePath, CancellationToken cancellationToken)
        {
            var fault = faults.FirstOrDefault(candidate => candidate.Point == actual);
            if (fault.Exception is not null) throw fault.Exception;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CorruptVerifiedBackup : ISqliteMigrationFaultInjector
    {
        public ValueTask InjectAsync(SqliteMigrationFaultPoint point, string? migrationId, string databasePath, CancellationToken cancellationToken)
        {
            if (point == SqliteMigrationFaultPoint.AfterBackupVerified)
            {
                var directory = Path.GetDirectoryName(databasePath)!;
                var stem = Path.GetFileNameWithoutExtension(databasePath);
                var backup = Directory.GetFiles(directory, $"{stem}.pre-0011_v2_data_platform-*.db").Single();
                File.WriteAllBytes(backup, [0x00, 0x01, 0x02]);
            }
            return ValueTask.CompletedTask;
        }
    }
}
