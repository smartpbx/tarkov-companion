namespace TarkovCompanion.App.Services.V2.Shell;

/// <summary>
/// Resolves a v1 page name (as <c>--page</c>/<see cref="Diagnostics.AppCommandLine.StartPage"/>
/// names it) to the current variant's address for the v2 route that now covers it, so a launch or
/// verification job written against v1 page names does not become fatal purely because a V2 shell
/// became the default.
/// </summary>
/// <remarks>
/// Most v1 names still resolve because <see cref="V2RouteRegistry"/> records them as
/// <see cref="V2RouteDefinition.LegacyPage"/>, and <see cref="V2ShellAddress.Parse"/> already
/// matches segments case-insensitively. Seven do not, for the same reason: the route each named
/// v1 page used to pass through to now hosts a real V2 workspace instead, so there is no
/// registry entry left to read. "Scanner" now hosts <c>LootScanView</c> (package 1, #282);
/// "Raid" now hosts the raid cockpit (package 2, #286); "History" now hosts the Debrief workspace
/// (package 3, #291); "Quests" and "Hideout" now host the Plan workspace and its Hideout section
/// (package 10, #288); "Squad" and "Group" now both host the Team workspace (package 9, #289).
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
        };

    /// <summary>The current variant's address for the route this v1 page name now belongs to, or null.</summary>
    public static string? TryResolve(V2RouteRegistry registry, V2ShellVariantDefinition variant, string requestedPage)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPage);

        var matched = registry.Routes.FirstOrDefault(candidate =>
            string.Equals(candidate.LegacyPage, requestedPage, StringComparison.OrdinalIgnoreCase));
        var routeId = matched is { } route
            ? route.Id
            : HistoricalOverrides.TryGetValue(requestedPage, out var overridden) ? overridden : (V2RouteId?)null;

        return routeId is { } id && variant.Addresses.TryGetValue(id, out var address) ? address : null;
    }
}
