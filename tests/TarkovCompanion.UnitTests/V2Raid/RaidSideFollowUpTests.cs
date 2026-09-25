using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Strategy.Prior;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>#875 follow-ups: the traffic basis line's count, and routes when the side is unknown.</summary>
public sealed class RaidSideFollowUpTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A PMC raid on Customs: the card header counts 14 of the player's own ways out, and the
    /// modelled-traffic line read "29 ways out (transits included; co-op hidden)" under it as if
    /// those were the player's too. The model does weigh every side's exits, so the line says so.
    /// </summary>
    [Theory]
    [InlineData(29, "29 ways out, both sides")]
    [InlineData(1, "1 way out, both sides")]
    public void TheTrafficBasisLineSaysItCountsBothSides(int extracts, string expected)
    {
        var label = RaidCockpitViewModel.CoverageLabel(new TrafficPriorBasis(5, extracts, 0, 0, 0, 0, 0, null, NowUtc), 5);

        Assert.Contains(expected, label, StringComparison.Ordinal);
        Assert.DoesNotContain("transits included", label, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MapFeatureFaction.Pmc, new[] { "ZB-1011", "RUAF Roadblock", "Unmarked Gap" })]
    [InlineData(MapFeatureFaction.Scav, new[] { "Old Gas Station", "RUAF Roadblock", "Unmarked Gap" })]
    public void AKnownSideRoutesToItsOwnAndSharedExits(MapFeatureFaction side, string[] expected)
    {
        var (targets, assumesPmc) = RaidExtractSide.RouteTargets(Exits(), side);

        Assert.Equal(expected, targets.Select(element => element.Label));
        Assert.False(assumesPmc);
    }

    /// <summary>
    /// The side never read: routes used to drop the Scav exits and quietly plan a PMC raid. Only
    /// exits both sides can use are routed to now.
    /// </summary>
    [Fact]
    public void AnUnknownSideRoutesOnlyToExitsBothSidesCanUse()
    {
        var (targets, assumesPmc) = RaidExtractSide.RouteTargets(Exits(), MapFeatureFaction.Unknown);

        Assert.Equal(["RUAF Roadblock", "Unmarked Gap"], targets.Select(element => element.Label));
        Assert.False(assumesPmc);
    }

    [Fact]
    public void AnUnknownSideOnAMapWithNoSharedExitFallsBackToPmcAndSaysSo()
    {
        MapOverlayElement[] oneSided = [Exit("ZB-1011", MapFeatureFaction.Pmc), Exit("Old Gas Station", MapFeatureFaction.Scav)];

        var (targets, assumesPmc) = RaidExtractSide.RouteTargets(oneSided, MapFeatureFaction.Unknown);

        Assert.Equal(["ZB-1011"], targets.Select(element => element.Label));
        Assert.True(assumesPmc);
    }

    private static MapOverlayElement[] Exits() =>
    [
        Exit("ZB-1011", MapFeatureFaction.Pmc),
        Exit("Old Gas Station", MapFeatureFaction.Scav),
        Exit("RUAF Roadblock", MapFeatureFaction.Shared),
        Exit("Unmarked Gap", MapFeatureFaction.Unknown),
    ];

    private static MapOverlayElement Exit(string label, MapFeatureFaction faction) =>
        new(MapOverlayKind.Extracts, new(1, 1), label) { Faction = faction };
}
