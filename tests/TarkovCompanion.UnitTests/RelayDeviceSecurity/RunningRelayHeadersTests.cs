using System.Net;
using TarkovCompanion.GroupServer;
using TarkovCompanion.GroupServer.Security;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

/// <summary>
/// The real relay, started as the real program, answering with its security headers.
/// </summary>
/// <remarks>
/// <c>RelayHttpSecurity</c> shipped complete and tested and was called by nothing, so the relay
/// that answered on the internet applied none of it while every unit test passed (#278). A test of
/// the middleware cannot catch that, because the defect is that <c>Program.cs</c> does not add it.
/// So this starts <c>TarkovCompanion.GroupServer</c> itself, on a loopback port, and reads what it
/// says. Delete the <c>UseRelayHttpSecurity</c> line from <c>Program.cs</c> and every test here
/// fails; that was checked, not assumed.
///
/// A child process rather than an in-process host because <c>Program.cs</c> is top-level statements
/// that end in <c>app.Run()</c>: there is no entry point to call and stop.
/// </remarks>
public sealed class RunningRelayHeadersTests
{
    [Theory]
    [InlineData("GET", "/health")]
    [InlineData("GET", "/tablet")]
    [InlineData("GET", "/")]
    [InlineData("GET", "/admin")]
    [InlineData("GET", "/catalog")]
    [InlineData("GET", "/no-such-route")]
    [InlineData("GET", "/reports")]
    [InlineData("POST", "/report")]
    public async Task TheRunningRelayAppliesItsSecurityHeadersToEveryKindOfAnswer(string method, string path)
    {
        await using var relay = await RunningRelay.StartAsync();

        var response = await relay.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path.TrimStart('/')));

        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
        Assert.Contains("frame-ancestors 'none'", Header(response, "Content-Security-Policy"), StringComparison.Ordinal);
        Assert.Contains("camera=()", Header(response, "Permissions-Policy"), StringComparison.Ordinal);
        Assert.Equal("same-origin", Header(response, "Cross-Origin-Opener-Policy"));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task TheRunningRelayServesEachPageUnderThePolicyBuiltFromThatPage()
    {
        await using var relay = await RunningRelay.StartAsync();
        var policies = RelayBrowserPages.ContentSecurityPolicies();

        foreach (var path in new[] { "/tablet", "/", "/admin" })
        {
            var response = await relay.Client.GetAsync(path);

            Assert.Equal(policies[path], Header(response, "Content-Security-Policy"));
        }
    }

    [Fact]
    public async Task TheRunningRelayStartsUnenforcedAndSaysSoBecauseItsOperatorHasNotNamedTheTunnel()
    {
        await using var relay = await RunningRelay.StartAsync();

        var forwarded = new HttpRequestMessage(HttpMethod.Get, "health");
        forwarded.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        var response = await relay.Client.SendAsync(forwarded);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
        Assert.Contains(RelayHttpSecurityOptions.TrustedForwardersVariable, relay.Log(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRunningRelayEnforcesTransportOnceTheOperatorNamesTheTunnel()
    {
        await using var relay = await RunningRelay.StartAsync(
            (RelayHttpSecurityOptions.TrustedForwardersVariable, "127.0.0.1"));

        var https = new HttpRequestMessage(HttpMethod.Get, "health");
        https.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        var http = new HttpRequestMessage(HttpMethod.Get, "health");
        http.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "http");

        var accepted = await relay.Client.SendAsync(https);
        var refused = await relay.Client.SendAsync(http);
        // What tarkov-group-update.sh does: a plain loopback request with no forwarded header.
        var updaterProbe = await relay.Client.GetAsync("health");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Contains("max-age=31536000", Header(accepted, "Strict-Transport-Security"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, updaterProbe.StatusCode);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        Assert.Single(response.Headers.GetValues(name));
}
