namespace TarkovCompanion.App.Services.V2.Shell;

/// <summary>How the primary destinations are laid out when there is room for either.</summary>
public enum V2NavigationStyle
{
    /// <summary>Variant A: a labelled rail beside the page.</summary>
    Rail = 1,

    /// <summary>Variant B: one row of destinations under the header.</summary>
    Row,
}

/// <summary>
/// Layout classes by effective content width, from docs/DESIGN_SYSTEM.md: narrow below 600 DIP,
/// compact to 899, standard to 1279, expanded from 1280.
/// </summary>
public enum V2WidthClass
{
    Narrow = 1,
    Compact,
    Standard,
    Expanded,
}

public static class V2ShellAdaptation
{
    /// <summary>
    /// The width class for an effective content width.
    /// </summary>
    /// <remarks>
    /// By width, never by device, and never a collapse to glyphs: below Standard a rail becomes a
    /// wrapping row that keeps every label, and Intel beside a page takes the page's place with
    /// Close Intel returning to it. Nothing is clipped and nothing silently disappears.
    /// </remarks>
    public static V2WidthClass Classify(double effectiveWidth) =>
        !double.IsFinite(effectiveWidth) || effectiveWidth >= 1280 ? V2WidthClass.Expanded
        : effectiveWidth >= 900 ? V2WidthClass.Standard
        : effectiveWidth >= 600 ? V2WidthClass.Compact
        : V2WidthClass.Narrow;

    /// <summary>Whether a variant's rail fits beside the page at this width.</summary>
    public static bool UsesRail(V2ShellVariantDefinition variant, V2WidthClass width) =>
        variant.Navigation == V2NavigationStyle.Rail && width >= V2WidthClass.Standard;

    /// <summary>Whether Intel can sit beside the page at this width rather than taking its place.</summary>
    public static bool IntelFitsBeside(V2WidthClass width) => width >= V2WidthClass.Standard;
}

/// <summary>Where item Intel opens.</summary>
public enum V2IntelPlacement
{
    /// <summary>Variant A: Intel is a workspace; details replace the page and offer Back.</summary>
    Workspace = 1,

    /// <summary>Variant B: Intel opens beside the current page, at the page's address plus its own.</summary>
    BesideCurrentPage,
}

/// <summary>Where item search lives.</summary>
public enum V2SearchPlacement
{
    /// <summary>Variant A: inside the Intel workspace; the header has no search.</summary>
    InsideItemsWorkspace = 1,

    /// <summary>Variant B: in the header on every page.</summary>
    Header,
}

/// <summary>Where Setup is reached from.</summary>
public enum V2SetupPlacement
{
    /// <summary>Variant A: its own labelled section at the end of the rail.</summary>
    LabelledRailSection = 1,

    /// <summary>Variant B: a header link, with readiness on Home.</summary>
    HeaderLink,
}

/// <summary>A labelled destination, in the order the variant offers it.</summary>
public sealed record V2DestinationDefinition(V2RouteId Route, string LabelKey);

/// <summary>
/// One recorded #265 presentation, as data.
/// </summary>
/// <remarks>
/// Everything #265 is comparing is a value here rather than a branch in a view: labels, their
/// order, the landing page, where Search, Intel, Stash scan and Setup sit, and the address each
/// route is given. The router, the views and the tests read these; none of them asks which
/// variant it is. When the sessions choose, the losing definition is deleted and nothing else has
/// to change shape.
///
/// A destination is normally the variant's label for a route-registry root. The small override
/// table records routes whose registry root is not a visible destination: item details in A, and
/// and Stash scan, which belongs to Plan in A and Prepare in B (it moved from Intel in #910).
/// Addresses remain free to be stable deep links rather than silently becoming the navigation
/// model; this matters for
/// <c>#/tablet</c>, which belongs to Team without being nested below <c>#/team</c>.
/// </remarks>
public sealed record V2ShellVariantDefinition
{
    public required V2ShellMode Mode { get; init; }

    public required string NameKey { get; init; }

    public required V2NavigationStyle Navigation { get; init; }

    /// <summary>The primary destinations, in order. Setup is placed separately.</summary>
    public required IReadOnlyList<V2DestinationDefinition> Destinations { get; init; }

    public required V2DestinationDefinition Setup { get; init; }

    public required V2SetupPlacement SetupPlacement { get; init; }

    public required V2SearchPlacement SearchPlacement { get; init; }

    public required V2IntelPlacement IntelPlacement { get; init; }

    /// <summary>Where a first launch opens.</summary>
    public required V2RouteId Landing { get; init; }

    /// <summary>
    /// Each addressable route's path. <c>{item}</c> is the item id. Under
    /// <see cref="V2IntelPlacement.BesideCurrentPage"/> the item route's path is a suffix appended to
    /// the page Intel opened beside.
    /// </summary>
    public required IReadOnlyDictionary<V2RouteId, string> Addresses { get; init; }

    /// <summary>Routes whose destination differs from their registry root.</summary>
    public IReadOnlyDictionary<V2RouteId, V2RouteId> DestinationOverrides { get; init; } =
        new Dictionary<V2RouteId, V2RouteId>();

    /// <summary>
    /// Old spellings of a page's address that still open it, so a saved link or a remembered
    /// address survives the page moving. Read, never written: the page's own address is formatted.
    /// </summary>
    public IReadOnlyDictionary<string, V2RouteId> AddressAliases { get; init; } =
        new Dictionary<string, V2RouteId>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Capabilities provided by chrome on every page rather than by a route.</summary>
    public required IReadOnlyList<V2CapabilityId> ChromeCapabilities { get; init; }

    public string Token => Mode.ToToken();

    /// <summary>Every capability this variant exposes, whether through a route or the chrome.</summary>
    public IReadOnlySet<V2CapabilityId> ExposedCapabilities(V2RouteRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var exposed = new HashSet<V2CapabilityId>(ChromeCapabilities);
        foreach (var route in Addresses.Keys)
        {
            exposed.UnionWith(registry[route].Capabilities);
        }

        return exposed;
    }

    /// <summary>The label a route is given when it is one of this variant's destinations.</summary>
    public string? DestinationLabelKey(V2RouteId route) =>
        route == Setup.Route
            ? Setup.LabelKey
            : Destinations.FirstOrDefault(destination => destination.Route == route)?.LabelKey;

    /// <summary>The visible destination that owns a route, independent of its address spelling.</summary>
    public V2RouteId? DestinationOf(V2RouteId route, V2RouteRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!Addresses.ContainsKey(route))
        {
            return null;
        }

        if (DestinationOverrides.TryGetValue(route, out var overridden))
        {
            return overridden;
        }

        var root = registry.RootOf(route);
        return root == Setup.Route || Destinations.Any(destination => destination.Route == root)
            ? root
            : null;
    }
}

/// <summary>The two recorded presentations from docs/design/v2/validation/navigation-variants.md.</summary>
public static class V2ShellVariants
{
    private static readonly IReadOnlyList<V2CapabilityId> SharedChrome =
        [V2Capabilities.Capture, V2Capabilities.Health, V2Capabilities.Commands, V2Capabilities.Continue];

    /// <summary>Variant A: a workspace rail, Setup &amp; Admin under its own heading, Capture in the header.</summary>
    public static V2ShellVariantDefinition A { get; } = new()
    {
        Mode = V2ShellMode.VariantA,
        NameKey = "V2.Shell.Variant.A",
        Navigation = V2NavigationStyle.Rail,
        Destinations =
        [
            new(V2Routes.Raid, "V2.Shell.Label.Raid"),
            new(V2Routes.Items, "V2.Shell.Label.Intel"),
            new(V2Routes.Plan, "V2.Shell.Label.Plan"),
            new(V2Routes.Team, "V2.Shell.Label.Team"),
            new(V2Routes.Debrief, "V2.Shell.Label.Debrief"),
        ],
        Setup = new(V2Routes.Setup, "V2.Shell.Label.SetupAdmin"),
        SetupPlacement = V2SetupPlacement.LabelledRailSection,
        SearchPlacement = V2SearchPlacement.InsideItemsWorkspace,
        IntelPlacement = V2IntelPlacement.Workspace,
        Landing = V2Routes.Setup,
        Addresses = new Dictionary<V2RouteId, string>
        {
            [V2Routes.Raid] = "raid",
            [V2Routes.Loot] = "raid/loot",
            [V2Routes.Items] = "intel",
            [V2Routes.Ammo] = "intel/ammo",
            [V2Routes.Keys] = "intel/keys",
            [V2Routes.Flea] = "intel/flea",
            [V2Routes.Crafts] = "intel/crafts",
            [V2Routes.Item] = "intel/item/{item}",
            // [#902 P9] The stash scan feeds the Keep list and Loadout, which are Plan's; it sat
            // beside the Ammo, Keys and Flea reference pages. Variant B already had it here.
            [V2Routes.Stash] = "plan/stash",
            [V2Routes.Plan] = "plan",
            [V2Routes.Hideout] = "plan/hideout",
            [V2Routes.Keep] = "plan/keep",
            [V2Routes.Loadout] = "plan/loadout",
            [V2Routes.Events] = "plan/events",
            [V2Routes.Team] = "team",
            [V2Routes.Group] = "team/group",
            [V2Routes.Tablet] = "tablet",
            [V2Routes.Debrief] = "debrief",
            [V2Routes.Setup] = "setup",
        },
        DestinationOverrides = new Dictionary<V2RouteId, V2RouteId>
        {
            [V2Routes.Item] = V2Routes.Items,
            [V2Routes.Stash] = V2Routes.Plan,
        },
        AddressAliases = new Dictionary<string, V2RouteId>(StringComparer.OrdinalIgnoreCase)
        {
            ["intel/stash"] = V2Routes.Stash,
        },
        ChromeCapabilities = SharedChrome,
    };

    /// <summary>Variant B: a workflow hub, Search and Capture in the header, Intel beside the page.</summary>
    public static V2ShellVariantDefinition B { get; } = new()
    {
        Mode = V2ShellMode.VariantB,
        NameKey = "V2.Shell.Variant.B",
        Navigation = V2NavigationStyle.Row,
        Destinations =
        [
            new(V2Routes.Home, "V2.Shell.Label.Home"),
            new(V2Routes.Raid, "V2.Shell.Label.Raid"),
            new(V2Routes.Plan, "V2.Shell.Label.Prepare"),
            new(V2Routes.Team, "V2.Shell.Label.Team"),
            new(V2Routes.Debrief, "V2.Shell.Label.History"),
        ],
        Setup = new(V2Routes.Setup, "V2.Shell.Label.Setup"),
        SetupPlacement = V2SetupPlacement.HeaderLink,
        SearchPlacement = V2SearchPlacement.Header,
        IntelPlacement = V2IntelPlacement.BesideCurrentPage,
        Landing = V2Routes.Home,
        Addresses = new Dictionary<V2RouteId, string>
        {
            [V2Routes.Home] = "home",
            [V2Routes.Raid] = "raid",
            [V2Routes.Loot] = "raid/loot",
            [V2Routes.Items] = "search",
            [V2Routes.Ammo] = "search/ammo",
            [V2Routes.Keys] = "search/keys",
            [V2Routes.Flea] = "search/flea",
            [V2Routes.Crafts] = "search/crafts",
            [V2Routes.Item] = "intel/{item}",
            [V2Routes.Stash] = "prepare/stash",
            [V2Routes.Plan] = "prepare",
            [V2Routes.Hideout] = "prepare/hideout",
            [V2Routes.Keep] = "prepare/keep",
            [V2Routes.Loadout] = "prepare/loadout",
            [V2Routes.Events] = "prepare/events",
            [V2Routes.Team] = "team",
            [V2Routes.Group] = "team/group",
            [V2Routes.Tablet] = "tablet",
            [V2Routes.Debrief] = "history",
            [V2Routes.Setup] = "setup",
        },
        DestinationOverrides = new Dictionary<V2RouteId, V2RouteId>
        {
            [V2Routes.Stash] = V2Routes.Plan,
        },
        ChromeCapabilities = SharedChrome,
    };

    public static V2ShellVariantDefinition For(V2ShellMode mode) => mode switch
    {
        V2ShellMode.VariantA => A,
        V2ShellMode.VariantB => B,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Only the V2 presentations have a variant definition."),
    };
}
