using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Application.Services.Feedback;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Loadouts;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Application.Services.Personalization;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Recommendations;
using TarkovCompanion.Application.Services.ReleaseExperience;
using TarkovCompanion.Application.Services.Shell;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Network;

namespace TarkovCompanion.App.Services.Settings;

/// <summary>One group of settings that Backup &amp; reset treats as a unit; each is one member of <c>SetupSettingsSnapshot</c>.</summary>
public enum SettingsDomain
{
    Appearance,
    InterfaceScale,
    Notifications,
    ScreenshotTidying,
    Network,
    FeatureFlags,
    Horizons,
    SquadSharing,
    Layout,
    MapDefaults,
}

/// <summary>A settings group, the store interface that persists it, and the Setup section that is its home (null: a page outside Setup).</summary>
public sealed record RegisteredSettings(SettingsDomain Domain, Type Store, V2SetupSection? Home);

/// <summary>One workspace-layout key (or every key under a prefix), the label a preview row shows, and its Setup home if it has one.</summary>
public sealed record RegisteredLayoutKey(string Key, bool IsPrefix, string LabelKey, V2SetupSection? Home = null)
{
    public bool Matches(string key) => IsPrefix
        ? key.StartsWith(Key, StringComparison.Ordinal)
        : string.Equals(key, Key, StringComparison.Ordinal);
}

/// <summary>
/// [#902] Every setting the app persists, in one list: what Reset this section, Reset everything,
/// Export and Import act on.
/// </summary>
/// <remarks>
/// <para>
/// Before this list, "Reset everything" covered three stores and said it covered everything. A new
/// store was invisible to it by default, and nobody noticed for months. The rule now runs the other
/// way: <c>SettingsRegistryTests</c> finds every file-backed store in the Infrastructure assembly and
/// every <see cref="WorkspaceLayoutKeys"/> key, and fails unless each is either registered here or
/// listed in <see cref="NotSettings"/> with the reason it is not a setting.
/// </para>
/// <para>
/// The workspace-layout store is registered whole, so a key someone adds without a line in
/// <see cref="LayoutKeys"/> is still exported and reset; the line gives it a name in the preview and,
/// where it has one, a Setup section whose Reset this section includes it.
/// </para>
/// </remarks>
public static class SettingsRegistry
{
    public static IReadOnlyList<RegisteredSettings> Domains { get; } =
    [
        new(SettingsDomain.Appearance, typeof(IWorkspacePreferenceStore), V2SetupSection.AppearanceWindow),
        new(SettingsDomain.InterfaceScale, typeof(IShellLayoutStore), V2SetupSection.AppearanceWindow),
        new(SettingsDomain.Notifications, typeof(INotificationSettingsStore), V2SetupSection.Notifications),
        new(SettingsDomain.ScreenshotTidying, typeof(IScreenshotRetentionStore), V2SetupSection.GameCapture),
        new(SettingsDomain.Network, typeof(INetworkControlsStore), V2SetupSection.DataNetwork),
        new(SettingsDomain.FeatureFlags, typeof(IFeatureFlagOverrideStore), V2SetupSection.UpdatesDiagnostics),
        new(SettingsDomain.Horizons, typeof(IRecommendationPolicyStore), V2SetupSection.ProfileProgress),
        // [#902 P6] Team › Squad is its home (P5); Reset everything and Import still cover it.
        new(SettingsDomain.SquadSharing, typeof(IGroupSettingsStore), null),
        new(SettingsDomain.Layout, typeof(IWorkspaceLayoutStore), null),
        new(SettingsDomain.MapDefaults, typeof(IMapVariantPreferenceStore), null),
    ];

    /// <summary>Stores that persist something other than a setting, and why each is left out of Backup &amp; reset.</summary>
    public static IReadOnlyDictionary<Type, string> NotSettings { get; } = new Dictionary<Type, string>
    {
        [typeof(IEftPathOverrideStore)] = "A folder on this PC; on another PC it points nowhere.",
        [typeof(IDesktopWindowPlacementStore)] = "Where the window sits on this PC's monitors.",
        [typeof(ILoadoutPresetStore)] = "Loadouts the player built: their data, not a choice of behaviour.",
        [typeof(IRaidMarkStore)] = "Marks placed during raids: data.",
        [typeof(IUserQuestMarkStore)] = "Objective markers the player placed: data.",
        [typeof(IHandDoneObjectiveStore)] = "Objectives marked done by hand: progress.",
        [typeof(IPlayerProfileService)] = "The profile and its progress; Profile & Progress moves it.",
        [typeof(IReleaseExperienceStateStore)] = "Which What's new the player has already read.",
        [typeof(IScreenshotTidyLedger)] = "A record of past tidy runs.",
        [typeof(IProblemReportOutboxStore)] = "Reports waiting to be sent.",
        [typeof(IDesktopCompanionAuthorityStore)] = "This PC's pairing identity: a secret.",
        [typeof(IDeviceSignatureCounterStore)] = "Replay counters for paired devices.",
        [typeof(IGuidedStashScanPendingStore)] = "A stash scan in progress.",
        [typeof(IEventCatalog)] = "Event definitions: data.",
        [typeof(IEventAuthoring)] = "Event definitions: data.",
        [typeof(TarkovCompanion.Core.Domain.Recognition.Learning.ICorrectionMemoryStore)] =
            "Icons and names learned from corrections: this PC's data, never exported; Setup deletes it.",
    };

    /// <summary>Every workspace-layout key with a name. Prefix entries cover keys made per card or per page.</summary>
    public static IReadOnlyList<RegisteredLayoutKey> LayoutKeys { get; } =
    [
        new(WorkspaceLayoutKeys.RaidPanelWidth, false, "Setup.Settings.Layout.RaidPanelWidth"),
        new(WorkspaceLayoutKeys.RaidPanelHidden, false, "Setup.Settings.Layout.RaidPanelHidden"),
        new(WorkspaceLayoutKeys.CoOpExtractVisibility, false, "Setup.Settings.Layout.CoOpExtracts"),
        new(WorkspaceLayoutKeys.RaidFollowZoom, false, "Setup.Settings.Layout.FollowZoom"),
        new(WorkspaceLayoutKeys.RaidLootValueThreshold, false, "Setup.Settings.Layout.LootThreshold"),
        new(WorkspaceLayoutKeys.RaidLootValueBasis, false, "Setup.Settings.Layout.LootBasis"),
        new(WorkspaceLayoutKeys.RaidLayerVisibility, false, "Setup.Settings.Layout.MapLayers"),
        new(WorkspaceLayoutKeys.RaidObjectiveRouteMaps, false, "Setup.Settings.Layout.ObjectiveRouteMaps"),
        new(WorkspaceLayoutKeys.RaidFollow, false, "Setup.Settings.Layout.Follow"),
        new(WorkspaceLayoutKeys.RaidLayerSchema, false, "Setup.Settings.Layout.MapLayers"),
        new(WorkspaceLayoutKeys.RaidShowCompleted, false, "Setup.Settings.Layout.ShowCompleted"),
        new(WorkspaceLayoutKeys.RaidSquadObjectives, false, "Setup.Settings.Layout.SquadObjectives"),
        new(WorkspaceLayoutKeys.RaidRouteSquad, false, "Setup.Settings.Layout.RouteSquad"),
        new(WorkspaceLayoutKeys.RaidMarkScope, false, "Setup.Settings.Layout.MarkScope"),
        new(WorkspaceLayoutKeys.RaidFollowFloor, false, "Setup.Settings.Layout.FollowFloor"),
        new(WorkspaceLayoutKeys.RaidDrawWidth, false, "Setup.Settings.Layout.DrawWidth"),
        new(WorkspaceLayoutKeys.LootAutoReturnSeconds, false, "Setup.Settings.Layout.LootReturn", V2SetupSection.GameCapture),
        new(WorkspaceLayoutKeys.LootOnTabletOnly, false, "Setup.Settings.Layout.LootTabletOnly", V2SetupSection.GameCapture),
        new(WorkspaceLayoutKeys.PlanLearnMode, false, "Setup.Settings.Layout.LearnMode", V2SetupSection.ProfileProgress),
        new(WorkspaceLayoutKeys.PlanSessionMinutes, false, "Setup.Settings.Layout.SessionLength", V2SetupSection.ProfileProgress),
        new(WorkspaceLayoutKeys.SoundSettings, false, "Setup.Settings.Layout.Sound", V2SetupSection.Notifications),
        new(WorkspaceLayoutKeys.LearnIconCrops, false, "Setup.Settings.Layout.LearnIconCrops", V2SetupSection.GameCapture),
        new(WorkspaceLayoutKeys.RaidCard(string.Empty), true, "Setup.Settings.Layout.RaidCard"),
        new(WorkspaceLayoutKeys.RaidSpawnRadius(string.Empty), true, "Setup.Settings.Layout.SpawnRadius"),
        new("page.", true, "Setup.Settings.Layout.PageFilters"),
    ];

    /// <summary>The registered name for a layout key, or null for a key nobody registered.</summary>
    public static RegisteredLayoutKey? FindLayoutKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return LayoutKeys.Where(entry => !entry.IsPrefix).FirstOrDefault(entry => entry.Matches(key))
            ?? LayoutKeys.Where(entry => entry.IsPrefix).FirstOrDefault(entry => entry.Matches(key));
    }

    /// <summary>The groups whose home is <paramref name="section"/>.</summary>
    public static IEnumerable<SettingsDomain> DomainsIn(V2SetupSection section) =>
        Domains.Where(entry => entry.Home == section).Select(entry => entry.Domain);

    /// <summary>Whether <paramref name="section"/> is the home of anything Reset this section could reset.</summary>
    public static bool HasSettings(V2SetupSection section) =>
        DomainsIn(section).Any() || LayoutKeys.Any(entry => entry.Home == section);

    /// <summary>Whether a layout key's home is <paramref name="section"/>.</summary>
    public static bool IsLayoutKeyIn(string key, V2SetupSection section) =>
        FindLayoutKey(key)?.Home == section;
}
