using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.GroupServer.Security;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

public sealed class RelayHttpSecurityTests
{
    [Fact]
    public void BrowserSessionCookieIsHostOnlySecureHttpOnlyStrictAndExactlyExpiring()
    {
        var expires = RelaySecurityTestFactory.Now.AddHours(1);
        var options = RelaySessionCookie.Create(expires, RelaySecurityTestFactory.Now);
        var sessionId = new DeviceSessionId(Guid.NewGuid());
        var secret = RelaySecurityTestFactory.Base64Url(new byte[32]);
        var value = RelaySessionCookie.Encode(sessionId, secret);

        Assert.StartsWith("__Host-", RelaySessionCookie.Name, StringComparison.Ordinal);
        Assert.True(options.Secure);
        Assert.True(options.HttpOnly);
        Assert.Equal(SameSiteMode.Strict, options.SameSite);
        Assert.Equal("/", options.Path);
        Assert.Null(options.Domain);
        Assert.Equal(expires, options.Expires);
        Assert.Equal(TimeSpan.FromHours(1), options.MaxAge);
        Assert.True(RelaySessionCookie.TryDecode(value, out var decoded));
        Assert.Equal(sessionId, decoded.SessionId);
        Assert.Equal(secret, decoded.Secret);
        Assert.False(RelaySessionCookie.TryDecode(value + "=", out _));
        Assert.False(RelaySessionCookie.TryDecode("a-known-room-key", out _));
    }

    [Fact]
    public void SecurityHeadersCoverIsolationCachingHstsAndSameOriginConnections()
    {
        var context = new DefaultHttpContext();

        RelaySecurityHeaders.Apply(context.Response, RelayHttpSurface.BrowserApplication, effectiveHttps: true);

        var csp = context.Response.Headers.ContentSecurityPolicy.ToString();
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("connect-src 'self'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.DoesNotContain("wss:", csp);
        Assert.Equal("text/html; charset=utf-8", context.Response.ContentType);
        Assert.Equal("DENY", context.Response.Headers.XFrameOptions.ToString());
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions.ToString());
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
        Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Contains("camera=()", context.Response.Headers["Permissions-Policy"].ToString());
        Assert.Contains("max-age=31536000", context.Response.Headers.StrictTransportSecurity.ToString());
    }

    [Fact]
    public void ForwardingIsAcceptedOnlyFromConfiguredPeerWithOneHttpsProto()
    {
        var untrusted = Context(IPAddress.Parse("198.51.100.10"));
        untrusted.Request.Headers["X-Forwarded-Proto"] = "https";
        var trusted = Context(IPAddress.Parse("10.0.0.2"));
        trusted.Request.Headers["X-Forwarded-Proto"] = "https";
        var ambiguous = Context(IPAddress.Parse("10.0.0.2"));
        ambiguous.Request.Headers["X-Forwarded-Proto"] = "https,http";
        var rfcForwarded = Context(IPAddress.Parse("10.0.0.2"));
        rfcForwarded.Request.Headers["Forwarded"] = "for=198.51.100.1;proto=https";
        var options = new RelayTransportPolicyOptions(
            requireHttps: true,
            allowLoopbackHttp: false,
            [IPAddress.Parse("::ffff:10.0.0.2")]);

        Assert.False(RelayTransportPolicy.Evaluate(untrusted, options).Allowed);
        var accepted = RelayTransportPolicy.Evaluate(trusted, options);
        Assert.True(accepted.Allowed);
        Assert.True(accepted.EffectiveHttps);
        Assert.False(RelayTransportPolicy.Evaluate(ambiguous, options).Allowed);
        Assert.False(RelayTransportPolicy.Evaluate(rfcForwarded, options).Allowed);
    }

    [Fact]
    public void BrowserCookieMutationRequiresCsrfFetchMetadataAndExactOrigin()
    {
        var valid = Context(IPAddress.Loopback);
        valid.Request.Scheme = "https";
        valid.Request.Host = new HostString("relay.example");
        valid.Request.Headers["Origin"] = "https://relay.example";
        valid.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        valid.Request.Headers[RelayCsrfProtector.HeaderName] = RelaySecurityTestFactory.Base64Url(new byte[32]);
        var crossSite = Context(IPAddress.Loopback);
        crossSite.Request.Scheme = "https";
        crossSite.Request.Host = new HostString("relay.example");
        crossSite.Request.Headers["Origin"] = "https://attacker.example";
        crossSite.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        crossSite.Request.Headers[RelayCsrfProtector.HeaderName] = RelaySecurityTestFactory.Base64Url(new byte[32]);

        Assert.True(RelayRequestGuard.ValidateBrowserMutation(valid.Request, cookieAuthenticated: true).Allowed);
        Assert.False(RelayRequestGuard.ValidateBrowserMutation(crossSite.Request, cookieAuthenticated: true).Allowed);
        Assert.True(RelayRequestGuard.ValidateBrowserMutation(crossSite.Request, cookieAuthenticated: false).Allowed);
    }

    [Fact]
    public async Task RequestAndProtocolLexicalBoundsRejectNullDepthStringsAndOversize()
    {
        var ordinary = new DefaultHttpContext();
        ordinary.Request.ContentType = "application/json; charset=utf-8";
        ordinary.Request.ContentLength = ProtocolBounds.MaxPayloadBytes + 1;
        var frame = new DefaultHttpContext();
        frame.Request.ContentType = "application/json";
        frame.Request.ContentLength = ProtocolBounds.MaxPayloadBytes + 1;

        Assert.Equal("request-too-large", RelayRequestGuard.ValidateJson<ClientHello>(ordinary.Request).Code);
        Assert.True(RelayRequestGuard.ValidateJson<OpaqueRelayFrame>(frame.Request).Allowed);
        await Assert.ThrowsAsync<InvalidDataException>(() => RelayRequestGuard.ReadProtocolJsonAsync<ClientHello>(
            new MemoryStream(new byte[RelaySecurityBounds.MaximumRequestBytes + 1])).AsTask());
        await Assert.ThrowsAnyAsync<JsonException>(() => RelayRequestGuard.ReadProtocolJsonAsync<ClientHello>(
            new MemoryStream("null"u8.ToArray())).AsTask());

        var oversizedString = Encoding.UTF8.GetBytes(
            "{\"supportedVersions\":{\"minimum\":{\"major\":2,\"minor\":0},\"maximum\":{\"major\":2,\"minor\":0}}," +
            "\"clientInstanceId\":\"" + new string('x', ProtocolBounds.MaxStringBytes + 1) + "\",\"optionalFeatures\":[]}");
        await Assert.ThrowsAnyAsync<JsonException>(() => RelayRequestGuard.ReadProtocolJsonAsync<ClientHello>(
            new MemoryStream(oversizedString)).AsTask());

        var nested = Encoding.UTF8.GetBytes(new string('[', ProtocolBounds.MaxJsonDepth + 1) +
            new string(']', ProtocolBounds.MaxJsonDepth + 1));
        await Assert.ThrowsAnyAsync<JsonException>(() => RelayRequestGuard.ReadProtocolJsonAsync<ClientHello>(
            new MemoryStream(nested)).AsTask());
    }

    [Fact]
    public void PublicErrorNeverReflectsExceptionOrTraceText()
    {
        var error = RelayPublicError.Create(
            "/var/lib/tarkov-group/devices.json: secret token abc failed",
            "trace-with-host-data");

        Assert.Equal("request-rejected", error.Code);
        Assert.True(Guid.TryParseExact(error.CorrelationId, "N", out _));
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/var/lib", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RateLimiterBoundsPartitionsAndRequiresOpaqueKeyedSourceHashes()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        var limiter = new RelayRateLimiter(
            clock,
            limit: 1,
            window: TimeSpan.FromMinutes(1),
            maximumPartitions: 2);
        var a = RelaySecurityTestFactory.SourceHash("198.51.100.1");
        var b = RelaySecurityTestFactory.SourceHash("198.51.100.2");
        var c = RelaySecurityTestFactory.SourceHash("198.51.100.3");

        Assert.True(limiter.TryConsume(a).Allowed);
        Assert.True(limiter.TryConsume(b).Allowed);
        Assert.False(limiter.TryConsume(c).Allowed);
        Assert.False(limiter.TryConsume(a).Allowed);
        Assert.Throws<ArgumentException>(() => limiter.TryConsume("198.51.100.4"));
        Assert.Equal(2, limiter.PartitionCount);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(limiter.TryConsume(c).Allowed);
    }

    [Fact]
    public async Task ConcurrencyGateRejectsWorkPastItsBound()
    {
        using var gate = new RelayConcurrencyGate(maximum: 1, wait: TimeSpan.FromMilliseconds(1));
        using var first = await gate.TryEnterAsync();

        var refused = await gate.TryEnterAsync();

        Assert.NotNull(first);
        Assert.Null(refused);
    }

    private static DefaultHttpContext Context(IPAddress remoteAddress)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remoteAddress;
        context.Request.Scheme = "http";
        return context;
    }
}
