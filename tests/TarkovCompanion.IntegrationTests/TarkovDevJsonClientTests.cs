using System.Net;
using System.Text.Json;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.IntegrationTests;

public sealed class TarkovDevJsonClientTests
{
    [Fact]
    public async Task EveryEndpointFamilyParsesFromOfflineFixtures()
    {
        var handler = new FixtureApiHandler();
        var client = CreateClient(handler);

        var items = await client.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);
        var maps = await client.GetMapsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);
        var tasks = await client.GetTasksAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);
        var hideout = await client.GetHideoutAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);
        var traders = await client.GetTradersAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);
        var crafts = await client.GetCraftsAsync(GameMode.Regular, TestContext.Current.CancellationToken);
        var barters = await client.GetBartersAsync(GameMode.Regular, TestContext.Current.CancellationToken);
        var prices = await client.GetPriceHistoryAsync(GameMode.Regular, "item-001", TestContext.Current.CancellationToken);

        Assert.Equal("Salewa first aid kit", items.Data.Items["item-001"].Name);
        Assert.Equal(string.Empty, items.Data.Items["item-002"].Description);
        Assert.True(items.Data.Items["item-001"].AdditionalData.ContainsKey("futureSchemaField"));
        Assert.Equal("Customs", maps.Data.Maps["map-001"].Name);
        Assert.Equal("Shortage", tasks.Data.Tasks["task-001"].Name);
        Assert.Equal("Hand over two first aid kits", tasks.Data.Tasks["task-001"].Objectives[0].Description);
        Assert.Equal("Medstation", hideout.Data["station-001"].Name);
        Assert.Equal("Therapist", traders.Data["trader-001"].Name);
        Assert.Single(crafts.Data);
        Assert.Single(barters.Data);
        Assert.Equal(2, prices.Data.Count);
    }

    [Fact]
    public async Task MissingRequiredFieldsFailClearlyWhileUnknownFieldsAreAllowed()
    {
        const string malformed = """
            {"data":{"items":{"bad":{"name":"Name","shortName":"Short","width":1,"height":1,"unknown":true}}},"translations":[]}
            """;
        var handler = new FixtureApiHandler
        {
            ResponseFactory = (_, _) => Task.FromResult(FixtureApiHandler.Json(malformed)),
        };
        var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<JsonException>(
            () => client.GetItemsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken));

        Assert.Contains("required", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingObjectiveDiscriminatorFailsClearly()
    {
        const string malformed = """
            {"data":{"tasks":{"task":{"id":"task","name":"Task","objectives":[{"id":"objective","description":"Missing type"}]}}},"translations":[]}
            """;
        var handler = new FixtureApiHandler
        {
            ResponseFactory = (_, _) => Task.FromResult(FixtureApiHandler.Json(malformed)),
        };
        var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<JsonException>(
            () => client.GetTasksAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken));

        Assert.Contains("required", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentCacheMissesShareOneRequest()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = await FixtureJson.ReadAsync("crafts.json");
        var handler = new FixtureApiHandler
        {
            ResponseFactory = async (_, _) =>
            {
                received.TrySetResult();
                await release.Task;
                return FixtureApiHandler.Json(body);
            },
        };
        var client = CreateClient(handler);

        var first = client.GetCraftsAsync(GameMode.Regular, TestContext.Current.CancellationToken);
        var second = client.GetCraftsAsync(GameMode.Regular, TestContext.Current.CancellationToken);
        await received.Task;
        Assert.Equal(1, handler.Count("regular/crafts"));
        release.SetResult();

        await Task.WhenAll(first, second);
        Assert.Equal(1, handler.Count("regular/crafts"));
    }

    [Fact]
    public async Task TransientRetriesStopAtConfiguredMaximum()
    {
        var handler = new FixtureApiHandler
        {
            ResponseFactory = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
        };
        var client = CreateClient(handler, maxAttempts: 3);

        await Assert.ThrowsAsync<TarkovDevRequestException>(
            () => client.GetCraftsAsync(GameMode.Regular, TestContext.Current.CancellationToken));

        Assert.Equal(3, handler.Count("regular/crafts"));
    }

    [Fact]
    public async Task EveryAttemptHasAHardTimeout()
    {
        var handler = new TimeoutApiHandler();
        var client = CreateClient(handler, requestTimeout: TimeSpan.FromMilliseconds(25));

        var error = await Assert.ThrowsAsync<TarkovDevRequestException>(
            () => client.GetCraftsAsync(GameMode.Regular, TestContext.Current.CancellationToken));

        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ConditionalRevalidationUsesValidatorsAndOfflineFailureReturnsStaleCache()
    {
        var time = new ManualTimeProvider(new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero));
        var body = await FixtureJson.ReadAsync("crafts.json");
        var lastModified = new DateTimeOffset(2026, 9, 9, 11, 0, 0, TimeSpan.Zero);
        var handler = new FixtureApiHandler
        {
            ResponseFactory = (request, count) =>
            {
                if (count == 1)
                {
                    return Task.FromResult(FixtureApiHandler.Json(body, etag: "\"v1\"", lastModified: lastModified));
                }

                Assert.Contains(request.Headers.IfNoneMatch, value => value.Tag == "\"v1\"");
                Assert.Equal(lastModified, request.Headers.IfModifiedSince);
                if (count == 2)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
                }

                throw new HttpRequestException("offline");
            },
        };
        var client = CreateClient(handler, timeProvider: time, maxAttempts: 3);

        await client.GetCraftsAsync(GameMode.Regular, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromHours(10));
        var revalidated = await client.GetCraftsAsync(GameMode.Regular, true, TestContext.Current.CancellationToken);
        Assert.False(revalidated.IsStale);

        time.Advance(TimeSpan.FromHours(10));
        var offline = await client.GetCraftsAsync(GameMode.Regular, true, TestContext.Current.CancellationToken);
        Assert.True(offline.IsStale);
        Assert.True(offline.IsFromCache);
        Assert.Equal(5, handler.Count("regular/crafts"));
    }

    [Fact]
    public async Task StaleWhileRevalidateReturnsCachedDataBeforeRefreshCompletes()
    {
        var time = new ManualTimeProvider(new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero));
        var oldBody = await FixtureJson.ReadAsync("crafts.json");
        const string newBody = """
            {"data":[{"id":"craft-002","requiredItems":[],"productItem":{"item":"item-001","count":1}}],"translations":[]}
            """;
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FixtureApiHandler
        {
            ResponseFactory = async (_, count) =>
            {
                if (count == 1)
                {
                    return FixtureApiHandler.Json(oldBody);
                }

                refreshStarted.TrySetResult();
                await releaseRefresh.Task;
                return FixtureApiHandler.Json(newBody);
            },
        };
        var client = CreateClient(handler, timeProvider: time);
        await client.GetCraftsAsync(GameMode.Regular, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromHours(10));

        var stale = await client.GetCraftsAsync(GameMode.Regular, TestContext.Current.CancellationToken);
        Assert.True(stale.IsStale);
        Assert.Equal("craft-001", stale.Data[0].Id);
        await refreshStarted.Task;
        releaseRefresh.SetResult();

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var current = await client.GetCraftsAsync(GameMode.Regular, TestContext.Current.CancellationToken);
            if (!current.IsStale && current.Data[0].Id == "craft-002")
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail("The background refresh did not publish the fresh fixture.");
    }

    private static TarkovDevJsonClient CreateClient(
        HttpMessageHandler handler,
        ManualTimeProvider? timeProvider = null,
        int maxAttempts = 1,
        TimeSpan? requestTimeout = null) =>
        new(
            new HttpClient(handler),
            new InMemoryTarkovDevResponseCache(),
            new DataTranslationService(),
            new()
            {
                BaseAddress = new("https://fixture.invalid/"),
                InitialRetryDelay = TimeSpan.Zero,
                MaxAttempts = maxAttempts,
                RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(12),
            },
            timeProvider);
}
