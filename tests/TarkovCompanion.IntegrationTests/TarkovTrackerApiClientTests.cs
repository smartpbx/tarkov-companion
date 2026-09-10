using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Infrastructure.TarkovTracker;

namespace TarkovCompanion.IntegrationTests;

public sealed class TarkovTrackerApiClientTests
{
    private const string TestToken = "PVP_not-a-real-credential";
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ClientUsesOnlyCanonicalGetRoutesAndDescriptiveUserAgent()
    {
        var handler = new RecordingHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/token" => Json(HttpStatusCode.OK, TokenJson(TestToken, "pvp", "GP")),
            "/progress" => Json(HttpStatusCode.OK, ProgressJson("pvp")),
            _ => throw new InvalidOperationException("Unexpected route."),
        });
        using var client = Client(handler);

        await client.ValidateTokenAsync(TestToken, GameMode.Regular, CancellationToken.None);
        await client.GetProgressAsync(TestToken, GameMode.Regular, null, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https", request.Uri.Scheme);
            Assert.Equal("api.tarkovtracker.org", request.Uri.Host);
            Assert.True(request.Uri.IsDefaultPort);
            Assert.DoesNotContain("team", request.Uri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(request.HasBearerCredential);
            Assert.InRange(request.UserAgent.Length, 5, 200);
        });
        Assert.Equal(["/token", "/progress"], handler.Requests.Select(value => value.Uri.AbsolutePath));

        Assert.Equal(
            [nameof(ITarkovTrackerApiClient.GetProgressAsync), nameof(ITarkovTrackerApiClient.ValidateTokenAsync)],
            typeof(ITarkovTrackerApiClient).GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("app")]
    [InlineData("plain-name")]
    public void UserAgentMustBeDescriptiveAndAtLeastFiveCharacters(string userAgent)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Client(new RecordingHandler(_ => throw new InvalidOperationException()), new()
            {
                Enabled = true,
                UserAgent = userAgent,
            }));
        Assert.Equal("UserAgent", exception.ParamName);
    }

    [Fact]
    public async Task DisabledNetworkAdapterRejectsBeforeCreatingARequest()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Network must not run."));
        using var client = Client(handler, new() { Enabled = false });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ValidateTokenAsync(TestToken, GameMode.Regular, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RedirectIsRejectedWithoutForwardingOrLeakingCredential()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.PermanentRedirect)
        {
            Headers = { Location = new Uri("https://untrusted.invalid/collect") },
        });
        using var client = Client(handler);

        var exception = await Assert.ThrowsAsync<TarkovTrackerApiException>(() =>
            client.ValidateTokenAsync(TestToken, GameMode.Regular, CancellationToken.None));

        Assert.Equal(TarkovTrackerApiFailure.RedirectRejected, exception.Failure);
        Assert.DoesNotContain(TestToken, exception.ToString(), StringComparison.Ordinal);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://api.tarkovtracker.org/token"), request.Uri);
    }

    [Fact]
    public async Task TokenPrefixModeReturnedModeAndGpPermissionAreValidated()
    {
        var handler = new RecordingHandler(_ => Json(
            HttpStatusCode.OK,
            TokenJson(TestToken, "pve", "GP")));
        using var client = Client(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => client.ValidateTokenAsync(
            "PVE_not-a-real-credential",
            GameMode.Regular,
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ValidateTokenAsync(
            "PVP_not a valid credential",
            GameMode.Regular,
            CancellationToken.None));
        Assert.Empty(handler.Requests);
        var wrongMode = await Assert.ThrowsAsync<TarkovTrackerApiException>(() =>
            client.ValidateTokenAsync(TestToken, GameMode.Regular, CancellationToken.None));
        Assert.Equal(TarkovTrackerApiFailure.InvalidResponse, wrongMode.Failure);

        handler.Responder = _ => Json(HttpStatusCode.OK, TokenJson(TestToken, "pvp", "TP"));
        var missingPermission = await Assert.ThrowsAsync<TarkovTrackerApiException>(() =>
            client.ValidateTokenAsync(TestToken, GameMode.Regular, CancellationToken.None));
        Assert.Equal(TarkovTrackerApiFailure.Forbidden, missingPermission.Failure);
        Assert.DoesNotContain(TestToken, missingPermission.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgressSupportsEtag304AndQuotaHeaders()
    {
        var call = 0;
        var handler = new RecordingHandler(_ =>
        {
            call++;
            var response = call == 1
                ? Json(HttpStatusCode.OK, ProgressJson("pvp"))
                : new HttpResponseMessage(HttpStatusCode.NotModified);
            response.Headers.ETag = EntityTagHeaderValue.Parse("W/\"fixture-etag\"");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Limit", "1000");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", call == 1 ? "999" : "998");
            response.Headers.TryAddWithoutValidation(
                "X-RateLimit-Reset",
                Now.AddHours(8).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return response;
        });
        using var client = Client(handler);

        var first = await client.GetProgressAsync(TestToken, GameMode.Regular, null, CancellationToken.None);
        var second = await client.GetProgressAsync(
            TestToken,
            GameMode.Regular,
            first.ETag,
            CancellationToken.None);

        Assert.False(first.NotModified);
        Assert.Equal(999, first.Quota.Remaining);
        Assert.True(second.NotModified);
        Assert.Null(second.Snapshot);
        Assert.Equal(998, second.Quota.Remaining);
        Assert.Equal("W/\"fixture-etag\"", handler.Requests[1].IfNoneMatch);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TarkovTrackerApiFailure.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, TarkovTrackerApiFailure.Forbidden)]
    public async Task AuthenticationFailuresAreTypedAndRedacted(
        HttpStatusCode statusCode,
        TarkovTrackerApiFailure expected)
    {
        using var client = Client(new RecordingHandler(_ => Json(statusCode, "{\"success\":false}")));

        var exception = await Assert.ThrowsAsync<TarkovTrackerApiException>(() =>
            client.GetProgressAsync(TestToken, GameMode.Regular, null, CancellationToken.None));

        Assert.Equal(expected, exception.Failure);
        Assert.DoesNotContain(TestToken, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryAfterAndServerFailuresProduceBoundedBackoffSignals()
    {
        var rateLimited = new RecordingHandler(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, "{\"success\":false}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return response;
        });
        using (var client = Client(rateLimited))
        {
            var exception = await Assert.ThrowsAsync<TarkovTrackerApiException>(() =>
                client.GetProgressAsync(TestToken, GameMode.Regular, null, CancellationToken.None));
            Assert.Equal(TarkovTrackerApiFailure.RateLimited, exception.Failure);
            Assert.Equal(Now.AddSeconds(90), exception.RetryAfterUtc);
        }

        using var serverClient = Client(new RecordingHandler(_ =>
            Json(HttpStatusCode.ServiceUnavailable, "{\"success\":false}")));
        var serverException = await Assert.ThrowsAsync<TarkovTrackerApiException>(() =>
            serverClient.GetProgressAsync(TestToken, GameMode.Regular, null, CancellationToken.None));
        Assert.Equal(TarkovTrackerApiFailure.ServerError, serverException.Failure);
        Assert.Equal(503, serverException.StatusCode);
    }

    [Fact]
    public async Task TimeoutAndCallerCancellationRemainDistinct()
    {
        using (var timeoutClient = Client(
            new DelayedHandler(TimeSpan.FromSeconds(2)),
            new() { Enabled = true, RequestTimeout = TimeSpan.FromMilliseconds(20) }))
        {
            var exception = await Assert.ThrowsAsync<TarkovTrackerApiException>(() =>
                timeoutClient.GetProgressAsync(TestToken, GameMode.Regular, null, CancellationToken.None));
            Assert.Equal(TarkovTrackerApiFailure.Timeout, exception.Failure);
        }

        using (var bodyTimeoutClient = Client(
            new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new DelayedReadStream(TimeSpan.FromSeconds(2))),
            }),
            new() { Enabled = true, RequestTimeout = TimeSpan.FromMilliseconds(20) }))
        {
            var exception = await Assert.ThrowsAsync<TarkovTrackerApiException>(() =>
                bodyTimeoutClient.GetProgressAsync(TestToken, GameMode.Regular, null, CancellationToken.None));
            Assert.Equal(TarkovTrackerApiFailure.Timeout, exception.Failure);
        }

        using var cancellationClient = Client(new DelayedHandler(TimeSpan.FromSeconds(2)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellationClient.GetProgressAsync(
            TestToken,
            GameMode.Regular,
            null,
            cancellation.Token));
    }

    [Fact]
    public async Task WrongProgressModeAndMalformedCountsAreRejectedWithoutSnapshot()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, ProgressJson("pve")));
        using var client = Client(handler);
        var wrongMode = await Assert.ThrowsAsync<TarkovTrackerApiException>(() =>
            client.GetProgressAsync(TestToken, GameMode.Regular, null, CancellationToken.None));
        Assert.Equal(TarkovTrackerApiFailure.InvalidResponse, wrongMode.Failure);

        handler.Responder = _ => Json(HttpStatusCode.OK, ProgressJson("pvp", count: "-1"));
        var badCount = await Assert.ThrowsAsync<TarkovTrackerApiException>(() =>
            client.GetProgressAsync(TestToken, GameMode.Regular, null, CancellationToken.None));
        Assert.Equal(TarkovTrackerApiFailure.InvalidResponse, badCount.Failure);
    }

    private static TarkovTrackerApiClient Client(
        HttpMessageHandler handler,
        TarkovTrackerOptions? options = null) =>
        new(handler, options ?? new() { Enabled = true }, new ManualTimeProvider(Now));

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string TokenJson(string token, string mode, string permission) =>
        $$$"""
        {"success":true,"permissions":["{{{permission}}}"],"token":"{{{token}}}","gameMode":"{{{mode}}}"}
        """;

    private static string ProgressJson(string mode, string count = "2") =>
        $$$"""
        {"success":true,"data":{"tasksProgress":[{"id":"task-known","complete":true,"failed":false,"invalid":false}],"taskObjectivesProgress":[{"id":"objective-known","complete":true,"count":{{{count}}},"invalid":false}],"ignoredIdentity":"not-mapped"},"meta":{"self":"not-mapped","gameMode":"{{{mode}}}"}}
        """;

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        bool HasBearerCredential,
        string UserAgent,
        string? IfNoneMatch);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = responder;

        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new(
                request.Method,
                request.RequestUri ?? throw new InvalidOperationException("Request URI was missing."),
                request.Headers.Authorization?.Scheme == "Bearer" &&
                    !string.IsNullOrWhiteSpace(request.Headers.Authorization.Parameter),
                request.Headers.UserAgent.ToString(),
                request.Headers.IfNoneMatch.SingleOrDefault()?.ToString()));
            return Task.FromResult(Responder(request));
        }
    }

    private sealed class DelayedHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return Json(HttpStatusCode.OK, ProgressJson("pvp"));
        }
    }

    private sealed class DelayedReadStream(TimeSpan delay) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
