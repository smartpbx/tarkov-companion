using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TarkovCompanion.GroupServer.Security;

/// <summary>
/// What the running relay applies to every response, and what it asks of every request's transport.
/// </summary>
/// <remarks>
/// <see cref="RelaySecurityHeaders"/>, <see cref="RelayTransportPolicy"/> and
/// <see cref="RelaySessionCookie"/> were merged, tested, and called by nothing: <c>Program.cs</c>
/// never composed them, so the relay that answered on the internet sent no CSP, no frame-ancestors,
/// no HSTS and checked no forwarded header (#278). This is the composition, and
/// <c>RunningRelayHeadersTests</c> starts the real <c>Program.cs</c> and fails if it is ever left
/// out again.
///
/// Transport enforcement is opt-in by configuration and headers are not, on purpose. The relay
/// listens on plain HTTP behind a tunnel that lives on a different host, so from its socket every
/// public request is an HTTP request from an address it has no reason to trust. Enforcing HTTPS
/// before the operator has said which peer is the tunnel would refuse every client, including the
/// updater's own health probe, at the moment a self-updating relay installs the build that enforces
/// it. Setting <see cref="TrustedForwardersVariable"/> is how the operator says which peer that is;
/// until then the relay says so in its log and behaves as it did before.
///
/// This policy needs no cookie policy today: no route sets a cookie, and a test asserts none does.
/// <see cref="RelaySessionCookie"/> stays for the operator session #310 asks for.
/// </remarks>
public sealed class RelayHttpSecurityOptions
{
    /// <summary>
    /// Comma-separated IP addresses of the reverse proxies whose forwarded scheme may be believed.
    /// Setting it also turns on HTTPS enforcement for everything that is not a loopback request.
    /// </summary>
    public const string TrustedForwardersVariable = "TARKOV_RELAY_TRUSTED_FORWARDERS";

    public RelayHttpSecurityOptions(
        RelayTransportPolicyOptions? transport,
        IReadOnlyDictionary<string, string> browserPolicies)
    {
        ArgumentNullException.ThrowIfNull(browserPolicies);
        Transport = transport;
        BrowserPolicies = browserPolicies;
    }

    /// <summary>The transport rules to enforce, or null when the operator has not configured them.</summary>
    public RelayTransportPolicyOptions? Transport { get; }

    /// <summary>Whether a request that fails the transport rules is refused.</summary>
    public bool TransportEnforced => Transport is not null;

    /// <summary>The exact Content-Security-Policy for each browser page, by request path.</summary>
    public IReadOnlyDictionary<string, string> BrowserPolicies { get; }

    /// <summary>The configuration this process was started with, for the pages this relay embeds.</summary>
    public static RelayHttpSecurityOptions FromEnvironment(ILogger? logger = null) =>
        FromConfiguration(
            Environment.GetEnvironmentVariable(TrustedForwardersVariable),
            RelayBrowserPages.ContentSecurityPolicies(),
            logger);

    /// <summary>Reads the one operator setting; anything else about the policy is fixed.</summary>
    public static RelayHttpSecurityOptions FromConfiguration(
        string? trustedForwarders,
        IReadOnlyDictionary<string, string> browserPolicies,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(trustedForwarders))
        {
            logger?.LogWarning(
                "Transport policy is not enforced: {Variable} is not set, so a request is not refused for arriving " +
                "over plain HTTP or with forwarded headers, and no HSTS is sent. Security headers are applied.",
                TrustedForwardersVariable);
            return new RelayHttpSecurityOptions(null, browserPolicies);
        }

        var forwarders = new List<IPAddress>();
        foreach (var entry in trustedForwarders.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (IPAddress.TryParse(entry, out var address) &&
                !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any))
            {
                forwarders.Add(address);
            }
            else
            {
                logger?.LogError(
                    "{Variable} has an entry that is not a usable IP address and it is ignored.",
                    TrustedForwardersVariable);
            }
        }

        // Set but unusable enforces with nobody trusted, which refuses forwarded traffic, rather
        // than reading a typo as "not configured" and quietly serving open. Loopback still works,
        // so the updater's health probe does and the operator can see what is wrong.
        logger?.LogInformation(
            "Transport policy is enforced with {Count} trusted forwarder(s); plain HTTP is accepted from loopback only.",
            forwarders.Count);
        return new RelayHttpSecurityOptions(
            new RelayTransportPolicyOptions(requireHttps: true, allowLoopbackHttp: true, forwarders),
            browserPolicies);
    }
}

/// <summary>The relay's embedded browser pages and the exact policy each one is allowed to run under.</summary>
public static class RelayBrowserPages
{
    public static IReadOnlyDictionary<string, string> ContentSecurityPolicies()
    {
        // The tablet draws the map's artwork from a blob it fetched with its own credential, so it
        // alone may load blob: images. The operator page never does.
        var tablet = RelayPageContentSecurityPolicy.Create(Tablet.Page, allowBlobImages: true);
        var admin = RelayPageContentSecurityPolicy.Create(AdminPanel.Page);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["/"] = tablet,
            ["/tablet"] = tablet,
            ["/admin"] = admin,
        };
    }
}

/// <summary>
/// A Content-Security-Policy for one embedded page that allows exactly the inline script and style
/// the page ships, by hash, and nothing else inline.
/// </summary>
/// <remarks>
/// Both pages are one embedded file, and that is a deliberate property of this relay (see
/// <see cref="Tablet"/>): a deployment that is one file stays one file. The policy in
/// <see cref="RelaySecurityHeaders.BrowserContentSecurityPolicy"/> allows only <c>'self'</c>
/// scripts, which refuses a page whose script is inline, so applying it as written would have
/// served a blank tablet. Splitting a fifteen-hundred-line script into a second file to satisfy a
/// header would also have rewritten every test that reads the page. A hash is the CSP mechanism
/// for exactly this: the browser runs that byte-for-byte script and nothing an injection could add,
/// and because the hash is computed from the page as embedded, editing the page cannot leave the
/// policy stale.
///
/// Inline <c>style</c> attributes and <c>on*</c> handlers are not covered by a hash and are not
/// allowed; <c>RelayHttpSecurityPipelineTests</c> fails if either comes back into a page.
/// </remarks>
public static partial class RelayPageContentSecurityPolicy
{
    public static string Create(string html, bool allowBlobImages = false)
    {
        ArgumentNullException.ThrowIfNull(html);
        return "default-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'; " +
            "form-action 'self'; script-src 'self'" + Hashes(html, InlineScript()) +
            "; style-src 'self'" + Hashes(html, InlineStyle()) +
            (allowBlobImages ? "; img-src 'self' data: blob:" : "; img-src 'self' data:") +
            "; connect-src 'self'";
    }

    /// <summary>The bodies of the inline blocks a policy has to hash, for tests to check independently.</summary>
    public static IReadOnlyList<string> InlineScriptBodies(string html) => Bodies(html, InlineScript());

    public static IReadOnlyList<string> InlineStyleBodies(string html) => Bodies(html, InlineStyle());

    public static string Hash(string body) =>
        "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Normalise(body)))) + "'";

    private static string Hashes(string html, Regex block) =>
        string.Concat(Bodies(html, block).Select(body => " " + Hash(body)));

    private static List<string> Bodies(string html, Regex block) =>
        block.Matches(html)
            .Where(match => !SourceAttribute().IsMatch(match.Groups["attributes"].Value) &&
                            match.Groups["body"].Value.Length > 0)
            .Select(match => match.Groups["body"].Value)
            .ToList();

    // The HTML parser turns CR and CRLF into LF before a browser hashes the text, so the hash has
    // to be of that text and not of whatever line endings the file happens to be checked out with.
    private static string Normalise(string body) => body.Replace("\r\n", "\n").Replace('\r', '\n');

    [GeneratedRegex(@"<script\b(?<attributes>[^>]*)>(?<body>.*?)</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InlineScript();

    [GeneratedRegex(@"<style\b(?<attributes>[^>]*)>(?<body>.*?)</style\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InlineStyle();

    [GeneratedRegex(@"\bsrc\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex SourceAttribute();
}

public static class RelayHttpSecurityPipeline
{
    /// <summary>
    /// Puts the relay's security headers on every response and refuses requests whose transport
    /// fails the configured policy. Must be the first middleware, so a refusal, a 404 and an
    /// unhandled fault carry the same headers as an answer.
    /// </summary>
    public static IApplicationBuilder UseRelayHttpSecurity(
        this IApplicationBuilder app,
        RelayHttpSecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        return app.Use(async (context, next) =>
        {
            var decision = options.Transport is { } transport
                ? RelayTransportPolicy.Evaluate(context, transport)
                : new RelayTransportDecision(true, context.Request.IsHttps, "allowed");

            string? pagePolicy = null;
            var isPage = (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
                options.BrowserPolicies.TryGetValue(PageKey(context.Request.Path), out pagePolicy);
            // Headers first and handlers after: a route that says how it may be cached (the update
            // feed, the catalog) replaces these by assigning its own, and one that says nothing
            // gets the strictest.
            RelaySecurityHeaders.Apply(
                context.Response,
                isPage ? RelayHttpSurface.BrowserApplication : RelayHttpSurface.Api,
                decision.EffectiveHttps);
            if (isPage)
            {
                context.Response.Headers.ContentSecurityPolicy = pagePolicy;
            }

            context.Response.OnStarting(static state =>
            {
                var response = (HttpResponse)state;
                // Pragma: no-cache beside a cacheable response is a contradiction a cache may resolve
                // either way; it belongs only where no-store is.
                if (!response.Headers.CacheControl.ToString().Contains("no-store", StringComparison.OrdinalIgnoreCase))
                {
                    response.Headers.Remove("Pragma");
                }

                // Any HTML that has no policy of its own gets the one with no inline script at all:
                // a page added without one renders broken, which is noticed, rather than unprotected.
                if (response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true &&
                    string.Equals(
                        response.Headers.ContentSecurityPolicy.ToString(),
                        RelaySecurityHeaders.ApiContentSecurityPolicy,
                        StringComparison.Ordinal))
                {
                    response.Headers.ContentSecurityPolicy = RelaySecurityHeaders.BrowserContentSecurityPolicy;
                }

                return Task.CompletedTask;
            }, context.Response);

            if (!decision.Allowed)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(
                    RelayPublicError.Create(decision.Code, context.TraceIdentifier)).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });
    }

    private static string PageKey(PathString path)
    {
        var value = path.Value;
        return string.IsNullOrEmpty(value) || value == "/" ? "/" : value.TrimEnd('/');
    }
}
