using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace TarkovCompanion.GroupServer.Diagnostics;

/// <summary>Counts an operator may see and a stranger may not.</summary>
public readonly record struct RelayLoad(int Rooms, int Members, int Held, int HeldTabletReads);

/// <summary>What the readiness answer says about the relay that is not a probe result.</summary>
public sealed record RelayOperatorContext(
    int Protocol,
    string Version,
    string? Commit,
    DateTimeOffset StartedUtc,
    bool TransportEnforced,
    Func<RelayLoad> Load);

public static class RelayReadinessRoutes
{
    /// <summary>
    /// Maps <c>GET /admin/readiness</c>: the operator's view of whether this relay is fit to serve,
    /// and the counts the public <c>/health</c> route no longer carries.
    /// </summary>
    /// <remarks>
    /// Behind the operator key, like everything else under <c>/admin</c>. <c>/health</c> is public
    /// and answers what a proxy and an updater need (status, protocol, build); how many rooms and
    /// members a relay holds, how close it is to its limits and whether its updater has gone quiet
    /// are things an operator wants and a stranger learns nothing good from. There are no paths, room
    /// identifiers, keys, names or positions anywhere in this answer.
    /// </remarks>
    /// <param name="authorise">Whether a request is the operator's; the operator key in production.</param>
    public static IEndpointRouteBuilder MapRelayReadiness(
        this IEndpointRouteBuilder app,
        RelayReadinessProbes probes,
        RelayOperatorContext context,
        Func<HttpRequest, bool>? authorise = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(context);
        authorise ??= RelayAdmin.IsAuthorised;
        app.MapGet("/admin/readiness", IResult (HttpRequest request) =>
        {
            if (!authorise(request))
            {
                return Results.Unauthorized();
            }

            var report = probes.Capture();
            var load = context.Load();
            return Results.Ok(new
            {
                status = report.Result.Status == RelayReadinessStatus.Ready ? "ready" : "degraded",
                observedUtc = report.Result.ObservedUtc,
                observationWindowStartedUtc = report.Result.ObservationWindowStartedUtc,
                checks = report.Result.Checks.Select(check => new { check = Name(check.Check), ready = check.IsReady }),
                signals = report.Result.Signals.Select(signal => new { signal = Name(signal.Signal), elevated = signal.IsElevated }),
                relay = new
                {
                    protocol = context.Protocol,
                    version = context.Version,
                    commit = context.Commit,
                    startedUtc = context.StartedUtc,
                    transportPolicy = context.TransportEnforced ? "enforced" : "not-enforced",
                    storage = report.StorageIsPersistent ? "persistent" : "memory",
                    updater = report.UpdaterConfigured ? "reporting" : "not-configured",
                    freeDiskMegabytes = report.Probe.FreeDiskMegabytes,
                    rooms = load.Rooms,
                    members = load.Members,
                    held = load.Held,
                    heldTabletReads = load.HeldTabletReads,
                },
            });
        });
        return app;
    }

    private static string Name<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
