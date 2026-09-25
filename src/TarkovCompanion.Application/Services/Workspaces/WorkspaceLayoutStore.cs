namespace TarkovCompanion.Application.Services.Workspaces;

/// <summary>
/// Where the window remembers how the player arranged it.
/// </summary>
/// <remarks>
/// [V2 rough package 46] "The left sidebar should be collapsible, the right one is too wide,
/// maybe we can make it adjustable" — and a choice like that is worth nothing if it has to be
/// made again every launch. Small, string-keyed and deliberately dumb: these are chrome
/// preferences, not state anything reasons about, and a corrupt or missing file simply means the
/// defaults. Nothing here decides what is true about a raid, so nothing here needs provenance.
/// </remarks>
public interface IWorkspaceLayoutStore
{
    /// <summary>The remembered value for a key, or null when there is none.</summary>
    string? Get(string key);

    /// <summary>Remembers a value, best effort; a failure to write is not worth telling anybody about.</summary>
    void Set(string key, string value);

    /// <summary>Everything remembered, for Setup's Backup &amp; reset (#902). A store that cannot list
    /// its entries has none to export.</summary>
    IReadOnlyDictionary<string, string> Entries => new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Replaces everything remembered with <paramref name="entries"/> in one write; an empty
    /// set is "Reset everything". A missing key reads as the default again.</summary>
    void Replace(IReadOnlyDictionary<string, string> entries)
    {
    }

    /// <summary>Raised after a <see cref="Replace"/>, on the thread that made it, so a page holding a
    /// value it read at construction reads it again instead of keeping the old one until a restart.</summary>
    event EventHandler? Replaced
    {
        add { }
        remove { }
    }
}

/// <summary>The keys, in one place, so a reader can see everything that is remembered.</summary>
public static class WorkspaceLayoutKeys
{
    /// <summary>The Raid context panel's width in device-independent pixels.</summary>
    public const string RaidPanelWidth = "raid.panel-width";

    /// <summary>Whether the Raid context panel is hidden entirely.</summary>
    public const string RaidPanelHidden = "raid.panel-hidden";

    /// <summary>[Issue 573] Hidden / Dim / Normal — how a co-op extract is drawn and offered.</summary>
    public const string CoOpExtractVisibility = "raid.coop-extract-visibility";

    /// <summary>[Issue 663] Magnification used while the raid map follows the player.</summary>
    public const string RaidFollowZoom = "raid.follow-zoom";

    /// <summary>[Issue 701] Minimum value used by the raid map's potential-loot layer.</summary>
    public const string RaidLootValueThreshold = "raid.loot-value-threshold";

    /// <summary>[Issue 701] Whether potential loot is ranked per item or per inventory slot.</summary>
    public const string RaidLootValueBasis = "raid.loot-value-basis";

    /// <summary>[Issue 796] The raid map layers the player turned on or off, as "id:1,id:0".</summary>
    public const string RaidLayerVisibility = "raid.layer-visibility";

    /// <summary>[#902] The maps an objective route from Plan was last opened on, as "customs,woods".</summary>
    public const string RaidObjectiveRouteMaps = "raid.objective-route-maps";

    /// <summary>[Issue 702] Whether a new screenshot moves the raid map back to the player.</summary>
    public const string RaidFollow = "raid.follow";

    /// <summary>[Issue 572] Seconds before an automatic Loot result returns to the map, or "off".</summary>
    public const string LootAutoReturnSeconds = "loot.auto-return-seconds";

    /// <summary>[Issue 572] "on" when a Loot result goes to the paired tablet and the desktop stays on the map.</summary>
    public const string LootOnTabletOnly = "loot.tablet-only";

    /// <summary>[Issue 288] "on" when Plan rows include the engine's short reason.</summary>
    public const string PlanLearnMode = "plan.learn-mode";

    /// <summary>[#902 P8] One page's remembered filters, chips and sorts; see <see cref="PageState"/>.</summary>
    public const string PageIntel = "page.intel";
    public const string PageAmmo = "page.ammo";
    public const string PageKeys = "page.keys";
    public const string PageCrafts = "page.crafts";
    public const string PagePlan = "page.plan";
    public const string PageHideout = "page.hideout";
    public const string PageDebrief = "page.debrief";
    public const string PageStash = "page.stash";

    /// <summary>[#902 P8] The Loot Scan's risk and verdict chip. Its phase is per raid and never stored.</summary>
    public const string PageLoot = "page.loot";

    /// <summary>Whether one card of the Raid side panel is open ("open"/"closed"); see RaidPanelCards.</summary>
    public static string RaidCard(string cardId) => $"raid.card.{cardId}";
}
