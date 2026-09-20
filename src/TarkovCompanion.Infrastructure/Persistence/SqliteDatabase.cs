using Microsoft.Data.Sqlite;

namespace TarkovCompanion.Infrastructure.Persistence;

public sealed record SqliteDatabaseOptions(string DatabasePath)
{
    /// <summary>The largest page count SQLite accepts, which is what an uncapped database has.</summary>
    private const long SqliteMaximumPageCount = 4_294_967_294;

    /// <summary>
    /// How long a statement waits for another connection's lock before it fails with SQLITE_BUSY.
    /// Null, which is what production uses, keeps what every launch has always had: SQLite's own
    /// five-second busy handler, inside Microsoft.Data.Sqlite's retry loop, which keeps retrying a
    /// busy statement until the 30-second command timeout. So the wait a player actually sees is the
    /// thirty seconds, and it happens on the calling thread, because that driver's "async" calls are
    /// synchronous. Setting a value bounds both, so a test can prove what a write does when the wait
    /// runs out without waiting half a minute to find out.
    /// </summary>
    public TimeSpan? BusyTimeout { get; init; }

    /// <summary>
    /// Caps the database at this many pages, after which SQLite fails any write that needs a new page
    /// with SQLITE_FULL: the same error, at the same moment, as a disk that has run out of room.
    /// Null (the default, and the only value production uses) leaves the database uncapped. There is
    /// no portable way to fill a disk inside a test, and a test that stubs the error proves nothing
    /// about what the transaction did with the rows it had already written.
    /// </summary>
    public int? MaximumPageCount { get; init; }

    /// <summary>The pragmas every connection is opened with.</summary>
    internal string OpenPragmas
    {
        get
        {
            var busy = BusyTimeout ?? TimeSpan.FromSeconds(5);
            if (busy < TimeSpan.Zero || busy > TimeSpan.FromMinutes(5))
            {
                throw new ArgumentOutOfRangeException(nameof(BusyTimeout), "The busy timeout must be between zero and five minutes.");
            }

            if (MaximumPageCount is <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaximumPageCount), "A page cap must be positive.");
            }

            // Always set, capped or not. A pooled connection keeps its pragmas, so a connection that
            // was opened for a capped database would otherwise hand its cap to the next caller.
            return FormattableString.Invariant(
                $"PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = {(long)busy.TotalMilliseconds}; PRAGMA max_page_count = {MaximumPageCount ?? SqliteMaximumPageCount};");
        }
    }

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
            if (BusyTimeout is { } busy)
            {
                // The driver's own retry loop, which is what actually bounds a busy statement.
                builder.DefaultTimeout = Math.Max(1, (int)Math.Ceiling(busy.TotalSeconds));
            }

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
        command.CommandText = options.OpenPragmas;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
