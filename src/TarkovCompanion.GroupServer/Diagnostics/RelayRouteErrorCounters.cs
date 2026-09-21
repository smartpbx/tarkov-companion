using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace TarkovCompanion.GroupServer.Diagnostics;

/// <summary>
/// How many times each route has answered with a server error since this process started.
/// </summary>
/// <remarks>
/// #562: production relay 2.0.1303 answered every <c>POST</c>/<c>GET</c>
/// <c>/v2/companion/relay/map</c> with a bare 500 for an hour before anybody noticed — the journal
/// had it, but nothing an operator looks at without SSHing in did. Counts only, the same rule
/// every other number <c>/health</c> reports already follows: never who, never where, never the
/// exception. Keyed by the route's own template (<c>"GET /v2/companion/relay/map"</c>), not the
/// literal request path, so a route with an id in it (<c>/frames/{deliveryId:long}/ack</c>) counts
/// as one entry rather than one per delivery id ever sent.
/// </remarks>
public sealed class RelayRouteErrorCounters
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>Records this response if it was a server error; a no-op otherwise.</summary>
    public void Observe(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Response.StatusCode < 500)
        {
            return;
        }

        var route = RouteKey(context);
        _counts.AddOrUpdate(route, 1, static (_, count) => count + 1);
    }

    /// <summary>Route template (or literal path, for whatever answered before routing matched
    /// anything) mapped to how many 5xx responses it has given since start. Empty once none have.</summary>
    public IReadOnlyDictionary<string, int> Snapshot() =>
        _counts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static string RouteKey(HttpContext context)
    {
        var pattern = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        return $"{context.Request.Method} {pattern ?? context.Request.Path.Value ?? "/"}";
    }
}
