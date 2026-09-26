using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Personalization;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Core.Network;

namespace TarkovCompanion.Application.Services.Setup;

/// <summary>The squad-sharing switches of group.json, without the address, name or key.</summary>
/// <param name="SharesReadyCheck">"My ready check" (#961), on by default like group.json's own.</param>
public sealed record SquadSharingChoices(bool IsEnabled, bool SharesLoadout, bool SharesQuests, bool SharesReadyCheck = true)
{
    /// <summary>What <c>GroupSharingSettings.Off</c> holds: nothing shared, quests on once joined.</summary>
    public static SquadSharingChoices Default { get; } = new(false, false, true);
}

/// <summary>
/// Everything Setup's "Reset this section", "Reset everything", export and import (#292 task 2)
/// treat as one bundle.
/// </summary>
/// <remarks>
/// [#902] It began as three records (appearance, notifications, screenshot tidying) under a button
/// labelled "Reset everything", which left map layers, page choices, network switches, feature flags,
/// squad sharing and the interface scale where they were. Every setting a player chooses is now one
/// member here, and <c>SettingsRegistry</c> in the App names each one's home and fails a test when a
/// new store appears that is neither registered nor listed as not being a setting.
///
/// <para>
/// What is <em>not</em> here is the point as much as what is. Screenshot and log folder overrides
/// are machine-specific paths, not preferences, and copying them to a new machine would point the
/// companion at a folder that does not exist there. The TarkovTracker token and the group relay's
/// credentials are secrets: <see cref="SetupSettingsExport"/> has no field for either, so there is
/// no path by which an export can carry one.
/// </para>
/// </remarks>
public sealed record SetupSettingsSnapshot(
    WorkspacePreferences Appearance,
    NotificationSettings Notifications,
    ScreenshotRetentionSettings ScreenshotRetention)
{
    /// <summary>The shape <see cref="SetupSettingsExport"/> writes and the newest one it reads.</summary>
    /// <remarks>Version 2 added every member below the first three. A version 1 file still reads,
    /// and leaves the members it does not name as they are.</remarks>
    public const int SchemaVersion = 2;

    /// <summary>The window's interface scale, 1 = 100% (shell.json).</summary>
    public double InterfaceScale { get; init; } = 1;

    /// <summary>Local only and the per-service switches (network.json).</summary>
    public NetworkControls Network { get; init; } = NetworkControls.Default;

    /// <summary>Feature flags the player set away from the ring default, by key (feature-flags.json).</summary>
    public IReadOnlyDictionary<string, bool> FeatureFlags { get; init; } = new SortedDictionary<string, bool>(StringComparer.Ordinal);

    /// <summary>How far ahead the advice looks for quest and hideout needs (recommendations.json).</summary>
    public RecommendationHorizonSettings Horizons { get; init; } = RecommendationHorizonSettings.Default;

    /// <summary>Share with my squad, my loadout, my quest progress (group.json, never its secrets).</summary>
    public SquadSharingChoices SquadSharing { get; init; } = SquadSharingChoices.Default;

    /// <summary>Map layers, the Raid panel, page filters and every other workspace-layout.json entry.
    /// A key that is absent is at its default.</summary>
    public IReadOnlyDictionary<string, string> Layout { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Per-map artwork, variant and rotation choices (map-defaults.json).</summary>
    public IReadOnlyDictionary<string, string> MapDefaults { get; init; } = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>What a player who has changed nothing has: every domain's own default.</summary>
    public static SetupSettingsSnapshot Default { get; } = new(
        WorkspacePreferences.Default,
        NotificationSettings.Default,
        ScreenshotRetentionSettings.Default);

    /// <summary>Equal when every member holds the same values, the dictionaries compared by content.</summary>
    public bool Equals(SetupSettingsSnapshot? other) =>
        other is not null
        && Appearance == other.Appearance
        && Notifications == other.Notifications
        && ScreenshotRetention == other.ScreenshotRetention
        && InterfaceScale.Equals(other.InterfaceScale)
        && Network == other.Network
        && Horizons == other.Horizons
        && SquadSharing == other.SquadSharing
        && SameEntries(FeatureFlags, other.FeatureFlags)
        && SameEntries(Layout, other.Layout)
        && SameEntries(MapDefaults, other.MapDefaults);

    public override int GetHashCode() => HashCode.Combine(Appearance, Notifications, ScreenshotRetention, InterfaceScale, Network, Horizons, SquadSharing);

    private static bool SameEntries<TValue>(IReadOnlyDictionary<string, TValue> left, IReadOnlyDictionary<string, TValue> right) =>
        left.Count == right.Count
        && left.All(entry => right.TryGetValue(entry.Key, out var value) && EqualityComparer<TValue>.Default.Equals(entry.Value, value));

    /// <summary>The same snapshot with every value inside the range this build understands.</summary>
    public SetupSettingsSnapshot Normalized() => this with
    {
        Appearance = Appearance.Normalized(),
        ScreenshotRetention = ScreenshotRetention with { RetentionHours = ScreenshotRetention.SafeRetentionHours },
        InterfaceScale = TarkovCompanion.Application.Services.Shell.ShellLayout.NearestScale(InterfaceScale),
        Horizons = Horizons.Normalized(),
    };
}
