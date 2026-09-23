using System.Text.Json;
using TarkovCompanion.Core.Abstractions.V2;
using static TarkovCompanion.CompanionProtocol.Tests.ProtocolTestData;

namespace TarkovCompanion.CompanionProtocol.Tests;

/// <summary>#289/#290: a mark's chosen lifetime and route ride the upsert into the canonical mark.</summary>
public sealed class MarkExtrasTests
{
    [Fact]
    public void LifetimeAndRouteReachTheCanonicalMarkAndOldClientsSendNeither()
    {
        var route = Guid.NewGuid();
        var withExtras = Draft(MapMarkLifetime.FiveMinutes, route, 2);
        var json = JsonSerializer.Serialize(withExtras, CompanionProtocolJson.Options);
        var back = JsonSerializer.Deserialize<MapMarkDraft>(json, CompanionProtocolJson.Options)!;
        Assert.Equal(MapMarkLifetime.FiveMinutes, back.Lifetime);
        Assert.Equal(route, back.RouteId);
        Assert.Equal(2, back.RouteStep);

        // A draft from a page that predates both writes neither, so its fingerprint is unchanged.
        var plain = JsonSerializer.Serialize(Draft(null, null, null), CompanionProtocolJson.Options);
        Assert.DoesNotContain("lifetime", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("route", plain, StringComparison.Ordinal);

        var now = Now;
        var command = new UpsertMarkCommand(Command(7), new AggregateRevision(1), now, now.AddSeconds(30), Mark(7), 0, withExtras);
        var reduction = Apply(InitialState(), command, TabletContext(now));
        Assert.Equal(CommandDisposition.Applied, reduction.Acknowledgement.Disposition);
        var mark = Assert.Single(reduction.State.Marks.Marks);
        Assert.Equal(MapMarkLifetime.FiveMinutes, mark.Lifetime);
        Assert.Equal(route, mark.RouteId);
        Assert.Equal(2, mark.RouteStep);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 13)]
    public void ARoutePointNeedsItsRouteAndAStepFromOneToTwelve(bool hasRoute, int? step)
    {
        Assert.Throws<ArgumentException>(() => Draft(null, hasRoute ? Guid.NewGuid() : null, step));
    }

    private static MapMarkDraft Draft(MapMarkLifetime? lifetime, Guid? routeId, int? routeStep) => new(
        MapMarkKind.Waypoint,
        MapMarkScope.PairedDevice,
        new MapMarkState("customs", null, 10, 20, null, lifetime == MapMarkLifetime.FiveMinutes ? Now.AddMinutes(5) : null),
        CoordinateSpaceKind.World,
        "v1",
        null,
        "#22D3EE",
        lifetime,
        routeId,
        routeStep);
}
