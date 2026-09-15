using System.Net;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.IntegrationTests;

/// <summary>
/// Preferring a group's own catalog mirror, and never depending on it.
/// </summary>
/// <remarks>
/// Every client otherwise syncs several megabytes of identical answers on its own connection.
/// A group that runs a server can hold one copy — but the moment that server can stop a client
/// working, it has stopped being an optimisation, which is what every one of these pins.
/// </remarks>
public sealed class CatalogMirrorClientTests
{
    private const string Mirror = "https://mirror.invalid/catalog/";
    private const string Upstream = "https://fixture.invalid/";

    [Fact]
    public async Task TheMirrorIsAskedFirstAndUpstreamIsNotAskedAtAll()
    {
        var seen = new List<string>();
        var handler = new FixtureApiHandler
        {
            ResponseFactory = (request, _) =>
            {
                seen.Add(request.RequestUri!.AbsoluteUri);
                return Task.FromResult(FixtureApiHandler.Json(Payload));
            },
        };

        var result = await Client(handler).GetMapsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.All(seen, uri => Assert.StartsWith(Mirror, uri, StringComparison.Ordinal));
        Assert.DoesNotContain(seen, uri => uri.StartsWith(Upstream, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMirrorThatIsNotThereCostsNothing()
    {
        // The whole promise. A group server that is off, moved or never configured has to be
        // exactly as good as not having one.
        var seen = new List<string>();
        var handler = new FixtureApiHandler
        {
            ResponseFactory = (request, _) =>
            {
                var uri = request.RequestUri!.AbsoluteUri;
                seen.Add(uri);
                return uri.StartsWith(Mirror, StringComparison.Ordinal)
                    ? throw new HttpRequestException("no route to host")
                    : Task.FromResult(FixtureApiHandler.Json(Payload));
            },
        };

        var result = await Client(handler).GetMapsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(seen, uri => uri.StartsWith(Upstream, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AMirrorThatAnswersBadlyFallsStraightThroughToUpstream(HttpStatusCode status)
    {
        // Including 404, which is not transient. Against upstream that is fatal because there
        // is nothing after it; against a mirror there is.
        var handler = new FixtureApiHandler
        {
            ResponseFactory = (request, _) =>
                request.RequestUri!.AbsoluteUri.StartsWith(Mirror, StringComparison.Ordinal)
                    ? Task.FromResult(new HttpResponseMessage(status))
                    : Task.FromResult(FixtureApiHandler.Json(Payload)),
        };

        var result = await Client(handler).GetMapsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task AMirrorServingSomethingThatIsNotTheCatalogFallsThroughRatherThanFailing()
    {
        // A captive portal, a misconfigured reverse proxy, or a server that has been replaced
        // by something else entirely. All of them answer 200 with the wrong body.
        var handler = new FixtureApiHandler
        {
            ResponseFactory = (request, _) =>
                request.RequestUri!.AbsoluteUri.StartsWith(Mirror, StringComparison.Ordinal)
                    ? Task.FromResult(FixtureApiHandler.Json("<html>Sign in to the network</html>"))
                    : Task.FromResult(FixtureApiHandler.Json(Payload)),
        };

        var result = await Client(handler).GetMapsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task ItIsTriedOnceRatherThanRetried()
    {
        // Retrying something optional while upstream sits there waiting is time the player
        // spends looking at an empty database.
        var mirrorRequests = 0;
        var handler = new FixtureApiHandler
        {
            ResponseFactory = (request, _) =>
            {
                if (!request.RequestUri!.AbsoluteUri.StartsWith(Mirror, StringComparison.Ordinal))
                {
                    return Task.FromResult(FixtureApiHandler.Json(Payload));
                }

                mirrorRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            },
        };

        await Client(handler, maxAttempts: 3).GetMapsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);

        Assert.Equal(1, mirrorRequests);
    }

    [Fact]
    public async Task WithNoMirrorConfiguredNothingChanges()
    {
        var seen = new List<string>();
        var handler = new FixtureApiHandler
        {
            ResponseFactory = (request, _) =>
            {
                seen.Add(request.RequestUri!.AbsoluteUri);
                return Task.FromResult(FixtureApiHandler.Json(Payload));
            },
        };

        await Client(handler, mirror: null).GetMapsAsync(GameMode.Regular, "en", TestContext.Current.CancellationToken);

        Assert.All(seen, uri => Assert.StartsWith(Upstream, uri, StringComparison.Ordinal));
    }

    [Fact]
    public void ARelativeMirrorAddressIsRefusedRatherThanQuietlyIgnored()
    {
        var error = Assert.Throws<ArgumentException>(() => new TarkovDevJsonClient(
            new HttpClient(new FixtureApiHandler()),
            new InMemoryTarkovDevResponseCache(),
            new DataTranslationService(),
            new() { MirrorAddress = new("catalog/", UriKind.Relative) }));

        Assert.Contains("absolute", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private const string Payload = """
        {"data":{"maps":{"fixture":{"id":"fixture","name":"Fixture","normalizedName":"fixture","extracts":[{"id":"extract","name":"Extract"}]}}},"translations":[]}
        """;

    private static TarkovDevJsonClient Client(
        HttpMessageHandler handler,
        int maxAttempts = 1,
        string? mirror = Mirror) =>
        new(
            new HttpClient(handler),
            new InMemoryTarkovDevResponseCache(),
            new DataTranslationService(),
            new()
            {
                BaseAddress = new(Upstream),
                MirrorAddress = mirror is null ? null : new Uri(mirror),
                InitialRetryDelay = TimeSpan.Zero,
                MaxAttempts = maxAttempts,
            });
}
