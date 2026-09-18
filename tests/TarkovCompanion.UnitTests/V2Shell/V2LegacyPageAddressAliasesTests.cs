using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>
/// Covers the fix for the windows-smoke regression where <c>--page Scanner</c> crashed a V2 shell
/// launch (an unhandled <see cref="ArgumentException"/> before the database ever migrated),
/// because "Scanner" is a v1 page name and not a v2 address.
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
    [InlineData("Raid")]
    [InlineData("raid")]
    public void RaidResolvesToTheRaidRouteBecauseThatRouteNowHostsTheCockpitInstead(string requestedPage)
    {
        var registry = V2RouteRegistry.Default;

        var addressA = V2LegacyPageAddressAliases.TryResolve(registry, V2ShellVariants.A, requestedPage);
        var addressB = V2LegacyPageAddressAliases.TryResolve(registry, V2ShellVariants.B, requestedPage);

        Assert.Equal(V2ShellVariants.A.Addresses[V2Routes.Raid], addressA);
        Assert.Equal(V2ShellVariants.B.Addresses[V2Routes.Raid], addressB);
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
    [InlineData("Quests")]
    [InlineData("quests")]
    public void QuestsResolvesToThePlanRouteBecauseThatRouteNowHostsThePlanWorkspaceInstead(string requestedPage)
    {
        var registry = V2RouteRegistry.Default;

        var addressA = V2LegacyPageAddressAliases.TryResolve(registry, V2ShellVariants.A, requestedPage);
        var addressB = V2LegacyPageAddressAliases.TryResolve(registry, V2ShellVariants.B, requestedPage);

        Assert.Equal(V2ShellVariants.A.Addresses[V2Routes.Plan], addressA);
        Assert.Equal(V2ShellVariants.B.Addresses[V2Routes.Plan], addressB);
    }

    [Theory]
    [InlineData("Hideout")]
    [InlineData("hideout")]
    public void HideoutResolvesToTheHideoutRouteBecauseThatRouteNowHostsTheHideoutWorkspaceInstead(string requestedPage)
    {
        var registry = V2RouteRegistry.Default;

        var addressA = V2LegacyPageAddressAliases.TryResolve(registry, V2ShellVariants.A, requestedPage);
        var addressB = V2LegacyPageAddressAliases.TryResolve(registry, V2ShellVariants.B, requestedPage);

        Assert.Equal(V2ShellVariants.A.Addresses[V2Routes.Hideout], addressA);
        Assert.Equal(V2ShellVariants.B.Addresses[V2Routes.Hideout], addressB);
    }

    [Theory]
    [InlineData("Squad", "team")]
    [InlineData("Group", "team/group")]
    public void SquadAndGroupResolveToTheTeamWorkspaceBecauseThoseRoutesNowHostItInstead(string requestedPage, string expectedAddress)
    {
        var resolved = V2LegacyPageAddressAliases.TryResolve(V2RouteRegistry.Default, V2ShellVariants.B, requestedPage);

        Assert.Equal(expectedAddress, resolved);
    }

    [Theory]
    [InlineData("Ammo", "intel/ammo", "search/ammo")]
    [InlineData("Keys", "intel/keys", "search/keys")]
    [InlineData("flea", "intel/flea", "search/flea")]
    public void AmmoKeysAndFleaResolveToTheirIntelWorkspacesInBothVariants(string requestedPage, string addressA, string addressB)
    {
        Assert.Equal(addressA, V2LegacyPageAddressAliases.TryResolve(V2RouteRegistry.Default, V2ShellVariants.A, requestedPage));
        Assert.Equal(addressB, V2LegacyPageAddressAliases.TryResolve(V2RouteRegistry.Default, V2ShellVariants.B, requestedPage));
    }

    [Theory]
    [InlineData("Loadout", "plan/loadout", "prepare/loadout")]
    [InlineData("events", "plan/events", "prepare/events")]
    public void LoadoutAndEventsResolveToTheirPlanWorkspacesInBothVariants(string requestedPage, string addressA, string addressB)
    {
        Assert.Equal(addressA, V2LegacyPageAddressAliases.TryResolve(V2RouteRegistry.Default, V2ShellVariants.A, requestedPage));
        Assert.Equal(addressB, V2LegacyPageAddressAliases.TryResolve(V2RouteRegistry.Default, V2ShellVariants.B, requestedPage));
    }

    [Fact]
    public void AnUnknownPageNameResolvesToNothing()
    {
        var resolved = V2LegacyPageAddressAliases.TryResolve(V2RouteRegistry.Default, V2ShellVariants.B, "NotARealPage");

        Assert.Null(resolved);
    }
}
