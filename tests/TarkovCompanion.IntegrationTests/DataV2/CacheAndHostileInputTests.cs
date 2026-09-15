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
    public async Task ForcedRequestDoesNotJoinStaleBackgroundRefreshAndDisposeDrainsIt()
    {
        var cache = new InMemoryTarkovDevResponseCache();
        const string oldJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Old\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        const string newJson = "{\"data\":{\"items\":{\"item\":{\"id\":\"item\",\"name\":\"Forced\",\"width\":1,\"height\":1}},\"itemCategories\":{}}}";
        await cache.PutAsync(new("regular/items", oldJson, DateTimeOffset.UtcNow.AddDays(-1), null, null),
            TestContext.Current.CancellationToken);
        var blocked = new CancellableHalfTransferStream(Encoding.UTF8.GetBytes("{\"data\":{\"items\":{"));
        var handler = new SequenceHandler(attempt => attempt == 1
            ? new(HttpStatusCode.OK) { Content = new StreamContent(blocked) }
            : new(HttpStatusCode.OK) { Content = new StringContent(newJson) });
        await using var client = Client(handler, cache, maximumBytes: 4096);

        var stale = await client.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);
        await blocked.FirstChunkRead.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var forced = await client.GetItemsAsync(GameMode.Regular, "en", force: true, TestContext.Current.CancellationToken);

        Assert.True(stale.IsStale);
        Assert.Equal("Old", stale.Data.Items["item"].Name);
        Assert.False(forced.IsStale);
        Assert.Equal("Forced", forced.Data.Items["item"].Name);
        Assert.Equal(2, handler.RequestCount);
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

    private static TarkovDevJsonClient Client(
        HttpMessageHandler handler,
        ITarkovDevResponseCache cache,
        long maximumBytes,
        int depth = 32,
        int maxAttempts = 1,
        TimeSpan? initialRetryDelay = null,
        TimeProvider? timeProvider = null) =>
        new(new HttpClient(handler), cache, new DataTranslationService(), new()
        {
            BaseAddress = new("https://fixture.invalid/"),
            MaxAttempts = maxAttempts,
            InitialRetryDelay = initialRetryDelay ?? TimeSpan.Zero,
            MaximumResponseBytes = maximumBytes,
            MaximumJsonDepth = depth,
        }, timeProvider);

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
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
}
