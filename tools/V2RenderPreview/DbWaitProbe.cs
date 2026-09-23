using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.Infrastructure.Persistence;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// [#270] <c>--db-thread-guard</c>: print every SQLite statement that starts on the interface thread,
/// with its stack. <c>--hold-db-write &lt;ms&gt;</c>: another connection holds the write lock for that
/// long, lets go for 50 ms, and takes it again, for the rest of the run.
/// </summary>
/// <remarks>
/// A statement on the interface thread costs almost nothing on an idle database, so a stall tour on
/// its own cannot see one. Held behind a writer, the same statement waits out the lock on the
/// interface thread, and the tour's "longest input wait" shows it. The holder is what the
/// background refresh, a WAL checkpoint and the maintenance pass do to a player's database; it
/// writes a row each time so the lock it takes is a real write lock, in a table of its own.
/// </remarks>
internal static class DbWaitProbe
{
    private static Thread? _holder;
    private static volatile bool _stop;
    private static int _lastCount;

    public static void EnableGuardIfAsked(IReadOnlyList<string> args)
    {
        if (!args.Contains("--db-thread-guard"))
        {
            return;
        }

        SqliteInterfaceThreadGuard.Enable(
            static () => Dispatcher.UIThread.CheckAccess(),
            static message => Console.WriteLine("[db-thread-guard] " + message));
        Console.WriteLine("[db-thread-guard] on");
    }

    public static void StartHoldIfAsked(IReadOnlyList<string> args, IServiceProvider services)
    {
        var holdMs = IntOption(args, "--hold-db-write");
        if (holdMs <= 0)
        {
            return;
        }

        var path = services.GetRequiredService<SqliteConnectionFactory>().DatabasePath;
        _holder = new Thread(() => Hold(path, TimeSpan.FromMilliseconds(holdMs)))
        {
            IsBackground = true,
            Name = "db write holder",
        };
        _holder.Start();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[hold-db-write] holding the write lock {holdMs} ms at a time on {path}"));
    }

    /// <summary>Statements the guard has seen on the interface thread since the last call, for a step's line.</summary>
    public static void Tally(string step)
    {
        if (!SqliteInterfaceThreadGuard.IsEnabled)
        {
            return;
        }

        var now = SqliteInterfaceThreadGuard.StatementsOnInterfaceThread;
        if (now != _lastCount)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  [db-thread-guard] {now - _lastCount} statements on the interface thread during '{step}'"));
            _lastCount = now;
        }
    }

    public static void Stop()
    {
        _stop = true;
        _holder?.Join(TimeSpan.FromSeconds(30));
        if (SqliteInterfaceThreadGuard.IsEnabled)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[db-thread-guard] total statements on the interface thread: {SqliteInterfaceThreadGuard.StatementsOnInterfaceThread}"));
        }
    }

    private static void Hold(string path, TimeSpan hold)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 60,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout = 60000; CREATE TABLE IF NOT EXISTS render_tool_write_hold(at TEXT NOT NULL);");
        var holds = 0;
        while (!_stop)
        {
            Execute(connection, "BEGIN IMMEDIATE;");
            Execute(connection, "INSERT INTO render_tool_write_hold(at) VALUES (datetime('now'));");
            var until = Stopwatch.StartNew();
            while (!_stop && until.Elapsed < hold)
            {
                Thread.Sleep(10);
            }

            Execute(connection, "COMMIT;");
            holds++;
            Thread.Sleep(50);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[hold-db-write] released after {holds} holds"));
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static int IntOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == name && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return 0;
    }
}
