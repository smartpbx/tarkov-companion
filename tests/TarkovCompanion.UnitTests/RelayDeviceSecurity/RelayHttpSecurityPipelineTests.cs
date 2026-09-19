using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TarkovCompanion.GroupServer;
using TarkovCompanion.GroupServer.Security;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

/// <summary>
/// The composed HTTP security policy, over a real Kestrel on a loopback port.
/// </summary>
/// <remarks>
/// <c>RelayHttpSecurityTests</c> proves each primitive on a <c>DefaultHttpContext</c>; these prove
/// what a client sees once they are composed, including the things a unit of one primitive cannot:
/// that a refusal and a 404 carry the headers too, that a route which says how it is cached keeps
/// saying it, and that the two embedded pages still run under the policy they are served with. The
/// last one was measured in a real browser as well: the policy as first written blocked the
/// tablet's only script, and a page that "has a CSP" but renders nothing is not a fix.
/// </remarks>
public sealed class RelayHttpSecurityPipelineTests
{
    private static readonly Regex BlockedMarkup = new(
        @"<(?<tag>script|style)\b[^>]*>.*?</\k<tag>\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    [Theory]
    [InlineData("/json")]
    [InlineData("/nowhere")]
    [InlineData("/tablet")]
    [InlineData("/admin")]
    [InlineData("/cacheable")]
    public async Task EveryResponseCarriesTheIsolationHeadersWhateverItsStatus(string path)
    {
        await using var relay = await PipelineHost.StartAsync(trustedForwarders: null);

        var response = await relay.Client.GetAsync(path);

        Assert.Equal("DENY", Single(response, "X-Frame-Options"));
        Assert.Equal("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.Equal("no-referrer", Single(response, "Referrer-Policy"));
        Assert.Equal("same-origin", Single(response, "Cross-Origin-Opener-Policy"));
        Assert.Equal("same-origin", Single(response, "Cross-Origin-Resource-Policy"));
        Assert.Contains("camera=()", Single(response, "Permissions-Policy"), StringComparison.Ordinal);
        var csp = Single(response, "Content-Security-Policy");
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Set-Cookie"), "no relay route sets a cookie");
    }

    [Fact]
    public async Task AnApiAnswerIsNoStoreAndAPageIsTheOnlyThingAllowedToRunScript()
    {
        await using var relay = await PipelineHost.StartAsync(trustedForwarders: null);

        var api = await relay.Client.GetAsync("/json");
        var page = await relay.Client.GetAsync("/tablet");

        Assert.Equal(RelaySecurityHeaders.ApiContentSecurityPolicy, Single(api, "Content-Security-Policy"));
        Assert.Equal("no-store, max-age=0", Single(api, "Cache-Control"));
        Assert.Contains("script-src", Single(page, "Content-Security-Policy"), StringComparison.Ordinal);
        Assert.DoesNotContain("script-src", Single(api, "Content-Security-Policy"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARouteThatSaysHowItIsCachedKeepsIt()
    {
        // The update feed, the catalog and the landmarks each mark themselves cacheable on purpose,
        // and the proxy in front of this relay is why: an installer that cannot be cached is served
        // from a one-gigabyte box every time.
        await using var relay = await PipelineHost.StartAsync(trustedForwarders: null);

        var cacheable = await relay.Client.GetAsync("/cacheable");
        var plain = await relay.Client.GetAsync("/json");

        Assert.Equal("public, max-age=3600", Single(cacheable, "Cache-Control"));
        Assert.False(cacheable.Headers.Contains("Pragma"), "Pragma: no-cache beside a cacheable answer contradicts it");
        Assert.Equal("no-cache", Single(plain, "Pragma"));
    }

    [Fact]
    public async Task HtmlWithNoPolicyOfItsOwnFallsBackToOneThatAllowsNoInlineScript()
    {
        // A page added later without an entry must render broken, which somebody notices, rather
        // than render under a policy nobody wrote for it.
        await using var relay = await PipelineHost.StartAsync(trustedForwarders: null);

        var response = await relay.Client.GetAsync("/unlisted-page");

        Assert.Equal(RelaySecurityHeaders.BrowserContentSecurityPolicy, Single(response, "Content-Security-Policy"));
    }

    [Fact]
    public void ThePolicyForAPageWithNothingInlineIsExactlyTheBaselinePolicy()
    {
        // The baseline constant and the builder describe the same directives; this is what stops
        // them drifting apart when one of them is edited.
        Assert.Equal(
            RelaySecurityHeaders.BrowserContentSecurityPolicy,
            RelayPageContentSecurityPolicy.Create("<!doctype html><title>x</title><script src=\"/a.js\"></script>"));
    }

    [Theory]
    [InlineData("/tablet")]
    [InlineData("/")]
    [InlineData("/admin")]
    public async Task ThePagePolicyAllowsTheExactInlineScriptAndStyleThePageShips(string path)
    {
        await using var relay = await PipelineHost.StartAsync(trustedForwarders: null);

        var response = await relay.Client.GetAsync(path);
        var html = path == "/admin" ? AdminPanel.Page : Tablet.Page;
        var csp = Single(response, "Content-Security-Policy");
        var directives = csp.Split("; ");

        var script = directives.Single(d => d.StartsWith("script-src ", StringComparison.Ordinal));
        var style = directives.Single(d => d.StartsWith("style-src ", StringComparison.Ordinal));
        // Hashed here by finding the one inline block by position, not by the pattern the relay uses.
        Assert.Contains(HashOfOnly(html, "<script>", "</script>"), script, StringComparison.Ordinal);
        Assert.Contains(HashOfOnly(html, "<style>", "</style>"), style, StringComparison.Ordinal);
        Assert.Equal(2, script.Split(' ').Length - 1);
        Assert.Equal(2, style.Split(' ').Length - 1);
    }

    [Fact]
    public async Task OnlyTheTabletMayLoadABlobImageBecauseItDrawsTheMapFromOne()
    {
        await using var relay = await PipelineHost.StartAsync(trustedForwarders: null);

        var tablet = Single(await relay.Client.GetAsync("/tablet"), "Content-Security-Policy");
        var admin = Single(await relay.Client.GetAsync("/admin"), "Content-Security-Policy");

        Assert.Contains("img-src 'self' data: blob:", tablet, StringComparison.Ordinal);
        Assert.DoesNotContain("blob:", admin, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Tablet")]
    [InlineData("Admin")]
    public void AnEmbeddedPageShipsNothingThatAHashCannotCover(string which)
    {
        // A hash covers a whole inline <script> or <style> element. It does not cover a style
        // attribute or an on* handler, and the policy has no 'unsafe-inline' to fall back on, so
        // either one silently does nothing in a browser. Both pages once had inline style
        // attributes, and one of them was the thing that hid the map until the tablet was paired.
        var html = which == "Tablet" ? Tablet.Page : AdminPanel.Page;
        var markup = BlockedMarkup.Replace(html, string.Empty);
        var script = string.Concat(RelayPageContentSecurityPolicy.InlineScriptBodies(html));

        Assert.DoesNotMatch(@"\sstyle\s*=", markup);
        Assert.DoesNotMatch(@"\son[a-z]+\s*=", markup);
        Assert.DoesNotContain("javascript:", markup, StringComparison.OrdinalIgnoreCase);
        foreach (var forbidden in new[]
        {
            "setAttribute(\"style\"", "setAttribute('style'", "style=\"", "eval(", "new Function(",
            "document.write", "insertAdjacentHTML", "cssText",
        })
        {
            Assert.DoesNotContain(forbidden, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WithNoTrustedForwarderConfiguredNothingIsRefusedAndNothingClaimsHttps()
    {
        // The state a relay is in before its operator has said which peer is the tunnel. Refusing
        // here would refuse every client, and the updater's own health probe with them.
        await using var relay = await PipelineHost.StartAsync(trustedForwarders: null);

        var forwarded = await relay.SendAsync("/json", ("X-Forwarded-Proto", "https"), ("X-Forwarded-For", "203.0.113.9"));
        var plain = await relay.Client.GetAsync("/json");

        Assert.Equal(HttpStatusCode.OK, forwarded.StatusCode);
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.False(forwarded.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task ATrustedProxyReportingHttpsIsAcceptedAndTheAnswerCarriesHsts()
    {
        await using var relay = await PipelineHost.StartAsync(trustedForwarders: "127.0.0.1");

        var response = await relay.SendAsync("/json", ("X-Forwarded-Proto", "https"), ("X-Forwarded-For", "203.0.113.9"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("max-age=31536000; includeSubDomains", Single(response, "Strict-Transport-Security"));
    }

    [Fact]
    public async Task AForwardedSchemeFromAnyoneNotTrustedIsRefusedNotIgnored()
    {
        // The connecting peer here is loopback, which this configuration does not trust to forward.
        // Believing its X-Forwarded-Proto would let any caller claim HTTPS.
        await using var relay = await PipelineHost.StartAsync(trustedForwarders: "10.9.9.9");

        var response = await relay.SendAsync("/json", ("X-Forwarded-Proto", "https"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("request-rejected", body.GetProperty("code").GetString());
        Assert.Matches("^[0-9a-f]{32}$", body.GetProperty("correlationId").GetString());
        // A refusal is a response like any other: it is isolated, unframeable and uncached.
        Assert.Equal("DENY", Single(response, "X-Frame-Options"));
        Assert.Equal("no-store, max-age=0", Single(response, "Cache-Control"));
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Theory]
    [InlineData("X-Forwarded-Proto", "http")]
    [InlineData("X-Forwarded-Proto", "https, https")]
    [InlineData("Forwarded", "for=203.0.113.9;proto=https")]
    [InlineData("X-Forwarded-For", "203.0.113.9, 198.51.100.1")]
    public async Task EvenATrustedProxyIsRefusedWhenItsHeadersAreAmbiguousOrNotHttps(string header, string value)
    {
        await using var relay = await PipelineHost.StartAsync(trustedForwarders: "127.0.0.1");

        var headers = header == "X-Forwarded-Proto"
            ? new[] { (header, value) }
            : [("X-Forwarded-Proto", "https"), (header, value)];
        var response = await relay.SendAsync("/json", headers);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PlainHttpFromLoopbackIsStillAcceptedWhenEnforcedSoTheUpdatersProbeWorks()
    {
        // tarkov-group-update.sh asks http://127.0.0.1:8090/health after installing a build and rolls
        // the install back if it does not answer. An enforcing build that refused that would roll
        // itself back forever.
        await using var relay = await PipelineHost.StartAsync(trustedForwarders: "10.9.9.9");

        var response = await relay.Client.GetAsync("/json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public void AConfigurationThatIsSetButUnusableEnforcesWithNobodyTrustedRatherThanServingOpen()
    {
        var pages = new Dictionary<string, string>();

        var typo = RelayHttpSecurityOptions.FromConfiguration("not-an-address", pages);
        var wildcard = RelayHttpSecurityOptions.FromConfiguration("0.0.0.0, ::", pages);
        var blank = RelayHttpSecurityOptions.FromConfiguration("  ", pages);
        var mixed = RelayHttpSecurityOptions.FromConfiguration("10.0.0.2; nonsense ::1", pages);

        Assert.True(typo.TransportEnforced);
        Assert.Empty(typo.Transport!.TrustedForwarders);
        Assert.True(wildcard.TransportEnforced);
        Assert.Empty(wildcard.Transport!.TrustedForwarders);
        Assert.False(blank.TransportEnforced);
        Assert.Equal(2, mixed.Transport!.TrustedForwarders.Count);
        Assert.True(mixed.Transport.RequireHttps);
        Assert.True(mixed.Transport.AllowLoopbackHttp);
    }

    private static string Single(HttpResponseMessage response, string header) =>
        Assert.Single(response.Headers.TryGetValues(header, out var values)
            ? values
            : response.Content.Headers.TryGetValues(header, out var contentValues) ? contentValues : []);

    private static string HashOfOnly(string html, string open, string close)
    {
        var start = html.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0 && html.IndexOf(open, start + 1, StringComparison.Ordinal) < 0, $"one {open} block");
        start += open.Length;
        var body = html[start..html.IndexOf(close, start, StringComparison.Ordinal)]
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        return "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body))) + "'";
    }

    private sealed class PipelineHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private PipelineHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<PipelineHost> StartAsync(string? trustedForwarders)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.UseRelayHttpSecurity(RelayHttpSecurityOptions.FromConfiguration(
                trustedForwarders,
                RelayBrowserPages.ContentSecurityPolicies()));
            app.MapGet("/json", () => Results.Ok(new { ok = true }));
            app.MapGet("/tablet", () => Results.Content(Tablet.Page, "text/html; charset=utf-8"));
            app.MapGet("/", () => Results.Content(Tablet.Page, "text/html; charset=utf-8"));
            app.MapGet("/admin", () => Results.Content(AdminPanel.Page, "text/html; charset=utf-8"));
            app.MapGet("/unlisted-page", () => Results.Content("<html><body>hi</body></html>", "text/html; charset=utf-8"));
            app.MapGet("/cacheable", (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "public, max-age=3600";
                return Results.Text("cached");
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.First();
            return new PipelineHost(app, new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/") });
        }

        public Task<HttpResponseMessage> SendAsync(string path, params (string Name, string Value)[] headers)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path.TrimStart('/'));
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
