using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TarkovCompanion.GroupServer.Security;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

/// <summary>
/// [#819] The real forwarded-headers middleware, with the options the relay builds, feeding the
/// real attempt limiter: the chain Program.cs runs for every keyed request.
/// </summary>
public sealed class RelayForwardedHeadersTests
{
    private static readonly IPAddress Tunnel = IPAddress.Parse("10.0.0.2");

    [Fact]
    public async Task TwoPlayersBehindATrustedTunnelAreLimitedSeparately()
    {
        var options = RelayForwardedHeaders.CreateOptions("10.0.0.2");
        var limiter = new RelayAttemptLimiter();

        for (var i = 0; i < RelayAttemptLimiter.RefusalThreshold; i++)
        {
            var guesser = await CallerAsync(options, Tunnel, "198.51.100.7");
            limiter.Record(guesser, authorised: false);
        }

        var guessing = await CallerAsync(options, Tunnel, "198.51.100.7");
        var bystander = await CallerAsync(options, Tunnel, "203.0.113.20");

        Assert.Equal("198.51.100.7", guessing);
        Assert.Equal("203.0.113.20", bystander);
        Assert.True(limiter.IsRefused(guessing, out _));
        Assert.False(limiter.IsRefused(bystander, out _));
        Assert.Equal(TimeSpan.Zero, limiter.DelayFor(bystander));
    }

    [Fact]
    public async Task OnlyTheEntryTheTrustedTunnelAppendedIsBelieved()
    {
        var options = RelayForwardedHeaders.CreateOptions("10.0.0.0/24");

        // The client wrote the left entry; the tunnel appended the right one.
        var caller = await CallerAsync(options, Tunnel, "192.0.2.1, 198.51.100.7");

        Assert.Equal("198.51.100.7", caller);
    }

    [Fact]
    public async Task AnUntrustedCallersForwardedForIsIgnored()
    {
        var options = RelayForwardedHeaders.CreateOptions("10.0.0.2");
        var direct = IPAddress.Parse("203.0.113.9");
        var limiter = new RelayAttemptLimiter();

        // Twenty guesses, each claiming to be somebody new.
        for (var i = 0; i < RelayAttemptLimiter.RefusalThreshold; i++)
        {
            limiter.Record(await CallerAsync(options, direct, $"198.51.100.{i + 1}"), authorised: false);
        }

        var next = await CallerAsync(options, direct, "198.51.100.200");

        Assert.Equal("203.0.113.9", next);
        Assert.True(limiter.IsRefused(next, out _));
    }

    [Fact]
    public async Task UnsetTrustsLoopbackOnly()
    {
        var options = RelayForwardedHeaders.CreateOptions(null);

        Assert.Equal("198.51.100.7", await CallerAsync(options, IPAddress.Loopback, "198.51.100.7"));
        Assert.Equal(Tunnel.ToString(), await CallerAsync(options, Tunnel, "198.51.100.7"));
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("0.0.0.0/0")]
    [InlineData("0.0.0.0")]
    [InlineData("::/0")]
    public void AValueThatWouldTrustNobodyOrEverybodyStopsTheRelay(string configured)
    {
        Assert.Throws<FormatException>(() => RelayForwardedHeaders.CreateOptions(configured));
    }

    private static async Task<string> CallerAsync(ForwardedHeadersOptions options, IPAddress peer, string forwardedFor)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = peer;
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        string? seen = null;
        var middleware = new ForwardedHeadersMiddleware(
            ctx =>
            {
                seen = RelayAttemptLimiter.CallerOf(ctx.Connection.RemoteIpAddress);
                return Task.CompletedTask;
            },
            NullLoggerFactory.Instance,
            Options.Create(options));

        await middleware.Invoke(context);

        return seen!;
    }
}
