using System.Diagnostics;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Infrastructure.Persistence;

namespace TarkovCompanion.UnitTests.Runtime;

/// <summary>
/// #453: a database call made from the interface thread must not run on the interface thread.
/// </summary>
/// <remarks>
/// Microsoft.Data.Sqlite's asynchronous methods are synchronous, so until
/// <see cref="SqliteConnectionFactory.OpenAsync"/> left the caller's thread itself, a view model
/// that awaited a repository from the dispatcher ran the whole query on the dispatcher — and, when
/// the background refresh held the write lock, waited for that lock on the dispatcher too. Both
/// tests stand a plain dedicated thread in for the interface thread, because what matters about it
/// is only that it is not a pool thread and that somebody is waiting for it to come back.
/// </remarks>
public sealed class SqliteLeavesTheCallersThreadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tc-sqlite-thread-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ARepositoryCallStartedOnAnInterfaceThreadDoesItsWorkOnAnother()
    {
        var factory = new SqliteConnectionFactory(new(Path.Combine(_directory, "thread.db")));
        var callerThread = 0;
        var workThread = 0;
        Task? pending = null;

        RunOnADedicatedThread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            pending = RepositoryShapedReadAsync(factory, thread => workThread = thread);
        });

        await pending!.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotEqual(0, workThread);
        Assert.NotEqual(callerThread, workThread);
    }

    [Fact]
    public async Task AWriteQueuedBehindAnotherWriterGivesTheInterfaceThreadBackAtOnce()
    {
        var path = Path.Combine(_directory, "locked.db");
        var factory = new SqliteConnectionFactory(new(path) { BusyTimeout = TimeSpan.FromSeconds(10) });
        await using (var setup = await factory.OpenAsync(CancellationToken.None))
        {
            await using var create = setup.CreateCommand();
            create.CommandText = "CREATE TABLE note(value TEXT NOT NULL);";
            await create.ExecuteNonQueryAsync();
        }

        // Somebody else is writing, the way the background refresh does for seconds at a time.
        await using var holder = await factory.OpenAsync(CancellationToken.None);
        await using (var begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync();
        }

        Task? pending = null;
        var heldTheCallerFor = TimeSpan.Zero;
        RunOnADedicatedThread(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            pending = RepositoryShapedWriteAsync(factory);
            heldTheCallerFor = stopwatch.Elapsed;
        });

        // The write is still waiting for the lock, and the thread that asked for it is not.
        Assert.False(pending!.IsCompleted);
        Assert.True(
            heldTheCallerFor < TimeSpan.FromSeconds(1),
            $"the calling thread was held for {heldTheCallerFor.TotalMilliseconds:0} ms behind another writer's lock");

        await using (var commit = holder.CreateCommand())
        {
            commit.CommandText = "COMMIT;";
            await commit.ExecuteNonQueryAsync();
        }

        await pending.WaitAsync(TimeSpan.FromSeconds(20));
        await using var read = holder.CreateCommand();
        read.CommandText = "SELECT COUNT(*) FROM note;";
        Assert.Equal(1L, await read.ExecuteScalarAsync());
    }

    /// <summary>The shape every repository has: open first, awaited without the context, then query.</summary>
    private static async Task RepositoryShapedReadAsync(SqliteConnectionFactory factory, Action<int> ranOn)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        ranOn(Environment.CurrentManagedThreadId);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        await command.ExecuteScalarAsync().ConfigureAwait(false);
    }

    private static async Task RepositoryShapedWriteAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO note(value) VALUES ('written');";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static void RunOnADedicatedThread(Action work)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "stand-in interface thread",
        };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the stand-in interface thread never came back");
        Assert.Null(failure);
    }
}
