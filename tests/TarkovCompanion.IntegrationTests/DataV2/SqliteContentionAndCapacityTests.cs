using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.IntegrationTests.DataV2;

/// <summary>
/// #270: what a write does when the database is locked by someone else, and when it has nowhere to
/// put the next page. Both are injected for real: a second connection holds the write lock, and
/// <see cref="SqliteDatabaseOptions.MaximumPageCount"/> makes SQLite raise SQLITE_FULL at the exact
/// moment a full disk would. The previous "disk-full" test threw an <see cref="IOException"/> from a
/// fault hook, which proves the migration runner handles an exception and says nothing about what
/// SQLite left behind.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteContentionAndCapacityTests
{
    private const int SqliteBusy = 5;
    private const int SqliteFull = 13;
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> LockableWrites => ["response-cache", "profile-workspace", "retention-settings"];

    public static TheoryData<string> GrowingWrites => ["response-cache", "profile-workspace"];

    [Theory]
    [MemberData(nameof(LockableWrites))]
    public async Task AWriteWaitsOutABriefLockAndThenLands(string write)
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var factory = new SqliteConnectionFactory(new(database.Path));
        var probe = Probe.For(write, large: false);
        await using var holder = await HoldWriteLockAsync(database.Path);

        var stopwatch = Stopwatch.StartNew();
        // On its own thread: Microsoft.Data.Sqlite's "async" calls run synchronously, so a locked write
        // blocks whoever called it, which is a fact about the driver these tests must not share.
        var pending = Task.Run(() => probe.WriteAsync(factory, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var early = await Task.WhenAny(pending, Task.Delay(400, TestContext.Current.CancellationToken));

        Assert.NotSame(pending, early);
        Assert.Equal(probe.Before, await probe.StateAsync(factory, TestContext.Current.CancellationToken));

        await holder.ReleaseAsync();
        await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(350), "the write must have waited for the lock, not skipped it");
        Assert.Equal(probe.After, await probe.StateAsync(factory, TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(LockableWrites))]
    public async Task AWriteThatOutlastsTheLockFailsAsBusyChangesNothingAndSucceedsOnceFreed(string write)
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var impatient = new SqliteConnectionFactory(new(database.Path) { BusyTimeout = TimeSpan.FromSeconds(1) });
        var probe = Probe.For(write, large: false);
        await using var holder = await HoldWriteLockAsync(database.Path);

        var failure = await Assert.ThrowsAsync<SqliteException>(() =>
            Task.Run(() => probe.WriteAsync(impatient, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));

        Assert.Equal(SqliteBusy, failure.SqliteErrorCode);
        await holder.ReleaseAsync();
        Assert.Equal(probe.Before, await probe.StateAsync(impatient, TestContext.Current.CancellationToken));
        Assert.Equal("ok", await IntegrityAsync(impatient));

        // The same write, unchanged, is all it takes once the other connection lets go.
        await probe.WriteAsync(impatient, TestContext.Current.CancellationToken);
        Assert.Equal(probe.After, await probe.StateAsync(impatient, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadersAreNotBlockedByAConnectionHoldingTheWriteLock()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var impatient = new SqliteConnectionFactory(new(database.Path) { BusyTimeout = TimeSpan.FromSeconds(1) });
        await Probe.For("profile-workspace", large: false).WriteAsync(impatient, TestContext.Current.CancellationToken);
        await using var holder = await HoldWriteLockAsync(database.Path);

        // Write-ahead logging is the reason a refresh writing in the background does not freeze the
        // page a player is reading, and it only holds if the read does not ask for the write lock too.
        // These readers each opened BEGIN IMMEDIATE, which does, and so waited behind the writer for the
        // whole busy timeout; they are one snapshot read now, and none of them waits.
        var reads = new (string Name, Func<Task> Read)[]
        {
            ("profile workspace", async () =>
                Assert.Equal(1, (await new SqliteProfileWorkspaceStore(impatient).ReadAsync(TestContext.Current.CancellationToken)).Revision)),
            ("quest progress", async () =>
                Assert.Empty((await new SqliteQuestProgressStore(impatient).GetAsync(
                    new(Guid.Parse("00000000-0000-0000-0000-0000000000f2"), TarkovCompanion.Core.Common.GameMode.Regular, "generation-1"),
                    TestContext.Current.CancellationToken)).Tasks)),
            ("outbox snapshot", async () =>
                await new SqliteOutboxStore(impatient).GetSnapshotAsync(Now, TestContext.Current.CancellationToken)),
        };
        foreach (var (name, read) in reads)
        {
            var stopwatch = Stopwatch.StartNew();
            await Task.Run(read, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(800), $"{name} waited {stopwatch.Elapsed.TotalMilliseconds:N0} ms behind the writer");
        }
    }

    [Theory]
    [MemberData(nameof(GrowingWrites))]
    public async Task AWriteThatNeedsANewPageOnAFullDatabaseFailsAsFullAndKeepsWhatWasThere(string write)
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var roomy = new SqliteConnectionFactory(new(database.Path));
        var earlier = Probe.For("response-cache", large: false, key: "regular/earlier");
        await earlier.WriteAsync(roomy, TestContext.Current.CancellationToken);
        var full = new SqliteConnectionFactory(new(database.Path) { MaximumPageCount = await PageCountAsync(roomy) });
        var probe = Probe.For(write, large: true);
        var stateBefore = await probe.StateAsync(full, TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<SqliteException>(() =>
            probe.WriteAsync(full, TestContext.Current.CancellationToken));

        Assert.Equal(SqliteFull, failure.SqliteErrorCode);
        // Nothing of the failed write is visible, and what was there before is intact and readable.
        Assert.Equal(stateBefore, await probe.StateAsync(full, TestContext.Current.CancellationToken));
        Assert.Equal("ok", await IntegrityAsync(full));
        var kept = await new SqliteTarkovDevResponseCache(full)
            .GetAsync("regular/earlier", TestContext.Current.CancellationToken);
        Assert.NotNull(kept);
        Assert.Equal(earlier.Body, kept.BodyJson);

        // Room comes back (the cap is what stood in for the disk), and the same write goes through.
        await probe.WriteAsync(roomy, TestContext.Current.CancellationToken);
        Assert.Equal(stateBefore + 1, await probe.StateAsync(roomy, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARefreshThatRunsOutOfRoomMidTransactionRollsBackEveryRowItHadWritten()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var roomy = new SqliteConnectionFactory(new(database.Path));
        await using (var connection = await roomy.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO items(id, name, short_name, normalized_name, description, category_type, width, height, slots, flea_eligible, source_updated_utc) VALUES ('held-item', 'Held before refresh', 'Held', 'held before refresh', '', 'Unknown', 1, 1, 1, 0, '2026-09-15T00:00:00Z');";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var full = new SqliteConnectionFactory(new(database.Path) { MaximumPageCount = await PageCountAsync(roomy) });
        var rows = Enumerable.Range(0, 3_000).ToArray();

        // A bulk insert in one transaction, the shape every catalog refresh has: it fills the file
        // part-way through, so some rows were already written when SQLite refused the next page.
        var failure = await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await using var connection = await full.OpenAsync(TestContext.Current.CancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
            foreach (var row in rows)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO items(id, name, short_name, normalized_name, description, category_type, width, height, slots, flea_eligible, source_updated_utc) VALUES ($id, $name, 'x', $name, $description, 'Unknown', 1, 1, 1, 0, '2026-09-19T00:00:00Z');";
                command.Parameters.AddWithValue("$id", $"bulk-{row}");
                command.Parameters.AddWithValue("$name", $"Bulk item {row}");
                command.Parameters.AddWithValue("$description", new string('d', 900));
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        });

        Assert.Equal(SqliteFull, failure.SqliteErrorCode);
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(roomy, "SELECT COUNT(*) FROM items;"));
        Assert.Equal("Held before refresh", await ScalarTextAsync(roomy, "SELECT name FROM items WHERE id = 'held-item';"));
        Assert.Equal("ok", await IntegrityAsync(roomy));
    }

    [Fact]
    public async Task TheCapOnOneConnectionIsNotInheritedByTheNextOneFromThePool()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var roomy = new SqliteConnectionFactory(new(database.Path));
        var pages = await PageCountAsync(roomy);
        var capped = new SqliteConnectionFactory(new(database.Path) { MaximumPageCount = pages });
        await using (await capped.OpenAsync(TestContext.Current.CancellationToken))
        {
        }

        // Pooling is on: the capped connection goes back to the pool and this one may be it. A cap that
        // leaked would make an ordinary launch after a test run fail as if the disk were full.
        await Probe.For("response-cache", large: true).WriteAsync(roomy, TestContext.Current.CancellationToken);

        Assert.True(await PageCountAsync(roomy) > pages);
    }

    private static async Task<int> PageCountAsync(SqliteConnectionFactory factory) =>
        (int)await V2TestDatabase.ScalarAsync(factory, "PRAGMA page_count;");

    private static async Task<string> IntegrityAsync(SqliteConnectionFactory factory) =>
        await ScalarTextAsync(factory, "PRAGMA integrity_check;");

    private static async Task<string> ScalarTextAsync(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<LockHolder> HoldWriteLockAsync(string databasePath)
    {
        // A separate connection with its own string: the lock has to belong to somebody else.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 0; BEGIN IMMEDIATE;";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return new(connection);
    }

    private sealed class LockHolder(SqliteConnection connection) : IAsyncDisposable
    {
        private bool _released;

        public async Task ReleaseAsync()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            await using var command = connection.CreateCommand();
            command.CommandText = "ROLLBACK;";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await ReleaseAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>One write the app makes, with a way to see whether it landed.</summary>
    private sealed record Probe(
        string Body,
        long Before,
        long After,
        Func<SqliteConnectionFactory, CancellationToken, Task> WriteAsync,
        Func<SqliteConnectionFactory, CancellationToken, Task<long>> StateAsync)
    {
        public static Probe For(string name, bool large, string key = "regular/items") => name switch
        {
            "response-cache" => ResponseCache(large, key),
            "profile-workspace" => ProfileWorkspace(large),
            "retention-settings" => RetentionSettings(),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such write."),
        };

        private static Probe ResponseCache(bool large, string key)
        {
            // Random bytes, so compression cannot shrink the body back under the page cap.
            var payload = large ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(1_500_000)) : "small";
            var body = $"{{\"data\":{{\"payload\":\"{payload}\"}}}}";
            return new(
                body,
                0,
                1,
                (factory, token) => new SqliteTarkovDevResponseCache(factory).PutAsync(new(key, body, Now, null, null), token),
                (factory, token) => ScalarAsync(factory, "SELECT COUNT(*) FROM http_response_cache;", token));
        }

        private static Probe ProfileWorkspace(bool large)
        {
            var wishlist = Enumerable.Range(0, large ? 6_000 : 3).Select(index => $"wishlist-item-{index:D6}-{new string('x', 24)}").ToArray();
            var id = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
            var replacement = new ProfileWorkspaceSnapshot(
                1,
                id,
                [
                    new ProfileRecord(
                        new ProfileContext(
                            new ProfileIdentity(id, "generation-1"),
                            ProfileGameMode.Pve,
                            new WipeSeason("wipe-1"),
                            new ProfileLocale("en", "US", "Etc/UTC"),
                            new DataSnapshotContext("snapshot-1", Now)),
                        "Fixture",
                        new ProfileProgress(5, wishlistItemIds: wishlist),
                        ProfileLifecycle.Active,
                        Now),
                ]);
            return new(
                string.Empty,
                0,
                1,
                async (factory, token) =>
                {
                    Assert.True(await new SqliteProfileWorkspaceStore(factory).TryReplaceAsync(0, replacement, token));
                },
                async (factory, token) => (await new SqliteProfileWorkspaceStore(factory).ReadAsync(token)).Revision);
        }

        private static Probe RetentionSettings() => new(
            string.Empty,
            0,
            1,
            (factory, token) => new SqliteScreenshotRetentionStore(factory).SaveAsync(new(true, 48), token),
            // Raw SQL, not GetAsync: reading an unset preference imports the default, which is a write.
            (factory, token) => ScalarAsync(factory, "SELECT COUNT(*) FROM retention_policies WHERE screenshot_retention_enabled = 1;", token));

        private static async Task<long> ScalarAsync(SqliteConnectionFactory factory, string sql, CancellationToken token)
        {
            await using var connection = await factory.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(token));
        }
    }
}
