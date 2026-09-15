using System.Net;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.App.Services.Updates;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests;

public sealed class AuthenticatedGitHubReleaseFeedTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"tarkov-authenticated-feed-{Guid.NewGuid():N}");

    [Fact]
    public async Task RingListingsUseTheProtectedCredential()
    {
        var handler = new QueueHandler(
            request =>
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("private-token", request.Headers.Authorization?.Parameter);
                Assert.Equal("api.github.com", request.RequestUri?.Host);
                return PrivateRepository();
            },
            request =>
            {
                Assert.Equal("private-token", request.Headers.Authorization?.Parameter);
                return JsonResponse("""
                [
                  {"type":"file","name":"release-index-g0000000001.json"},
                  {"type":"dir","name":"ignored"}
                ]
                """);
            });
        using var feed = CreateFeed(handler);

        var names = await feed.ListRingAsync("stable", default);

        Assert.Equal(new[] { "release-index-g0000000001.json" }, names);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task AnAuthenticatedAssetRedirectNeverForwardsTheCredential()
    {
        Directory.CreateDirectory(_root);
        var bytes = "verified package"u8.ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var handler = new QueueHandler(
            _ => PrivateRepository(),
            request =>
            {
                Assert.Equal("private-token", request.Headers.Authorization?.Parameter);
                return JsonResponse($$"""
                    {
                      "draft": false,
                      "immutable": true,
                      "tag_name": "v2-build-2.0.0",
                      "assets": [{
                        "name": "package.nupkg",
                        "state": "uploaded",
                        "size": {{bytes.Length}},
                        "digest": "sha256:{{digest}}",
                        "url": "https://api.github.com/repos/acme/private-feed/releases/assets/7"
                      }]
                    }
                    """);
            },
            request =>
            {
                Assert.Equal("private-token", request.Headers.Authorization?.Parameter);
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("https://objects.githubusercontent.com/signed-package") },
                };
            },
            request =>
            {
                Assert.Null(request.Headers.Authorization);
                Assert.Equal("objects.githubusercontent.com", request.RequestUri?.Host);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            });
        using var feed = CreateFeed(handler);
        var destination = Path.Combine(_root, "package.nupkg");

        await feed.DownloadAssetAsync(
            "v2-build-2.0.0",
            "package.nupkg",
            destination,
            ReleaseFeedLimits.MaximumArtifactBytes,
            default);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.Equal(4, handler.RequestCount);
    }

    [Fact]
    public async Task ADownloadedAssetThatDisagreesWithGitHubDigestIsRemoved()
    {
        Directory.CreateDirectory(_root);
        var bytes = "substituted"u8.ToArray();
        var handler = new QueueHandler(
            _ => PrivateRepository(),
            _ => JsonResponse($$"""
                {
                  "draft": false,
                  "immutable": true,
                  "tag_name": "v2-build-2.0.0",
                  "assets": [{
                    "name": "package.nupkg",
                    "state": "uploaded",
                    "size": {{bytes.Length}},
                    "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "url": "https://api.github.com/repos/acme/private-feed/releases/assets/7"
                  }]
                }
                """),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        using var feed = CreateFeed(handler);
        var destination = Path.Combine(_root, "package.nupkg");

        await Assert.ThrowsAsync<InvalidDataException>(() => feed.DownloadAssetAsync(
            "v2-build-2.0.0",
            "package.nupkg",
            destination,
            ReleaseFeedLimits.MaximumArtifactBytes,
            default));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void ThePublicSourceRepositoryCannotBeUsedAsThePrivateFeed()
    {
        var options = Options() with { Repository = "smartpbx/tarkov-companion" };

        Assert.Throws<ArgumentException>(() => new AuthenticatedGitHubReleaseFeed(
            options,
            Credentials(options),
            new QueueHandler()));
    }

    [Fact]
    public async Task ARepositoryReportedAsPublicIsRefusedBeforeItsRingIsRead()
    {
        var handler = new QueueHandler(
            _ => JsonResponse("""{"visibility":"public"}"""),
            _ => throw new InvalidOperationException("The public feed's ring must not be read."));
        using var feed = CreateFeed(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => feed.ListRingAsync("stable", default));

        Assert.Equal(1, handler.RequestCount);
    }

    private AuthenticatedGitHubReleaseFeed CreateFeed(HttpMessageHandler handler)
    {
        var options = Options();
        return new AuthenticatedGitHubReleaseFeed(options, Credentials(options), handler);
    }

    private SignedReleaseFeedOptions Options() => new()
    {
        Repository = "acme/private-feed",
        StagingRoot = _root,
        CosignPath = Path.Combine(_root, "cosign.exe"),
        CosignSha256 = "9fe59be0eca1271873ce019061335eb1ac419b7059202e797828467ddabe33be",
        TrustRootPath = Path.Combine(_root, "trusted-root.json"),
    };

    private static ReleaseFeedCredentialProvider Credentials(SignedReleaseFeedOptions options) =>
        new(new FakeSecrets(), options.CredentialReference);

    private static HttpResponseMessage JsonResponse(string value)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
        return response;
    }

    private static HttpResponseMessage PrivateRepository() =>
        JsonResponse("""{"visibility":"private"}""");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new(responses);

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            Assert.NotEmpty(_responses);
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    private sealed class FakeSecrets : IIntegrationSecretStore
    {
        public bool IsAvailable => true;

        public Task SaveAsync(
            IntegrationSecretReference reference,
            string secret,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> LoadAsync(
            IntegrationSecretReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>("private-token");
        }

        public Task<bool> ExistsAsync(
            IntegrationSecretReference reference,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(
            IntegrationSecretReference reference,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
