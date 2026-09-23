using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TarkovCompanion.Infrastructure.Persistence;

/// <summary>
/// Diagnostic: says so, with the caller's stack, whenever a SQLite statement starts on the
/// interface thread.
/// </summary>
/// <remarks>
/// #270. <see cref="SqliteConnectionFactory.OpenAsync"/> moves a repository call off the interface
/// thread by hopping before the open, which only helps the calls that go through an open. A
/// statement run on a connection somebody already holds, a synchronous wait on a task that is
/// doing database work, or a lock held across a query all put the interface thread back behind a
/// writer's lock — up to the 30-second command timeout, because Microsoft.Data.Sqlite's "async"
/// calls are synchronous. None of those is visible from the call site, so this watches the one
/// place every statement passes: SQLite's own trace callback, which runs on the thread executing
/// the statement, before it waits for any lock.
///
/// Off unless enabled, and when off it costs nothing: no callback is installed. It is a finder for
/// a developer, not a production feature, so it reports and never throws. Reports are rate-limited
/// per statement: the first time a statement is seen on the interface thread, then at most once
/// per <see cref="ReportInterval"/> with a count, so a loop over rows cannot flood a log.
/// </remarks>
public static class SqliteInterfaceThreadGuard
{
    /// <summary>How long a statement that keeps running on the interface thread waits between reports.</summary>
    public static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(10);

    /// <summary>How many distinct statements are remembered before new ones are reported without a count.</summary>
    private const int MaximumTracked = 512;

    private static readonly Dictionary<string, (long LastReported, int Unreported)> Seen = new(StringComparer.Ordinal);
    private static volatile Func<bool>? _isInterfaceThread;
    private static volatile Action<string>? _report;
    private static int _statementsOnInterfaceThread;

    // Kept in a field: SQLitePCL holds the delegate for the connection's lifetime, and a lambda
    // allocated per open would be one more object per connection for no reason.
    private static readonly SQLitePCL.delegate_trace Trace = OnStatement;

    public static bool IsEnabled => _isInterfaceThread is not null;

    /// <summary>Statements that have started on the interface thread since <see cref="Enable"/>.</summary>
    public static int StatementsOnInterfaceThread => Volatile.Read(ref _statementsOnInterfaceThread);

    /// <param name="isInterfaceThread">True on the thread that must never wait for the database.</param>
    /// <param name="report">Where a finding goes; called on the offending thread, so it must be quick.</param>
    public static void Enable(Func<bool> isInterfaceThread, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(isInterfaceThread);
        ArgumentNullException.ThrowIfNull(report);
        lock (Seen)
        {
            Seen.Clear();
        }

        Interlocked.Exchange(ref _statementsOnInterfaceThread, 0);
        _report = report;
        _isInterfaceThread = isInterfaceThread;
    }

    public static void Disable()
    {
        _isInterfaceThread = null;
        _report = null;
    }

    /// <summary>Installs the trace on a connection that has just been opened, when the guard is on.</summary>
    /// <remarks>
    /// Installed on every open, not once: pooled connections are reused, but a connection opened
    /// before <see cref="Enable"/> was called would otherwise never be watched. Installing again
    /// replaces the callback rather than adding a second one.
    /// </remarks>
    internal static void Attach(SqliteConnection connection)
    {
        if (_isInterfaceThread is null || connection.Handle is not { } handle)
        {
            return;
        }

        SQLitePCL.raw.sqlite3_trace(handle, Trace, null);
    }

    private static void OnStatement(object userData, SQLitePCL.utf8z statement)
    {
        try
        {
            if (_isInterfaceThread is not { } isInterfaceThread || !isInterfaceThread())
            {
                return;
            }

            Interlocked.Increment(ref _statementsOnInterfaceThread);
            var sql = statement.utf8_to_string() ?? string.Empty;
            var unreported = Admit(sql, Stopwatch.GetTimestamp());
            if (unreported < 0)
            {
                return;
            }

            _report?.Invoke(Describe(sql, unreported, new StackTrace(1, fNeedFileInfo: false).ToString()));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A diagnostic must never fail the statement it is watching.
        }
    }

    /// <summary>
    /// Whether this sighting is reported: the number of earlier sightings it stands for, or -1 if it
    /// is held back.
    /// </summary>
    internal static int Admit(string sql, long timestamp)
    {
        var key = Key(sql);
        lock (Seen)
        {
            if (!Seen.TryGetValue(key, out var entry))
            {
                if (Seen.Count < MaximumTracked)
                {
                    Seen[key] = (timestamp, 0);
                }

                return 0;
            }

            if (Stopwatch.GetElapsedTime(entry.LastReported, timestamp) < ReportInterval)
            {
                Seen[key] = entry with { Unreported = entry.Unreported + 1 };
                return -1;
            }

            Seen[key] = (timestamp, 0);
            return entry.Unreported;
        }
    }

    internal static string Describe(string sql, int unreported, string stack)
    {
        var shown = Key(sql);
        var more = unreported > 0
            ? string.Create(CultureInfo.InvariantCulture, $" ({unreported} more since the last report)")
            : string.Empty;
        return $"SQLite statement started on the interface thread{more}: {shown}{Environment.NewLine}{stack}";
    }

    /// <summary>The statement's first line, trimmed: a key that tells call sites apart without holding whole scripts.</summary>
    private static string Key(string sql)
    {
        var text = sql.AsSpan().Trim();
        var newline = text.IndexOfAny('\r', '\n');
        if (newline >= 0)
        {
            text = text[..newline];
        }

        return text.Length > 160 ? string.Concat(text[..160], "…") : text.ToString();
    }
}
