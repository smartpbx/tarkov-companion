using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.LootScan;

namespace TarkovCompanion.UnitTests.LootScan;

/// <summary>#274/#282/#291: saved loot scans, their ruleset version, and their bound.</summary>
public sealed class LootScanHistoryStoreTests
{
    private const string SeedDatabase = "/root/orca/seed/catalog-2026-09-14.db";
    private static readonly Guid RaidId = Guid.Parse("50000000-0000-0000-0000-000000000274");
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_saved_scan_reads_back_from_a_new_store_with_its_ruleset_and_calls()
    {
        await using var database = await TempDatabase.CreateAsync();
        var scan = Scan("scan-1", Now);

        await new SqliteLootScanHistoryStore(database.Factory).SaveAsync(scan, CancellationToken.None);

        var read = Assert.Single(await new SqliteLootScanHistoryStore(database.Factory)
            .ListForRaidAsync(RaidId, CancellationToken.None));
        Assert.Equal("scan-1", read.ScanId);
        Assert.Equal(RaidId, read.RaidId);
        Assert.Equal("customs", read.MapId);
        Assert.Equal(Now, read.EvaluatedUtc);
        Assert.Equal("recommendation-274.2", read.RulesetVersion);
        Assert.False(read.IsComplete);
        Assert.Equal(scan.Items, read.Items);
        Assert.Equal(1, read.Count(LootScanVerdict.Swap));
        Assert.Equal(390_000, read.TakenValueRoubles);
    }

    [Fact]
    public async Task The_same_scan_decided_again_replaces_its_earlier_save()
    {
        await using var database = await TempDatabase.CreateAsync();
        var store = new SqliteLootScanHistoryStore(database.Factory);
        await store.SaveAsync(Scan("scan-1", Now), CancellationToken.None);

        await store.SaveAsync(Scan("scan-1", Now) with { RulesetVersion = "recommendation-274.3", Items = [] }, CancellationToken.None);

        var read = Assert.Single(await store.ListRecentAsync(10, CancellationToken.None));
        Assert.Equal("recommendation-274.3", read.RulesetVersion);
        Assert.Empty(read.Items);
    }

    [Fact]
    public async Task Saving_keeps_the_newest_500_and_nothing_older_than_90_days()
    {
        await using var database = await TempDatabase.CreateAsync();
        var store = new SqliteLootScanHistoryStore(database.Factory);
        await store.SaveAsync(Scan("ancient", Now.AddDays(-91)), CancellationToken.None);
        for (var index = 0; index < LootScanHistoryRetention.MaximumScans + 1; index++)
        {
            await store.SaveAsync(Scan($"scan-{index:D4}", Now.AddMinutes(index)), CancellationToken.None);
        }

        var kept = await store.ListRecentAsync(1_000, CancellationToken.None);
        Assert.Equal(LootScanHistoryRetention.MaximumScans, kept.Count);
        Assert.DoesNotContain(kept, scan => scan.ScanId is "ancient" or "scan-0000");
        Assert.Equal("scan-0500", kept[0].ScanId);
    }

    [Fact]
    public async Task The_maintenance_prune_removes_scans_older_than_the_retention_cutoff()
    {
        await using var database = await TempDatabase.CreateAsync();
        var store = new SqliteLootScanHistoryStore(database.Factory);
        await store.SaveAsync(Scan("old", Now.AddDays(-40)), CancellationToken.None);
        await store.SaveAsync(Scan("new", Now), CancellationToken.None);

        var result = await new SqliteDataPlatformMaintenance(database.Factory)
            .RunAsync("prune", dryRun: false, Now.AddDays(-30), CancellationToken.None);

        Assert.True(result.AffectedRows >= 1);
        Assert.Equal("new", Assert.Single(await store.ListRecentAsync(10, CancellationToken.None)).ScanId);
    }

    /// <summary>
    /// 0019 on a copy of the seeded catalog the renders use (a real database at 0010), and on a new
    /// one: it applies, its rollback removes the table, and it applies again once un-recorded.
    /// </summary>
    [Fact]
    public async Task Migration_0019_applies_rolls_back_and_applies_again()
    {
        var sources = new List<string?> { null };
        if (File.Exists(SeedDatabase))
        {
            sources.Add(SeedDatabase);
        }

        foreach (var source in sources)
        {
            await using var database = await TempDatabase.CreateAsync(source);
            await new SqliteLootScanHistoryStore(database.Factory).SaveAsync(Scan("scan-1", Now), CancellationToken.None);
            Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM loot_scans;"));

            var rollback = SqliteMigrationRunner.ReadFixture("0019_loot_scan_history").RollbackSql;
            await database.ExecuteAsync(rollback + " DELETE FROM schema_migrations WHERE version = '0019_loot_scan_history';");
            Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name = 'loot_scans';"));

            var again = await new SqliteMigrationRunner(database.Factory).ApplyAsync(CancellationToken.None);
            Assert.Equal(["0019_loot_scan_history"], again.Applied);
            Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM loot_scans;"));
            Assert.Equal(2, await database.ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND tbl_name = 'loot_scans' AND name LIKE 'idx_%';"));
        }
    }

    private static SavedLootScan Scan(string id, DateTimeOffset at) => new(
        id,
        RaidId,
        "customs",
        at,
        "recommendation-274.2",
        IsComplete: false,
        [
            new("item-gpu", "Graphics card", LootScanVerdict.Take, "Place in rig, row 1, column 1", 300_000, 0.93, "Hideout · 1 for Render need"),
            new("item-drill", "Electric drill", LootScanVerdict.Swap, "Place in backpack, row 3, column 2 · rotate", 90_000, null, "Hideout"),
            new(null, "Item at row 2, column 4", LootScanVerdict.Review, string.Empty, null, null, "Not identified"),
        ]);

    private sealed class TempDatabase : IAsyncDisposable
    {
        private readonly string _path;

        private TempDatabase(string path)
        {
            _path = path;
            Factory = new SqliteConnectionFactory(new(path));
        }

        public SqliteConnectionFactory Factory { get; }

        public static async Task<TempDatabase> CreateAsync(string? copyFrom = null)
        {
            var path = Path.Combine(Path.GetTempPath(), $"loot-history-{Guid.NewGuid():N}.db");
            if (copyFrom is not null)
            {
                File.Copy(copyFrom, path);
            }

            var database = new TempDatabase(path);
            await new SqliteMigrationRunner(database.Factory).ApplyAsync(CancellationToken.None);
            return database;
        }

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = await Factory.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
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
            foreach (var file in new[] { _path, _path + "-shm", _path + "-wal" })
            {
                File.Delete(file);
            }

            // Backups the runner takes before a migration sit beside the database.
            foreach (var backup in Directory.EnumerateFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
            {
                File.Delete(backup);
            }

            return ValueTask.CompletedTask;
        }
    }
}
