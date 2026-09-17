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
/// matches segments case-insensitively. "Scanner" and "History" do not: their routes (Loot,
/// Debrief) now host real V2 workspaces instead of the legacy Scanner/History pages, so there is
/// no registry entry left to read. The overrides below are those historical mappings, not a
/// general escape hatch — a route that never had a v1 page has nothing to alias, and gets none.
/// </remarks>
public static class V2LegacyPageAddressAliases
{
    private static readonly IReadOnlyDictionary<string, V2RouteId> HistoricalOverrides =
        new Dictionary<string, V2RouteId>(StringComparer.OrdinalIgnoreCase)
        {
            ["Scanner"] = V2Routes.Loot,
            ["History"] = V2Routes.Debrief,
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
