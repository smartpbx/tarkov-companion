using Microsoft.Data.Sqlite;

namespace TarkovCompanion.Infrastructure.Persistence;

public sealed record SqliteDatabaseOptions(string DatabasePath)
{
    public string ConnectionString
    {
        get
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                // Shared cache replaces WAL's reader/writer independence with in-process
                // table locks, and the resulting SQLITE_LOCKED is not covered by
                // busy_timeout. Microsoft.Data.Sqlite then retries with a blocking sleep on
                // the calling thread, so a read taken while the background refresh is
                // writing could stall for the full command timeout.
                Cache = SqliteCacheMode.Private,
                ForeignKeys = true,
                Pooling = true,
            };
            return builder.ToString();
        }
    }
}

public sealed class SqliteConnectionFactory(SqliteDatabaseOptions options)
{
    public string DatabasePath => options.DatabasePath;

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(options.DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
