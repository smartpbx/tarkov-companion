using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>
/// Covers the fix for the windows-smoke regression where <c>--page Scanner</c> crashed the V2
/// default launch (an unhandled <see cref="ArgumentException"/> before the database ever
/// migrated), because "Scanner" is a v1 page name and not a v2 address.
/// </summary>
public sealed class V2LegacyPageAddressAliasesTests
{
    [Theory]
    [InlineData("Scanner")]
    [InlineData("scanner")]
    public void ScannerResolvesToTheLootRouteBecauseThatRouteNowHostsLootScanInstead(string requestedPage)
    {
        var registry = V2RouteRegistry.Default;

        var addressA = V2LegacyPageAddressAliases.TryResolve(registry, V2ShellVariants.A, requestedPage);
        var addressB = V2LegacyPageAddressAliases.TryResolve(registry, V2ShellVariants.B, requestedPage);

        Assert.Equal(V2ShellVariants.A.Addresses[V2Routes.Loot], addressA);
        Assert.Equal(V2ShellVariants.B.Addresses[V2Routes.Loot], addressB);
    }

    [Theory]
    [InlineData("History")]
    [InlineData("history")]
    public void HistoryResolvesToTheDebriefRouteBecauseThatRouteNowHostsTheDebriefWorkspaceInstead(string requestedPage)
    {
        var registry = V2RouteRegistry.Default;

        var addressA = V2LegacyPageAddressAliases.TryResolve(registry, V2ShellVariants.A, requestedPage);
        var addressB = V2LegacyPageAddressAliases.TryResolve(registry, V2ShellVariants.B, requestedPage);

        Assert.Equal(V2ShellVariants.A.Addresses[V2Routes.Debrief], addressA);
        Assert.Equal(V2ShellVariants.B.Addresses[V2Routes.Debrief], addressB);
    }

    [Theory]
    [InlineData("Raid", "raid")]
    [InlineData("Squad", "team")]
    [InlineData("Group", "team/group")]
    [InlineData("Quests", "prepare")]
    public void EveryStillHostedV1PageNameResolvesThroughTheRegistryForVariantB(string requestedPage, string expectedAddress)
    {
        var resolved = V2LegacyPageAddressAliases.TryResolve(V2RouteRegistry.Default, V2ShellVariants.B, requestedPage);

        Assert.Equal(expectedAddress, resolved);
    }

    [Fact]
    public void AnUnknownPageNameResolvesToNothing()
    {
        var resolved = V2LegacyPageAddressAliases.TryResolve(V2RouteRegistry.Default, V2ShellVariants.B, "NotARealPage");

        Assert.Null(resolved);
    }
}
