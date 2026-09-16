using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Loadouts;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.IntegrationTests.DataV2;

[Collection(SqliteCollection.Name)]
public sealed class CacheAndHostileInputTests
{
    public static IEnumerable<object[]> HostileResponseCases()
    {
        yield return ["not-json", 32, 4096L];
        yield return ["{\"future\":true}", 32, 4096L];
        yield return
        [
            "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Deep\",\"width\":1,\"height\":1}},\"itemCategories\":{}},\"future\":" +
            string.Concat(Enumerable.Repeat("{\"child\":", 12)) + "null" + new string('}', 12) + "}",
            8,
            4096L,
        ];
        yield return
        [
            "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Large\",\"width\":1,\"height\":1}},\"itemCategories\":{}},\"padding\":\"" +
            new string('x', 3000) + "\"}",
            32,
            2048L,
        ];
    }

    public static IEnumerable<object[]> CachedPolicyCases()
    {
        yield return
        [
            "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Too large\",\"width\":1,\"height\":1}},\"itemCategories\":{}},\"padding\":\"" +
            new string('x', 1400) + "\"}",
            32,
        ];
        yield return
        [
            "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Too deep\",\"width\":1,\"height\":1}},\"itemCategories\":{}},\"future\":" +
            string.Concat(Enumerable.Repeat("{\"child\":", 12)) + "null" + new string('}', 12) + "}",
            8,
        ];
    }

    public static IEnumerable<object[]> NestedShrinkCases()
    {
        yield return
        [
            "items",
            "item categories",
            "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Item\",\"width\":1,\"height\":1}},\"itemCategories\":{\"a\":{\"id\":\"a\"},\"b\":{\"id\":\"b\"},\"c\":{\"id\":\"c\"}}}}",
            "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Item\",\"width\":1,\"height\":1}},\"itemCategories\":{\"a\":{\"id\":\"a\"}}}}",
        ];
        yield return
        [
            "items",
            "item category memberships",
            "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Item\",\"width\":1,\"height\":1,\"categories\":[\"a\",\"b\",\"c\"]}},\"itemCategories\":{}}}",
            "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Item\",\"width\":1,\"height\":1,\"categories\":[\"a\"]}},\"itemCategories\":{}}}",
        ];
        yield return
        [
            "items",
            "item trader offers",
            "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Item\",\"width\":1,\"height\":1,\"sellToTrader\":[{\"trader\":\"a\",\"price\":1},{\"trader\":\"b\",\"price\":1},{\"trader\":\"c\",\"price\":1}]}},\"itemCategories\":{}}}",
            "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Item\",\"width\":1,\"height\":1,\"sellToTrader\":[{\"trader\":\"a\",\"price\":1}]}},\"itemCategories\":{}}}",
        ];
        yield return
        [
            "maps",
            "map extracts",
            "{\"data\":{\"maps\":{\"map\":{\"id\":\"map\",\"name\":\"Map\",\"extracts\":[{\"id\":\"a\"},{\"id\":\"b\"},{\"id\":\"c\"}]}},\"lootContainers\":{}}}",
            "{\"data\":{\"maps\":{\"map\":{\"id\":\"map\",\"name\":\"Map\",\"extracts\":[{\"id\":\"a\"}]}},\"lootContainers\":{}}}",
        ];
        yield return
        [
            "maps",
            "map locks",
            "{\"data\":{\"maps\":{\"map\":{\"id\":\"map\",\"name\":\"Map\",\"extracts\":[{\"id\":\"extract\"}],\"locks\":[{\"id\":\"a\"},{\"id\":\"b\"},{\"id\":\"c\"}]}},\"lootContainers\":{}}}",
            "{\"data\":{\"maps\":{\"map\":{\"id\":\"map\",\"name\":\"Map\",\"extracts\":[{\"id\":\"extract\"}],\"locks\":[{\"id\":\"a\"}]}},\"lootContainers\":{}}}",
        ];
        yield return
        [
            "tasks",
            "task objectives",
            "{\"data\":{\"tasks\":{\"task\":{\"id\":\"task\",\"name\":\"Task\",\"objectives\":[{\"id\":\"a\",\"type\":\"visit\"},{\"id\":\"b\",\"type\":\"visit\"},{\"id\":\"c\",\"type\":\"visit\"}]}}}}",
            "{\"data\":{\"tasks\":{\"task\":{\"id\":\"task\",\"name\":\"Task\",\"objectives\":[{\"id\":\"a\",\"type\":\"visit\"}]}}}}",
        ];
        yield return
        [
            "tasks",
            "task prerequisites",
            "{\"data\":{\"tasks\":{\"task\":{\"id\":\"task\",\"name\":\"Task\",\"taskRequirements\":[{\"task\":\"a\"},{\"task\":\"b\"},{\"task\":\"c\"}]}}}}",
            "{\"data\":{\"tasks\":{\"task\":{\"id\":\"task\",\"name\":\"Task\",\"taskRequirements\":[{\"task\":\"a\"}]}}}}",
        ];
        yield return
        [
            "tasks",
            "objective item targets",
            "{\"data\":{\"tasks\":{\"task\":{\"id\":\"task\",\"name\":\"Task\",\"objectives\":[{\"id\":\"objective\",\"type\":\"giveItem\",\"items\":[\"a\",\"b\",\"c\"]}]}}}}",
            "{\"data\":{\"tasks\":{\"task\":{\"id\":\"task\",\"name\":\"Task\",\"objectives\":[{\"id\":\"objective\",\"type\":\"giveItem\",\"items\":[\"a\"]}]}}}}",
        ];
        yield return
        [
            "tasks",
            "objective map links",
            "{\"data\":{\"tasks\":{\"task\":{\"id\":\"task\",\"name\":\"Task\",\"objectives\":[{\"id\":\"objective\",\"type\":\"visit\",\"maps\":[\"a\",\"b\",\"c\"]}]}}}}",
            "{\"data\":{\"tasks\":{\"task\":{\"id\":\"task\",\"name\":\"Task\",\"objectives\":[{\"id\":\"objective\",\"type\":\"visit\",\"maps\":[\"a\"]}]}}}}",
        ];
        yield return
        [
            "tasks",
            "objective zones",
            "{\"data\":{\"tasks\":{\"task\":{\"id\":\"task\",\"name\":\"Task\",\"objectives\":[{\"id\":\"objective\",\"type\":\"visit\",\"zones\":[{},{},{}]}]}}}}",
            "{\"data\":{\"tasks\":{\"task\":{\"id\":\"task\",\"name\":\"Task\",\"objectives\":[{\"id\":\"objective\",\"type\":\"visit\",\"zones\":[{}]}]}}}}",
        ];
        yield return
        [
            "hideout",
            "hideout levels",
            "{\"data\":{\"station\":{\"id\":\"station\",\"name\":\"Station\",\"levels\":[{\"id\":\"a\",\"level\":1},{\"id\":\"b\",\"level\":2},{\"id\":\"c\",\"level\":3}]}}}",
            "{\"data\":{\"station\":{\"id\":\"station\",\"name\":\"Station\",\"levels\":[{\"id\":\"a\",\"level\":1}]}}}",
        ];
        yield return
        [
            "hideout",
            "hideout item requirements",
            "{\"data\":{\"station\":{\"id\":\"station\",\"name\":\"Station\",\"levels\":[{\"id\":\"level\",\"level\":1,\"itemRequirements\":[{\"item\":\"a\",\"count\":1},{\"item\":\"b\",\"count\":1},{\"item\":\"c\",\"count\":1}]}]}}}",
            "{\"data\":{\"station\":{\"id\":\"station\",\"name\":\"Station\",\"levels\":[{\"id\":\"level\",\"level\":1,\"itemRequirements\":[{\"item\":\"a\",\"count\":1}]}]}}}",
        ];
        yield return
        [
            "traders",
            "trader levels",
            "{\"data\":{\"trader\":{\"id\":\"trader\",\"name\":\"Trader\",\"levels\":[{\"id\":\"a\",\"level\":1},{\"id\":\"b\",\"level\":2},{\"id\":\"c\",\"level\":3}]}}}",
            "{\"data\":{\"trader\":{\"id\":\"trader\",\"name\":\"Trader\",\"levels\":[{\"id\":\"a\",\"level\":1}]}}}",
        ];
        yield return
        [
            "crafts",
            "craft requirements",
            "{\"data\":[{\"id\":\"craft\",\"requiredItems\":[{\"item\":\"a\",\"count\":1},{\"item\":\"b\",\"count\":1},{\"item\":\"c\",\"count\":1}],\"productItem\":{\"item\":\"out\",\"count\":1}}]}",
            "{\"data\":[{\"id\":\"craft\",\"requiredItems\":[{\"item\":\"a\",\"count\":1}],\"productItem\":{\"item\":\"out\",\"count\":1}}]}",
        ];
        yield return
        [
            "barters",
            "barter requirements",
            "{\"data\":[{\"id\":\"barter\",\"requiredItems\":[{\"item\":\"a\",\"count\":1},{\"item\":\"b\",\"count\":1},{\"item\":\"c\",\"count\":1}],\"offeredItem\":{\"item\":\"out\",\"count\":1}}]}",
            "{\"data\":[{\"id\":\"barter\",\"requiredItems\":[{\"item\":\"a\",\"count\":1}],\"offeredItem\":{\"item\":\"out\",\"count\":1}}]}",
        ];
    }

    [Fact]
    public async Task ContentAddressedCacheDeduplicatesCompressedBodiesAndCleanupIsInspectable()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var cache = new SqliteTarkovDevResponseCache(database.Factory, new()
        {
            MaximumCompressedBytes = 1024 * 1024,
            MaximumEntries = 2,
            MaximumAge = TimeSpan.FromHours(1),
        });
        var now = DateTimeOffset.UtcNow;
        var body = "{\"data\":{\"value\":\"" + new string('x', 64 * 1024) + "\"}}";
        await cache.PutAsync(new("regular/items", body, now, null, null), TestContext.Current.CancellationToken);
        await cache.PutAsync(new("pve/items", body, now, null, null), TestContext.Current.CancellationToken);
        var inspection = await cache.InspectAsync(now, TestContext.Current.CancellationToken);
        Assert.Equal(2, inspection.EntryCount);
        Assert.Equal(1, inspection.UniqueBodyCount);
        Assert.True(inspection.CompressedBytes < inspection.UncompressedBytes);

        var dryRun = await cache.CleanupAsync(now.AddHours(2), true, TestContext.Current.CancellationToken);
        Assert.Equal(2, dryRun.RemovedEntries);
        Assert.NotNull(await cache.GetAsync("regular/items", TestContext.Current.CancellationToken));
        var cleanup = await cache.CleanupAsync(now.AddHours(2), false, TestContext.Current.CancellationToken);
        Assert.Equal(2, cleanup.RemovedEntries);
        Assert.Equal(1, cleanup.RemovedBodies);
        Assert.Null(await cache.GetAsync("regular/items", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SqlitePutDoesNotProtectAnAlreadyExpiredResponseFromTheAgeBudget()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        var cache = new SqliteTarkovDevResponseCache(database.Factory, new()
        {
            MaximumCompressedBytes = 1024 * 1024,
            MaximumEntries = 2,
            MaximumAge = TimeSpan.FromHours(1),
        }, time);

        await cache.PutAsync(
            new("regular/items", "{\"data\":{}}", now.AddHours(-2), null, null),
            TestContext.Current.CancellationToken);

        Assert.Null(await cache.GetAsync("regular/items", TestContext.Current.CancellationToken));
        var inspection = await cache.InspectAsync(now, TestContext.Current.CancellationToken);
        Assert.Equal(0, inspection.EntryCount);
        Assert.Equal(0, inspection.UniqueBodyCount);
    }

    [Fact]
    public async Task RepresentativeSevenEndpointFirstSyncStaysWithinFiftyMiBOnDisk()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var cache = new SqliteTarkovDevResponseCache(database.Factory, new()
        {
            MaximumCompressedBytes = 64L * 1024 * 1024,
            MaximumEntries = 16,
            MaximumAge = TimeSpan.FromDays(1),
        });
        await using var client = new TarkovDevJsonClient(
            new HttpClient(new PaddedFixtureApiHandler()),
            cache,
            new DataTranslationService(),
            new()
            {
                BaseAddress = new("https://fixture.invalid/"),
                InitialRetryDelay = TimeSpan.Zero,
                MaxAttempts = 1,
                MaximumResponseBytes = 16L * 1024 * 1024,
            });
        var operation = new TarkovDevDataRefreshOperation(
            client,
            new SqliteDataRefreshRepository(database.Factory),
            new SqliteSyncStateRepository(database.Factory));

        var result = await operation.RefreshAsync(
            new(GameMode.Regular, "en", true),
            TestContext.Current.CancellationToken);

        Assert.Equal(7, result.Count);
        Assert.All(result, endpoint => Assert.True(endpoint.Updated, $"{endpoint.Endpoint}: {endpoint.Error}"));
        Assert.Equal(7, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM dataset_heads WHERE state = 'current';"));
        foreach (var table in new[]
        {
            "items", "maps", "tasks", "hideout_stations", "traders", "crafts", "barters",
        })
        {
            Assert.True(
                await V2TestDatabase.ScalarAsync(database.Factory, $"SELECT COUNT(*) FROM [{table}];") > 0,
                $"Production first sync did not normalize {table}.");
        }

        var evidence = await cache.InspectAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.True(evidence.EntryCount >= 7);
        Assert.True(evidence.UniqueBodyCount >= 7);
        Assert.True(evidence.UncompressedBytes >= PaddedFixtureApiHandler.MinimumPrimaryPayloadBytes);
        var onDiskBytes = DatabaseFootprint(database.Path);
        Assert.True(
            onDiskBytes <= 50L * 1024 * 1024,
            $"Representative first sync used {onDiskBytes:N0} bytes on disk; budget is 52,428,800.");
    }

    [Fact]
    public async Task ProductionPathLoadsPersistsAndQueriesEightyThousandItemsWithinMeasuredBaseline()
    {
        const int itemCount = 80_001;
        var json = BuildLargeItemEnvelope(itemCount);
        Assert.InRange(Encoding.UTF8.GetByteCount(json), 4 * 1024 * 1024, 16 * 1024 * 1024);
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var client = Client(
            new StaticHandler(new(HttpStatusCode.OK) { Content = new StringContent(json) }),
            new SqliteTarkovDevResponseCache(database.Factory),
            maximumBytes: 16 * 1024 * 1024);

        var elapsed = Stopwatch.StartNew();
        var response = await client.GetItemsAsync(
            GameMode.Regular,
            "en",
            TestContext.Current.CancellationToken);
        await new SqliteDataRefreshRepository(database.Factory).RefreshItemsAsync(
            response.Data,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
        var queried = await new SqliteItemRepository(database.Factory).GetAsync(
            "item-80000",
            TestContext.Current.CancellationToken);
        elapsed.Stop();

        Assert.Equal(itemCount, response.Data.Items.Count);
        Assert.Equal(itemCount, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM items;"));
        Assert.Equal("Item 80000", queried?.Name);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(120),
            $"80K production parse, normalized write, and indexed lookup took {elapsed.Elapsed}.");
    }

    [Fact]
    public async Task ProductionPathLoadsPersistsAndQueriesTwoPointThreeMiBMapWithinMeasuredBaseline()
    {
        const int targetBytes = 2_300 * 1024;
        const string prefix = "{\"data\":{\"maps\":{\"map\":{\"id\":\"map\",\"name\":\"Measured map\",\"extracts\":[{\"id\":\"extract\",\"name\":\"Extract\"}],\"fixturePadding\":\"";
        const string suffix = "\"}},\"lootContainers\":{}}}";
        var json = prefix + new string('m', targetBytes - prefix.Length - suffix.Length) + suffix;
        Assert.Equal(targetBytes, Encoding.UTF8.GetByteCount(json));
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var client = Client(
            new StaticHandler(new(HttpStatusCode.OK) { Content = new StringContent(json) }),
            new SqliteTarkovDevResponseCache(database.Factory),
            maximumBytes: 16 * 1024 * 1024);

        var elapsed = Stopwatch.StartNew();
        var response = await client.GetMapsAsync(
            GameMode.Regular,
            "en",
            TestContext.Current.CancellationToken);
        await new SqliteDataRefreshRepository(database.Factory).RefreshMapsAsync(
            response.Data,
            TestContext.Current.CancellationToken);
        var queried = await new SqliteMapDefinitionCache(database.Factory).GetAsync(
            "map",
            TestContext.Current.CancellationToken);
        elapsed.Stop();

        Assert.Equal("Measured map", response.Data.Maps["map"].Name);
        Assert.Equal("Extract", Assert.Single(queried!.Extracts).Name);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(30),
            $"2.3 MiB production parse, normalized write, and map query took {elapsed.Elapsed}.");
    }

    [Fact]
    public async Task CompositeMapChildIdentitiesCannotCollideAcrossDelimiterShapedIds()
    {
        const string json = "{\"data\":{\"maps\":{\"a\":{\"id\":\"a\",\"name\":\"First\",\"extracts\":[{\"id\":\"b:0:c\",\"name\":\"First extract\"}],\"locks\":[{\"id\":\"b:c\"}]},\"a:0:b\":{\"id\":\"a:0:b\",\"name\":\"Second\",\"extracts\":[{\"id\":\"c\",\"name\":\"Second extract\"}],\"locks\":[{\"id\":\"c\"}]}},\"lootContainers\":{}}}";
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var cache = new SqliteTarkovDevResponseCache(database.Factory);
        var handler = new SequenceHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json),
        });
        await using var client = Client(handler, cache, maximumBytes: 4096);

        var response = await client.GetMapsAsync(
            GameMode.Regular,
            "en",
            TestContext.Current.CancellationToken);
        await new SqliteDataRefreshRepository(database.Factory).RefreshMapsAsync(
            response.Data,
            TestContext.Current.CancellationToken);
        var cached = await client.GetMapsAsync(
            GameMode.Regular,
            "en",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, cached.Data.Maps.Count);
        Assert.True(cached.IsFromCache);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(2, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM maps;"));
        Assert.Equal(2, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM map_extracts;"));
        Assert.Equal(2, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM map_locks;"));
    }

    [Fact]
    public async Task CorruptCompressedLocalJsonIsQuarantinedAsRecoverableState()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var cache = new SqliteTarkovDevResponseCache(database.Factory);
        await cache.PutAsync(new("regular/items", "{\"data\":{}}", DateTimeOffset.UtcNow, null, null), TestContext.Current.CancellationToken);
        await using (var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE raw_endpoint_bodies SET compressed_body = X'000102', compressed_bytes = 3;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        Assert.Null(await cache.GetAsync("regular/items", TestContext.Current.CancellationToken));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM local_json_recovery WHERE document_key = 'cache:regular/items' AND state = 'quarantined' AND diagnostic_code = 'cache-json-invalid';"));
    }

    [Fact]
    public async Task DynamicAndOversizedCacheMetadataIsQuarantinedWithoutMaterializingIt()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var cache = new SqliteTarkovDevResponseCache(database.Factory);
        await cache.PutAsync(new("regular/items", "{\"data\":{}}", DateTimeOffset.UtcNow, null, null),
            TestContext.Current.CancellationToken);
        await using (var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA foreign_keys = OFF;
                UPDATE http_response_cache
                SET content_sha256 = zeroblob(8192), etag = zeroblob(8192);
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        Assert.Null(await cache.GetAsync("regular/items", TestContext.Current.CancellationToken));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM local_json_recovery WHERE document_key = 'cache:regular/items' AND state = 'quarantined' AND content_sha256 IS NULL AND diagnostic_code = 'cache-metadata-invalid';"));
    }

    [Fact]
    public async Task InspectionAndCleanupFailClosedOnOversizedDynamicMetadata()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var cache = new SqliteTarkovDevResponseCache(database.Factory);
        await cache.PutAsync(new("regular/items", "{\"data\":{}}", DateTimeOffset.UtcNow, null, null),
            TestContext.Current.CancellationToken);
        await using (var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE http_response_cache SET last_accessed_utc = zeroblob(8192);";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => cache.InspectAsync(
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.CleanupAsync(
            DateTimeOffset.UtcNow,
            dryRun: true,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CacheImplementationsRejectMetadataThatMaintenanceCannotReadBack()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        ITarkovDevResponseCache[] caches =
        [
            new InMemoryTarkovDevResponseCache(),
            new SqliteTarkovDevResponseCache(database.Factory),
        ];
        var oversizedKey = new string('\u00e9', 2049);
        var oversizedEntityTag = new string('\u00e9', 2049);
        var body = "{\"data\":{}}";

        foreach (var cache in caches)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.GetAsync(
                oversizedKey,
                TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.PutAsync(
                new(oversizedKey, body, DateTimeOffset.UtcNow, null, null),
                TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.PutAsync(
                new("regular/items", body, DateTimeOffset.UtcNow, oversizedEntityTag, null),
                TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.PutAsync(
                new("regular/items", body, DateTimeOffset.UtcNow, null, null, new string('a', 65)),
                TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.QuarantineAsync(
                "regular/items",
                new string('a', 65),
                "cache-json-invalid",
                TestContext.Current.CancellationToken));
            Assert.Equal(0, (await cache.InspectAsync(
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken)).EntryCount);
        }
    }

    [Fact]
    public async Task CleanupEvictsLargeSharedBodyMetadataSetWithoutQuadraticRescans()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        // Pin the clock: with the system clock this fixed date aged past MaximumAge a day after
        // the test was written, GetAsync returned null, and the test failed on every later run.
        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var cache = new SqliteTarkovDevResponseCache(database.Factory, new()
        {
            MaximumEntries = 128,
            MaximumCompressedBytes = 1024,
            MaximumUncompressedBodyBytes = 4096,
            MaximumAge = TimeSpan.FromDays(1),
        }, new ManualTimeProvider(now));
        await cache.PutAsync(new("regular/items", "{\"data\":{}}", now, null, null),
            TestContext.Current.CancellationToken);
        var hash = (await cache.GetAsync("regular/items", TestContext.Current.CancellationToken))!.ContentSha256!;

        await using (var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH RECURSIVE sequence(value) AS (
                    SELECT 1
                    UNION ALL
                    SELECT value + 1 FROM sequence WHERE value < 5000
                )
                INSERT INTO http_response_cache(
                    cache_key, content_sha256, cached_utc, last_accessed_utc, etag, last_modified)
                SELECT printf('bulk/%05d', value), $hash, $now, $now, NULL, NULL
                FROM sequence;
                """;
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var result = await cache.CleanupAsync(now, dryRun: true, TestContext.Current.CancellationToken);

        Assert.Equal(5001 - 128, result.RemovedEntries);
        Assert.Equal(0, result.RemovedBodies);
        Assert.Equal(0, result.ReclaimedCompressedBytes);
        Assert.Equal(5001, (await cache.InspectAsync(now, TestContext.Current.CancellationToken)).EntryCount);
    }

    [Fact]
    public async Task MalformedLocalCacheMetadataIsQuarantinedInsteadOfEscapingStartup()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var cache = new SqliteTarkovDevResponseCache(database.Factory);
        await cache.PutAsync(new("regular/items", "{\"data\":{}}", DateTimeOffset.UtcNow, null, null),
            TestContext.Current.CancellationToken);
        await using (var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE raw_endpoint_bodies SET created_utc = 'not-a-timestamp';";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        Assert.Null(await cache.GetAsync("regular/items", TestContext.Current.CancellationToken));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM local_json_recovery WHERE document_key = 'cache:regular/items' AND state = 'quarantined' AND diagnostic_code = 'cache-json-invalid';"));
    }

    [Fact]
    public async Task CacheBlobLengthAndTypeAreCheckedBeforeAHostileBodyCanBeMaterialized()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var cache = new SqliteTarkovDevResponseCache(database.Factory, new()
        {
            MaximumCompressedBytes = 1024,
            MaximumUncompressedBodyBytes = 4096,
        });
        await cache.PutAsync(new("regular/items", "{\"data\":{}}", DateTimeOffset.UtcNow, null, null),
            TestContext.Current.CancellationToken);
        await using (var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            // A corrupt or hostile local database can lie in the declared-size column. The cache
            // must inspect SQLite's actual BLOB length before asking the provider for its bytes.
            command.CommandText = """
                PRAGMA ignore_check_constraints = ON;
                UPDATE raw_endpoint_bodies
                SET compressed_body = zeroblob(8192),
                    compressed_bytes = 3,
                    uncompressed_bytes = 'hostile';
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        Assert.Null(await cache.GetAsync("regular/items", TestContext.Current.CancellationToken));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM local_json_recovery WHERE document_key = 'cache:regular/items' AND state = 'quarantined' AND diagnostic_code = 'cache-metadata-invalid';"));
    }

    [Fact]
    public async Task ResponseByteBudgetRefusesBeforeCachePublication()
    {
        var cache = new InMemoryTarkovDevResponseCache();
        var content = new ByteArrayContent(new byte[2048]);
        var client = Client(new StaticHandler(new(HttpStatusCode.OK) { Content = content }), cache, maximumBytes: 1024);
        var exception = await Assert.ThrowsAnyAsync<InvalidDataException>(() =>
            client.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken));
        var refusal = Assert.IsType<TarkovDevDatasetRefusalMarker>(exception.InnerException);
        Assert.IsType<TarkovDevResponseBudgetException>(refusal.InnerException);
        Assert.Equal(0, (await cache.InspectAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).EntryCount);
    }

    [Theory]
    [MemberData(nameof(HostileResponseCases))]
    public async Task HostileBodiesAreRecordedAsRefusedInsteadOfStaleOrFailed(
        string hostileJson,
        int maximumDepth,
        long maximumBytes)
    {
        const string lastKnownGood = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Last known good\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var cache = new SqliteTarkovDevResponseCache(database.Factory);
        await cache.PutAsync(
            new("regular/items", lastKnownGood, DateTimeOffset.UtcNow, null, null),
            TestContext.Current.CancellationToken);
        await using var client = Client(
            new EndpointOverrideFixtureHandler("regular/items", hostileJson),
            cache,
            maximumBytes,
            maximumDepth);

        var results = await new TarkovDevDataRefreshOperation(
                client,
                new SqliteDataRefreshRepository(database.Factory),
                new SqliteSyncStateRepository(database.Factory))
            .RefreshAsync(
                new(GameMode.Regular, "en", true),
                TestContext.Current.CancellationToken);

        var items = Assert.Single(results, result => result.Endpoint == "items");
        Assert.False(items.Updated);
        Assert.False(items.UsedStaleCache);
        Assert.NotNull(items.Error);
        Assert.Contains("Refused", items.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("refused", await ScalarTextAsync(
            database.Factory,
            "SELECT state FROM dataset_heads WHERE source_key = 'items' AND game_mode = 'regular' AND language = 'en';"));
        Assert.Equal(lastKnownGood, (await cache.GetAsync(
            "regular/items",
            TestContext.Current.CancellationToken))!.BodyJson);
    }

    [Theory]
    [MemberData(nameof(CachedPolicyCases))]
    public async Task FreshCacheWrittenUnderLooserPolicyIsRevalidatedBeforeShortcut(
        string cachedJson,
        int maximumDepth)
    {
        const string repairedJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Repaired\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        var cache = new InMemoryTarkovDevResponseCache();
        await cache.PutAsync(
            new("regular/items", cachedJson, DateTimeOffset.UtcNow, null, null),
            TestContext.Current.CancellationToken);
        var handler = new SequenceHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(repairedJson),
        });
        await using var client = Client(handler, cache, maximumBytes: 1024, depth: maximumDepth);

        var response = await client.GetItemsAsync(
            GameMode.Regular,
            "en",
            TestContext.Current.CancellationToken);

        Assert.Equal("Repaired", response.Data.Items["item"].Name);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(repairedJson, (await cache.GetAsync(
            "regular/items",
            TestContext.Current.CancellationToken))!.BodyJson);
    }

    [Fact]
    public async Task HalfTransferFailureNeverPublishesCacheEntry()
    {
        var cache = new InMemoryTarkovDevResponseCache();
        var prefix = Encoding.UTF8.GetBytes("{\"data\":{\"items\":{");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HalfTransferStream(prefix)) };
        var client = Client(new StaticHandler(response), cache, maximumBytes: 4096);
        var failure = await Assert.ThrowsAsync<TarkovDevRequestException>(() =>
            client.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken));
        Assert.IsType<IOException>(failure.InnerException);
        Assert.Equal(0, (await cache.InspectAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).EntryCount);
    }

    [Fact]
    public async Task ConnectionResetRetriesAndPublishesOnlyTheCompleteResponse()
    {
        var cache = new InMemoryTarkovDevResponseCache();
        var prefix = Encoding.UTF8.GetBytes("{\"data\":{\"items\":{");
        const string complete = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Complete\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        var handler = new SequenceHandler(attempt => attempt == 1
            ? new(HttpStatusCode.OK) { Content = new StreamContent(new HalfTransferStream(prefix)) }
            : new(HttpStatusCode.OK) { Content = new StringContent(complete) });
        var client = Client(handler, cache, maximumBytes: 4096, maxAttempts: 2);

        var response = await client.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);

        Assert.Equal("Complete", response.Data.Items["item"].Name);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(1, (await cache.InspectAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).EntryCount);
    }

    [Fact]
    public async Task MalformedMirrorUtf8FallsThroughToUpstream()
    {
        const string complete = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Upstream\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        var handler = new SequenceHandler(attempt => attempt == 1
            ? new(HttpStatusCode.OK) { Content = new ByteArrayContent([0xC3, 0x28]) }
            : new(HttpStatusCode.OK) { Content = new StringContent(complete) });
        await using var client = Client(
            handler,
            new InMemoryTarkovDevResponseCache(),
            maximumBytes: 4096,
            mirrorAddress: new("https://mirror.invalid/catalog/"));

        var response = await client.GetItemsAsync(
            GameMode.Regular,
            "en",
            TestContext.Current.CancellationToken);

        Assert.Equal("Upstream", response.Data.Items["item"].Name);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task RetryBackoffUsesInjectedClockInsteadOfWallTime()
    {
        const string complete = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Recovered\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        var cache = new InMemoryTarkovDevResponseCache();
        var clock = new ImmediateTimerTimeProvider();
        var handler = new SequenceHandler(attempt => attempt == 1
            ? new(HttpStatusCode.ServiceUnavailable)
            : new(HttpStatusCode.OK) { Content = new StringContent(complete) });
        await using var client = Client(
            handler,
            cache,
            maximumBytes: 4096,
            maxAttempts: 2,
            initialRetryDelay: TimeSpan.FromHours(1),
            timeProvider: clock);

        var response = await client.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("Recovered", response.Data.Items["item"].Name);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(1, clock.TimerCreationCount);
    }

    [Fact]
    public async Task CancellationDuringResponseStreamingLeavesNoPartialCacheEntry()
    {
        var cache = new InMemoryTarkovDevResponseCache();
        var stream = new CancellableHalfTransferStream(Encoding.UTF8.GetBytes("{\"data\":{\"items\":{"));
        await using var client = Client(
            new StaticHandler(new(HttpStatusCode.OK) { Content = new StreamContent(stream) }),
            cache,
            maximumBytes: 4096);
        using var cancellation = new CancellationTokenSource();
        var request = client.GetItemsAsync(GameMode.Regular, "en", cancellation.Token);
        await stream.FirstChunkRead.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(0, (await cache.InspectAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).EntryCount);
    }

    [Fact]
    public async Task CancelledCacheMissWaiterDoesNotCancelAnotherSharedCaller()
    {
        const string json = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Shared\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        var cache = new InMemoryTarkovDevResponseCache();
        var handler = new GateHandler(json);
        await using var client = Client(handler, cache, maximumBytes: 4096);
        using var cancelledWaiter = new CancellationTokenSource();

        var first = client.GetItemsAsync(GameMode.Regular, "en", cancelledWaiter.Token);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = client.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);

        cancelledWaiter.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        handler.Release();

        Assert.Equal("Shared", (await second).Data.Items["item"].Name);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task OlderBackgroundResponseCannotOverwriteCompletedForcedRefresh()
    {
        var cache = new InMemoryTarkovDevResponseCache();
        const string oldJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Old\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        const string backgroundJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Background\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        const string newJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Forced\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        await cache.PutAsync(new("regular/items", oldJson, DateTimeOffset.UtcNow.AddDays(-1), null, null),
            TestContext.Current.CancellationToken);
        var handler = new OrderedGateHandler(backgroundJson, newJson);
        await using var client = Client(handler, cache, maximumBytes: 4096);

        var stale = await client.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);
        await handler.FirstStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var forced = await client.GetItemsAsync(GameMode.Regular, "en", force: true, TestContext.Current.CancellationToken);
        handler.ReleaseFirst();
        await client.DisposeAsync();

        Assert.True(stale.IsStale);
        Assert.Equal("Old", stale.Data.Items["item"].Name);
        Assert.False(forced.IsStale);
        Assert.Equal("Forced", forced.Data.Items["item"].Name);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(newJson, (await cache.GetAsync("regular/items", TestContext.Current.CancellationToken))!.BodyJson);
    }

    [Fact]
    public async Task CacheMissSupersededByForcedRefreshCannotOverwriteNormalizedPublication()
    {
        const string obsoleteJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Obsolete\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        const string forcedJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Forced\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var cache = new SqliteTarkovDevResponseCache(database.Factory);
        var handler = new NormalizedPublicationRaceHandler(obsoleteJson, forcedJson);
        await using var client = Client(handler, cache, maximumBytes: 4096);
        var operation = new TarkovDevDataRefreshOperation(
            client,
            new SqliteDataRefreshRepository(database.Factory),
            new SqliteSyncStateRepository(database.Factory));

        var obsoleteRun = operation.RefreshAsync(
            new(GameMode.Regular, "en", false),
            TestContext.Current.CancellationToken);
        await handler.ObsoleteItemsStarted.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var forcedRun = await operation.RefreshAsync(
            new(GameMode.Regular, "en", true),
            TestContext.Current.CancellationToken);
        Assert.All(forcedRun, endpoint => Assert.True(endpoint.Updated, $"{endpoint.Endpoint}: {endpoint.Error}"));
        Assert.Equal("Forced", await ScalarTextAsync(
            database.Factory,
            "SELECT name FROM items WHERE id = 'item';"));

        handler.ReleaseObsoleteItems();
        var completedObsoleteRun = await obsoleteRun;

        Assert.All(
            completedObsoleteRun,
            endpoint => Assert.StartsWith("Superseded:", endpoint.Error!, StringComparison.Ordinal));
        Assert.Equal("Forced", await ScalarTextAsync(
            database.Factory,
            "SELECT name FROM items WHERE id = 'item';"));
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(forcedJson)))
            .ToLowerInvariant();
        Assert.Equal(expectedHash, await ScalarTextAsync(
            database.Factory,
            """
            SELECT publication.content_sha256
            FROM dataset_heads AS head
            JOIN dataset_publications AS publication
              ON publication.publication_id = head.visible_publication_id
            WHERE head.source_key = 'items'
              AND head.game_mode = 'regular'
              AND head.language = 'en';
            """));
        Assert.Equal(forcedJson, (await cache.GetAsync(
            "regular/items",
            TestContext.Current.CancellationToken))!.BodyJson);
        Assert.Equal(2, handler.ItemRequestCount);
    }

    [Fact]
    public async Task ForegroundEpochIsCapturedAtRegistrationBeforeItsLazyStarts()
    {
        const string obsoleteJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Obsolete\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        const string forcedJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Forced\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        var cancellationToken = TestContext.Current.CancellationToken;
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseForeground = new ManualResetEventSlim();
        var handler = new SequenceHandler(attempt => new(HttpStatusCode.OK)
        {
            // The force is the first transfer allowed to start. The registered foreground Lazy
            // starts only after that force has published and retired its epoch.
            Content = new StringContent(attempt == 1 ? forcedJson : obsoleteJson),
        });
        var cache = new InMemoryTarkovDevResponseCache();
        await using var client = new TarkovDevJsonClient(
            new HttpClient(handler),
            cache,
            new DataTranslationService(),
            new()
            {
                BaseAddress = new("https://fixture.invalid/"),
                MaxAttempts = 1,
                InitialRetryDelay = TimeSpan.Zero,
                MaximumResponseBytes = 4096,
            },
            timeProvider: null,
            afterForegroundRefreshRegistered: cacheKey =>
            {
                if (cacheKey == "regular/items")
                {
                    registered.TrySetResult();
                    releaseForeground.Wait(cancellationToken);
                }
            });

        var foreground = Task.Run(
            () => client.GetItemsAsync(GameMode.Regular, "en", cancellationToken),
            cancellationToken);
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        TarkovDevResponse<TarkovDevItemsData> forced;
        try
        {
            forced = await client.GetItemsAsync(
                GameMode.Regular,
                "en",
                force: true,
                cancellationToken);
        }
        finally
        {
            releaseForeground.Set();
        }

        var resolvedForeground = await foreground.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal("Forced", forced.Data.Items["item"].Name);
        Assert.Equal("Forced", resolvedForeground.Data.Items["item"].Name);
        Assert.Equal(forcedJson, (await cache.GetAsync(
            "regular/items",
            cancellationToken))!.BodyJson);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task StaleReaderDuringForcedRefreshDoesNotStartACompetingPublisher()
    {
        var cache = new InMemoryTarkovDevResponseCache();
        const string oldJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Old\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        const string forcedJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Forced\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        await cache.PutAsync(new("regular/items", oldJson, DateTimeOffset.UtcNow.AddDays(-1), null, null),
            TestContext.Current.CancellationToken);
        var handler = new GateHandler(forcedJson);
        await using var client = Client(handler, cache, maximumBytes: 4096);

        var force = client.GetItemsAsync(
            GameMode.Regular,
            "en",
            force: true,
            TestContext.Current.CancellationToken);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var stale = await client.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);
        Assert.True(stale.IsStale);
        Assert.Equal("Old", stale.Data.Items["item"].Name);
        Assert.Equal(1, handler.RequestCount);

        handler.Release();
        Assert.Equal("Forced", (await force).Data.Items["item"].Name);
        await client.DisposeAsync();
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(forcedJson, (await cache.GetAsync(
            "regular/items",
            TestContext.Current.CancellationToken))!.BodyJson);
    }

    [Fact]
    public async Task DisposeCancelsAndDrainsForcedTransferBeforeReturning()
    {
        var cache = new InMemoryTarkovDevResponseCache();
        const string oldJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Old\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        const string newJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Late\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        await cache.PutAsync(new("regular/items", oldJson, DateTimeOffset.UtcNow, null, null),
            TestContext.Current.CancellationToken);
        var handler = new GateHandler(newJson);
        var client = Client(handler, cache, maximumBytes: 4096);

        var forced = client.GetItemsAsync(
            GameMode.Regular,
            "en",
            force: true,
            TestContext.Current.CancellationToken);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await client.DisposeAsync();
        handler.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => forced);
        Assert.Equal(oldJson, (await cache.GetAsync("regular/items", TestContext.Current.CancellationToken))!.BodyJson);
    }

    [Fact]
    public async Task NormalizationInvalidResponseCannotPoisonCacheBeforePersistence()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var cache = new SqliteTarkovDevResponseCache(database.Factory);
        const string goodJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Good\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        const string mismatchedJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"different\",\"name\":\"Poison\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        var handler = new SequenceHandler(attempt => new(HttpStatusCode.OK)
        {
            Content = new StringContent(attempt == 1 ? goodJson : mismatchedJson),
        });
        await using var client = Client(handler, cache, maximumBytes: 4096);
        var repository = new SqliteDataRefreshRepository(database.Factory);
        var good = await client.GetItemsAsync(GameMode.Regular, "en", true, TestContext.Current.CancellationToken);
        await repository.RefreshItemsAsync(good.Data, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var refused = await client.GetItemsAsync(GameMode.Regular, "en", true, TestContext.Current.CancellationToken);

        Assert.True(refused.IsStale);
        Assert.Equal("Good", refused.Data.Items["item"].Name);
        Assert.Equal(goodJson, (await cache.GetAsync("regular/items", TestContext.Current.CancellationToken))!.BodyJson);
        Assert.Equal("Good", await ScalarTextAsync(database.Factory,
            "SELECT name FROM items WHERE id = 'item';"));
    }

    [Theory]
    [InlineData("{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Item\",\"width\":1,\"height\":1,\"types\":null}},\"itemCategories\":{}}}")]
    [InlineData("{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Item\",\"width\":1,\"height\":1,\"types\":[null]}},\"itemCategories\":{}}}")]
    [InlineData("{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Item\",\"width\":1,\"height\":1,\"categories\":[null]}},\"itemCategories\":{}}}")]
    [InlineData("{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Item\",\"width\":1,\"height\":1,\"sellToTrader\":[{\"trader\":\"trader\",\"currency\":\"RUB\",\"price\":1},{\"trader\":\"trader\",\"currency\":\"RUB\",\"price\":2}]}},\"itemCategories\":{}}}")]
    [InlineData("{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Item\",\"width\":1,\"height\":1,\"weight\":1000001}},\"itemCategories\":{}}}")]
    [InlineData("{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Item\",\"width\":1,\"height\":1,\"properties\":{\"weight\":1000001}}},\"itemCategories\":{}}}")]
    public async Task PersistenceInvalidItemCollectionsAreRefusedBeforeCachePublication(string json)
    {
        var cache = new InMemoryTarkovDevResponseCache();
        await using var client = Client(
            new StaticHandler(new(HttpStatusCode.OK) { Content = new StringContent(json) }),
            cache,
            maximumBytes: 4096);

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => client.GetItemsAsync(
            GameMode.Regular,
            "en",
            TestContext.Current.CancellationToken));

        Assert.Null(await cache.GetAsync("regular/items", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ObjectiveIdsThatCollideAcrossTasksAreRefusedBeforeCachePublication()
    {
        const string json = "{\"data\":{\"tasks\":{\"a\":{\"id\":\"a\",\"name\":\"A\",\"objectives\":[{\"id\":\"shared\",\"type\":\"giveItem\"}]},\"b\":{\"id\":\"b\",\"name\":\"B\",\"objectives\":[{\"id\":\"shared\",\"type\":\"giveItem\"}]} }}}";
        var cache = new InMemoryTarkovDevResponseCache();
        await using var client = Client(
            new StaticHandler(new(HttpStatusCode.OK) { Content = new StringContent(json) }),
            cache,
            maximumBytes: 4096);

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => client.GetTasksAsync(
            GameMode.Regular,
            "en",
            TestContext.Current.CancellationToken));

        Assert.Null(await cache.GetAsync("regular/tasks", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{\"data\":{\"station\":{\"id\":\"station\",\"name\":\"Station\",\"levels\":[{\"id\":\"station-1\",\"level\":1,\"traderRequirements\":[{\"trader\":\"trader\"}]}]}}}")]
    [InlineData("{\"data\":{\"station\":{\"id\":\"station\",\"name\":\"Station\",\"levels\":[{\"id\":\"station-1\",\"level\":1,\"traderRequirements\":[{\"trader\":\"trader\",\"level\":2,\"value\":3}]}]}}}")]
    public async Task AmbiguousOrMissingHideoutTraderLevelsAreRefusedBeforeCachePublication(string json)
    {
        var cache = new InMemoryTarkovDevResponseCache();
        await using var client = Client(
            new StaticHandler(new(HttpStatusCode.OK) { Content = new StringContent(json) }),
            cache,
            maximumBytes: 4096);

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => client.GetHideoutAsync(
            GameMode.Regular,
            "en",
            TestContext.Current.CancellationToken));

        Assert.Null(await cache.GetAsync("regular/hideout", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownNestedMapCoordinatesSurviveNormalizedSourceRoundTrip()
    {
        const string json = "{\"data\":{\"maps\":{\"map\":{\"id\":\"map\",\"name\":\"Map\",\"extracts\":[{\"id\":\"extract\",\"name\":\"Extract\",\"position\":{\"x\":1,\"y\":2,\"futureAxis\":7}}]}},\"lootContainers\":{}}}";
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var client = Client(
            new StaticHandler(new(HttpStatusCode.OK) { Content = new StringContent(json) }),
            new InMemoryTarkovDevResponseCache(),
            maximumBytes: 4096);
        var response = await client.GetMapsAsync(
            GameMode.Regular,
            "en",
            TestContext.Current.CancellationToken);

        await new SqliteDataRefreshRepository(database.Factory).RefreshMapsAsync(
            response.Data,
            TestContext.Current.CancellationToken);

        var source = await ScalarTextAsync(database.Factory, "SELECT source_json FROM maps WHERE id = 'map';");
        Assert.Contains("futureAxis", source, StringComparison.Ordinal);
        Assert.Contains(":7", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingPriceAndWeightRemainNullThroughProductionFactsAndLoadoutTotals()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var items = new TarkovDevItemsData
        {
            Items = new Dictionary<string, TarkovDevItem>(StringComparer.Ordinal)
            {
                ["backpack"] = new()
                {
                    Id = "backpack",
                    Name = "Unpriced backpack",
                    Width = 2,
                    Height = 2,
                    Types = ["backpack"],
                },
                ["key"] = new()
                {
                    Id = "key",
                    Name = "Unpriced key",
                    Width = 1,
                    Height = 1,
                    Types = ["keys"],
                },
            },
        };
        var refresh = new SqliteDataRefreshRepository(database.Factory);
        await refresh.RefreshItemsAsync(
            items,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
        var catalog = new SqliteItemFactCatalog(database.Factory);

        var facts = await catalog.GetLoadoutFactsAsync(TestContext.Current.CancellationToken);
        var backpack = facts.Single(value => value.ItemId == "backpack");
        Assert.Null(backpack.ApproximateCostRoubles);
        Assert.Null(backpack.WeightKg);
        Assert.Null((await catalog.GetKeyFactsAsync(TestContext.Current.CancellationToken))
            .Single(value => value.ItemId == "key").AcquisitionCostRoubles);

        Assert.Equal(PriceHistoryRefreshOutcome.Updated, await refresh.RefreshPriceHistoryAsync(
            "backpack",
            [new TarkovDevPricePoint { Timestamp = null, Price = 12_345, PriceMin = 10_000 }],
            TestContext.Current.CancellationToken));
        var unresolved = Assert.Single(await new SqlitePriceHistoryRepository(database.Factory)
            .GetUnresolvedAsync("backpack", TestContext.Current.CancellationToken));
        Assert.Equal(0, unresolved.SourceOrdinal);
        Assert.Equal(12_345, unresolved.FleaPriceRoubles);
        Assert.Contains("priceMin", unresolved.RawJson, StringComparison.Ordinal);

        var evaluation = await new LoadoutIntelligenceService(facts, new AmmoIntelligenceService([]))
            .EvaluateAsync(
                new LoadoutSelection(
                    null,
                    null,
                    [],
                    null,
                    [],
                    null,
                    null,
                    null,
                    "backpack",
                    []),
                null,
                TestContext.Current.CancellationToken);
        Assert.Null(evaluation.ApproximateCostRoubles);
        Assert.Null(evaluation.ApproximateWeightKg);
    }

    [Fact]
    public async Task MissingCatalogItemReturnsClassifiedPriceOutcomeWithoutMutation()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var repository = new SqliteDataRefreshRepository(database.Factory);

        var outcome = await repository.RefreshPriceHistoryAsync(
            "missing-item",
            [new TarkovDevPricePoint { Timestamp = 1_700_000_000_000, Price = 42_000 }],
            TestContext.Current.CancellationToken);

        Assert.Equal(PriceHistoryRefreshOutcome.ItemNotInCatalog, outcome);
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM price_history;"));
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM price_history_unresolved_time;"));
    }

    [Fact]
    public async Task CatalogDeleteThatWinsTheWriterRaceReturnsItemNotInCatalog()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var repository = new SqliteDataRefreshRepository(database.Factory);
        await repository.RefreshItemsAsync(
            new TarkovDevItemsData
            {
                Items = new Dictionary<string, TarkovDevItem>(StringComparer.Ordinal)
                {
                    ["contested"] = new()
                    {
                        Id = "contested",
                        Name = "Contested item",
                        Width = 1,
                        Height = 1,
                    },
                },
            },
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);

        await using var deleteConnection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var deleteTransaction = (SqliteTransaction)await deleteConnection
            .BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var delete = deleteConnection.CreateCommand())
        {
            delete.Transaction = deleteTransaction;
            delete.CommandText = "DELETE FROM items WHERE id = 'contested';";
            Assert.Equal(1, await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        var refresh = Task.Run(
            () => repository.RefreshPriceHistoryAsync(
                "contested",
                [new TarkovDevPricePoint { Timestamp = 1_700_000_000_000, Price = 42_000 }],
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        Assert.False(refresh.IsCompleted);

        await deleteTransaction.CommitAsync(TestContext.Current.CancellationToken);
        var outcome = await refresh.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(PriceHistoryRefreshOutcome.ItemNotInCatalog, outcome);
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM price_history;"));
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM price_history_unresolved_time;"));
    }

    [Fact]
    public async Task TypedInvalidFreshCacheIsQuarantinedBeforeShortcutRead()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var cache = new SqliteTarkovDevResponseCache(database.Factory);
        const string poisonedJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"different\",\"name\":\"Poison\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        const string repairedJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Repaired\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        await cache.PutAsync(new("regular/items", poisonedJson, DateTimeOffset.UtcNow, null, null),
            TestContext.Current.CancellationToken);
        await using var client = Client(
            new StaticHandler(new(HttpStatusCode.OK) { Content = new StringContent(repairedJson) }),
            cache,
            maximumBytes: 4096);

        var response = await client.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);

        Assert.Equal("Repaired", response.Data.Items["item"].Name);
        Assert.Equal(repairedJson, (await cache.GetAsync("regular/items", TestContext.Current.CancellationToken))!.BodyJson);
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM local_json_recovery WHERE document_key = 'cache:regular/items' AND diagnostic_code = 'cache-dataset-invalid';"));
    }

    [Fact]
    public async Task TranslationThatBreaksTypedIdentityIsQuarantinedBeforePersistence()
    {
        var cache = new InMemoryTarkovDevResponseCache();
        const string source = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Item\",\"width\":1,\"height\":1}},\"itemCategories\":{}},\"translations\":[\"$.data.items.*.id\"]}";
        const string hostileTranslation = "{\"data\":{\"item\":\"different\"}}";
        var handler = new SequenceHandler(attempt => new(HttpStatusCode.OK)
        {
            Content = new StringContent(attempt == 1 ? source : hostileTranslation),
        });
        await using var client = Client(handler, cache, maximumBytes: 4096);

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => client.GetItemsAsync(
            GameMode.Regular,
            "en",
            TestContext.Current.CancellationToken));

        Assert.NotNull(await cache.GetAsync("regular/items", TestContext.Current.CancellationToken));
        Assert.Null(await cache.GetAsync("regular/items_en", TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task TranslationAmplificationIsRefusedBeforeReplacingNormalizedItems()
    {
        const int maximumBytes = 1024;
        var itemJson = string.Join(",", Enumerable.Range(0, 8).Select(index =>
            $"\"item-{index}\":{{\"id\":\"item-{index}\",\"name\":\"KEY\",\"width\":1,\"height\":1}}"));
        var source = $"{{\"data\":{{\"items\":{{{itemJson}}},\"itemCategories\":{{}}}},\"translations\":[\"$.data.items.*.name\"]}}";
        var translation = "{\"data\":{\"KEY\":\"" + new string('x', 900) + "\"}}";
        Assert.True(Encoding.UTF8.GetByteCount(source) <= maximumBytes);
        Assert.True(Encoding.UTF8.GetByteCount(translation) <= maximumBytes);

        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var repository = new SqliteDataRefreshRepository(database.Factory);
        await repository.RefreshItemsAsync(
            new TarkovDevItemsData
            {
                Items = new Dictionary<string, TarkovDevItem>(StringComparer.Ordinal)
                {
                    ["last-known-good"] = new()
                    {
                        Id = "last-known-good",
                        Name = "Last known good",
                        Width = 1,
                        Height = 1,
                    },
                },
            },
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
        var cache = new SqliteTarkovDevResponseCache(database.Factory);
        await using var client = Client(
            new TranslationPublicationHandler(source, translation),
            cache,
            maximumBytes);

        var results = await new TarkovDevDataRefreshOperation(
                client,
                repository,
                new SqliteSyncStateRepository(database.Factory))
            .RefreshAsync(
                new(GameMode.Regular, "en", true),
                TestContext.Current.CancellationToken);

        var items = Assert.Single(results, result => result.Endpoint == "items");
        Assert.False(items.Updated);
        Assert.False(items.UsedStaleCache);
        Assert.Contains("response budget", items.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM items;"));
        Assert.Equal("Last known good", await ScalarTextAsync(
            database.Factory,
            "SELECT name FROM items WHERE id = 'last-known-good';"));
        Assert.Null(await cache.GetAsync("regular/items_en", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnsupportedBaseTranslationDirectiveIsRefusedBeforeBaseCachePublication()
    {
        const string source = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Item\",\"width\":1,\"height\":1}},\"itemCategories\":{}},\"translations\":[\"unsupported.path\"]}";
        var cache = new InMemoryTarkovDevResponseCache();
        var handler = new SequenceHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(source),
        });
        await using var client = Client(handler, cache, maximumBytes: 4096);

        var exception = await Assert.ThrowsAnyAsync<InvalidDataException>(() => client.GetItemsAsync(
            GameMode.Regular,
            "en",
            TestContext.Current.CancellationToken));

        Assert.Contains("Unsupported translation path", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await cache.GetAsync("regular/items", TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task TranslatedRowsAndPublicationHashUseTheSameMergedDocument()
    {
        const string source = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"ITEM_NAME\",\"width\":1,\"height\":1}},\"itemCategories\":{}},\"translations\":[\"$.data.items.*.name\"]}";
        const string translation = "{\"data\":{\"ITEM_NAME\":\"Localized item\"}}";
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var client = Client(
            new TranslationPublicationHandler(source, translation),
            new SqliteTarkovDevResponseCache(database.Factory),
            maximumBytes: 4096);

        var results = await new TarkovDevDataRefreshOperation(
                client,
                new SqliteDataRefreshRepository(database.Factory),
                new SqliteSyncStateRepository(database.Factory))
            .RefreshAsync(
                new(GameMode.Regular, "en", true),
                TestContext.Current.CancellationToken);

        Assert.All(results, endpoint => Assert.True(endpoint.Updated, $"{endpoint.Endpoint}: {endpoint.Error}"));
        Assert.Equal("Localized item", await ScalarTextAsync(
            database.Factory,
            "SELECT name FROM items WHERE id = 'item';"));
        var mergedJson = new DataTranslationService().Apply(source, translation);
        var mergedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mergedJson)))
            .ToLowerInvariant();
        var rawHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))
            .ToLowerInvariant();
        var publicationHash = await ScalarTextAsync(
            database.Factory,
            """
            SELECT publication.content_sha256
            FROM dataset_heads AS head
            JOIN dataset_publications AS publication
              ON publication.publication_id = head.visible_publication_id
            WHERE head.source_key = 'items'
              AND head.game_mode = 'regular'
              AND head.language = 'en';
            """);
        Assert.Equal(mergedHash, publicationHash);
        Assert.NotEqual(rawHash, publicationHash);
    }

    [Fact]
    public async Task HostileTranslationRefreshRetainsTheLastKnownGoodTranslation()
    {
        var cache = new InMemoryTarkovDevResponseCache();
        const string source = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"ITEM_NAME\",\"width\":1,\"height\":1}},\"itemCategories\":{}},\"translations\":[\"$.data.items.*.name\",\"$.data.items.*.id\"]}";
        const string goodTranslation = "{\"data\":{\"ITEM_NAME\":\"Localized\",\"item\":\"item\"}}";
        const string hostileTranslation = "{\"data\":{\"ITEM_NAME\":\"Poisoned\",\"item\":\"different\"}}";
        await cache.PutAsync(
            new("regular/items", source, DateTimeOffset.UtcNow.AddHours(-1), null, null),
            TestContext.Current.CancellationToken);
        await cache.PutAsync(
            new("regular/items_en", goodTranslation, DateTimeOffset.UtcNow.AddHours(-1), null, null),
            TestContext.Current.CancellationToken);
        var handler = new SequenceHandler(attempt => new(HttpStatusCode.OK)
        {
            Content = new StringContent(attempt == 1 ? source : hostileTranslation),
        });
        await using var client = Client(handler, cache, maximumBytes: 4096);

        var response = await client.GetItemsAsync(
            GameMode.Regular,
            "en",
            force: true,
            TestContext.Current.CancellationToken);

        Assert.True(response.IsStale);
        Assert.Equal("Localized", response.Data.Items["item"].Name);
        Assert.Equal(goodTranslation, (await cache.GetAsync(
            "regular/items_en",
            TestContext.Current.CancellationToken))!.BodyJson);
        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [MemberData(nameof(NestedShrinkCases))]
    public async Task NestedDatasetShrinkIsRefusedAgainstLastKnownGood(
        string endpoint,
        string dimension,
        string lastKnownGood,
        string candidate)
    {
        var cache = new InMemoryTarkovDevResponseCache();
        var cacheKey = $"regular/{endpoint}";
        await cache.PutAsync(
            new(cacheKey, lastKnownGood, DateTimeOffset.UtcNow.AddDays(-1), null, null),
            TestContext.Current.CancellationToken);
        await using var client = Client(
            new StaticHandler(new(HttpStatusCode.OK) { Content = new StringContent(candidate) }),
            cache,
            maximumBytes: 16 * 1024);

        var outcome = await FetchForcedAsync(client, endpoint);

        Assert.True(outcome.IsStale);
        Assert.NotNull(outcome.RefusalReason);
        Assert.Contains(dimension, outcome.RefusalReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(lastKnownGood, (await cache.GetAsync(
            cacheKey,
            TestContext.Current.CancellationToken))!.BodyJson);
    }

    [Fact]
    public async Task OneMapsExtractsCannotDisappearBehindAStableAggregateCount()
    {
        const string lastKnownGood = """
            {"data":{"maps":{"lighthouse":{"id":"lighthouse","name":"Lighthouse","extracts":[{"id":"stage"},{"id":"road"},{"id":"train"}]},"factory":{"id":"factory","name":"Factory","extracts":[{"id":"gate-three"}]}},"lootContainers":{}}}
            """;
        const string candidate = """
            {"data":{"maps":{"lighthouse":{"id":"lighthouse","name":"Lighthouse","extracts":[]},"factory":{"id":"factory","name":"Factory","extracts":[{"id":"gate-zero"},{"id":"gate-one"},{"id":"gate-two"},{"id":"gate-three"}]}},"lootContainers":{}}}
            """;
        var cache = new InMemoryTarkovDevResponseCache();
        await cache.PutAsync(
            new("regular/maps", lastKnownGood, DateTimeOffset.UtcNow.AddDays(-1), null, null),
            TestContext.Current.CancellationToken);
        await using var client = Client(
            new StaticHandler(new(HttpStatusCode.OK) { Content = new StringContent(candidate) }),
            cache,
            maximumBytes: 16 * 1024);

        var response = await client.GetMapsAsync(
            GameMode.Regular,
            "en",
            force: true,
            TestContext.Current.CancellationToken);

        Assert.True(response.IsStale);
        Assert.Contains("lighthouse", response.RefusalReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("extracts", response.RefusalReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(lastKnownGood, (await cache.GetAsync(
            "regular/maps",
            TestContext.Current.CancellationToken))!.BodyJson);
    }

    [Fact]
    public async Task OneMapsLocksCannotDisappearBehindAStableAggregateCount()
    {
        const string lastKnownGood = """
            {"data":{"maps":{"lighthouse":{"id":"lighthouse","name":"Lighthouse","extracts":[{"id":"stage"}],"locks":[{"id":"usec-one"},{"id":"usec-two"},{"id":"usec-three"}]},"factory":{"id":"factory","name":"Factory","extracts":[{"id":"gate-three"}],"locks":[{"id":"factory-key"}]}},"lootContainers":{}}}
            """;
        const string candidate = """
            {"data":{"maps":{"lighthouse":{"id":"lighthouse","name":"Lighthouse","extracts":[{"id":"stage"}],"locks":[]},"factory":{"id":"factory","name":"Factory","extracts":[{"id":"gate-three"}],"locks":[{"id":"factory-zero"},{"id":"factory-one"},{"id":"factory-two"},{"id":"factory-three"}]}},"lootContainers":{}}}
            """;
        var cache = new InMemoryTarkovDevResponseCache();
        await cache.PutAsync(
            new("regular/maps", lastKnownGood, DateTimeOffset.UtcNow.AddDays(-1), null, null),
            TestContext.Current.CancellationToken);
        await using var client = Client(
            new StaticHandler(new(HttpStatusCode.OK) { Content = new StringContent(candidate) }),
            cache,
            maximumBytes: 16 * 1024);

        var response = await client.GetMapsAsync(
            GameMode.Regular,
            "en",
            force: true,
            TestContext.Current.CancellationToken);

        Assert.True(response.IsStale);
        Assert.Contains("lighthouse", response.RefusalReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("locks", response.RefusalReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(lastKnownGood, (await cache.GetAsync(
            "regular/maps",
            TestContext.Current.CancellationToken))!.BodyJson);
    }

    [Fact]
    public async Task OneMapsLooseLootCannotDisappearBehindAStableAggregateCount()
    {
        const string lastKnownGood = """
            {"data":{"maps":{"lighthouse":{"id":"lighthouse","name":"Lighthouse","extracts":[{"id":"stage"}],"lootLoose":[{"items":["a"]},{"items":["b"]},{"items":["c"]}]} ,"factory":{"id":"factory","name":"Factory","extracts":[{"id":"gate-three"}],"lootLoose":[{"items":["d"]}]}},"lootContainers":{}}}
            """;
        const string candidate = """
            {"data":{"maps":{"lighthouse":{"id":"lighthouse","name":"Lighthouse","extracts":[{"id":"stage"}],"lootLoose":[]} ,"factory":{"id":"factory","name":"Factory","extracts":[{"id":"gate-three"}],"lootLoose":[{"items":["a"]},{"items":["b"]},{"items":["c"]},{"items":["d"]}]}},"lootContainers":{}}}
            """;
        var cache = new InMemoryTarkovDevResponseCache();
        await cache.PutAsync(
            new("regular/maps", lastKnownGood, DateTimeOffset.UtcNow.AddDays(-1), null, null),
            TestContext.Current.CancellationToken);
        await using var client = Client(
            new StaticHandler(new(HttpStatusCode.OK) { Content = new StringContent(candidate) }),
            cache,
            maximumBytes: 16 * 1024);

        var response = await client.GetMapsAsync(
            GameMode.Regular,
            "en",
            force: true,
            TestContext.Current.CancellationToken);

        Assert.True(response.IsStale);
        Assert.Contains("lighthouse", response.RefusalReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("loose-loot positions", response.RefusalReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(lastKnownGood, (await cache.GetAsync(
            "regular/maps",
            TestContext.Current.CancellationToken))!.BodyJson);
    }

    [Fact]
    public async Task QuarantineFenceCannotDeleteAConcurrentGoodReplacement()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var cache = new SqliteTarkovDevResponseCache(database.Factory);
        const string oldBody = "{\"data\":{\"value\":\"old\"}}";
        const string replacement = "{\"data\":{\"value\":\"replacement\"}}";
        await cache.PutAsync(
            new("regular/items", oldBody, DateTimeOffset.UtcNow.AddMinutes(-1), null, null),
            TestContext.Current.CancellationToken);
        var observedHash = (await cache.GetAsync(
            "regular/items",
            TestContext.Current.CancellationToken))!.ContentSha256!;

        await cache.PutAsync(
            new("regular/items", replacement, DateTimeOffset.UtcNow, null, null),
            TestContext.Current.CancellationToken);
        var removed = await cache.QuarantineAsync(
            "regular/items",
            observedHash,
            "cache-dataset-invalid",
            TestContext.Current.CancellationToken);

        Assert.False(removed);
        Assert.Equal(replacement, (await cache.GetAsync(
            "regular/items",
            TestContext.Current.CancellationToken))!.BodyJson);
    }

    [Fact]
    public async Task DeepAndEmptyDatasetsAreRefusedBeforeCacheReplacement()
    {
        var cache = new InMemoryTarkovDevResponseCache();
        var empty = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":{\"items\":{},\"itemCategories\":{}}}")
        };
        var client = Client(new StaticHandler(empty), cache, maximumBytes: 4096);
        await Assert.ThrowsAnyAsync<InvalidDataException>(() =>
            client.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken));
        Assert.Equal(0, (await cache.InspectAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).EntryCount);

        var deep = "{\"data\":" + string.Concat(Enumerable.Repeat("{\"x\":", 40)) + "1" + new string('}', 40) + "}";
        var deepClient = Client(new StaticHandler(new(HttpStatusCode.OK) { Content = new StringContent(deep) }), cache, maximumBytes: 4096, depth: 8);
        await Assert.ThrowsAnyAsync<InvalidDataException>(() =>
            deepClient.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken));

        var nestedUnknown = string.Concat(Enumerable.Repeat("{\"child\":", 40)) + "null" + new string('}', 40);
        var allowed = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Item\",\"width\":1,\"height\":1}},\"itemCategories\":{}},\"future\":" + nestedUnknown + "}";
        var allowedClient = Client(
            new StaticHandler(new(HttpStatusCode.OK) { Content = new StringContent(allowed) }),
            new InMemoryTarkovDevResponseCache(),
            maximumBytes: 4096,
            depth: 64);
        Assert.Single((await allowedClient.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken)).Data.Items);
    }

    [Fact]
    public async Task OversizedNormalizedTextIsRefusedBeforeReplacingLastKnownGoodCache()
    {
        const string lastKnownGood = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Last known good\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        var oversizedName = new string('\u00e9', 2049);
        var candidate = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"" + oversizedName +
            "\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        var cache = new InMemoryTarkovDevResponseCache();
        await cache.PutAsync(
            new("regular/items", lastKnownGood, DateTimeOffset.UtcNow.AddDays(-1), null, null),
            TestContext.Current.CancellationToken);
        await using var client = Client(
            new StaticHandler(new(HttpStatusCode.OK) { Content = new StringContent(candidate) }),
            cache,
            maximumBytes: 16 * 1024);

        var response = await client.GetItemsAsync(
            GameMode.Regular,
            "en",
            force: true,
            TestContext.Current.CancellationToken);

        Assert.True(response.IsStale);
        Assert.Contains("UTF-8 budget", response.RefusalReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(lastKnownGood, (await cache.GetAsync(
            "regular/items",
            TestContext.Current.CancellationToken))!.BodyJson);
    }

    [Fact]
    public async Task OfflineStaleReadReconnectsWithBoundedProbeAttempts()
    {
        var now = new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var cache = new InMemoryTarkovDevResponseCache(timeProvider: clock);
        const string oldJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Old\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        const string newJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"New\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        await cache.PutAsync(new("regular/items", oldJson, now.AddHours(-10), null, null), TestContext.Current.CancellationToken);
        var probes = 0;
        var handler = new StaticHandler(new(HttpStatusCode.OK) { Content = new StringContent(newJson) });
        var client = new TarkovDevJsonClient(new HttpClient(handler), cache, new DataTranslationService(), new()
        {
            BaseAddress = new("https://fixture.invalid/"), MaxAttempts = 1, InitialRetryDelay = TimeSpan.Zero,
            OfflineReconnectDelay = TimeSpan.Zero, MaximumOfflineReconnectAttempts = 3,
            OfflineProbe = () => Interlocked.Increment(ref probes) <= 2,
        }, clock);

        var stale = await client.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);
        Assert.True(stale.IsStale);
        Assert.Equal("Old", stale.Data.Items["item"].Name);
        for (var attempt = 0; attempt < 100 && (await cache.GetAsync("regular/items", TestContext.Current.CancellationToken))!.BodyJson == oldJson; attempt++)
            await Task.Yield();
        Assert.Equal(newJson, (await cache.GetAsync("regular/items", TestContext.Current.CancellationToken))!.BodyJson);
        Assert.InRange(probes, 3, 5);
    }

    [Fact]
    public async Task OfflineEnvironmentTransitionIsObservedWithoutRestartingTheClient()
    {
        const string variable = "TARKOV_COMPANION_OFFLINE";
        var original = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "1");
            var now = new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero);
            var cache = new InMemoryTarkovDevResponseCache();
            const string oldJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Old\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
            const string newJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Reconnected\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
            await cache.PutAsync(new("regular/items", oldJson, now.AddHours(-10), null, null),
                TestContext.Current.CancellationToken);
            await using var client = new TarkovDevJsonClient(
                new HttpClient(new StaticHandler(new(HttpStatusCode.OK) { Content = new StringContent(newJson) })),
                cache,
                new DataTranslationService(),
                new()
                {
                    BaseAddress = new("https://fixture.invalid/"),
                    MaxAttempts = 1,
                    InitialRetryDelay = TimeSpan.Zero,
                    OfflineReconnectDelay = TimeSpan.FromMilliseconds(20),
                    MaximumOfflineReconnectAttempts = 5,
                });

            var stale = await client.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);
            Assert.True(stale.IsStale);
            Environment.SetEnvironmentVariable(variable, "0");

            for (var attempt = 0; attempt < 100 &&
                 (await cache.GetAsync("regular/items", TestContext.Current.CancellationToken))!.BodyJson == oldJson;
                 attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
            }

            Assert.Equal(newJson, (await cache.GetAsync(
                "regular/items",
                TestContext.Current.CancellationToken))!.BodyJson);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    [Fact]
    public async Task ProductionCompositionKeepsTheDataHandlerAvailableForOfflineReconnect()
    {
        const string variable = AppComposition.OfflineEnvironmentVariable;
        var original = Environment.GetEnvironmentVariable(variable);
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-v2-offline-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable(variable, "1");
            const string oldJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Old\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
            const string reconnectedJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Reconnected\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
            var handler = new SequenceHandler(_ => new(HttpStatusCode.OK)
            {
                Content = new StringContent(reconnectedJson),
            });
            await using var services = AppComposition.Build(
                new AppCommandLine(false, false, false, false, null, null, null),
                new(DataRoot: root, HttpMessageHandler: handler));
            await services.GetRequiredService<IRuntimeDataStore>()
                .InitializeAsync(TestContext.Current.CancellationToken);
            var cache = services.GetRequiredService<ITarkovDevResponseCache>();
            await cache.PutAsync(
                new("regular/items", oldJson, DateTimeOffset.UtcNow.AddDays(-1), null, null),
                TestContext.Current.CancellationToken);
            var client = services.GetRequiredService<TarkovDevJsonClient>();

            var stale = await client.GetItemsAsync(
                GameMode.Regular,
                "en",
                TestContext.Current.CancellationToken);
            Assert.True(stale.IsStale);
            Assert.Equal(0, handler.RequestCount);

            Environment.SetEnvironmentVariable(variable, "0");
            for (var attempt = 0; attempt < 200 &&
                 (await cache.GetAsync("regular/items", TestContext.Current.CancellationToken))!.BodyJson == oldJson;
                 attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
            }

            Assert.Equal(reconnectedJson, (await cache.GetAsync(
                "regular/items",
                TestContext.Current.CancellationToken))!.BodyJson);
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
            ScratchDirectory.Remove(root);
        }
    }

    private static TarkovDevJsonClient Client(
        HttpMessageHandler handler,
        ITarkovDevResponseCache cache,
        long maximumBytes,
        int depth = 32,
        int maxAttempts = 1,
        TimeSpan? initialRetryDelay = null,
        TimeProvider? timeProvider = null,
        Uri? mirrorAddress = null) =>
        new(new HttpClient(handler), cache, new DataTranslationService(), new()
        {
            BaseAddress = new("https://fixture.invalid/"),
            MirrorAddress = mirrorAddress,
            MaxAttempts = maxAttempts,
            InitialRetryDelay = initialRetryDelay ?? TimeSpan.Zero,
            MaximumResponseBytes = maximumBytes,
            MaximumJsonDepth = depth,
        }, timeProvider);

    private static async Task<ResponseOutcome> FetchForcedAsync(
        TarkovDevJsonClient client,
        string endpoint)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        return endpoint switch
        {
            "items" => Outcome(await client.GetItemsAsync(
                GameMode.Regular, "en", true, cancellationToken)),
            "maps" => Outcome(await client.GetMapsAsync(
                GameMode.Regular, "en", true, cancellationToken)),
            "tasks" => Outcome(await client.GetTasksAsync(
                GameMode.Regular, "en", true, cancellationToken)),
            "hideout" => Outcome(await client.GetHideoutAsync(
                GameMode.Regular, "en", true, cancellationToken)),
            "traders" => Outcome(await client.GetTradersAsync(
                GameMode.Regular, "en", true, cancellationToken)),
            "crafts" => Outcome(await client.GetCraftsAsync(
                GameMode.Regular, true, cancellationToken)),
            "barters" => Outcome(await client.GetBartersAsync(
                GameMode.Regular, true, cancellationToken)),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null),
        };

        static ResponseOutcome Outcome<T>(TarkovDevResponse<T> response) =>
            new(response.IsStale, response.RefusalReason);
    }

    private static async Task<string> ScalarTextAsync(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static long DatabaseFootprint(string databasePath) =>
        new[] { databasePath, databasePath + "-wal", databasePath + "-shm" }
            .Where(File.Exists)
            .Sum(path => new FileInfo(path).Length);

    private static string BuildLargeItemEnvelope(int itemCount)
    {
        var json = new StringBuilder(itemCount * 80);
        json.Append("{\"data\":{\"items\":{");
        for (var index = 0; index < itemCount; index++)
        {
            if (index > 0) json.Append(',');
            var ordinal = index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
            json.Append("\"item-").Append(ordinal).Append("\":{\"id\":\"item-").Append(ordinal)
                .Append("\",\"name\":\"Item ").Append(ordinal).Append("\",\"width\":1,\"height\":1}");
        }

        return json.Append("},\"itemCategories\":{}}}").ToString();
    }

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private sealed class EndpointOverrideFixtureHandler(string path, string body) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _fixtures = new(new FixtureApiHandler());

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            request.RequestUri?.AbsolutePath.Trim('/') == path
                ? Task.FromResult(FixtureApiHandler.Json(body))
                : _fixtures.SendAsync(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fixtures.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TranslationPublicationHandler(
        string source,
        string translation) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _fixtures = new(new FixtureApiHandler());

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            request.RequestUri?.AbsolutePath.Trim('/') switch
            {
                "regular/items" => Task.FromResult(FixtureApiHandler.Json(source)),
                "regular/items_en" => Task.FromResult(FixtureApiHandler.Json(translation)),
                _ => _fixtures.SendAsync(request, cancellationToken),
            };

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fixtures.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class PaddedFixtureApiHandler : HttpMessageHandler
    {
        private static readonly IReadOnlyDictionary<string, int> TargetBytes =
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["regular/items"] = 6 * 1024 * 1024,
                ["regular/maps"] = 2_300 * 1024,
                ["regular/tasks"] = 2 * 1024 * 1024,
                ["regular/hideout"] = 768 * 1024,
                ["regular/traders"] = 512 * 1024,
                ["regular/crafts"] = 768 * 1024,
                ["regular/barters"] = 768 * 1024,
            };

        private readonly HttpMessageInvoker _fixtures = new(new FixtureApiHandler());

        public static long MinimumPrimaryPayloadBytes =>
            TargetBytes.Values.Sum(value => (long)value) - (256L * 1024);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var fixture = await _fixtures.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await fixture.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var path = request.RequestUri?.AbsolutePath.Trim('/') ?? string.Empty;
            if (TargetBytes.TryGetValue(path, out var targetBytes))
            {
                var document = JsonNode.Parse(body)?.AsObject()
                    ?? throw new InvalidDataException($"Fixture '{path}' is not a JSON object.");
                var paddingLength = Math.Max(0, targetBytes - Encoding.UTF8.GetByteCount(body) - 64);
                document["fixturePadding"] = DeterministicPadding(path, paddingLength);
                body = document.ToJsonString();
            }

            return FixtureApiHandler.Json(body);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _fixtures.Dispose();
            base.Dispose(disposing);
        }

        private static string DeterministicPadding(string key, int length)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
            var state = key.Aggregate(2166136261u, static (current, value) => (current ^ value) * 16777619u);
            var characters = new char[length];
            for (var index = 0; index < characters.Length; index++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                characters[index] = alphabet[(int)(state & 63)];
            }

            return new(characters);
        }
    }

    private sealed class SequenceHandler(Func<int, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(Interlocked.Increment(ref _requestCount)));
    }

    private sealed class GateHandler(string body) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        public Task Started => _started.Task;
        public int RequestCount => Volatile.Read(ref _requestCount);
        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    private sealed class OrderedGateHandler(string firstBody, string laterBody) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        public Task FirstStarted => _firstStarted.Task;
        public int RequestCount => Volatile.Read(ref _requestCount);
        public void ReleaseFirst() => _releaseFirst.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var ordinal = Interlocked.Increment(ref _requestCount);
            if (ordinal == 1)
            {
                _firstStarted.TrySetResult();
                await _releaseFirst.Task.WaitAsync(cancellationToken);
            }

            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(ordinal == 1 ? firstBody : laterBody),
            };
        }
    }

    private sealed class NormalizedPublicationRaceHandler(
        string obsoleteItemsBody,
        string forcedItemsBody) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _fixtures = new(new FixtureApiHandler());
        private readonly TaskCompletionSource _obsoleteItemsStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseObsoleteItems =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _itemRequestCount;

        public Task ObsoleteItemsStarted => _obsoleteItemsStarted.Task;
        public int ItemRequestCount => Volatile.Read(ref _itemRequestCount);
        public void ReleaseObsoleteItems() => _releaseObsoleteItems.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.Trim('/') != "regular/items")
            {
                return await _fixtures.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            var ordinal = Interlocked.Increment(ref _itemRequestCount);
            if (ordinal == 1)
            {
                _obsoleteItemsStarted.TrySetResult();
                await _releaseObsoleteItems.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return FixtureApiHandler.Json(ordinal == 1 ? obsoleteItemsBody : forcedItemsBody);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fixtures.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>A fake clock that makes any requested delay immediately observable and complete.</summary>
    private sealed class ImmediateTimerTimeProvider : TimeProvider
    {
        private int _timerCreationCount;

        public int TimerCreationCount => Volatile.Read(ref _timerCreationCount);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            Interlocked.Increment(ref _timerCreationCount);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return ImmediateTimer.Instance;
        }

        private sealed class ImmediateTimer : ITimer
        {
            public static ImmediateTimer Instance { get; } = new();
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class CancellableHalfTransferStream(byte[] prefix) : Stream
    {
        private readonly TaskCompletionSource _firstChunkRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _read;

        public Task FirstChunkRead => _firstChunkRead.Task;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_read)
            {
                _read = true;
                prefix.CopyTo(buffer);
                _firstChunkRead.TrySetResult();
                return prefix.Length;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
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

    private sealed class HalfTransferStream(byte[] prefix) : Stream
    {
        private bool _read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (_read) throw new IOException("connection-reset");
            _read = true;
            prefix.CopyTo(buffer);
            return prefix.Length;
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

    private sealed record ResponseOutcome(bool IsStale, string? RefusalReason);
}
