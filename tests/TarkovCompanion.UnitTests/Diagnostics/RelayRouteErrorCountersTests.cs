using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using TarkovCompanion.GroupServer.Diagnostics;

namespace TarkovCompanion.UnitTests.Diagnostics;

/// <summary>
/// #562: production answered every map publish and read with a bare 500 for an hour, and nothing
/// an operator looks at without reading the journal said so. This is the count behind
/// <c>/health</c>'s new <c>errors</c> object.
/// </summary>
public sealed class RelayRouteErrorCountersTests
{
    [Fact]
    public void A2xxOr4xxLeavesTheCountAtZero()
    {
        var counters = new RelayRouteErrorCounters();
        counters.Observe(ContextFor("GET", "/v2/companion/relay/map", 200));
        counters.Observe(ContextFor("GET", "/v2/companion/relay/map", 404));
        counters.Observe(ContextFor("POST", "/v2/companion/relay/map", 403));

        Assert.Empty(counters.Snapshot());
    }

    [Fact]
    public void A5xxIsCountedByMethodAndRoute()
    {
        var counters = new RelayRouteErrorCounters();
        counters.Observe(ContextFor("POST", "/v2/companion/relay/map", 500));
        counters.Observe(ContextFor("POST", "/v2/companion/relay/map", 500));
        counters.Observe(ContextFor("GET", "/v2/companion/relay/map", 503));

        var snapshot = counters.Snapshot();
        Assert.Equal(2, snapshot["POST /v2/companion/relay/map"]);
        Assert.Equal(1, snapshot["GET /v2/companion/relay/map"]);
    }

    [Fact]
    public void ARouteTemplateCountsOnceRatherThanOncePerId()
    {
        // The route this test cares most about: a hundred different sessions failing on
        // /frames/{deliveryId}/ack must be one entry, not a hundred an operator has to add up.
        var counters = new RelayRouteErrorCounters();
        counters.Observe(ContextFor("POST", "/v2/companion/relay/frames/1/ack", 500, routeTemplate: "/v2/companion/relay/frames/{deliveryId:long}/ack"));
        counters.Observe(ContextFor("POST", "/v2/companion/relay/frames/2/ack", 500, routeTemplate: "/v2/companion/relay/frames/{deliveryId:long}/ack"));

        var snapshot = counters.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(2, snapshot["POST /v2/companion/relay/frames/{deliveryId:long}/ack"]);
    }

    [Fact]
    public void ARouteWithNoMatchedEndpointFallsBackToTheLiteralPath()
    {
        var counters = new RelayRouteErrorCounters();
        counters.Observe(ContextFor("GET", "/not-a-route", 500));

        Assert.Equal(1, counters.Snapshot()["GET /not-a-route"]);
    }

    private static HttpContext ContextFor(string method, string path, int statusCode, string? routeTemplate = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.StatusCode = statusCode;
        if (routeTemplate is not null)
        {
            context.SetEndpoint(new RouteEndpoint(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse(routeTemplate),
                order: 0,
                metadata: null,
                displayName: routeTemplate));
        }

        return context;
    }
}
