using System.Runtime.CompilerServices;
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

    /// <summary>
    /// Opens a connection, and leaves the caller's thread first if that thread is an interface thread.
    /// </summary>
    /// <remarks>
    /// Microsoft.Data.Sqlite has no asynchronous I/O: every <c>...Async</c> call on it runs to
    /// completion on the thread that called it and returns a finished task. A view model that awaits
    /// a repository from the dispatcher therefore never leaves the dispatcher, and an entire load —
    /// every query, every row read — happens inside one dispatcher turn. Measured on 2026-09-20 with
    /// the render tool's <c>--ui-stalls</c>: the ten startup pages ran as a single 2,565 ms turn, and
    /// opening Plan as a single 1,337 ms turn, on a fast disk with nothing else writing. With the
    /// background refresh holding the write lock, a write from the interface waited on it for up to
    /// the 30-second command timeout, on the interface thread (#453, "the app freezes").
    ///
    /// Every repository opens its connection first and awaits that with
    /// <c>ConfigureAwait(false)</c>. So yielding to the thread pool here, once, moves the whole of
    /// that repository call — the open, the queries, the mapping — off the caller's thread, and the
    /// caller's own <c>await</c> brings only the finished result back.
    ///
    /// The test is "am I already on the pool", not "is there a synchronization context". An
    /// interface thread is never a pool thread, but it does not always carry a context: the render
    /// tool's main thread has none outside a dispatcher operation, and the first version of this,
    /// which asked for one, left Plan's whole read on that thread. A background service already on
    /// the pool gains nothing from another hop and does not take one.
    ///
    /// #270: it returns <see cref="SqliteOpening"/>, not a task, because a task could not keep that
    /// promise. A pooled open takes microseconds, so the pool could finish it before the caller
    /// reached its <c>await</c>; an await on a finished task does not wait, and the whole repository
    /// call then ran on the interface thread after all. The render tool's thread guard caught the
    /// map's quest-progress read doing exactly that, <c>BEGIN</c> included, on a loaded host. The
    /// opening never lets a caller that is not a pool thread continue inline.
    /// </remarks>
    public SqliteOpening OpenAsync(CancellationToken cancellationToken) =>
        Thread.CurrentThread.IsThreadPoolThread
            ? new(OpenCoreAsync(cancellationToken), leaveCaller: false)
            : new(Task.Run(() => OpenCoreAsync(cancellationToken), cancellationToken), leaveCaller: true);

    private async Task<SqliteConnection> OpenCoreAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(options.DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        Track(connection);
        SqliteInterfaceThreadGuard.Attach(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = options.OpenPragmas;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static int _openConnections;

    /// <summary>How many connections this process has open right now: database calls in flight.</summary>
    /// <remarks>
    /// Every repository opens one connection per call and disposes it when the call returns, so
    /// this is how many calls are under way. It exists because database work no longer happens
    /// inside the caller's dispatcher turn: a tool that photographs a page has to be able to ask
    /// whether the page has finished reading, rather than pumping a fixed number of turns and
    /// hoping, which is what silently produced half-loaded renders the first time this moved.
    /// </remarks>
    public static int OpenConnectionCount => Volatile.Read(ref _openConnections);

    private static void Track(SqliteConnection connection)
    {
        Interlocked.Increment(ref _openConnections);
        var counted = 1;
        connection.StateChange += (_, change) =>
        {
            if (change.CurrentState == System.Data.ConnectionState.Closed && Interlocked.Exchange(ref counted, 0) == 1)
            {
                Interlocked.Decrement(ref _openConnections);
            }
        };
    }
}

/// <summary>
/// A connection being opened, awaited like a task, whose continuation never runs on the thread that
/// asked for it unless that thread is a pool thread.
/// </summary>
/// <remarks>
/// #270. <see cref="SqliteConnectionFactory.OpenAsync"/> starts the open on the pool so that the rest
/// of a repository call — every query, and every wait for another writer's lock — runs there too.
/// With a plain task that held only while the open was still running when the caller awaited it:
/// a finished task makes <c>await</c> continue inline, on the interface thread. This awaiter reports
/// itself unfinished to such a caller and resumes it on the pool, so how quickly the open happened
/// to finish no longer decides which thread the queries run on. <c>ConfigureAwait</c> is accepted,
/// because every repository writes it, and changes nothing: coming back to the caller's context is
/// exactly what this exists to prevent.
/// </remarks>
public readonly struct SqliteOpening
{
    private readonly Task<SqliteConnection> _task;
    private readonly bool _leaveCaller;

    internal SqliteOpening(Task<SqliteConnection> task, bool leaveCaller)
    {
        _task = task;
        _leaveCaller = leaveCaller;
    }

    public SqliteOpening ConfigureAwait(bool continueOnCapturedContext) => this;

    public Awaiter GetAwaiter() => new(_task, _leaveCaller);

    public readonly struct Awaiter : ICriticalNotifyCompletion
    {
        private readonly Task<SqliteConnection> _task;
        private readonly bool _leaveCaller;

        internal Awaiter(Task<SqliteConnection> task, bool leaveCaller)
        {
            _task = task;
            _leaveCaller = leaveCaller;
        }

        public bool IsCompleted => _task.IsCompleted && (!_leaveCaller || Thread.CurrentThread.IsThreadPoolThread);

        /// <summary>The connection; blocks until it is open when called before the open finished.</summary>
        public SqliteConnection GetResult() => _task.GetAwaiter().GetResult();

        public void OnCompleted(Action continuation)
        {
            if (_task.IsCompleted)
            {
                ThreadPool.QueueUserWorkItem(static next => next(), continuation, preferLocal: false);
                return;
            }

            _task.ConfigureAwait(false).GetAwaiter().OnCompleted(continuation);
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            if (_task.IsCompleted)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static next => next(), continuation, preferLocal: false);
                return;
            }

            _task.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(continuation);
        }
    }
}
