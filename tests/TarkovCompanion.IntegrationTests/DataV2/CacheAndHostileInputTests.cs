using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.IntegrationTests.DataV2;

[Collection(SqliteCollection.Name)]
public sealed class CacheAndHostileInputTests
{
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
    public async Task FiftyMiBFirstSyncEvidenceFitsAsCompressedDeduplicatedRawBodies()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var cache = new SqliteTarkovDevResponseCache(database.Factory, new()
        {
            MaximumCompressedBytes = 64L * 1024 * 1024,
            MaximumEntries = 16,
            MaximumAge = TimeSpan.FromDays(1),
        });
        const int target = 50 * 1024 * 1024;
        var body = "{\"data\":\"" + new string('x', target) + "\"}";
        await cache.PutAsync(new("regular/first-sync", body, DateTimeOffset.UtcNow, null, null), TestContext.Current.CancellationToken);
        var evidence = await cache.InspectAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.True(evidence.UncompressedBytes >= target);
        Assert.True(evidence.CompressedBytes < 128 * 1024);
        Assert.Equal(body.Length, (await cache.GetAsync("regular/first-sync", TestContext.Current.CancellationToken))!.BodyJson.Length);
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
    public async Task ResponseByteBudgetRefusesBeforeCachePublication()
    {
        var cache = new InMemoryTarkovDevResponseCache();
        var content = new ByteArrayContent(new byte[2048]);
        var client = Client(new StaticHandler(new(HttpStatusCode.OK) { Content = content }), cache, maximumBytes: 1024);
        await Assert.ThrowsAsync<TarkovDevResponseBudgetException>(() =>
            client.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken));
        Assert.Equal(0, (await cache.InspectAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).EntryCount);
    }

    [Fact]
    public async Task HalfTransferFailureNeverPublishesCacheEntry()
    {
        var cache = new InMemoryTarkovDevResponseCache();
        var prefix = Encoding.UTF8.GetBytes("{\"data\":{\"items\":{");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HalfTransferStream(prefix)) };
        var client = Client(new StaticHandler(response), cache, maximumBytes: 4096);
        await Assert.ThrowsAsync<IOException>(() =>
            client.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken));
        Assert.Equal(0, (await cache.InspectAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).EntryCount);
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
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            client.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken));
        Assert.Equal(0, (await cache.InspectAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).EntryCount);

        var deep = "{\"data\":" + string.Concat(Enumerable.Repeat("{\"x\":", 40)) + "1" + new string('}', 40) + "}";
        var deepClient = Client(new StaticHandler(new(HttpStatusCode.OK) { Content = new StringContent(deep) }), cache, maximumBytes: 4096, depth: 8);
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() =>
            deepClient.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken));
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

    private static TarkovDevJsonClient Client(HttpMessageHandler handler, ITarkovDevResponseCache cache, long maximumBytes, int depth = 32) =>
        new(new HttpClient(handler), cache, new DataTranslationService(), new()
        {
            BaseAddress = new("https://fixture.invalid/"),
            MaxAttempts = 1,
            InitialRetryDelay = TimeSpan.Zero,
            MaximumResponseBytes = maximumBytes,
            MaximumJsonDepth = depth,
        });

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
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
}
