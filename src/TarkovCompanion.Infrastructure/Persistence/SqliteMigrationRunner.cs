using System.Reflection;
using Microsoft.Data.Sqlite;

namespace TarkovCompanion.Infrastructure.Persistence;

public sealed class SqliteMigrationRunner(SqliteConnectionFactory connectionFactory)
{
    private const string ResourceMarker = ".Persistence.Migrations.";

    public async Task<IReadOnlyList<string>> ApplyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureMigrationTableAsync(connection, cancellationToken).ConfigureAwait(false);
        var applied = await GetAppliedAsync(connection, cancellationToken).ConfigureAwait(false);
        var assembly = typeof(SqliteMigrationRunner).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(x => x.Contains(ResourceMarker, StringComparison.Ordinal) && x.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();

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

        return newlyApplied;
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
