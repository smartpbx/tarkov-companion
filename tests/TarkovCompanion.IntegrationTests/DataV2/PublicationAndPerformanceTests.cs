using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.IntegrationTests.DataV2;

[Collection(SqliteCollection.Name)]
public sealed class PublicationAndPerformanceTests
{
    [Fact]
    public async Task NormalizedRowsAndPublicationHeadRollBackTogether()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using (var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO items(
                    id, name, short_name, normalized_name, description, category_type,
                    width, height, slots, flea_eligible, source_updated_utc)
                VALUES ('held-item', 'Held before refresh', 'Held', 'held before refresh', '', 'Unknown',
                        1, 1, 1, 0, '2026-09-15T00:00:00Z');

                CREATE TRIGGER inject_publication_failure
                BEFORE INSERT ON dataset_publications
                BEGIN
                    SELECT RAISE(ABORT, 'injected-publication-failure');
                END;
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var client = new TarkovDevJsonClient(
            new HttpClient(new FixtureApiHandler()),
            new InMemoryTarkovDevResponseCache(),
            new DataTranslationService(),
            new()
            {
                BaseAddress = new("https://fixture.invalid/"),
                InitialRetryDelay = TimeSpan.Zero,
            });
        var operation = new TarkovDevDataRefreshOperation(
            client,
            new SqliteDataRefreshRepository(database.Factory),
            new SqliteSyncStateRepository(database.Factory));

        await Assert.ThrowsAsync<SqliteException>(() => operation.RefreshAsync(
            new SyncRequest(GameMode.Regular, "en", true),
            TestContext.Current.CancellationToken));

        Assert.Equal("Held before refresh", await ScalarTextAsync(
            database.Factory,
            "SELECT name FROM items WHERE id = 'held-item';"));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM items;"));
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM dataset_publications;"));
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM dataset_heads;"));
    }

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
        var run = await state.BeginRunAsync(
            "regular",
            "en",
            ["items", "maps", "tasks"],
            now,
            TestContext.Current.CancellationToken);
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
        await state.CompleteRunAsync(run.RunId, now.AddMinutes(3), TestContext.Current.CancellationToken);
        Assert.Equal("partial", await ScalarTextAsync(database.Factory, $"SELECT state FROM dataset_sync_runs WHERE run_id = '{run.RunId}';"));
        var recordedRun = Assert.Single(await state.ListDatasetSyncRunsAsync(
            "regular",
            "en",
            10,
            TestContext.Current.CancellationToken));
        Assert.Equal(run.RunId, recordedRun.RunId);
        Assert.Equal(run.PublicationOrder, recordedRun.PublicationOrder);
        Assert.Equal(3, recordedRun.EndpointCount);
        Assert.Equal(1, recordedRun.SuccessfulCount);
        Assert.Equal(1, recordedRun.StaleCount);
        Assert.Equal(1, recordedRun.RefusedCount);
        Assert.Equal(0, recordedRun.FailureCount);
        Assert.Equal("partial", recordedRun.State);
    }

    [Fact]
    public async Task NewerRunThatCommitsFirstFencesAndRollsBackTheOlderNormalizedRefresh()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        using var olderHandler = new PausedItemsFixtureHandler();
        await using var olderClient = FixtureClient(
            olderHandler,
            new InMemoryTarkovDevResponseCache(),
            TimeSpan.FromSeconds(30));
        var olderRefresh = new TarkovDevDataRefreshOperation(
            olderClient,
            new SqliteDataRefreshRepository(database.Factory),
            new SqliteSyncStateRepository(database.Factory));
        var olderTask = olderRefresh.RefreshAsync(
            new(GameMode.Regular, "en", true),
            TestContext.Current.CancellationToken);
        await olderHandler.ItemsRequestStarted.WaitAsync(TimeSpan.FromSeconds(5));

        await using (var newerClient = FixtureClient(
                         new FixtureApiHandler(),
                         new InMemoryTarkovDevResponseCache()))
        {
            var newer = await new TarkovDevDataRefreshOperation(
                    newerClient,
                    new SqliteDataRefreshRepository(database.Factory),
                    new SqliteSyncStateRepository(database.Factory))
                .RefreshAsync(new(GameMode.Regular, "en", true), TestContext.Current.CancellationToken);
            Assert.All(newer, endpoint => Assert.True(endpoint.Updated, endpoint.Error));
        }

        var heldName = await ScalarTextAsync(database.Factory, "SELECT name FROM items WHERE id = 'item-001';");
        var heldBasePrice = await V2TestDatabase.ScalarAsync(
            database.Factory,
            "SELECT base_price FROM items WHERE id = 'item-001';");
        var heldHead = await ScalarTextAsync(database.Factory,
            "SELECT visible_publication_id FROM dataset_heads WHERE source_key = 'items';");
        var heldHash = await ScalarTextAsync(database.Factory, $"""
            SELECT content_sha256 FROM dataset_publications WHERE publication_id = '{heldHead}';
            """);
        var materializationBefore = Assert.IsType<DatasetEndpointMaterializationEntry>(
            await new SqliteSyncStateRepository(database.Factory).GetEndpointMaterializationAsync(
                "items",
                TestContext.Current.CancellationToken));

        olderHandler.ReleaseItems();
        var older = await olderTask;

        Assert.All(older, endpoint => Assert.StartsWith("Superseded:", endpoint.Error!, StringComparison.Ordinal));
        Assert.Equal(heldName, await ScalarTextAsync(database.Factory, "SELECT name FROM items WHERE id = 'item-001';"));
        Assert.Equal(heldBasePrice, await V2TestDatabase.ScalarAsync(
            database.Factory,
            "SELECT base_price FROM items WHERE id = 'item-001';"));
        Assert.Equal(heldHead, await ScalarTextAsync(database.Factory,
            "SELECT visible_publication_id FROM dataset_heads WHERE source_key = 'items';"));
        Assert.Equal(heldHash, await ScalarTextAsync(database.Factory, $"""
            SELECT content_sha256 FROM dataset_publications WHERE publication_id = '{heldHead}';
            """));
        Assert.Equal(7, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM dataset_publications;"));
        Assert.Equal(2, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM items;"));
        var materializationAfter = Assert.IsType<DatasetEndpointMaterializationEntry>(
            await new SqliteSyncStateRepository(database.Factory).GetEndpointMaterializationAsync(
                "items",
                TestContext.Current.CancellationToken));
        Assert.Equal(materializationBefore, materializationAfter);
        Assert.Equal(materializationAfter.ClaimedPublicationOrder, materializationAfter.MaterializedPublicationOrder);
        Assert.Equal(materializationAfter.ClaimedRunId, materializationAfter.MaterializedRunId);
    }

    [Fact]
    public async Task SupersededRepositoryAttemptReturnsTypedResultWithoutMovingTheHead()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 15, 5, 0, 0, TimeSpan.Zero);
        var state = new SqliteSyncStateRepository(database.Factory);
        var older = await state.BeginRunAsync(
            "regular", "en", ["items"], now, TestContext.Current.CancellationToken);
        var newer = await state.BeginRunAsync(
            "regular", "en", ["items"], now, TestContext.Current.CancellationToken);
        var currentHash = new string('b', 64);
        await state.RecordAsync(
            new("items", "regular", "en", now, now, null, null, "current", null),
            currentHash,
            TestContext.Current.CancellationToken,
            newer,
            2);
        var currentHead = await ScalarTextAsync(database.Factory,
            "SELECT visible_publication_id FROM dataset_heads WHERE source_key = 'items';");

        var superseded = await Assert.ThrowsAsync<DatasetPublicationSupersededException>(() => state.RecordAsync(
            new("items", "regular", "en", now, now, null, null, "failed", "older"),
            null,
            TestContext.Current.CancellationToken,
            older,
            0));

        Assert.Equal("items", superseded.SourceKey);
        Assert.Equal(older.PublicationOrder, superseded.AttemptedPublicationOrder);
        Assert.Equal(newer.PublicationOrder, superseded.ClaimedPublicationOrder);
        Assert.Equal(currentHead, await ScalarTextAsync(database.Factory,
            "SELECT visible_publication_id FROM dataset_heads WHERE source_key = 'items';"));
        Assert.Equal(currentHash, await ScalarTextAsync(database.Factory, $"""
            SELECT content_sha256 FROM dataset_publications WHERE publication_id = '{currentHead}';
            """));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM dataset_publications;"));
    }

    [Fact]
    public async Task GlobalEndpointMaterializationInvalidatesOtherContextVisibleHeads()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using (var regularClient = FixtureClient(
                         new FixtureApiHandler(),
                         new InMemoryTarkovDevResponseCache()))
        {
            var regular = await new TarkovDevDataRefreshOperation(
                    regularClient,
                    new SqliteDataRefreshRepository(database.Factory),
                    new SqliteSyncStateRepository(database.Factory))
                .RefreshAsync(new(GameMode.Regular, "en", true), TestContext.Current.CancellationToken);
            Assert.All(regular, endpoint => Assert.True(endpoint.Updated, endpoint.Error));
        }

        var regularPublication = await ScalarTextAsync(database.Factory, """
            SELECT visible_publication_id
            FROM dataset_heads
            WHERE source_key = 'items' AND game_mode = 'regular' AND language = 'en';
            """);
        await using (var pveClient = FixtureClient(
                         new RemappedModeFixtureHandler("pve", "regular"),
                         new InMemoryTarkovDevResponseCache()))
        {
            var pve = await new TarkovDevDataRefreshOperation(
                    pveClient,
                    new SqliteDataRefreshRepository(database.Factory),
                    new SqliteSyncStateRepository(database.Factory))
                .RefreshAsync(new(GameMode.Pve, "en", true), TestContext.Current.CancellationToken);
            Assert.All(pve, endpoint => Assert.True(endpoint.Updated, endpoint.Error));
        }

        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory, """
            SELECT COUNT(*) FROM dataset_heads
            WHERE source_key = 'items' AND game_mode = 'regular' AND language = 'en'
              AND visible_publication_id IS NULL
              AND last_known_good_publication_id IS NOT NULL
              AND state = 'last_known_good';
            """));
        Assert.Equal(regularPublication, await ScalarTextAsync(database.Factory, """
            SELECT last_known_good_publication_id FROM dataset_heads
            WHERE source_key = 'items' AND game_mode = 'regular' AND language = 'en';
            """));
        Assert.Equal("superseded", await ScalarTextAsync(database.Factory, """
            SELECT status FROM sync_state
            WHERE source_key = 'items' AND game_mode = 'regular' AND language = 'en';
            """));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory, """
            SELECT COUNT(*) FROM sync_state
            WHERE source_key = 'items' AND game_mode = 'regular' AND language = 'en'
              AND last_success_utc IS NULL AND content_hash IS NULL;
            """));
        Assert.Equal("current", await ScalarTextAsync(database.Factory, """
            SELECT state FROM dataset_heads
            WHERE source_key = 'items' AND game_mode = 'pve' AND language = 'en';
            """));
        var pvePublication = await ScalarTextAsync(database.Factory, """
            SELECT visible_publication_id FROM dataset_heads
            WHERE source_key = 'items' AND game_mode = 'pve' AND language = 'en';
            """);
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory, """
            SELECT COUNT(*) FROM dataset_heads
            WHERE source_key = 'items' AND visible_publication_id IS NOT NULL;
            """));
        var materialization = Assert.IsType<DatasetEndpointMaterializationEntry>(
            await new SqliteSyncStateRepository(database.Factory).GetEndpointMaterializationAsync(
                "items",
                TestContext.Current.CancellationToken));
        Assert.Equal("pve", materialization.MaterializedGameMode);
        Assert.Equal("en", materialization.MaterializedLanguage);
        Assert.Equal(pvePublication, materialization.MaterializedPublicationId);
        Assert.Equal(materialization.ClaimedPublicationOrder, materialization.MaterializedPublicationOrder);
    }

    [Fact]
    public async Task ServiceUnavailableRefreshPublishesStaleWhilePreservingLastKnownGood()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var cache = new SqliteTarkovDevResponseCache(database.Factory);
        await using (var initialClient = FixtureClient(new FixtureApiHandler(), cache))
        {
            var initial = await new TarkovDevDataRefreshOperation(
                    initialClient,
                    new SqliteDataRefreshRepository(database.Factory),
                    new SqliteSyncStateRepository(database.Factory))
                .RefreshAsync(new(GameMode.Regular, "en", true), TestContext.Current.CancellationToken);
            Assert.All(initial, endpoint => Assert.True(endpoint.Updated));
        }

        var originalLkg = await ScalarTextAsync(database.Factory,
            "SELECT last_known_good_publication_id FROM dataset_heads WHERE source_key = 'items';");
        await using (var faultClient = FixtureClient(
                         new FaultingFixtureHandler("regular/items", EndpointFault.ServiceUnavailable),
                         cache))
        {
            var refresh = await new TarkovDevDataRefreshOperation(
                    faultClient,
                    new SqliteDataRefreshRepository(database.Factory),
                    new SqliteSyncStateRepository(database.Factory))
                .RefreshAsync(new(GameMode.Regular, "en", true), TestContext.Current.CancellationToken);
            var items = refresh.Single(endpoint => endpoint.Endpoint == "items");
            Assert.True(items.UsedStaleCache);
            Assert.Null(items.Error);
        }

        Assert.Equal("stale", await ScalarTextAsync(database.Factory,
            "SELECT state FROM dataset_heads WHERE source_key = 'items';"));
        Assert.Equal(originalLkg, await ScalarTextAsync(database.Factory,
            "SELECT last_known_good_publication_id FROM dataset_heads WHERE source_key = 'items';"));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM dataset_sync_runs WHERE state = 'stale';"));
    }

    [Fact]
    public async Task HostileRefreshPublishesRefusedThroughTheProductionOperation()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var cache = new SqliteTarkovDevResponseCache(database.Factory);
        await using (var initialClient = FixtureClient(new FixtureApiHandler(), cache))
        {
            var initial = await new TarkovDevDataRefreshOperation(
                    initialClient,
                    new SqliteDataRefreshRepository(database.Factory),
                    new SqliteSyncStateRepository(database.Factory))
                .RefreshAsync(new(GameMode.Regular, "en", true), TestContext.Current.CancellationToken);
            Assert.All(initial, endpoint => Assert.True(endpoint.Updated));
        }

        var heldCount = await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM items;");
        var heldLkg = await ScalarTextAsync(database.Factory,
            "SELECT last_known_good_publication_id FROM dataset_heads WHERE source_key = 'items';");
        await using var hostileClient = FixtureClient(
            new BodyOverrideFixtureHandler(
                "regular/items",
                "{\"data\":{\"items\":{},\"itemCategories\":{}}}"),
            cache);

        var refresh = await new TarkovDevDataRefreshOperation(
                hostileClient,
                new SqliteDataRefreshRepository(database.Factory),
                new SqliteSyncStateRepository(database.Factory))
            .RefreshAsync(new(GameMode.Regular, "en", true), TestContext.Current.CancellationToken);

        var items = refresh.Single(endpoint => endpoint.Endpoint == "items");
        Assert.False(items.Updated);
        Assert.False(items.UsedStaleCache);
        Assert.NotNull(items.Error);
        Assert.Contains("Refused", items.Error!, StringComparison.Ordinal);
        Assert.Equal(heldCount, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM items;"));
        Assert.Equal("refused", await ScalarTextAsync(database.Factory,
            "SELECT state FROM dataset_heads WHERE source_key = 'items';"));
        Assert.Equal(heldLkg, await ScalarTextAsync(database.Factory,
            "SELECT last_known_good_publication_id FROM dataset_heads WHERE source_key = 'items';"));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM dataset_sync_runs WHERE state = 'partial' AND refused_count = 1 AND failure_count = 0;"));
    }

    [Theory]
    [InlineData(EndpointFault.Timeout)]
    [InlineData(EndpointFault.ConnectionReset)]
    public async Task FirstSyncNetworkFaultRemainsExplicitPartial(EndpointFault fault)
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var client = FixtureClient(
            new FaultingFixtureHandler("regular/items", fault),
            new SqliteTarkovDevResponseCache(database.Factory),
            TimeSpan.FromMilliseconds(25));
        var result = await new TarkovDevDataRefreshOperation(
                client,
                new SqliteDataRefreshRepository(database.Factory),
                new SqliteSyncStateRepository(database.Factory))
            .RefreshAsync(new(GameMode.Regular, "en", true), TestContext.Current.CancellationToken);

        var items = result.Single(endpoint => endpoint.Endpoint == "items");
        Assert.False(items.Updated);
        Assert.False(items.UsedStaleCache);
        Assert.NotNull(items.Error);
        Assert.Equal("partial", await ScalarTextAsync(database.Factory,
            "SELECT state FROM dataset_heads WHERE source_key = 'items';"));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM dataset_sync_runs WHERE state = 'partial' AND failure_count = 1 AND successful_count = 6;"));
    }

    [Fact]
    public async Task PublicationMetadataSurvivesCacheEvictionWhileMaintenancePrunesSupersededHistory()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 15, 5, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now.AddDays(-10));
        var cache = new SqliteTarkovDevResponseCache(database.Factory, new()
        {
            MaximumAge = TimeSpan.FromHours(1),
        }, time);
        var state = new SqliteSyncStateRepository(database.Factory);

        await cache.PutAsync(new("regular/items", "{\"data\":{\"version\":1}}", now.AddDays(-10), null, null),
            TestContext.Current.CancellationToken);
        var oldHash = (await cache.InspectAsync(time.GetUtcNow(), TestContext.Current.CancellationToken)).Entries.Single().ContentSha256;
        var oldRun = await state.BeginRunAsync(
            "regular", "en", ["items"], now.AddDays(-10), TestContext.Current.CancellationToken);
        await state.RecordAsync(
            new("items", "regular", "en", now.AddDays(-10), now.AddDays(-10), null, null, "current", null),
            oldHash,
            TestContext.Current.CancellationToken,
            oldRun);
        var oldPublication = await ScalarTextAsync(database.Factory,
            "SELECT visible_publication_id FROM dataset_heads WHERE source_key = 'items';");

        time.Advance(TimeSpan.FromDays(10));
        await cache.PutAsync(new("regular/items", "{\"data\":{\"version\":2}}", now, null, null),
            TestContext.Current.CancellationToken);
        var currentHash = (await cache.InspectAsync(now, TestContext.Current.CancellationToken)).Entries.Single().ContentSha256;
        var currentRun = await state.BeginRunAsync(
            "regular", "en", ["items"], now, TestContext.Current.CancellationToken);
        await state.RecordAsync(
            new("items", "regular", "en", now, now, null, null, "current", null),
            currentHash,
            TestContext.Current.CancellationToken,
            currentRun);
        var currentPublication = await ScalarTextAsync(database.Factory,
            "SELECT visible_publication_id FROM dataset_heads WHERE source_key = 'items';");

        var preview = await cache.CleanupAsync(now.AddHours(2), true, TestContext.Current.CancellationToken);
        Assert.Equal(1, preview.RemovedEntries);
        Assert.Equal(1, preview.RemovedBodies);
        var cleanup = await cache.CleanupAsync(now.AddHours(2), false, TestContext.Current.CancellationToken);
        Assert.Equal(1, cleanup.RemovedEntries);
        Assert.Equal(1, cleanup.RemovedBodies);
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM raw_endpoint_bodies;"));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
            $"SELECT COUNT(*) FROM dataset_publications WHERE publication_id = '{oldPublication}' AND content_sha256 = '{oldHash}';"));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
            $"SELECT COUNT(*) FROM dataset_publications WHERE publication_id = '{currentPublication}' AND content_sha256 = '{currentHash}';"));

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
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory,
            $"SELECT COUNT(*) FROM raw_endpoint_bodies WHERE content_sha256 = '{currentHash}';"));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
            $"SELECT COUNT(*) FROM dataset_publications WHERE publication_id = '{currentPublication}' AND content_sha256 = '{currentHash}';"));
        Assert.Equal(currentPublication, await ScalarTextAsync(database.Factory,
            "SELECT last_known_good_publication_id FROM dataset_heads WHERE source_key = 'items';"));
    }

    [Fact]
    public async Task ProductionRefreshCapturesRealQueryShapesAndIndexesScopedReads()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using (var client = FixtureClient(
                         new FixtureApiHandler(),
                         new SqliteTarkovDevResponseCache(database.Factory)))
        {
            var refresh = await new TarkovDevDataRefreshOperation(
                    client,
                    new SqliteDataRefreshRepository(database.Factory),
                    new SqliteSyncStateRepository(database.Factory))
                .RefreshAsync(new(GameMode.Regular, "en", true), TestContext.Current.CancellationToken);
            Assert.All(refresh, endpoint => Assert.True(endpoint.Updated, $"{endpoint.Endpoint}: {endpoint.Error}"));
        }

        var plans = await new SqliteQueryPlanAuditor(database.Factory).CaptureAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            ["item", "quest", "price", "map", "history", "profile", "craft", "quest-progress", "requirements", "item-search"],
            plans.Select(plan => plan.Path));
        Assert.All(plans, plan =>
        {
            Assert.NotEmpty(plan.ReaderPath);
            Assert.DoesNotContain("SELECT *", plan.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(plan.Statements);
            Assert.All(
                plan.Statements.Where(statement => statement.IndexRequired),
                statement => Assert.True(
                    statement.MeetsExpectation,
                    $"{plan.Path}/{statement.Name}: {string.Join(" | ", statement.Steps)}"));
        });
        Assert.Equal(
            [
                "map/catalog-scan", "history/history-list", "profile/profile-contexts",
                "requirements/quest-items", "requirements/hideout-items", "item-search/full-text", "item-search/fuzzy-names",
            ],
            plans.SelectMany(plan => plan.Statements
                .Where(statement => !statement.IndexRequired)
                .Select(statement => $"{plan.Path}/{statement.Name}")));
    }

    [Fact]
    public async Task PublicationCompletionAndNewestRaidWindowsHaveExactSupportingIndexes()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);

        Assert.Equal("run_id,state", await ScalarTextAsync(database.Factory, """
            SELECT group_concat(name, ',')
            FROM (
                SELECT name
                FROM pragma_index_info('idx_dataset_publications_run_state')
                ORDER BY seqno);
            """));
        Assert.Equal("raid_id,recorded_utc,id", await ScalarTextAsync(database.Factory, """
            SELECT group_concat(name, ',')
            FROM (
                SELECT name
                FROM pragma_index_info('idx_raid_field_history_recent')
                ORDER BY seqno);
            """));
    }

    [Fact]
    public async Task V2SchemaRejectsInvalidCraftFactsRecoveryHashesAndRetentionStorageClasses()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var invalidStatements = new[]
        {
            """
            INSERT INTO craft_history(history_id, craft_id, recorded_utc, source, payload_json)
            VALUES ('not-a-canonical-guid', 'craft', '2026-09-15T00:00:00Z', 'fixture', '{}');
            """,
            """
            INSERT INTO craft_history(
                history_id, craft_id, station_level, recorded_utc, source, payload_json)
            VALUES ('10000000-0000-0000-0000-000000000001', 'craft', 1.5,
                    '2026-09-15T00:00:00Z', 'fixture', '{}');
            """,
            """
            INSERT INTO craft_history(
                history_id, craft_id, output_count, recorded_utc, source, payload_json)
            VALUES ('10000000-0000-0000-0000-000000000002', 'craft', -1,
                    '2026-09-15T00:00:00Z', 'fixture', '{}');
            """,
            """
            INSERT INTO craft_history(
                history_id, craft_id, estimated_cost_roubles, recorded_utc, source, payload_json)
            VALUES ('10000000-0000-0000-0000-000000000003', 'craft', -1,
                    '2026-09-15T00:00:00Z', 'fixture', '{}');
            """,
            """
            INSERT INTO craft_history(
                history_id, craft_id, estimated_yield_roubles, recorded_utc, source, payload_json)
            VALUES ('10000000-0000-0000-0000-000000000004', 'craft', -1,
                    '2026-09-15T00:00:00Z', 'fixture', '{}');
            """,
            """
            INSERT INTO local_json_recovery(
                document_key, state, detected_utc, content_sha256, diagnostic_code)
            VALUES ('fixture', 'malformed', '2026-09-15T00:00:00Z',
                    'gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg',
                    'fixture');
            """,
            """
            INSERT INTO retention_policies(
                policy_key, screenshot_retention_enabled, debug_capture_enabled,
                data_retention_days, updated_utc)
            VALUES ('invalid-storage', 0, 0, 1.5, '2026-09-15T00:00:00Z');
            """,
        };

        await using var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken);
        foreach (var sql in invalidStatements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await Assert.ThrowsAsync<SqliteException>(() =>
                command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task MaintenanceDryRunDoesNotMutateAndSchedulesBoundedOperations()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new SqliteDataPlatformMaintenance(database.Factory, clock);
        var before = await service.InspectAsync(TestContext.Current.CancellationToken);
        var dryRun = await service.RunAsync("vacuum", true, clock.GetUtcNow().AddDays(-30), TestContext.Current.CancellationToken);
        var after = await service.InspectAsync(TestContext.Current.CancellationToken);
        Assert.True(dryRun.DryRun);
        Assert.Equal(before, after);

        await service.ScheduleAsync("prune", false, 24, TestContext.Current.CancellationToken);
        await service.ScheduleAsync("reindex", true, 1, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromHours(2));
        var runtime = new SqliteRuntimeDataStore(
            new(database.Path),
            database.Factory,
            new SqliteMigrationRunner(database.Factory),
            clock,
            maintenance: service);
        await runtime.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM maintenance_history WHERE operation = 'reindex' AND status = 'completed';"));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM maintenance_schedules WHERE operation = 'reindex' AND enabled = 1 AND last_run_utc IS NOT NULL;"));

        var schedule = (await service.ListSchedulesAsync(TestContext.Current.CancellationToken))
            .Single(candidate => candidate.Operation == "reindex");
        Assert.True(schedule.Enabled);
        Assert.Equal(1, schedule.IntervalHours);
        Assert.NotNull(schedule.LastRunUtc);
        Assert.NotNull(schedule.NextRunUtc);
        var history = Assert.Single(await service.ListHistoryAsync(10, TestContext.Current.CancellationToken));
        Assert.Equal("reindex", history.Operation);
        Assert.Equal("completed", history.Status);
        Assert.Null(history.DiagnosticCode);
    }

    [Fact]
    public async Task MaintenanceReadersRejectDynamicStorageAndNonCanonicalIdentitiesAndTimes()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero);
        var service = new SqliteDataPlatformMaintenance(database.Factory, new ManualTimeProvider(now));

        await using (var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA ignore_check_constraints = ON;
                UPDATE maintenance_schedules
                SET interval_hours = 1.5
                WHERE operation = 'reindex';
                PRAGMA ignore_check_constraints = OFF;
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ListSchedulesAsync(TestContext.Current.CancellationToken));

        await using (var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE maintenance_schedules
                SET interval_hours = 1,
                    updated_utc = '2026-09-15T06:00:00.000Z'
                WHERE operation = 'reindex';
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ListSchedulesAsync(TestContext.Current.CancellationToken));

        await using (var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO maintenance_history(
                    run_id, operation, dry_run, started_utc, completed_utc,
                    reclaimed_bytes, affected_rows, status, diagnostic_code)
                VALUES ($id, 'reindex', 0, $timestamp, $timestamp, 0, 0, 'completed', NULL);
                """;
            command.Parameters.AddWithValue("$id", "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA");
            command.Parameters.AddWithValue("$timestamp", now.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ListHistoryAsync(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunDueSkipsMalformedScheduleAndContinuesIndependentMaintenance()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero));
        var service = new SqliteDataPlatformMaintenance(database.Factory, clock);
        await service.ScheduleAsync("prune", true, 1, TestContext.Current.CancellationToken);
        await service.ScheduleAsync("reindex", true, 1, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromHours(2));

        await using (var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA ignore_check_constraints = ON;
                UPDATE maintenance_schedules
                SET interval_hours = 1.5
                WHERE operation = 'prune';
                PRAGMA ignore_check_constraints = OFF;
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var result = await service.RunDueAsync(TestContext.Current.CancellationToken);

        var completed = Assert.Single(result);
        Assert.Equal("reindex", completed.Operation);
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(
            database.Factory,
            "SELECT COUNT(*) FROM maintenance_history WHERE operation = 'prune';"));
    }

    [Fact]
    public async Task FirstStartWithoutRetentionPolicyPreservesUserHistory()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero));
        await SeedRetentionHistoryAsync(database.Factory, clock.GetUtcNow().AddDays(-90), "unconfigured");
        var maintenance = new SqliteDataPlatformMaintenance(database.Factory, clock);
        var runtime = new SqliteRuntimeDataStore(
            new(database.Path),
            database.Factory,
            new SqliteMigrationRunner(database.Factory),
            clock,
            maintenance: maintenance);

        await runtime.InitializeAsync(TestContext.Current.CancellationToken);

        await AssertRetentionHistoryAsync(database.Factory, "unconfigured", 1);
        var prune = (await maintenance.ListSchedulesAsync(TestContext.Current.CancellationToken))
            .Single(schedule => schedule.Operation == "prune");
        Assert.False(prune.Enabled);
        Assert.Null(prune.NextRunUtc);
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM maintenance_history WHERE operation = 'prune';"));
    }

    [Fact]
    public async Task ScheduledPruneWithNullRetentionPolicyPreservesUserHistory()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero));
        await SeedRetentionHistoryAsync(database.Factory, clock.GetUtcNow().AddDays(-90), "null-policy");
        await new SqliteV2DataStore(database.Factory).SaveRetentionPolicyAsync(
            new("local", false, null, false, null, clock.GetUtcNow(), "{}"),
            TestContext.Current.CancellationToken);
        var maintenance = new SqliteDataPlatformMaintenance(database.Factory, clock);
        await maintenance.ScheduleAsync("prune", true, 1, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromHours(2));
        var runtime = new SqliteRuntimeDataStore(
            new(database.Path),
            database.Factory,
            new SqliteMigrationRunner(database.Factory),
            clock,
            maintenance: maintenance);

        await runtime.InitializeAsync(TestContext.Current.CancellationToken);

        await AssertRetentionHistoryAsync(database.Factory, "null-policy", 1);
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM maintenance_history WHERE operation = 'prune';"));
        var prune = (await maintenance.ListSchedulesAsync(TestContext.Current.CancellationToken))
            .Single(schedule => schedule.Operation == "prune");
        Assert.True(prune.Enabled);
        Assert.Null(prune.LastRunUtc);
    }

    [Fact]
    public async Task ScheduledPruneSkipsDirectlyPoisonedInt64RetentionValue()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero));
        await SeedRetentionHistoryAsync(database.Factory, clock.GetUtcNow().AddDays(-90), "poisoned-policy");
        await using (var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA ignore_check_constraints = ON;
                INSERT INTO retention_policies(
                    policy_key, screenshot_retention_enabled, screenshot_retention_hours,
                    debug_capture_enabled, data_retention_days, updated_utc, extension_json)
                VALUES ('local', 0, NULL, 0, $days, $updated, '{}');
                PRAGMA ignore_check_constraints = OFF;
                """;
            command.Parameters.AddWithValue("$days", long.MaxValue);
            command.Parameters.AddWithValue("$updated", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var maintenance = new SqliteDataPlatformMaintenance(database.Factory, clock);
        await maintenance.ScheduleAsync("prune", true, 1, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromHours(2));

        Assert.Empty(await maintenance.RunDueAsync(TestContext.Current.CancellationToken));
        await AssertRetentionHistoryAsync(database.Factory, "poisoned-policy", 1);
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM maintenance_history WHERE operation = 'prune';"));
        var prune = (await maintenance.ListSchedulesAsync(TestContext.Current.CancellationToken))
            .Single(schedule => schedule.Operation == "prune");
        Assert.Null(prune.LastRunUtc);
    }

    [Fact]
    public async Task ScheduledPruneSkipsDirectlyPoisonedNonIntegerRetentionValue()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero));
        await SeedRetentionHistoryAsync(database.Factory, clock.GetUtcNow().AddDays(-90), "non-integer-policy");
        await using (var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA ignore_check_constraints = ON;
                INSERT INTO retention_policies(
                    policy_key, screenshot_retention_enabled, screenshot_retention_hours,
                    debug_capture_enabled, data_retention_days, updated_utc, extension_json)
                VALUES ('local', 0, NULL, 0, 1.5, $updated, '{}');
                PRAGMA ignore_check_constraints = OFF;
                """;
            command.Parameters.AddWithValue("$updated", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var maintenance = new SqliteDataPlatformMaintenance(database.Factory, clock);
        await maintenance.ScheduleAsync("prune", true, 1, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromHours(2));

        Assert.Empty(await maintenance.RunDueAsync(TestContext.Current.CancellationToken));
        await AssertRetentionHistoryAsync(database.Factory, "non-integer-policy", 1);
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM maintenance_history WHERE operation = 'prune';"));
    }

    [Fact]
    public async Task ScheduledPruneDerivesCutoffFromExplicitRetentionDays()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero));
        await SeedRetentionHistoryAsync(database.Factory, clock.GetUtcNow().AddDays(-11), "expired");
        await SeedRetentionHistoryAsync(database.Factory, clock.GetUtcNow().AddDays(-9), "retained");
        await new SqliteV2DataStore(database.Factory).SaveRetentionPolicyAsync(
            new("local", false, null, false, 10, clock.GetUtcNow(), "{}"),
            TestContext.Current.CancellationToken);
        var maintenance = new SqliteDataPlatformMaintenance(database.Factory, clock);
        await maintenance.ScheduleAsync("prune", true, 1, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromHours(2));
        var runtime = new SqliteRuntimeDataStore(
            new(database.Path),
            database.Factory,
            new SqliteMigrationRunner(database.Factory),
            clock,
            maintenance: maintenance);

        await runtime.InitializeAsync(TestContext.Current.CancellationToken);

        await AssertRetentionHistoryAsync(database.Factory, "expired", 0);
        await AssertRetentionHistoryAsync(database.Factory, "retained", 1);
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM maintenance_history WHERE operation = 'prune' AND status = 'completed' AND affected_rows = 4;"));
        var prune = (await maintenance.ListSchedulesAsync(TestContext.Current.CancellationToken))
            .Single(schedule => schedule.Operation == "prune");
        Assert.NotNull(prune.LastRunUtc);
    }

    [Fact]
    public async Task ScheduledPruneRevalidatesRetentionAfterClaimBeforeDeleting()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var scheduledAt = new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero);
        await SeedRetentionHistoryAsync(database.Factory, scheduledAt.AddDays(-90), "revoked-policy");
        await new SqliteV2DataStore(database.Factory).SaveRetentionPolicyAsync(
            new("local", false, null, false, 10, scheduledAt, "{}"),
            TestContext.Current.CancellationToken);
        var setup = new SqliteDataPlatformMaintenance(database.Factory, new ManualTimeProvider(scheduledAt));
        await setup.ScheduleAsync("prune", true, 1, TestContext.Current.CancellationToken);

        var clock = new PausingSecondReadTimeProvider(scheduledAt.AddHours(2));
        var maintenance = new SqliteDataPlatformMaintenance(database.Factory, clock);
        var run = Task.Run(
            () => maintenance.RunDueAsync(TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        try
        {
            await clock.Paused.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await new SqliteV2DataStore(database.Factory).SaveRetentionPolicyAsync(
                new("local", false, null, false, null, scheduledAt.AddHours(2), "{}"),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            clock.Release();
        }

        Assert.Empty(await run);
        await AssertRetentionHistoryAsync(database.Factory, "revoked-policy", 1);
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory, """
            SELECT COUNT(*)
            FROM maintenance_history
            WHERE operation = 'prune' AND status = 'failed'
              AND diagnostic_code = 'ScheduledPruneAuthorizationChangedException';
            """));
    }

    [Fact]
    public async Task ConcurrentScheduledRunnersClaimOneDueOperation()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new SqliteDataPlatformMaintenance(database.Factory, clock);
        await service.ScheduleAsync("prune", false, 24, TestContext.Current.CancellationToken);
        await service.ScheduleAsync("reindex", true, 1, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromHours(2));

        var runs = await Task.WhenAll(
            service.RunDueAsync(TestContext.Current.CancellationToken),
            service.RunDueAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, runs.Sum(run => run.Length));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM maintenance_history WHERE operation = 'reindex' AND status = 'completed';"));
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

    [Fact]
    public async Task UnreadSchemaAuditNamesRealReadersAndReportsOnlyTheMeasuredGaps()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var auditor = new SqliteV2UnreadSchemaAuditor(database.Factory);
        var baseline = await auditor.AuditAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            [
                ("item_metrics_v2", "measured_utc"),
                ("item_metrics_v2", "source"),
                ("outbox_target_operations", "applied_utc"),
                ("profile_workspaces", "updated_utc"),
            ],
            baseline.Select(member => (member.Table, member.Column)));
        Assert.All(baseline, member => Assert.Equal("no-production-read-path", member.Reason));

        await using (var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "ALTER TABLE maintenance_history ADD COLUMN abandoned_write_only_value TEXT;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var unread = await auditor.AuditAsync(TestContext.Current.CancellationToken);
        Assert.Equal(baseline.Length + 1, unread.Length);
        Assert.Contains(unread, member =>
            member.Table == "maintenance_history" &&
            member.Column == "abandoned_write_only_value" &&
            member.Reason == "no-production-read-path");

        await using (var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE maintenance_schedules;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var missing = await auditor.AuditAsync(TestContext.Current.CancellationToken);
        Assert.Contains(missing, value =>
            value.Table == "maintenance_schedules" && value.Column == "<missing-table>");
    }

    [Fact]
    public async Task UnreadSchemaAuditRejectsADeclarationWhoseProductionReaderDoesNotExist()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var falseDeclaration = new SqliteProductionReadPath(
            "maintenance_history",
            typeof(SqliteDataPlatformMaintenance),
            "ReaderThatDoesNotExist",
            ["run_id"]);
        var auditor = new SqliteV2UnreadSchemaAuditor(
            database.Factory,
            SqliteV2UnreadSchemaAuditor.DefaultReadPaths.Add(falseDeclaration));

        var result = await auditor.AuditAsync(TestContext.Current.CancellationToken);

        var invalid = Assert.Single(result, member => member.Reason == "declared-reader-not-found");
        Assert.Equal("maintenance_history", invalid.Table);
        Assert.Equal("*", invalid.Column);
        Assert.EndsWith("SqliteDataPlatformMaintenance.ReaderThatDoesNotExist", invalid.ReaderPath!, StringComparison.Ordinal);
    }

    private static async Task<string> ScalarTextAsync(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Assert.IsType<string>(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static async Task SeedRetentionHistoryAsync(
        SqliteConnectionFactory factory,
        DateTimeOffset recordedUtc,
        string suffix)
    {
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO player_profiles(id, name, game_mode, faction, level, created_utc, updated_utc)
            VALUES ('retention-profile', 'Retention fixture', 'Regular', 'USEC', 1, $recorded, $recorded)
            ON CONFLICT(id) DO NOTHING;

            INSERT INTO raids(id, profile_id, mode, start_utc)
            VALUES ($raid, 'retention-profile', 'Regular', $recorded);

            INSERT INTO observed_inventory_snapshots(
                snapshot_id, profile_id, generation, game_mode, recorded_utc, source,
                producer_version, is_current, payload_json)
            VALUES ($inventory, 'retention-profile', 'wipe', 'Regular', $recorded,
                    $source, 'retention-test', 0, '{}');

            INSERT INTO raid_field_history(
                raid_id, field_name, value_json, provenance_kind, source, recorded_utc)
            VALUES ($raid, 'retention-fixture', '{}', 'manual', $source, $recorded);

            INSERT INTO craft_history(
                history_id, craft_id, recorded_utc, source, payload_json)
            VALUES ($craft, 'retention-craft', $recorded, $source, '{}');

            INSERT INTO model_snapshots(
                model_snapshot_id, model_kind, presentation_kind, source, generated_utc,
                model_version, payload_json)
            VALUES ($model, 'retention-fixture', 'historical', $source, $recorded,
                    'retention-test', '{}');
            """;
        command.Parameters.AddWithValue("$recorded", recordedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$raid", $"retention-raid-{suffix}");
        command.Parameters.AddWithValue("$inventory", $"retention-inventory-{suffix}");
        command.Parameters.AddWithValue("$craft", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$model", $"retention-model-{suffix}");
        command.Parameters.AddWithValue("$source", $"retention-{suffix}");
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task AssertRetentionHistoryAsync(
        SqliteConnectionFactory factory,
        string suffix,
        int expected)
    {
        Assert.Equal(expected, await V2TestDatabase.ScalarAsync(factory,
            $"SELECT COUNT(*) FROM observed_inventory_snapshots WHERE snapshot_id = 'retention-inventory-{suffix}';"));
        Assert.Equal(expected, await V2TestDatabase.ScalarAsync(factory,
            $"SELECT COUNT(*) FROM raid_field_history WHERE source = 'retention-{suffix}';"));
        Assert.Equal(expected, await V2TestDatabase.ScalarAsync(factory,
            $"SELECT COUNT(*) FROM craft_history WHERE source = 'retention-{suffix}';"));
        Assert.Equal(expected, await V2TestDatabase.ScalarAsync(factory,
            $"SELECT COUNT(*) FROM model_snapshots WHERE model_snapshot_id = 'retention-model-{suffix}';"));
    }

    private static TarkovDevJsonClient FixtureClient(
        HttpMessageHandler handler,
        ITarkovDevResponseCache cache,
        TimeSpan? requestTimeout = null) =>
        new(
            new HttpClient(handler),
            cache,
            new DataTranslationService(),
            new()
            {
                BaseAddress = new("https://fixture.invalid/"),
                InitialRetryDelay = TimeSpan.Zero,
                MaxAttempts = 1,
                RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5),
            });

    public enum EndpointFault
    {
        ServiceUnavailable,
        Timeout,
        ConnectionReset,
    }

    private sealed class PausedItemsFixtureHandler : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _fixtures = new(new FixtureApiHandler());
        private readonly TaskCompletionSource<bool> _itemsRequestStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseItems =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ItemsRequestStarted => _itemsRequestStarted.Task;

        public void ReleaseItems() => _releaseItems.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(
                    request.RequestUri?.AbsolutePath.Trim('/'),
                    "regular/items",
                    StringComparison.Ordinal))
            {
                return await _fixtures.SendAsync(request, cancellationToken);
            }

            _itemsRequestStarted.TrySetResult(true);
            await _releaseItems.Task.WaitAsync(cancellationToken);
            using var fixture = await _fixtures.SendAsync(request, cancellationToken);
            var body = await fixture.Content.ReadAsStringAsync(cancellationToken);
            var olderBody = body.Replace(
                "\"basePrice\": 18000",
                "\"basePrice\": 999999",
                StringComparison.Ordinal);
            if (string.Equals(body, olderBody, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The paused fixture did not create an older distinct item document.");
            }

            return FixtureApiHandler.Json(olderBody);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _releaseItems.TrySetResult(true);
                _fixtures.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class PausingSecondReadTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly TaskCompletionSource<bool> _paused =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        public Task Paused => _paused.Task;

        public void Release() => _release.TrySetResult(true);

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref _readCount) == 2)
            {
                _paused.TrySetResult(true);
                _release.Task.GetAwaiter().GetResult();
            }

            return utcNow;
        }
    }

    private sealed class RemappedModeFixtureHandler(string fromMode, string toMode) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _fixtures = new(new FixtureApiHandler());

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Fixture request had no URI.");
            var prefix = $"/{fromMode}/";
            if (uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            {
                var builder = new UriBuilder(uri)
                {
                    Path = $"/{toMode}/{uri.AbsolutePath[prefix.Length..]}",
                };
                request.RequestUri = builder.Uri;
            }

            return _fixtures.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _fixtures.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class FaultingFixtureHandler(string path, EndpointFault fault) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _fixtures = new(new FixtureApiHandler());

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(request.RequestUri?.AbsolutePath.Trim('/'), path, StringComparison.Ordinal))
            {
                return await _fixtures.SendAsync(request, cancellationToken);
            }

            return fault switch
            {
                EndpointFault.ServiceUnavailable => new(HttpStatusCode.ServiceUnavailable),
                EndpointFault.ConnectionReset => new(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ResetAfterPrefixStream(
                        Encoding.UTF8.GetBytes("{\"data\":{\"items\":{"))),
                },
                EndpointFault.Timeout => await NeverCompletesAsync(cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(fault)),
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _fixtures.Dispose();
            base.Dispose(disposing);
        }

        private static async Task<HttpResponseMessage> NeverCompletesAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The timeout fixture can complete only through cancellation.");
        }
    }

    private sealed class BodyOverrideFixtureHandler(string path, string body) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _fixtures = new(new FixtureApiHandler());

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            string.Equals(request.RequestUri?.AbsolutePath.Trim('/'), path, StringComparison.Ordinal)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body),
                })
                : _fixtures.SendAsync(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing) _fixtures.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ResetAfterPrefixStream(byte[] prefix) : Stream
    {
        private bool _read;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_read) throw new IOException("fixture-connection-reset");
            _read = true;
            prefix.CopyTo(buffer);
            return ValueTask.FromResult(prefix.Length);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
