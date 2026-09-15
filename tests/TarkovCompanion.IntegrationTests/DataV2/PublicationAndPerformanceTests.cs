using System.Diagnostics;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.IntegrationTests.DataV2;

[Collection(SqliteCollection.Name)]
public sealed class PublicationAndPerformanceTests
{
    [Fact]
    public async Task RefusedAndStalePublicationStatesPreserveLastKnownGoodAtomically()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 15, 5, 0, 0, TimeSpan.Zero);
        var cache = new SqliteTarkovDevResponseCache(database.Factory);
        var body = "{\"data\":{\"one\":1}}";
        await cache.PutAsync(new("regular/items", body, now, null, null), TestContext.Current.CancellationToken);
        var hash = (await cache.InspectAsync(now, TestContext.Current.CancellationToken)).Entries.Single().ContentSha256;
        var state = new SqliteSyncStateRepository(database.Factory);
        var run = await state.BeginRunAsync("regular", "en", 3, now, TestContext.Current.CancellationToken);
        await state.RecordAsync(new("items", "regular", "en", now, now, null, null, "current", null), hash,
            TestContext.Current.CancellationToken, run, 100);
        var lkg = await ScalarTextAsync(database.Factory, "SELECT last_known_good_publication_id FROM dataset_heads WHERE source_key = 'items';");
        var visible = await ScalarTextAsync(database.Factory, "SELECT visible_publication_id FROM dataset_heads WHERE source_key = 'items';");
        Assert.Equal(lkg, visible);

        await state.RecordAsync(new("items", "regular", "en", null, now.AddMinutes(1), null, null, "refused", "shrunken"), null,
            TestContext.Current.CancellationToken, run, 0);
        Assert.Equal("refused", await ScalarTextAsync(database.Factory, "SELECT state FROM dataset_heads WHERE source_key = 'items';"));
        Assert.Equal(lkg, await ScalarTextAsync(database.Factory, "SELECT visible_publication_id FROM dataset_heads WHERE source_key = 'items';"));

        await state.RecordAsync(new("items", "regular", "en", null, now.AddMinutes(2), null, null, "stale", null), hash,
            TestContext.Current.CancellationToken, run, 100);
        Assert.Equal("stale", await ScalarTextAsync(database.Factory, "SELECT state FROM dataset_heads WHERE source_key = 'items';"));
        Assert.Equal(lkg, await ScalarTextAsync(database.Factory, "SELECT last_known_good_publication_id FROM dataset_heads WHERE source_key = 'items';"));
        Assert.NotEqual(lkg, await ScalarTextAsync(database.Factory, "SELECT visible_publication_id FROM dataset_heads WHERE source_key = 'items';"));
        await state.CompleteRunAsync(run, now.AddMinutes(3), TestContext.Current.CancellationToken);
        Assert.Equal("partial", await ScalarTextAsync(database.Factory, $"SELECT state FROM dataset_sync_runs WHERE run_id = '{run}';"));
    }

    [Fact]
    public async Task CacheAndMaintenanceRetainHeadBodiesWhilePruningSupersededHistory()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 15, 5, 0, 0, TimeSpan.Zero);
        var cache = new SqliteTarkovDevResponseCache(database.Factory, new()
        {
            MaximumAge = TimeSpan.FromHours(1),
        });
        var state = new SqliteSyncStateRepository(database.Factory);

        await cache.PutAsync(new("regular/items", "{\"data\":{\"version\":1}}", now.AddDays(-10), null, null),
            TestContext.Current.CancellationToken);
        var oldHash = (await cache.InspectAsync(now, TestContext.Current.CancellationToken)).Entries.Single().ContentSha256;
        await state.RecordAsync(
            new("items", "regular", "en", now.AddDays(-10), now.AddDays(-10), null, null, "current", null),
            oldHash,
            TestContext.Current.CancellationToken);
        var oldPublication = await ScalarTextAsync(database.Factory,
            "SELECT visible_publication_id FROM dataset_heads WHERE source_key = 'items';");

        await cache.PutAsync(new("regular/items", "{\"data\":{\"version\":2}}", now, null, null),
            TestContext.Current.CancellationToken);
        var currentHash = (await cache.InspectAsync(now, TestContext.Current.CancellationToken)).Entries.Single().ContentSha256;
        await state.RecordAsync(
            new("items", "regular", "en", now, now, null, null, "current", null),
            currentHash,
            TestContext.Current.CancellationToken);
        var currentPublication = await ScalarTextAsync(database.Factory,
            "SELECT visible_publication_id FROM dataset_heads WHERE source_key = 'items';");

        var preview = await cache.CleanupAsync(now.AddHours(2), true, TestContext.Current.CancellationToken);
        Assert.Equal(1, preview.RemovedEntries);
        Assert.Equal(0, preview.RemovedBodies);
        var cleanup = await cache.CleanupAsync(now.AddHours(2), false, TestContext.Current.CancellationToken);
        Assert.Equal(1, cleanup.RemovedEntries);
        Assert.Equal(0, cleanup.RemovedBodies);
        Assert.Equal(2, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM raw_endpoint_bodies;"));

        var maintenance = await new SqliteDataPlatformMaintenance(database.Factory).RunAsync(
            "prune",
            false,
            now.AddDays(-1),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, maintenance.AffectedRows);
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory,
            $"SELECT COUNT(*) FROM dataset_publications WHERE publication_id = '{oldPublication}';"));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
            $"SELECT COUNT(*) FROM dataset_publications WHERE publication_id = '{currentPublication}' AND previous_lkg_publication_id IS NULL;"));
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory,
            $"SELECT COUNT(*) FROM raw_endpoint_bodies WHERE content_sha256 = '{oldHash}';"));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
            $"SELECT COUNT(*) FROM raw_endpoint_bodies WHERE content_sha256 = '{currentHash}';"));
        Assert.Equal(currentPublication, await ScalarTextAsync(database.Factory,
            "SELECT last_known_good_publication_id FROM dataset_heads WHERE source_key = 'items';"));
    }

    [Fact]
    public async Task EightyThousandItemCatalogHasBoundedExactLookupAndIndexedQueryPlans()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var write = Stopwatch.StartNew();
        await using (var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH RECURSIVE sequence(value) AS (
                    VALUES(1) UNION ALL SELECT value + 1 FROM sequence WHERE value < 80001)
                INSERT INTO items(
                    id, name, short_name, normalized_name, normalized_short_name, description,
                    category_type, width, height, slots, flea_eligible, source_updated_utc)
                SELECT printf('item-%06d', value), printf('Item %06d', value), printf('I%06d', value),
                       printf('item %06d', value), printf('i%06d', value), '', 'Unknown', 1, 1, 1, 1,
                       '2026-09-15T00:00:00Z'
                FROM sequence;
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        write.Stop();
        Assert.Equal(80001, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM items;"));
        Assert.True(write.Elapsed < TimeSpan.FromSeconds(15), $"80K catalog seed took {write.Elapsed}.");

        var read = Stopwatch.StartNew();
        await using (var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM items WHERE id = 'item-080001';";
            Assert.Equal("Item 080001", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        read.Stop();
        Assert.True(read.Elapsed < TimeSpan.FromSeconds(1), $"Exact item lookup took {read.Elapsed}.");

        var plans = await new SqliteQueryPlanAuditor(database.Factory).CaptureAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["item", "quest", "price", "map", "history", "profile", "craft"], plans.Select(plan => plan.Path));
        Assert.All(plans, plan => Assert.True(plan.UsesIndex, $"{plan.Path}: {string.Join(" | ", plan.Steps)}"));
    }

    [Fact]
    public async Task MaintenanceDryRunDoesNotMutateAndSchedulesBoundedOperations()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var service = new SqliteDataPlatformMaintenance(database.Factory);
        var before = await service.InspectAsync(TestContext.Current.CancellationToken);
        var dryRun = await service.RunAsync("vacuum", true, DateTimeOffset.UtcNow.AddDays(-30), TestContext.Current.CancellationToken);
        var after = await service.InspectAsync(TestContext.Current.CancellationToken);
        Assert.True(dryRun.DryRun);
        Assert.Equal(before.CachedResponses, after.CachedResponses);
        Assert.Equal(before.CompletedOutboxRows, after.CompletedOutboxRows);
        await service.ScheduleAsync("reindex", true, 24, TestContext.Current.CancellationToken);
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM maintenance_schedules WHERE operation = 'reindex' AND enabled = 1;"));
        Assert.Empty(await new SqliteV2UnreadSchemaAuditor(database.Factory).AuditAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnresolvedQuestIdentifiersRemainExplicitInsteadOfBeingDropped()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO quest_catalog_snapshots(
                source_key, source_uri, local_game_mode, source_mode, language, payload_sha256,
                translated_payload_sha256, fetched_utc, validated_utc, raw_json, translated_json)
            VALUES ('json.tarkov.dev/tasks', 'https://json.tarkov.dev/regular/tasks', 'Regular', 'regular', 'en',
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                    '2026-09-15T00:00:00Z', '2026-09-15T00:00:00Z', '{}', '{}');
            INSERT INTO quest_catalog_tasks(
                source_key, source_mode, language, id, name, source_game_modes_json, raw_json)
            VALUES ('json.tarkov.dev/tasks', 'regular', 'en', 'unresolved-task', 'Unknown task', '[]', '{}');
            INSERT INTO quest_catalog_objectives(
                source_key, source_mode, language, task_id, id, is_failure_condition, source_ordinal,
                source_type, normalized_kind, is_unsupported, description, subtype_json, raw_json)
            VALUES ('json.tarkov.dev/tasks', 'regular', 'en', 'unresolved-task', 'objective', 0, 0,
                    'giveItem', 'GiveItem', 0, 'Unknown upstream item', '{}', '{}');
            INSERT INTO quest_objective_item_targets(
                source_key, source_mode, language, task_id, objective_id, is_failure_condition,
                source_field, alternative_group, source_ordinal, item_id, target_count, found_in_raid_required)
            VALUES ('json.tarkov.dev/tasks', 'regular', 'en', 'unresolved-task', 'objective', 0,
                    'items', 0, 0, 'upstream-item-not-in-catalog', NULL, NULL);
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        Assert.Equal("upstream-item-not-in-catalog", await ScalarTextAsync(database.Factory,
            "SELECT item_id FROM quest_objective_item_targets WHERE task_id = 'unresolved-task';"));
    }

    private static async Task<string> ScalarTextAsync(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Assert.IsType<string>(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
