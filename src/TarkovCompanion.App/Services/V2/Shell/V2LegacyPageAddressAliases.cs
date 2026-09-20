namespace TarkovCompanion.App.Services.V2.Shell;

/// <summary>
/// Resolves a v1 page name (as <c>--page</c>/<see cref="Diagnostics.AppCommandLine.StartPage"/>
/// names it) to the current variant's address for the v2 route that now covers it, so a launch or
/// verification job written against v1 page names does not become fatal purely because a V2 shell
/// became the default.
/// </summary>
/// <remarks>
/// Every one of the fourteen is listed below, because none of them is a route's own name any
/// more: each V1 page's route hosts a real V2 workspace, and #294 removed the registry's
/// <c>LegacyPage</c> field along with the last route that named one. "Scanner" now hosts <c>LootScanView</c> (package 1, #282);
/// "Raid" now hosts the raid cockpit (package 2, #286); "History" now hosts the Debrief workspace
/// (package 3, #291); "Quests" and "Hideout" now host the Plan workspace and its Hideout section
/// (package 10, #288); "Squad" and "Group" now both host the Team workspace (package 9, #289);
/// "Items" now hosts the Intel workspace (package 17); "Ammo", "Keys" and "Flea" now host their own
/// Intel workspaces, and "Loadout" and "Events" their Plan workspaces (package 28); "Settings" now hosts
/// the V2 Setup workspace, whose address is spelled "setup" (#292).
/// The overrides below are those historical mappings, not a general escape hatch — a route that
/// never had a v1 page has nothing to alias, and gets none.
/// </remarks>
public static class V2LegacyPageAddressAliases
{
    private static readonly IReadOnlyDictionary<string, V2RouteId> HistoricalOverrides =
        new Dictionary<string, V2RouteId>(StringComparer.OrdinalIgnoreCase)
        {
            ["Scanner"] = V2Routes.Loot,
            ["Raid"] = V2Routes.Raid,
            ["History"] = V2Routes.Debrief,
            ["Quests"] = V2Routes.Plan,
            ["Hideout"] = V2Routes.Hideout,
            ["Squad"] = V2Routes.Team,
            ["Group"] = V2Routes.Group,
            ["Items"] = V2Routes.Items,
            ["Ammo"] = V2Routes.Ammo,
            ["Keys"] = V2Routes.Keys,
            ["Flea"] = V2Routes.Flea,
            ["Loadout"] = V2Routes.Loadout,
            ["Events"] = V2Routes.Events,
            // [#294] "Settings" was the one V1 page name with no entry, so `--page Settings`
            // was fatal under the default shell while the other thirteen resolved. It was not
            // spotted because the V1 name and the V2 address happen to differ only here: every
            // other page's address either matches its V1 name or had an override written for it.
            ["Settings"] = V2Routes.Setup,
        };

    /// <summary>The current variant's address for the route this v1 page name now belongs to, or null.</summary>
    public static string? TryResolve(V2RouteRegistry registry, V2ShellVariantDefinition variant, string requestedPage)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPage);

        // The registry is still taken, and still checked, so that a name in the table below
        // cannot outlive the route it points at: [route] throws on an unknown id.
        return HistoricalOverrides.TryGetValue(requestedPage, out var routeId) &&
            registry.TryGet(routeId, out _) &&
            variant.Addresses.TryGetValue(routeId, out var address)
                ? address
                : null;
    }
}
