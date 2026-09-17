using System.Diagnostics.CodeAnalysis;

namespace TarkovCompanion.App.Services.V2.Shell;

/// <summary>What a route draws in the content area.</summary>
public enum V2RouteContent
{
    /// <summary>A V1 page, hosted unchanged until its feature migrates (#294 removes these).</summary>
    LegacyPage = 1,

    /// <summary>The readiness checklist, with Continue where the route asks for it.</summary>
    Readiness,

    /// <summary>A state presenter: the capability exists, and says honestly what it has.</summary>
    StatePresenter,

    /// <summary>One item's facts, with their source and age.</summary>
    ItemIntel,

    /// <summary>A self-contained V2 workspace over a merged backend (stash scan, debrief).</summary>
    Workspace,

    /// <summary>The raid cockpit: the V2 map renderer plus its timer/extract panel (package 2).</summary>
    RaidCockpit,

    /// <summary>The Loot Scan review surface: one frozen capture's take/swap/leave/review decisions.</summary>
    LootScan,

    /// <summary>The native V2 Setup page (#292): sectioned settings over the existing view models.</summary>
    SetupWorkspace,

    /// <summary>The Intel workspace (package 17): search results, the selected item, its prices and needs.</summary>
    IntelWorkspace,
}

/// <summary>
/// One route: its capabilities, what it draws, and the V1 page it adapts, if any.
/// </summary>
/// <param name="Id">The stable identity; never shown.</param>
/// <param name="Capabilities">What a player can do here, whichever variant is showing it.</param>
/// <param name="Content">What the content area draws.</param>
/// <param name="HeadingKey">The heading when no variant label applies (sections, panels).</param>
/// <param name="LegacyPage">The V1 destination name this route hosts, exactly as V1 navigation names it.</param>
/// <param name="Parent">The route this one is a section of, for local section tabs.</param>
/// <param name="TakesItem">Whether the route is addressed with an item id.</param>
/// <param name="ShowsReadiness">Whether the readiness checklist leads the page.</param>
/// <param name="ShowsContinue">Whether Continue (recent places) leads the page.</param>
/// <param name="UsesGameData">Whether the page's usefulness depends on the synced game data.</param>
public sealed record V2RouteDefinition(
    V2RouteId Id,
    IReadOnlyList<V2CapabilityId> Capabilities,
    V2RouteContent Content,
    string HeadingKey,
    string? LegacyPage = null,
    V2RouteId? Parent = null,
    bool TakesItem = false,
    bool ShowsReadiness = false,
    bool ShowsContinue = false,
    bool UsesGameData = false);

/// <summary>
/// The one route table both variants are built from.
/// </summary>
/// <remarks>
/// A second table per variant would let the variants drift apart in what they can do while
/// looking as though they only differ in where things are, which is exactly the confound #265's
/// comparison forbids: "the only intended differences are the destinations, their labels, and
/// where results and Intel open".
/// </remarks>
public sealed class V2RouteRegistry
{
    private readonly Dictionary<V2RouteId, V2RouteDefinition> _routes;

    public V2RouteRegistry(IReadOnlyList<V2RouteDefinition> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        _routes = new Dictionary<V2RouteId, V2RouteDefinition>();
        foreach (var route in routes)
        {
            if (!_routes.TryAdd(route.Id, route))
            {
                throw new ArgumentException($"Route '{route.Id}' is defined twice.", nameof(routes));
            }
        }

        foreach (var route in routes)
        {
            if (route.Parent is { } parent && !_routes.ContainsKey(parent))
            {
                throw new ArgumentException($"Route '{route.Id}' names an unknown parent '{parent}'.", nameof(routes));
            }

            if ((route.Content == V2RouteContent.LegacyPage) != (route.LegacyPage is not null))
            {
                throw new ArgumentException($"Route '{route.Id}' must name a V1 page exactly when it hosts one.", nameof(routes));
            }
        }

        Routes = routes;
    }

    /// <summary>The routes in declaration order, which is the order sections are offered in.</summary>
    public IReadOnlyList<V2RouteDefinition> Routes { get; }

    public static V2RouteRegistry Default { get; } = new(
    [
        new(V2Routes.Home, [V2Capabilities.Readiness, V2Capabilities.Continue], V2RouteContent.Readiness, "V2.Shell.Route.Home",
            ShowsReadiness: true, ShowsContinue: true),
        // The raid cockpit hosts the V2 map renderer directly (package 2); it no longer passes
        // through to the V1 "Raid" page, which remains reachable from V1 navigation only.
        new(V2Routes.Raid, [V2Capabilities.Raid], V2RouteContent.RaidCockpit, "V2.Shell.Route.Raid",
            UsesGameData: true),
        new(V2Routes.Loot, [V2Capabilities.LootDecision], V2RouteContent.LootScan, "V2.Shell.Route.Loot",
            Parent: V2Routes.Raid, UsesGameData: true),
        // V2 rough package 17: Intel is a native workspace (results, item, prices) rather than
        // the V1 Items page; the item route draws the same workspace with that item selected.
        new(V2Routes.Items, [V2Capabilities.ItemSearch], V2RouteContent.IntelWorkspace, "V2.Shell.Route.Items",
            UsesGameData: true),
        new(V2Routes.Ammo, [V2Capabilities.ReferenceData], V2RouteContent.LegacyPage, "V2.Shell.Route.Ammo", "Ammo",
            Parent: V2Routes.Items, UsesGameData: true),
        new(V2Routes.Keys, [V2Capabilities.ReferenceData], V2RouteContent.LegacyPage, "V2.Shell.Route.Keys", "Keys",
            Parent: V2Routes.Items, UsesGameData: true),
        new(V2Routes.Flea, [V2Capabilities.ReferenceData], V2RouteContent.LegacyPage, "V2.Shell.Route.Flea", "Flea",
            Parent: V2Routes.Items, UsesGameData: true),
        new(V2Routes.Item, [V2Capabilities.ItemIntel], V2RouteContent.ItemIntel, "V2.Shell.Route.Item",
            TakesItem: true, UsesGameData: true),
        new(V2Routes.Stash, [V2Capabilities.StashScan], V2RouteContent.Workspace, "V2.Shell.Route.Stash"),
        // V2 rough — package 10 (Plan workspace + Hideout section). Refs #288 #307.
        new(V2Routes.Plan, [V2Capabilities.Plan], V2RouteContent.Workspace, "V2.Shell.Route.Quests",
            UsesGameData: true),
        new(V2Routes.Hideout, [V2Capabilities.Plan], V2RouteContent.Workspace, "V2.Shell.Route.Hideout",
            Parent: V2Routes.Plan, UsesGameData: true),
        new(V2Routes.Loadout, [V2Capabilities.Plan], V2RouteContent.LegacyPage, "V2.Shell.Route.Loadout", "Loadout",
            Parent: V2Routes.Plan, UsesGameData: true),
        new(V2Routes.Events, [V2Capabilities.Plan], V2RouteContent.LegacyPage, "V2.Shell.Route.Events", "Events",
            Parent: V2Routes.Plan),
        // v2r-team (package 9, wave 2): one native V2 Team workspace (presence, marks, group
        // sharing, paired devices) replaces the Squad/Group passthroughs and the Tablet state
        // presenter. Group and Tablet stay as separate addresses/section tabs but point at the
        // same workspace rather than their own content.
        new(V2Routes.Team, [V2Capabilities.Team], V2RouteContent.Workspace, "V2.Shell.Route.Squad"),
        new(V2Routes.Group, [V2Capabilities.Team], V2RouteContent.Workspace, "V2.Shell.Route.Group",
            Parent: V2Routes.Team),
        new(V2Routes.Tablet, [V2Capabilities.TabletPreview], V2RouteContent.Workspace, "V2.Shell.Route.Tablet",
            Parent: V2Routes.Team),
        new(V2Routes.Debrief, [V2Capabilities.Debrief], V2RouteContent.Workspace, "V2.Shell.Route.History"),
        new(V2Routes.Setup, [V2Capabilities.Setup, V2Capabilities.Readiness], V2RouteContent.SetupWorkspace, "V2.Shell.Route.Settings",
            ShowsReadiness: true),
    ]);

    public V2RouteDefinition this[V2RouteId id] => _routes.TryGetValue(id, out var route)
        ? route
        : throw new KeyNotFoundException($"No route is registered as '{id}'.");

    public bool TryGet(V2RouteId id, [NotNullWhen(true)] out V2RouteDefinition? route) => _routes.TryGetValue(id, out route);

    /// <summary>The route itself when it has no parent, otherwise the top of its section chain.</summary>
    public V2RouteId RootOf(V2RouteId id)
    {
        var current = this[id];
        while (current.Parent is { } parent)
        {
            current = this[parent];
        }

        return current.Id;
    }

    /// <summary>A root route followed by its sections, in declaration order.</summary>
    public IReadOnlyList<V2RouteDefinition> SectionGroup(V2RouteId id)
    {
        var root = RootOf(id);
        return Routes.Where(route => route.Id == root || route.Parent == root).ToArray();
    }

    /// <summary>The local, directly clickable routes around the current page.</summary>
    /// <remarks>
    /// Registry children form the ordinary group. A variant may attach a standalone route to a
    /// destination (Stash scan), but an address segment is never used to guess that ownership.
    /// </remarks>
    public IReadOnlyList<V2RouteDefinition> VisibleSections(
        V2ShellVariantDefinition variant,
        V2RouteId current)
    {
        ArgumentNullException.ThrowIfNull(variant);
        _ = this[current];
        var destination = variant.DestinationOf(current, this);
        var root = destination ?? RootOf(current);
        var definitions = SectionGroup(root)
            .Where(route => !route.TakesItem && variant.Addresses.ContainsKey(route.Id))
            .ToList();

        if (destination is { } owner)
        {
            definitions.AddRange(Routes.Where(route =>
                !route.TakesItem &&
                variant.Addresses.ContainsKey(route.Id) &&
                !definitions.Any(existing => existing.Id == route.Id) &&
                variant.DestinationOf(route.Id, this) == owner));
        }

        return definitions;
    }
}
