using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace TarkovCompanion.IntegrationTests;

internal sealed class FixtureApiHandler : HttpMessageHandler
{
    private readonly string _fixtureRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", "api");
    private readonly ConcurrentDictionary<string, int> _requestCounts = new(StringComparer.Ordinal);

    public Func<HttpRequestMessage, int, Task<HttpResponseMessage>>? ResponseFactory { get; init; }

    public int Count(string path) => _requestCounts.TryGetValue(path, out var count) ? count : 0;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath.Trim('/')
            ?? throw new InvalidOperationException("Fixture request did not have a URI.");
        var count = _requestCounts.AddOrUpdate(path, 1, (_, current) => current + 1);
        if (ResponseFactory is not null)
        {
            return await ResponseFactory(request, count).ConfigureAwait(false);
        }

        var fileName = path switch
        {
            "regular/items" => "items.json",
            "regular/items_en" => "items_en.json",
            "regular/maps" => "maps.json",
            "regular/maps_en" => "maps_en.json",
            "regular/tasks" => "tasks.json",
            "regular/tasks_en" => "tasks_en.json",
            "regular/hideout" => "hideout.json",
            "regular/hideout_en" => "hideout_en.json",
            "regular/traders" => "traders.json",
            "regular/traders_en" => "traders_en.json",
            "regular/crafts" => "crafts.json",
            "regular/barters" => "barters.json",
            "regular/prices/item-001" => "prices-item-001.json",
            _ => throw new InvalidOperationException($"No offline fixture is registered for '{path}'."),
        };
        var body = await File.ReadAllTextAsync(Path.Combine(_fixtureRoot, fileName), cancellationToken).ConfigureAwait(false);
        return Json(body);
    }

    public static HttpResponseMessage Json(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? etag = null,
        DateTimeOffset? lastModified = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        response.Headers.ETag = etag is null ? null : EntityTagHeaderValue.Parse(etag);
        response.Content.Headers.LastModified = lastModified;
        return response;
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow += duration;
}

internal sealed class TimeoutApiHandler : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The timeout handler should only complete through cancellation.");
    }
}

internal static class FixtureJson
{
    public static Task<string> ReadAsync(string fileName) =>
        File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "api", fileName));
}

internal static class TestContext
{
    public static TestExecutionContext Current { get; } = new();
}

internal sealed class TestExecutionContext
{
    public CancellationToken CancellationToken => CancellationToken.None;
}
