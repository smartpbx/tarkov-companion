using Microsoft.Data.Sqlite;
using TarkovCompanion.Infrastructure.Persistence;

namespace TarkovCompanion.IntegrationTests;

/// <summary>
/// The copy taken before a migration changes anything.
/// </summary>
/// <remarks>
/// Migration 0007 drops tables and nothing anywhere took a copy first. The file has been 114 MB,
/// and what is in it — raid history, quest progress, what the player marked — exists nowhere else
/// and cannot be re-synced from upstream.
///
/// Insurance rather than a bug fix: no migration has lost anything yet, and the cost of finding
/// out the hard way is the only copy of somebody's wipe.
/// </remarks>
[Collection(SqliteCollection.Name)]
public sealed class MigrationBackupTests
{
    [Fact]
    public async Task A_database_with_something_in_it_is_copied_before_it_is_changed()
    {
        await using var scratch = new Scratch();

        // A database at 0001 only, which is what an installation from before the later
        // migrations looks like.
        await scratch.StopAfterAsync("0001_initial");
        var outcome = await new SqliteMigrationRunner(scratch.Factory).ApplyAsync(CancellationToken.None);

        Assert.NotNull(outcome.BackupPath);
        Assert.True(File.Exists(outcome.BackupPath), "the copy should be on disk beside the database");
    }

    [Fact]
    public async Task The_copy_is_a_database_holding_what_the_original_held()
    {
        // VACUUM INTO rather than a file copy, because WAL means the bytes on disk at any
        // instant are the database minus whatever is still in the write-ahead log. A copy that
        // could not be opened, or that had lost the last write, would be worse than none: it
        // would look like insurance and not be.
        await using var scratch = new Scratch();
        await scratch.StopAfterAsync("0001_initial");
        await scratch.ExecuteAsync(
            "INSERT INTO traders(id, name, source_json) VALUES ('prapor', 'Prapor', '{}');");

        var outcome = await new SqliteMigrationRunner(scratch.Factory).ApplyAsync(CancellationToken.None);

        await using var copy = new SqliteConnection($"Data Source={outcome.BackupPath};Mode=ReadOnly");
        await copy.OpenAsync();
        await using var command = copy.CreateCommand();
        command.CommandText = "SELECT name FROM traders WHERE id = 'prapor';";
        Assert.Equal("Prapor", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_database_being_created_is_not_copied()
    {
        // There is nothing to lose. Copying an empty file on every fresh install would put a
        // second database beside the first for no reason anybody could name.
        await using var scratch = new Scratch();

        var outcome = await new SqliteMigrationRunner(scratch.Factory).ApplyAsync(CancellationToken.None);

        Assert.Null(outcome.BackupPath);
        Assert.NotEmpty(outcome.Applied);
    }

    [Fact]
    public async Task A_database_already_up_to_date_is_not_copied()
    {
        await using var scratch = new Scratch();
        await new SqliteMigrationRunner(scratch.Factory).ApplyAsync(CancellationToken.None);

        var second = await new SqliteMigrationRunner(scratch.Factory).ApplyAsync(CancellationToken.None);

        Assert.Null(second.BackupPath);
        Assert.Empty(second.Applied);
    }

    [Fact]
    public async Task Only_two_copies_are_kept()
    {
        // The question somebody asks is always "before this update" or "before the one where it
        // broke". Keeping ten would be keeping eight nobody opens, of a file that has been
        // 114 MB.
        await using var scratch = new Scratch();
        await scratch.StopAfterAsync("0001_initial");
        foreach (var old in new[] { "0002_old", "0003_old", "0004_old" })
        {
            await File.WriteAllTextAsync(
                Path.Combine(scratch.Directory, $"{Scratch.Stem}.pre-{old}.db"),
                "not really a database, but it is a file with the right name");
        }

        await new SqliteMigrationRunner(scratch.Factory).ApplyAsync(CancellationToken.None);

        Assert.Equal(2, Directory.GetFiles(scratch.Directory, $"{Scratch.Stem}.pre-*.db").Length);
    }

    [Fact]
    public async Task A_version_this_build_has_never_heard_of_is_reported_rather_than_refused()
    {
        // A database written by a newer build. Its tables are still there and its rows are
        // still there, so refusing to start would be a worse answer than saying so — and the
        // Data state can then say "update to use it" instead of the application failing on the
        // first query for a table it has never seen.
        await using var scratch = new Scratch();
        await new SqliteMigrationRunner(scratch.Factory).ApplyAsync(CancellationToken.None);
        await scratch.ExecuteAsync(
            "INSERT INTO schema_migrations(version, applied_utc) VALUES ('0042_from_the_future', '2030-01-01T00:00:00Z');");

        var outcome = await new SqliteMigrationRunner(scratch.Factory).ApplyAsync(CancellationToken.None);

        Assert.Equal(["0042_from_the_future"], outcome.FromNewerBuild);
    }

    [Fact]
    public async Task A_database_this_build_understands_reports_nothing_from_the_future()
    {
        await using var scratch = new Scratch();

        Assert.Empty((await new SqliteMigrationRunner(scratch.Factory).ApplyAsync(CancellationToken.None)).FromNewerBuild);
    }

    // #292 task 3: Setup > Data reads GetStatusAsync/BackUpNowAsync rather than reimplementing
    // the migration/backup logic above.
    [Fact]
    public async Task A_fresh_database_reports_no_version_and_no_backup()
    {
        await using var scratch = new Scratch();
        var runner = new SqliteMigrationRunner(scratch.Factory);

        var status = await runner.GetStatusAsync(CancellationToken.None);

        Assert.Null(status.CurrentVersion);
        Assert.Null(status.LastAppliedUtc);
        Assert.Null(status.LastVerifiedBackupPath);
    }

    [Fact]
    public async Task StatusReportsTheNewestAppliedMigration()
    {
        await using var scratch = new Scratch();
        await new SqliteMigrationRunner(scratch.Factory).ApplyAsync(CancellationToken.None);

        var status = await new SqliteMigrationRunner(scratch.Factory).GetStatusAsync(CancellationToken.None);

        Assert.NotNull(status.CurrentVersion);
        Assert.NotNull(status.LastAppliedUtc);
        Assert.Equal(SqliteMigrationLedger.Entries[^1].Id, status.CurrentVersion);
    }

    [Fact]
    public async Task BackUpNowMakesAVerifiedCopyStatusThenReports()
    {
        await using var scratch = new Scratch();
        await new SqliteMigrationRunner(scratch.Factory).ApplyAsync(CancellationToken.None);
        var runner = new SqliteMigrationRunner(scratch.Factory);

        var backupPath = await runner.BackUpNowAsync(CancellationToken.None);
        var status = await runner.GetStatusAsync(CancellationToken.None);

        Assert.True(File.Exists(backupPath));
        Assert.Equal(backupPath, status.LastVerifiedBackupPath);
        Assert.Equal(new FileInfo(backupPath).Length, status.LastVerifiedBackupBytes);
        Assert.NotNull(status.LastVerifiedBackupUtc);
    }

    [Fact]
    public async Task BackUpNowIsAlsoKeptUnderTheSameTwoBackupRetention()
    {
        await using var scratch = new Scratch();
        await new SqliteMigrationRunner(scratch.Factory).ApplyAsync(CancellationToken.None);
        var runner = new SqliteMigrationRunner(scratch.Factory);

        await runner.BackUpNowAsync(CancellationToken.None);
        await runner.BackUpNowAsync(CancellationToken.None);
        await runner.BackUpNowAsync(CancellationToken.None);

        Assert.Equal(2, Directory.GetFiles(scratch.Directory, $"{Scratch.Stem}.pre-*.db").Length);
    }

    private sealed class Scratch : IAsyncDisposable
    {
        public const string Stem = "companion";

        public Scratch()
        {
            Directory = Path.Combine(Path.GetTempPath(), $"tarkov-migration-backup-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
            Factory = new(new(Path.Combine(Directory, $"{Stem}.db")));
        }

        public string Directory { get; }

        public SqliteConnectionFactory Factory { get; }

        /// <summary>
        /// Brings the database up to one migration and tells it that is all there is.
        /// </summary>
        /// <remarks>
        /// Applying everything and then deleting rows from schema_migrations would leave a
        /// database whose tables do not match its record, and re-running those migrations
        /// against it would fail for reasons that have nothing to do with what is being tested.
        /// Running one and stopping is the state an older installation is actually in.
        /// </remarks>
        public async Task StopAfterAsync(string version)
        {
            await using var connection = await Factory.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version TEXT PRIMARY KEY,
                    applied_utc TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();

            var assembly = typeof(SqliteMigrationRunner).Assembly;
            var resource = assembly.GetManifestResourceNames()
                .Single(name => name.EndsWith($".{version}.sql", StringComparison.Ordinal));
            await using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);

            command.CommandText = await reader.ReadToEndAsync();
            await command.ExecuteNonQueryAsync();
            command.CommandText =
                "INSERT INTO schema_migrations(version, applied_utc) VALUES ($version, '2026-01-01T00:00:00Z');";
            command.Parameters.AddWithValue("$version", version);
            await command.ExecuteNonQueryAsync();
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = await Factory.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(Directory))
            {
                ScratchDirectory.Remove(Directory);
            }

            return ValueTask.CompletedTask;
        }
    }
}
