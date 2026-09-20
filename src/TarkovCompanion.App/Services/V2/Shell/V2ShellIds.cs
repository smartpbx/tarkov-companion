namespace TarkovCompanion.App.Services.V2.Shell;

/// <summary>
/// Something a player can do, independent of what it is called and where it sits.
/// </summary>
/// <remarks>
/// #265 is comparing labels and placement, so neither can be an identity. "Plan" in one variant
/// is "Prepare" in the other; Stash scan lives under Intel in one and Prepare in the other; Intel
/// is a destination in one and a panel in the other. A persisted address, a recent, a pin or a
/// test written against a label would silently change meaning the day a session moves a label.
/// </remarks>
public readonly record struct V2CapabilityId
{
    public V2CapabilityId(string value) => Value = V2ShellIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>A stable route, independent of the address a variant gives it and the label it shows.</summary>
public readonly record struct V2RouteId
{
    public V2RouteId(string value) => Value = V2ShellIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

internal static class V2ShellIdentifier
{
    public const int MaxLength = 64;

    /// <summary>Lower-case letters, digits, dots and dashes: a name for code, never for a person.</summary>
    public static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > MaxLength ||
            !value.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-'))
        {
            throw new ArgumentException($"'{value}' is not a stable shell identifier.", parameterName);
        }

        return value;
    }
}

/// <summary>Every capability the provisional shell exposes, in both variants.</summary>
public static class V2Capabilities
{
    public static readonly V2CapabilityId Readiness = new("readiness");
    public static readonly V2CapabilityId Continue = new("continue");
    public static readonly V2CapabilityId Raid = new("raid");
    public static readonly V2CapabilityId LootDecision = new("loot-decision");
    public static readonly V2CapabilityId ItemSearch = new("item-search");
    public static readonly V2CapabilityId ItemIntel = new("item-intel");
    public static readonly V2CapabilityId ReferenceData = new("reference-data");
    public static readonly V2CapabilityId StashScan = new("stash-scan");
    public static readonly V2CapabilityId Plan = new("plan");
    public static readonly V2CapabilityId Team = new("team");
    public static readonly V2CapabilityId TabletPreview = new("tablet-preview");
    public static readonly V2CapabilityId Debrief = new("debrief");
    public static readonly V2CapabilityId Setup = new("setup");
    public static readonly V2CapabilityId Capture = new("capture");
    public static readonly V2CapabilityId Health = new("health");
    public static readonly V2CapabilityId Commands = new("commands");

    public static IReadOnlyList<V2CapabilityId> All { get; } =
    [
        Readiness, Continue, Raid, LootDecision, ItemSearch, ItemIntel, ReferenceData, StashScan,
        Plan, Team, TabletPreview, Debrief, Setup, Capture, Health, Commands,
    ];
}

/// <summary>Every route the provisional shell knows. Variants choose addresses and labels for them.</summary>
public static class V2Routes
{
    public static readonly V2RouteId Home = new("home");
    public static readonly V2RouteId Raid = new("raid");
    public static readonly V2RouteId Loot = new("raid.loot");
    public static readonly V2RouteId Items = new("items");
    public static readonly V2RouteId Ammo = new("items.ammo");
    public static readonly V2RouteId Keys = new("items.keys");
    public static readonly V2RouteId Flea = new("items.flea");
    // #287: Crafts & barters, a fourth reference workspace beside Ammo/Keys/Flea.
    public static readonly V2RouteId Crafts = new("items.crafts");
    public static readonly V2RouteId Item = new("item");
    public static readonly V2RouteId Stash = new("stash");
    public static readonly V2RouteId Plan = new("plan");
    public static readonly V2RouteId Hideout = new("plan.hideout");
    public static readonly V2RouteId Keep = new("plan.keep");
    public static readonly V2RouteId Loadout = new("plan.loadout");
    public static readonly V2RouteId Events = new("plan.events");
    public static readonly V2RouteId Team = new("team");
    public static readonly V2RouteId Group = new("team.group");
    public static readonly V2RouteId Tablet = new("team.tablet");
    public static readonly V2RouteId Debrief = new("debrief");
    public static readonly V2RouteId Setup = new("setup");
}
