using System.Net;
using Microsoft.AspNetCore.Http;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.GroupServer.Security;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

public sealed class RelayHttpSecurityTests
{
    [Fact]
    public void BrowserSessionCookieIsHostOnlySecureHttpOnlyAndStrict()
    {
        var options = RelaySessionCookie.Create(RelaySecurityTestFactory.Now.AddHours(1));
        var sessionId = new DeviceSessionId(Guid.NewGuid());
        var secret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var value = RelaySessionCookie.Encode(sessionId, secret);

        Assert.StartsWith("__Host-", RelaySessionCookie.Name, StringComparison.Ordinal);
        Assert.True(options.Secure);
        Assert.True(options.HttpOnly);
        Assert.Equal(SameSiteMode.Strict, options.SameSite);
        Assert.Equal("/", options.Path);
        Assert.Null(options.Domain);
        Assert.True(RelaySessionCookie.TryDecode(value, out var decoded));
        Assert.Equal(sessionId, decoded.SessionId);
        Assert.Equal(secret, decoded.Secret);
        Assert.False(RelaySessionCookie.TryDecode("a-known-room-key", out _));
    }

    [Fact]
    public void SecurityHeadersCoverBrowserIsolationCachingAndHsts()
    {
        var context = new DefaultHttpContext();

        RelaySecurityHeaders.Apply(context.Response, RelayHttpSurface.BrowserApplication, effectiveHttps: true);

        Assert.Contains("frame-ancestors 'none'", context.Response.Headers.ContentSecurityPolicy.ToString());
        Assert.DoesNotContain("unsafe-inline", context.Response.Headers.ContentSecurityPolicy.ToString());
        Assert.Equal("text/html; charset=utf-8", context.Response.ContentType);
        Assert.Equal("DENY", context.Response.Headers.XFrameOptions.ToString());
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions.ToString());
        Assert.Equal("no-referrer", context.Response.Headers.ReferrerPolicy.ToString());
        Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Contains("camera=()", context.Response.Headers["Permissions-Policy"].ToString());
        Assert.Contains("max-age=31536000", context.Response.Headers.StrictTransportSecurity.ToString());
    }

    [Fact]
    public void UntrustedForwardedHeadersAreRejectedAndTrustedHttpsIsAccepted()
    {
        var untrusted = Context(IPAddress.Parse("198.51.100.10"));
        untrusted.Request.Headers["X-Forwarded-Proto"] = "https";
        var trusted = Context(IPAddress.Parse("10.0.0.2"));
        trusted.Request.Headers["X-Forwarded-Proto"] = "https";
        var options = new RelayTransportPolicyOptions(
            requireHttps: true,
            allowLoopbackHttp: false,
            [IPAddress.Parse("10.0.0.2")]);

        Assert.False(RelayTransportPolicy.Evaluate(untrusted, options).Allowed);
        var accepted = RelayTransportPolicy.Evaluate(trusted, options);
        Assert.True(accepted.Allowed);
        Assert.True(accepted.EffectiveHttps);
    }

    [Fact]
    public void CleartextIsRejectedExceptExplicitLoopbackDevelopment()
    {
        var remote = Context(IPAddress.Parse("198.51.100.20"));
        var loopback = Context(IPAddress.Loopback);
        var options = new RelayTransportPolicyOptions(requireHttps: true, allowLoopbackHttp: true);

        Assert.False(RelayTransportPolicy.Evaluate(remote, options).Allowed);
        Assert.True(RelayTransportPolicy.Evaluate(loopback, options).Allowed);
    }

    [Fact]
    public async Task RequestContentTypeDeclaredLengthAndStreamingLengthAreBounded()
    {
        var accepted = new DefaultHttpContext();
        accepted.Request.ContentType = "application/json; charset=utf-8";
        accepted.Request.ContentLength = 2;
        var wrongType = new DefaultHttpContext();
        wrongType.Request.ContentType = "text/plain";
        var declaredLarge = new DefaultHttpContext();
        declaredLarge.Request.ContentType = "application/json";
        declaredLarge.Request.ContentLength = RelaySecurityBounds.MaximumRequestBytes + 1;

        Assert.True(RelayRequestGuard.ValidateJson(accepted.Request).Allowed);
        Assert.Equal("unsupported-content-type", RelayRequestGuard.ValidateJson(wrongType.Request).Code);
        Assert.Equal("request-too-large", RelayRequestGuard.ValidateJson(declaredLarge.Request).Code);
        await Assert.ThrowsAsync<InvalidDataException>(() => RelayRequestGuard.ReadProtocolJsonAsync<ClientHello>(
            new MemoryStream(new byte[RelaySecurityBounds.MaximumRequestBytes + 1])).AsTask());
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
    public void RateLimiterBoundsPartitionsAsWellAsRequests()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        var limiter = new RelayRateLimiter(
            clock,
            limit: 1,
            window: TimeSpan.FromMinutes(1),
            maximumPartitions: 2);
        var a = RelayRateLimiter.HashSource("a");
        var b = RelayRateLimiter.HashSource("b");
        var c = RelayRateLimiter.HashSource("c");

        Assert.True(limiter.TryConsume(a).Allowed);
        Assert.True(limiter.TryConsume(b).Allowed);
        Assert.False(limiter.TryConsume(c).Allowed);
        Assert.False(limiter.TryConsume(a).Allowed);
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
