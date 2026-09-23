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
}

/// <summary>The keys, in one place, so a reader can see everything that is remembered.</summary>
public static class WorkspaceLayoutKeys
{
    /// <summary>How much of the shell's navigation rail is showing: labels, icons or nothing.</summary>
    public const string NavigationRail = "shell.navigation-rail";

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

    /// <summary>[Issue 572] Seconds before an automatic Loot result returns to the map, or "off".</summary>
    public const string LootAutoReturnSeconds = "loot.auto-return-seconds";

    /// <summary>[Issue 572] "on" when a Loot result goes to the paired tablet and the desktop stays on the map.</summary>
    public const string LootOnTabletOnly = "loot.tablet-only";

    /// <summary>Whether one card of the Raid side panel is open ("open"/"closed"); see RaidPanelCards.</summary>
    public static string RaidCard(string cardId) => $"raid.card.{cardId}";
}
