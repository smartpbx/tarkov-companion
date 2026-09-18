using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.GroupServer;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.StateSync;
using TarkovCompanion.GroupServer.Storage;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

/// <summary>A real in-process relay: Kestrel on a loopback port, with the routes under test.</summary>
/// <remarks>
/// Real because the two things worth proving about these routes are both Kestrel's: its global
/// body limit, which refuses everything the map routes accept unless each request raises it, and
/// how long a held read actually stays open. Neither a <c>DefaultHttpContext</c> nor
/// <c>TestServer</c> shows either one.
/// </remarks>
internal sealed class RelayMapTestHost : IAsyncDisposable
{
    /// <summary>The same global limit <c>Program.cs</c> configures.</summary>
    public const int GlobalKestrelLimit = 32 * 1024;

    /// <summary>The store this host is serving, so a test can publish without going over HTTP.</summary>
    public RelayMapSurfaceStore Surfaces { get; private init; } = null!;

    private readonly WebApplication _app;
    private readonly RelayTestContext _context;

    private RelayMapTestHost(
        WebApplication app,
        RelayTestContext context,
        HttpClient client,
        RelaySessionCredential owner,
        RelaySessionCredential tablet)
    {
        _app = app;
        _context = context;
        Client = client;
        Owner = owner;
        Tablet = tablet;
    }

    public HttpClient Client { get; }

    public RelaySessionCredential Owner { get; }

    public RelaySessionCredential Tablet { get; }

    /// <summary>The owner as the registry authenticated it, for publishing without going over HTTP.</summary>
    public RelayPrincipal OwnerPrincipal { get; private init; } = null!;

    public static async Task<RelayMapTestHost> StartAsync()
    {
        var context = await RelaySecurityTestFactory.BootstrapAsync();
        var ownerPrincipal = await context.AuthenticateOwnerAsync();
        var completed = await RelaySecurityTestFactory.CompletedPairingAsync("kestrel-tablet", context.Clock.UtcNow);
        var paired = await context.Registry.AddPairedDeviceAsync(
            ownerPrincipal,
            completed,
            DeviceAuthorizationRole.Member,
            CompanionSurfaceKind.TabletLandscape);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        // Exactly what Program.cs configures. Every assertion here is about a body larger
        // than this number reaching a handler anyway.
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = GlobalKestrelLimit);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var surfaces = new RelayMapSurfaceStore(context.Registry, context.Clock);
        app.MapRelayCompanionRoutes(
            context.Registry,
            new OpaqueRelayFrameHub(context.Registry, context.Clock),
            context.Recovery,
            new RelayOwnerClaimGate(context.Clock),
            surfaces);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/") };
        return new RelayMapTestHost(
            app,
            context,
            client,
            context.OwnerCredential,
            Assert.IsType<RelaySessionCredential>(paired.Value))
        {
            Surfaces = surfaces,
            OwnerPrincipal = ownerPrincipal,
        };
    }

    public Task<HttpResponseMessage> PostAsync(
        string path,
        RelaySessionCredential credential,
        byte[] body,
        string mediaType)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        Authenticate(request, credential);
        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(
        string path,
        RelaySessionCredential credential,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        Authenticate(request, credential);
        return Client.SendAsync(request, cancellationToken);
    }

    private static void Authenticate(HttpRequestMessage request, RelaySessionCredential credential)
    {
        request.Headers.Add("X-Relay-Session", credential.SessionId.Value.ToString("D"));
        request.Headers.Add("X-Relay-Credential", credential.Secret);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _context.Dispose();
    }
}
