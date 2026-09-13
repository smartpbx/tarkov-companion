using System.Reflection;
using Microsoft.Data.Sqlite;

namespace TarkovCompanion.Infrastructure.Persistence;

/// <summary>What a migration run did, which nothing used to be able to ask.</summary>
/// <param name="Applied">The versions this run brought the database up to.</param>
/// <param name="FromNewerBuild">
/// Versions the database records that this build has never heard of.
/// </param>
/// <param name="BackupPath">The copy taken before anything was applied, if one was.</param>
public sealed record MigrationOutcome(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> FromNewerBuild,
    string? BackupPath)
{
    public static MigrationOutcome Nothing { get; } = new([], [], null);
}

public sealed class SqliteMigrationRunner(SqliteConnectionFactory connectionFactory)
{
    private const string ResourceMarker = ".Persistence.Migrations.";

    /// <summary>
    /// How many copies are kept beside the database.
    /// </summary>
    /// <remarks>
    /// Two, because the question somebody asks is always "before this update" or "before the
    /// one where it broke". Keeping ten would be keeping eight nobody opens, of a file that has
    /// been 114 MB.
    /// </remarks>
    private const int BackupsKept = 2;

    public async Task<MigrationOutcome> ApplyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureMigrationTableAsync(connection, cancellationToken).ConfigureAwait(false);
        var applied = await GetAppliedAsync(connection, cancellationToken).ConfigureAwait(false);
        var assembly = typeof(SqliteMigrationRunner).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(x => x.Contains(ResourceMarker, StringComparison.Ordinal) && x.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var known = resources.Select(ResourceVersion).ToArray();
        var pending = known.Where(version => !applied.Contains(version)).ToArray();

        // A database this build does not fully understand. Reported rather than acted on: the
        // versions it has are still there, the tables they made are still there, and refusing
        // to start would be a worse answer than saying so.
        var fromNewerBuild = applied
            .Where(version => !known.Contains(version, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Only where there is something to lose. A database with nothing applied yet is one
        // this run is about to create, and copying an empty file helps nobody.
        var backup = pending.Length > 0 && applied.Count > 0
            ? await BackUpAsync(connection, pending[^1], cancellationToken).ConfigureAwait(false)
            : null;

        var newlyApplied = new List<string>();
        foreach (var resourceName in resources)
        {
            var version = ResourceVersion(resourceName);
            if (applied.Contains(version))
            {
                continue;
            }

            await ApplyOneAsync(connection, assembly, resourceName, version, cancellationToken).ConfigureAwait(false);
            newlyApplied.Add(version);
        }

        return new(newlyApplied, fromNewerBuild, backup);
    }

    /// <summary>
    /// Copies the database beside itself before anything is changed.
    /// </summary>
    /// <remarks>
    /// Migration 0007 drops tables, and nothing anywhere took a copy first. The file has been
    /// 114 MB, and what is in it — raid history, quest progress, what the player marked — exists
    /// nowhere else and cannot be re-synced from upstream. This is insurance rather than a bug
    /// fix: no migration has lost anything yet, and the cost of finding out the hard way is the
    /// only copy of somebody's wipe.
    ///
    /// VACUUM INTO rather than a file copy, because the database is in WAL mode: the bytes on
    /// disk at any instant are not the database, they are the database minus whatever is still
    /// in the write-ahead log. VACUUM INTO asks SQLite for a consistent copy and gets one, and
    /// it comes out compacted for free.
    ///
    /// A failure here is logged by the caller and does not stop the migration. A backup that
    /// could not be written is worth knowing about; refusing to start the application over it
    /// would turn a precaution into an outage.
    /// </remarks>
    private async Task<string?> BackUpAsync(
        SqliteConnection connection,
        string upcomingVersion,
        CancellationToken cancellationToken)
    {
        var databasePath = connection.DataSource;
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(databasePath);
        var destination = Path.Combine(directory, $"{stem}.pre-{upcomingVersion}.db");
        try
        {
            // VACUUM INTO refuses to overwrite, and a second run of the same pending migration
            // is exactly the case where the existing copy is the one worth keeping — it was
            // taken before the attempt that did not finish.
            if (!File.Exists(destination))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "VACUUM INTO $destination;";
                command.Parameters.AddWithValue("$destination", destination);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            Prune(directory, stem);
            return destination;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Keeps the newest copies and removes the rest.</summary>
    private static void Prune(string directory, string stem)
    {
        try
        {
            foreach (var stale in new DirectoryInfo(directory)
                .GetFiles($"{stem}.pre-*.db")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(BackupsKept))
            {
                stale.Delete();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A copy that could not be removed is a copy taking up room, which is a great deal
            // less serious than the one this whole method exists to keep.
        }
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

    private static async Task ApplyOneAsync(
        SqliteConnection connection,
        Assembly assembly,
        string resourceName,
        string version,
        CancellationToken cancellationToken)
    {
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var migration = connection.CreateCommand();
        migration.Transaction = (SqliteTransaction)transaction;
        migration.CommandText = sql;
        await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var record = connection.CreateCommand();
        record.Transaction = (SqliteTransaction)transaction;
        record.CommandText = "INSERT INTO schema_migrations(version, applied_utc) VALUES ($version, $appliedUtc);";
        record.Parameters.AddWithValue("$version", version);
        record.Parameters.AddWithValue("$appliedUtc", DateTimeOffset.UtcNow.ToString("O"));
        await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ResourceVersion(string resourceName)
    {
        var markerIndex = resourceName.IndexOf(ResourceMarker, StringComparison.Ordinal);
        return resourceName[(markerIndex + ResourceMarker.Length)..^4];
    }
}
