using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace TarkovCompanion.Infrastructure.Persistence;

public enum MigrationRecoveryState
{
    Current = 1,
    OriginalIntact,
    RestoredVerifiedBackup,
    RecoverableBackupAvailable,
}

public sealed record MigrationOutcome(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> FromNewerBuild,
    string? BackupPath,
    MigrationRecoveryState RecoveryState = MigrationRecoveryState.Current)
{
    public static MigrationOutcome Nothing { get; } = new([], [], null);
}

public sealed class SqliteMigrationException : Exception
{
    public SqliteMigrationException(
        string message,
        MigrationRecoveryState recoveryState,
        string? lastRecoverableBackupPath,
        Exception innerException)
        : base(message, innerException)
    {
        RecoveryState = recoveryState;
        LastRecoverableBackupPath = lastRecoverableBackupPath;
    }

    public MigrationRecoveryState RecoveryState { get; }

    public string? LastRecoverableBackupPath { get; }
}

public enum SqliteMigrationFaultPoint
{
    BeforeBackup = 1,
    AfterBackupCreated,
    AfterBackupVerified,
    BeforeMigration,
    AfterMigrationSql,
    BeforeMigrationCommit,
    BeforeRestore,
    AfterRestoreCopy,
}

/// <summary>A deterministic test seam for disk-full, interruption, rollback, and restore failures.</summary>
public interface ISqliteMigrationFaultInjector
{
    ValueTask InjectAsync(
        SqliteMigrationFaultPoint point,
        string? migrationId,
        string databasePath,
        CancellationToken cancellationToken);
}

public sealed class SqliteMigrationRunner
{
    private const int BackupsKept = 2;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ISqliteMigrationFaultInjector? _faultInjector;
    private readonly TimeProvider _timeProvider;

    public SqliteMigrationRunner(
        SqliteConnectionFactory connectionFactory,
        ISqliteMigrationFaultInjector? faultInjector = null,
        TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory;
        _faultInjector = faultInjector;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<MigrationOutcome> ApplyAsync(CancellationToken cancellationToken)
    {
        ValidateLedger();
        var resources = ResolveResources();
        var databasePath = Path.GetFullPath(_connectionFactory.DatabasePath);
        var existedBeforeRun = File.Exists(databasePath) && new FileInfo(databasePath).Length > 0;
        string? backupPath = null;
        List<string> newlyApplied = [];
        IReadOnlyList<string> fromNewerBuild = [];
        Exception? failure = null;

        try
        {
            await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureMigrationTableAsync(connection, cancellationToken).ConfigureAwait(false);
            await VerifyOpenDatabaseAsync(connection, cancellationToken).ConfigureAwait(false);
            var applied = await GetAppliedAsync(connection, cancellationToken).ConfigureAwait(false);
            var known = SqliteMigrationLedger.Entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
            var pending = SqliteMigrationLedger.Entries.Where(entry => !applied.Contains(entry.Id)).ToArray();
            fromNewerBuild = applied.Where(version => !known.Contains(version)).Order(StringComparer.Ordinal).ToArray();

            // A fully migrated database may carry additive work from a newer build and remain
            // readable here. A mixed ledger is different: applying an older missing migration
            // to a schema this binary does not understand is a destructive sidegrade, not an
            // availability fallback.
            if (fromNewerBuild.Count > 0 && pending.Length > 0)
            {
                throw new NewerSchemaCompatibilityException();
            }

            if (existedBeforeRun && applied.Count > 0 && pending.Any(entry => entry.IsDestructive))
            {
                var upcoming = pending.Last(entry => entry.IsDestructive).Id;
                backupPath = await BackUpAndVerifyAsync(connection, upcoming, cancellationToken).ConfigureAwait(false);
            }

            foreach (var definition in pending)
            {
                await ApplyOneAsync(connection, resources[definition.Id].Upgrade, definition.Id, cancellationToken)
                    .ConfigureAwait(false);
                newlyApplied.Add(definition.Id);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (failure is null)
        {
            if (backupPath is not null)
            {
                PruneBackups(databasePath, backupPath);
            }

            return new(newlyApplied, fromNewerBuild, backupPath);
        }

        if (backupPath is null)
        {
            if (failure is NewerSchemaCompatibilityException)
            {
                throw new SqliteMigrationException(
                    "The database contains migrations from a newer build while known migrations are missing; this build refused to modify the newer schema.",
                    MigrationRecoveryState.OriginalIntact,
                    null,
                    failure);
            }

            var recoverable = await FindNewestVerifiedBackupAsync(databasePath).ConfigureAwait(false);
            throw new SqliteMigrationException(
                recoverable is null
                    ? "The migration failed before changing a database with a verified recovery copy."
                    : "The migration failed; a previously verified recovery copy remains available.",
                recoverable is null ? MigrationRecoveryState.OriginalIntact : MigrationRecoveryState.RecoverableBackupAvailable,
                recoverable,
                failure);
        }

        try
        {
            // Recovery must finish even when the initiating cancellation token caused the
            // interruption. Stopping halfway through restoration would turn cancellation into
            // database loss; the bounded local copy is the cleanup for that cancelled work.
            await RestoreVerifiedBackupAsync(databasePath, backupPath, CancellationToken.None).ConfigureAwait(false);
            throw new SqliteMigrationException(
                "The migration failed and the verified pre-migration database was restored.",
                MigrationRecoveryState.RestoredVerifiedBackup,
                backupPath,
                failure);
        }
        catch (SqliteMigrationException)
        {
            throw;
        }
        catch (Exception restoreFailure)
        {
            throw new SqliteMigrationException(
                "The migration and automatic restore both failed; the verified backup remains available.",
                MigrationRecoveryState.RecoverableBackupAvailable,
                backupPath,
                new AggregateException(failure, restoreFailure));
        }
    }

    /// <summary>Returns embedded upgrade/rollback SQL for deterministic migration fixtures.</summary>
    public static (string UpgradeSql, string RollbackSql) ReadFixture(string migrationId)
    {
        ValidateLedger();
        var resources = ResolveResources();
        if (!resources.TryGetValue(migrationId, out var pair))
        {
            throw new KeyNotFoundException($"Migration '{migrationId}' has no embedded fixture pair.");
        }

        return (ReadResource(pair.Upgrade, CancellationToken.None).GetAwaiter().GetResult(),
            ReadResource(pair.Rollback, CancellationToken.None).GetAwaiter().GetResult());
    }

    private async Task<string> BackUpAndVerifyAsync(
        SqliteConnection connection,
        string upcomingVersion,
        CancellationToken cancellationToken)
    {
        var databasePath = Path.GetFullPath(connection.DataSource);
        var directory = Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("The SQLite database must have a parent directory.");
        var stem = Path.GetFileNameWithoutExtension(databasePath);
        var destination = Path.Combine(directory, $"{stem}.pre-{upcomingVersion}-{Guid.NewGuid():N}.db");

        try
        {
            await InjectAsync(SqliteMigrationFaultPoint.BeforeBackup, upcomingVersion, databasePath, cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $destination;";
            command.Parameters.AddWithValue("$destination", destination);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await InjectAsync(SqliteMigrationFaultPoint.AfterBackupCreated, upcomingVersion, databasePath, cancellationToken)
                .ConfigureAwait(false);
            if (!await VerifyDatabaseFileAsync(destination).ConfigureAwait(false))
            {
                throw new InvalidDataException("The pre-migration backup failed SQLite integrity verification.");
            }

            await InjectAsync(SqliteMigrationFaultPoint.AfterBackupVerified, upcomingVersion, databasePath, cancellationToken)
                .ConfigureAwait(false);
            // Verification is a boundary, not a permanent property. A disk or external process can
            // alter the copy immediately afterwards, and fault tests do exactly that. Recheck after
            // the injection boundary before any migration SQL is allowed to run.
            if (!await VerifyDatabaseFileAsync(destination).ConfigureAwait(false))
            {
                throw new InvalidDataException("The verified pre-migration backup changed before migration began.");
            }

            return destination;
        }
        catch
        {
            try { File.Delete(destination); } catch (IOException) { }
            throw;
        }
    }

    private async Task RestoreVerifiedBackupAsync(
        string databasePath,
        string backupPath,
        CancellationToken cancellationToken)
    {
        if (!await VerifyDatabaseFileAsync(backupPath).ConfigureAwait(false))
        {
            throw new InvalidDataException("The recovery copy no longer passes SQLite integrity verification.");
        }

        await InjectAsync(SqliteMigrationFaultPoint.BeforeRestore, null, databasePath, cancellationToken).ConfigureAwait(false);
        var directory = Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("The SQLite database must have a parent directory.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(databasePath)}.restore-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(backupPath, temporary, overwrite: false);
            await InjectAsync(SqliteMigrationFaultPoint.AfterRestoreCopy, null, databasePath, cancellationToken)
                .ConfigureAwait(false);
            if (!await VerifyDatabaseFileAsync(temporary).ConfigureAwait(false))
            {
                throw new InvalidDataException("The staged recovery copy failed SQLite integrity verification.");
            }

            SqliteConnection.ClearAllPools();
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");
            File.Move(temporary, databasePath, overwrite: true);
            if (!await VerifyDatabaseFileAsync(databasePath).ConfigureAwait(false))
            {
                throw new InvalidDataException("The restored database failed SQLite integrity verification.");
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private async Task ApplyOneAsync(
        SqliteConnection connection,
        string resourceName,
        string version,
        CancellationToken cancellationToken)
    {
        await InjectAsync(SqliteMigrationFaultPoint.BeforeMigration, version, connection.DataSource, cancellationToken)
            .ConfigureAwait(false);
        var sql = await ReadResource(resourceName, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var migration = connection.CreateCommand())
        {
            migration.Transaction = transaction;
            migration.CommandText = sql;
            await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InjectAsync(SqliteMigrationFaultPoint.AfterMigrationSql, version, connection.DataSource, cancellationToken)
            .ConfigureAwait(false);
        await using (var record = connection.CreateCommand())
        {
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO schema_migrations(version, applied_utc) VALUES ($version, $appliedUtc);";
            record.Parameters.AddWithValue("$version", version);
            record.Parameters.AddWithValue(
                "$appliedUtc",
                _timeProvider.GetUtcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InjectAsync(SqliteMigrationFaultPoint.BeforeMigrationCommit, version, connection.DataSource, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private ValueTask InjectAsync(
        SqliteMigrationFaultPoint point,
        string? migrationId,
        string databasePath,
        CancellationToken cancellationToken) =>
        _faultInjector?.InjectAsync(point, migrationId, databasePath, cancellationToken) ?? ValueTask.CompletedTask;

    private static void ValidateLedger()
    {
        var ids = SqliteMigrationLedger.Entries.Select(entry => entry.Id).ToArray();
        if (ids.Length == 0 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
            !ids.SequenceEqual(ids.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            ids.Any(id => id.Length < 6 || !id.AsSpan(0, 4).ToString().All(char.IsAsciiDigit) || id[4] != '_'))
        {
            throw new InvalidOperationException("The SQLite migration ledger must contain unique ordered identifiers.");
        }
    }

    private static Dictionary<string, (string Upgrade, string Rollback)> ResolveResources()
    {
        var names = typeof(SqliteMigrationRunner).Assembly.GetManifestResourceNames();
        var result = new Dictionary<string, (string Upgrade, string Rollback)>(StringComparer.Ordinal);
        foreach (var definition in SqliteMigrationLedger.Entries)
        {
            var upgrade = names.SingleOrDefault(name => name.EndsWith(definition.UpgradeResourceSuffix, StringComparison.Ordinal));
            var rollback = names.SingleOrDefault(name => name.EndsWith(definition.RollbackResourceSuffix, StringComparison.Ordinal));
            if (upgrade is null || rollback is null)
            {
                throw new InvalidOperationException($"Migration '{definition.Id}' must have one embedded upgrade and rollback fixture.");
            }

            result.Add(definition.Id, (upgrade, rollback));
        }

        var unreserved = names
            .Where(name => name.Contains(".Persistence.Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .Where(name => !result.Values.Any(pair => pair.Upgrade == name || pair.Rollback == name))
            .ToArray();
        if (unreserved.Length > 0)
        {
            throw new InvalidOperationException($"Unreserved migration resources: {string.Join(", ", unreserved)}");
        }

        return result;
    }

    private static async Task<string> ReadResource(string resourceName, CancellationToken cancellationToken)
    {
        var assembly = typeof(SqliteMigrationRunner).Assembly;
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureMigrationTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version TEXT PRIMARY KEY,
                applied_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HashSet<string>> GetAppliedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var applied = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_migrations;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied.Add(reader.GetString(0));
        }

        return applied;
    }

    private static async Task VerifyOpenDatabaseAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SQLite quick_check failed: {result ?? "no result"}.");
        }
    }

    private static async Task<bool> VerifyDatabaseFileAsync(string path)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            await VerifyOpenDatabaseAsync(connection, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or InvalidDataException)
        {
            return false;
        }
    }

    private static async Task<string?> FindNewestVerifiedBackupAsync(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(databasePath);
        foreach (var candidate in new DirectoryInfo(directory).GetFiles($"{stem}.pre-*.db").OrderByDescending(file => file.LastWriteTimeUtc))
        {
            if (await VerifyDatabaseFileAsync(candidate.FullName).ConfigureAwait(false))
            {
                return candidate.FullName;
            }
        }

        return null;
    }

    private static void PruneBackups(string databasePath, string protectedBackupPath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(databasePath);
        foreach (var stale in new DirectoryInfo(directory)
            .GetFiles($"{stem}.pre-*.db")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(BackupsKept))
        {
            if (!string.Equals(stale.FullName, protectedBackupPath, StringComparison.Ordinal))
            {
                try
                {
                    stale.Delete();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A stale backup occupying disk is less dangerous than deleting the wrong
                    // recovery copy. Maintenance reports the leftover file on its next dry run.
                }
            }
        }
    }

    private sealed class NewerSchemaCompatibilityException()
        : InvalidOperationException("A mixed newer schema cannot be migrated by an older build.");
}
