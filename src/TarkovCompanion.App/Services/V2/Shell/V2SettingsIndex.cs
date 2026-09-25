using TarkovCompanion.App.ViewModels.V2.Setup;

namespace TarkovCompanion.App.Services.V2.Shell;

/// <summary>
/// One setting the command palette can find: the words it answers to, the page and Setup section
/// that is its home, and the control on that page to land on.
/// </summary>
/// <param name="Id">Stable, for the palette row's automation id.</param>
/// <param name="LabelKey">The setting's name, as the page itself calls it.</param>
/// <param name="Route">The page that is its home.</param>
/// <param name="Section">The Setup section, when the page is Setup.</param>
/// <param name="Target">The automation id of the row's control (or of the group holding it).</param>
/// <param name="Keywords">Other words a player types for it, in lower case.</param>
public sealed record V2SettingEntry(
    string Id,
    string LabelKey,
    V2RouteId Route,
    V2SetupSection? Section,
    string Target,
    params string[] Keywords);

/// <summary>
/// Every setting a player can change, listed once, so Ctrl+K finds "local only" or "quiet hours"
/// and lands on that row rather than on a page the player then has to read.
/// </summary>
/// <remarks>
/// [#902 P9] The palette was built from the route table, and the route table does not model Setup
/// sections or the rows in them: typing "notifications", "local only" or "theme" found nothing.
/// The targets are the automation ids the views already carry (the packaged Windows smoke finds
/// controls by the same ids), so a row renamed on screen keeps working here; a target that moved
/// to another section lands on the page heading instead of failing. When #902's settings registry
/// (section 4 of the proposal) exists, this list is the thing it replaces.
/// </remarks>
public static class V2SettingsIndex
{
    private static V2SettingEntry Setup(string id, string labelKey, V2SetupSection section, string target, params string[] keywords) =>
        new(id, labelKey, V2Routes.Setup, section, target, keywords);

    public static IReadOnlyList<V2SettingEntry> All { get; } =
    [
        // Game & Profile
        Setup("profiles", "V2.Shell.Setting.Profiles", V2SetupSection.GameProfile, "v2-setup-profile-list", "game mode", "pve", "pvp", "wipe"),
        Setup("screenshot-folder", "V2.Shell.Setting.ScreenshotFolder", V2SetupSection.GameProfile, "v2-setup-screenshot-folder", "screenshots", "folder", "path"),
        Setup("log-folder", "V2.Shell.Setting.LogFolder", V2SetupSection.GameProfile, "v2-setup-log-folder", "logs", "folder", "path"),
        Setup("language", "V2.Shell.Setting.Language", V2SetupSection.GameProfile, "v2-setup-language-choices", "english", "locale"),

        // Recognition
        Setup("loot-return", "V2.Shell.Setting.LootReturn", V2SetupSection.Recognition, "v2-setup-loot-return", "loot scan", "back to map", "seconds"),
        Setup("loot-tablet-only", "V2.Shell.Setting.LootTabletOnly", V2SetupSection.Recognition, "v2-setup-loot-tablet-only", "loot scan", "tablet"),

        // Data
        Setup("game-data", "V2.Shell.Setting.GameData", V2SetupSection.Data, "v2-setup-sync", "sync", "tarkov.dev", "refresh", "catalog"),

        // Progress
        Setup("quest-horizon", "V2.Shell.Setting.QuestHorizon", V2SetupSection.Progress, "v2-setup-recommendation-quests-nextonly", "plan ahead", "recommendations", "next 3"),
        Setup("hideout-horizon", "V2.Shell.Setting.HideoutHorizon", V2SetupSection.Progress, "v2-setup-recommendation-hideout-nextonly", "plan ahead", "recommendations", "stations"),
        Setup("quest-screenshots", "V2.Shell.Setting.QuestScreenshots", V2SetupSection.Progress, "v2-quest-sync", "quests", "sync", "screenshot"),
        Setup("move-progress", "V2.Shell.Setting.MoveProgress", V2SetupSection.Progress, "v2-setup-exchange-path", "export", "import", "backup", "json"),
        Setup("tarkovtracker", "V2.Shell.Setting.TarkovTracker", V2SetupSection.Progress, "v2-setup-tracker-token", "tracker", "token"),

        // Team & Devices
        Setup("squad", "V2.Shell.Setting.Squad", V2SetupSection.TeamDevices, "v2-setup-open-team", "team", "group", "share"),

        // Updates
        Setup("updates", "V2.Shell.Setting.CheckUpdates", V2SetupSection.Updates, "v2-setup-check-update", "update", "channel", "version"),
        Setup("go-back", "V2.Shell.Setting.GoBack", V2SetupSection.Updates, "v2-setup-rollback", "rollback", "previous build", "downgrade"),

        // Privacy
        Setup("screenshot-cleanup", "V2.Shell.Setting.ScreenshotCleanup", V2SetupSection.Privacy, "v2-setup-choose-retention", "tidy", "retention", "delete screenshots"),

        // Notifications
        Setup("notifications", "V2.Shell.Setting.Notifications", V2SetupSection.Notifications, "v2-setup-notifications", "alerts", "tray"),
        Setup("notification-popup", "V2.Shell.Setting.NotificationPopup", V2SetupSection.Notifications, "v2-setup-notification-popup", "pop-up", "toast", "notifications"),
        Setup("quiet-hours", "V2.Shell.Setting.QuietHours", V2SetupSection.Notifications, "v2-setup-notification-quiet", "do not disturb", "notifications"),
        Setup("recent-notifications", "V2.Shell.Setting.RecentNotifications", V2SetupSection.Notifications, "v2-setup-notifications-recent", "history", "notifications"),

        // Accessibility
        Setup("theme", "V2.Shell.Setting.Theme", V2SetupSection.Accessibility, "v2-setup-appearance-themes", "dark", "light", "appearance", "colour", "color"),
        Setup("colour-vision", "V2.Shell.Setting.ColourVision", V2SetupSection.Accessibility, "v2-setup-appearance-visions", "colour blind", "color blind", "deuteranopia", "protanopia"),
        Setup("text-size", "V2.Shell.Setting.TextSize", V2SetupSection.Accessibility, "v2-setup-appearance-textscales", "font", "bigger text"),
        Setup("density", "V2.Shell.Setting.Density", V2SetupSection.Accessibility, "v2-setup-appearance-densities", "compact", "spacing"),
        Setup("reduce-motion", "V2.Shell.Setting.ReduceMotion", V2SetupSection.Accessibility, "v2-setup-appearance-motion", "animation", "motion"),
        Setup("focus-ring", "V2.Shell.Setting.FocusRing", V2SetupSection.Accessibility, "v2-setup-appearance-focus", "keyboard", "outline"),
        Setup("interface-scale", "V2.Shell.Setting.InterfaceScale", V2SetupSection.Accessibility, "v2-setup-scale-larger", "zoom", "size", "dpi"),
        Setup("capture-shortcut", "V2.Shell.Setting.CaptureShortcut", V2SetupSection.Accessibility, "v2-setup-capture-shortcut", "alt+shift+c", "hotkey", "capture"),
        Setup("shortcuts", "V2.Shell.Setting.Shortcuts", V2SetupSection.Accessibility, "v2-setup-accessibility-shortcuts", "keyboard", "hotkeys", "keys"),
        Setup("settings-backup", "V2.Shell.Setting.SettingsBackup", V2SetupSection.Accessibility, "v2-setup-settings-admin", "export", "import", "reset everything", "defaults"),

        // Displays
        Setup("displays", "V2.Shell.Setting.Displays", V2SetupSection.Displays, "v2-setup-displays", "monitor", "screen", "window"),

        // Diagnostics
        Setup("self-test", "V2.Shell.Setting.SelfTest", V2SetupSection.Diagnostics, "v2-setup-previous-run", "diagnostics", "check"),
        Setup("drawing-tools", "V2.Shell.Setting.DrawingTools", V2SetupSection.Diagnostics, "v2-setup-flag-draw-mode", "draw", "pencil", "feature flag"),
        Setup("tablet-cards", "V2.Shell.Setting.TabletCards", V2SetupSection.Diagnostics, "v2-setup-flag-tablet-review-cards", "tablet", "stash", "flea", "feature flag"),
        Setup("report-problem", "V2.Shell.Setting.ReportProblem", V2SetupSection.Diagnostics, "v2-setup-report-problem", "bug", "feedback", "diagnostics"),

        // Data & Privacy
        Setup("local-only", "V2.Shell.Setting.LocalOnly", V2SetupSection.DataPrivacy, "v2-setup-network-local-only", "offline", "network", "privacy", "internet"),
        Setup("squad-sharing", "V2.Shell.Setting.SquadSharing", V2SetupSection.DataPrivacy, "v2-setup-network-SquadSharing-switch", "network", "relay", "team", "group"),
        Setup("update-checks", "V2.Shell.Setting.UpdateChecks", V2SetupSection.DataPrivacy, "v2-setup-network-UpdateChecks-switch", "network", "updates"),
        Setup("problem-reports", "V2.Shell.Setting.ProblemReports", V2SetupSection.DataPrivacy, "v2-setup-network-ProblemReports-switch", "network", "bug"),
        Setup("tracker-network", "V2.Shell.Setting.TrackerNetwork", V2SetupSection.DataPrivacy, "v2-setup-network-TarkovTracker-switch", "network", "tracker"),

        // About
        Setup("whats-new", "V2.Shell.Setting.WhatsNew", V2SetupSection.About, "v2-setup-whats-new", "changelog", "release notes", "update"),

        // On the pages that read them
        new("learn-mode", "V2.Shell.Setting.LearnMode", V2Routes.Plan, null, "v2-plan-learn-mode", "explain", "recommendations", "why"),
        new("loot-rules", "V2.Shell.Setting.LootRules", V2Routes.Keep, null, "v2-keep-loot-rules", "always leave", "always take", "pinned", "wishlist", "undo"),
    ];
}
