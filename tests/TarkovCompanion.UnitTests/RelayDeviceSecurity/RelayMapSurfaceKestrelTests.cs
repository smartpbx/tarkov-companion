using System.Net;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

/// <summary>
/// The map routes against a real Kestrel, because the limit that breaks them is Kestrel's.
/// </summary>
/// <remarks>
/// The relay sets one global <c>MaxRequestBodySize</c> of 32 KiB (Program.cs). Everything this
/// file's routes accept is larger than that — a map surface is up to 1 MiB and a rasterized plan
/// up to 24 MiB — so without a per-request override Kestrel answers 413 before the handler runs
/// at all, and every bound and refusal written in <c>RelayMapSurfaceStore</c> is unreachable. That
/// is the same defect #414 fixed for <c>/report</c>, and it is invisible to a
/// <c>DefaultHttpContext</c> test and to <c>TestServer</c>, neither of which enforces a Kestrel
/// limit. So this one starts a real in-process server on a loopback port and speaks HTTP to it.
///
/// The other half matters just as much: an oversized body must come back as this relay's own
/// refusal, with its own code, rather than as a transport 413 — otherwise raising the limit would
/// have replaced one silent failure with another.
/// </remarks>
public sealed class RelayMapSurfaceKestrelTests
{
    /// <summary>The same global limit <c>Program.cs</c> configures; the point of the test is that it is smaller.</summary>
    private const int GlobalKestrelLimit = RelayMapTestHost.GlobalKestrelLimit;

    [Fact]
    public async Task ASurfaceAndItsArtworkSurviveTheServersOwnBodyLimit()
    {
        await using var relay = await RelayMapTestHost.StartAsync();
        var surface = Encoding.UTF8.GetBytes(
            $$"""{"mapId":"customs","padding":"{{new string('p', RelayMapSurfaceStore.MaximumSurfaceBytes - 40)}}"}""");
        Assert.True(surface.Length > GlobalKestrelLimit, "the surface has to be larger than the limit under test");
        Assert.True(surface.Length <= RelayMapSurfaceStore.MaximumSurfaceBytes);
        var artwork = RandomNumberGenerator.GetBytes(100 * 1024);
        var sha = Convert.ToHexStringLower(SHA256.HashData(artwork));

        var publishedSurface = await relay.PostAsync("v2/companion/relay/map", relay.Owner, surface, "application/json");
        var publishedArtwork = await relay.PostAsync(
            $"v2/companion/relay/map/artwork?sha256={sha}",
            relay.Owner,
            artwork,
            "image/png");
        var readSurface = await relay.GetAsync("v2/companion/relay/map", relay.Tablet);
        var readArtwork = await relay.GetAsync("v2/companion/relay/map/artwork", relay.Tablet);

        Assert.Equal(HttpStatusCode.OK, publishedSurface.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publishedArtwork.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readSurface.StatusCode);
        Assert.Equal(surface, await readSurface.Content.ReadAsByteArrayAsync());
        Assert.Equal(artwork, await readArtwork.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AnOversizedBodyIsRefusedByThisRelayRatherThanByTheTransport()
    {
        await using var relay = await RelayMapTestHost.StartAsync();

        var oversized = await relay.PostAsync(
            "v2/companion/relay/map",
            relay.Owner,
            new byte[RelayMapSurfaceStore.MaximumSurfaceBytes + 1],
            "application/json");

        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Contains("map surface", await oversized.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        // And nothing was kept: a refused publish must not leave a half-read body behind as state.
        Assert.Equal(HttpStatusCode.NotFound, (await relay.GetAsync("v2/companion/relay/map", relay.Tablet)).StatusCode);
    }

    [Fact]
    public async Task AnUnauthenticatedCallerReadsNothingOverTheWireEither()
    {
        await using var relay = await RelayMapTestHost.StartAsync();
        await relay.PostAsync(
            "v2/companion/relay/map",
            relay.Owner,
            Encoding.UTF8.GetBytes("""{"mapId":"customs"}"""),
            "application/json");

        using var anonymous = new HttpRequestMessage(HttpMethod.Get, "v2/companion/relay/map");
        var refused = await relay.Client.SendAsync(anonymous);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }
}
