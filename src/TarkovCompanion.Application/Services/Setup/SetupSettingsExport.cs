using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Personalization;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Core.Network;

namespace TarkovCompanion.Application.Services.Setup;

/// <summary>What came back from reading an import file: either a snapshot ready to preview, or why not.</summary>
public sealed record SetupSettingsValidationResult(bool IsValid, SetupSettingsSnapshot? Snapshot, string? Error)
{
    public static SetupSettingsValidationResult Success(SetupSettingsSnapshot snapshot) => new(true, snapshot, null);

    public static SetupSettingsValidationResult Failure(string error) => new(false, null, error);
}

/// <summary>
/// Writes and reads the JSON file "Export preferences" and "Import" trade (#292 task 2).
/// </summary>
/// <remarks>
/// <para>
/// The document is its own record, deliberately not <see cref="SetupSettingsSnapshot"/> itself,
/// for the same reason <c>JsonFileWorkspacePreferenceStore</c>'s own document is separate from
/// <see cref="WorkspacePreferences"/>: every field nullable so a hand-edited file with one key in
/// it reads as "that choice was not made" rather than as the zero of its type, and enums written
/// as names so a diff of two exported files is readable.
/// </para>
/// <para>
/// Validation never fails on an out-of-range value — a hand-edited text scale of 900 or an unknown
/// theme name is clamped to the nearest one this build understands, the same as a stored preference
/// file. The preview built from the result shows the corrected value, not the original, so nothing
/// is applied silently: what the player confirms is what lands. Validation does fail on a file this
/// build cannot read at all (malformed JSON) or was written by a newer one (a schema version this
/// build does not know), because guessing at either is worse than saying so.
/// </para>
/// </remarks>
public static class SetupSettingsExport
{
    /// <summary>The shape this build writes and the newest one it will read.</summary>
    public const int SchemaVersion = SetupSettingsSnapshot.SchemaVersion;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The exported text. Never contains a secret: there is no field for one to occupy.</summary>
    public static string ToJson(SetupSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var normalized = snapshot.Normalized();
        var document = new Document(
            SchemaVersion,
            normalized.Appearance.Theme,
            normalized.Appearance.ColorVision,
            normalized.Appearance.TextScalePercent,
            normalized.Appearance.Density,
            normalized.Appearance.ReduceMotion,
            normalized.Appearance.FocusAlwaysVisible,
            normalized.Notifications.SquadMark,
            normalized.Notifications.DebriefReady,
            normalized.Notifications.DataRefreshFailed,
            normalized.Notifications.UpdateReady,
            normalized.Notifications.RelayUnreachable,
            normalized.Notifications.ShowsDesktopPopup,
            normalized.ScreenshotRetention.IsEnabled,
            normalized.ScreenshotRetention.RetentionHours,
            normalized.Notifications.FleaSold,
            normalized.Notifications.QuietHours,
            normalized.Notifications.QuietFromHour,
            normalized.Notifications.QuietToHour)
        {
            InterfaceScale = normalized.InterfaceScale,
            Network = new NetworkDocument(
                normalized.Network.LocalOnly,
                normalized.Network.SquadSharing,
                normalized.Network.UpdateChecks,
                normalized.Network.ProblemReports,
                normalized.Network.TarkovTracker),
            FeatureFlags = new SortedDictionary<string, bool>(normalized.FeatureFlags.ToDictionary(), StringComparer.Ordinal),
            Horizons = new HorizonsDocument(normalized.Horizons.Quest, normalized.Horizons.Hideout),
            SquadSharing = new SquadSharingDocument(
                normalized.SquadSharing.IsEnabled,
                normalized.SquadSharing.SharesLoadout,
                normalized.SquadSharing.SharesQuests),
            Layout = new SortedDictionary<string, string>(normalized.Layout.ToDictionary(), StringComparer.Ordinal),
            MapDefaults = new SortedDictionary<string, string>(normalized.MapDefaults.ToDictionary(), StringComparer.OrdinalIgnoreCase),
        };
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    /// <summary>Reads and validates an exported file's text, without applying anything.</summary>
    /// <remarks>A group the file does not name at all (a version 1 file names only appearance,
    /// notifications and screenshot tidying) reads as its default here.</remarks>
    public static SetupSettingsValidationResult Validate(string json) => Validate(json, SetupSettingsSnapshot.Default);

    /// <summary>Reads and validates an exported file's text against what is in force now.</summary>
    /// <remarks>
    /// A group the file does not name at all keeps <paramref name="current"/>'s value, so importing
    /// a version 1 file never resets the map layers it predates. A group it does name is taken whole:
    /// a missing switch inside it is that switch's default.
    /// </remarks>
    public static SetupSettingsValidationResult Validate(string json, SetupSettingsSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(json);
        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            return SetupSettingsValidationResult.Failure($"Not a valid preferences file: {exception.Message}");
        }

        if (document is null)
        {
            return SetupSettingsValidationResult.Failure("The file is empty.");
        }

        if (document.SchemaVersion > SchemaVersion)
        {
            return SetupSettingsValidationResult.Failure(
                "This file was exported by a newer version of the app and cannot be understood.");
        }

        var snapshot = new SetupSettingsSnapshot(
            new WorkspacePreferences(
                document.Theme ?? WorkspacePreferences.Default.Theme,
                document.ColorVision ?? WorkspacePreferences.Default.ColorVision,
                document.TextScalePercent ?? WorkspacePreferences.Default.TextScalePercent,
                document.Density ?? WorkspacePreferences.Default.Density,
                document.ReduceMotion ?? WorkspacePreferences.Default.ReduceMotion,
                document.FocusAlwaysVisible ?? WorkspacePreferences.Default.FocusAlwaysVisible),
            new NotificationSettings
            {
                SquadMark = document.NotifySquadMark ?? NotificationSettings.Default.SquadMark,
                DebriefReady = document.NotifyDebriefReady ?? NotificationSettings.Default.DebriefReady,
                DataRefreshFailed = document.NotifyDataRefreshFailed ?? NotificationSettings.Default.DataRefreshFailed,
                UpdateReady = document.NotifyUpdateReady ?? NotificationSettings.Default.UpdateReady,
                RelayUnreachable = document.NotifyRelayUnreachable ?? NotificationSettings.Default.RelayUnreachable,
                ShowsDesktopPopup = document.NotifyDesktopPopup ?? NotificationSettings.Default.ShowsDesktopPopup,
                FleaSold = document.NotifyFleaSold ?? NotificationSettings.Default.FleaSold,
                QuietHours = document.QuietHours ?? NotificationSettings.Default.QuietHours,
                QuietFromHour = Math.Clamp(document.QuietFromHour ?? NotificationSettings.Default.QuietFromHour, 0, 23),
                QuietToHour = Math.Clamp(document.QuietToHour ?? NotificationSettings.Default.QuietToHour, 0, 23),
            },
            new ScreenshotRetentionSettings(
                document.ScreenshotCleanupEnabled ?? ScreenshotRetentionSettings.Default.IsEnabled,
                document.ScreenshotRetentionHours ?? ScreenshotRetentionSettings.Default.RetentionHours))
        {
            InterfaceScale = document.InterfaceScale ?? current.InterfaceScale,
            Network = document.Network is { } network
                ? new NetworkControls
                {
                    LocalOnly = network.LocalOnly ?? NetworkControls.Default.LocalOnly,
                    SquadSharing = network.SquadSharing ?? NetworkControls.Default.SquadSharing,
                    UpdateChecks = network.UpdateChecks ?? NetworkControls.Default.UpdateChecks,
                    ProblemReports = network.ProblemReports ?? NetworkControls.Default.ProblemReports,
                    TarkovTracker = network.TarkovTracker ?? NetworkControls.Default.TarkovTracker,
                }
                : current.Network,
            FeatureFlags = document.FeatureFlags is { } flags
                ? new SortedDictionary<string, bool>(
                    flags.Where(entry => !string.IsNullOrWhiteSpace(entry.Key)).ToDictionary(),
                    StringComparer.Ordinal)
                : current.FeatureFlags,
            Horizons = document.Horizons is { } horizons
                ? new RecommendationHorizonSettings(
                    horizons.Quest ?? RecommendationHorizonSettings.Default.Quest,
                    horizons.Hideout ?? RecommendationHorizonSettings.Default.Hideout)
                : current.Horizons,
            SquadSharing = document.SquadSharing is { } squad
                ? new SquadSharingChoices(
                    squad.IsEnabled ?? SquadSharingChoices.Default.IsEnabled,
                    squad.SharesLoadout ?? SquadSharingChoices.Default.SharesLoadout,
                    squad.SharesQuests ?? SquadSharingChoices.Default.SharesQuests)
                : current.SquadSharing,
            Layout = document.Layout is { } layout
                ? new SortedDictionary<string, string>(
                    layout.Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value is not null).ToDictionary(),
                    StringComparer.Ordinal)
                : current.Layout,
            MapDefaults = document.MapDefaults is { } maps
                ? new SortedDictionary<string, string>(
                    maps.Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value))
                        .ToDictionary(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase)
                : current.MapDefaults,
        }
        .Normalized();

        return SetupSettingsValidationResult.Success(snapshot);
    }

    /// <remarks>
    /// No field for the TarkovTracker token or the group relay's credentials: #292 task 2 asks
    /// that an export never carries a secret, and the way to make that true forever rather than
    /// true today is to give this type nowhere to put one.
    /// </remarks>
    private sealed record Document(
        int SchemaVersion,
        AppearanceTheme? Theme,
        ColorVisionMode? ColorVision,
        int? TextScalePercent,
        InterfaceDensity? Density,
        bool? ReduceMotion,
        bool? FocusAlwaysVisible,
        bool? NotifySquadMark,
        bool? NotifyDebriefReady,
        bool? NotifyDataRefreshFailed,
        bool? NotifyUpdateReady,
        bool? NotifyRelayUnreachable,
        bool? NotifyDesktopPopup,
        bool? ScreenshotCleanupEnabled,
        int? ScreenshotRetentionHours,
        bool? NotifyFleaSold = null,
        bool? QuietHours = null,
        int? QuietFromHour = null,
        int? QuietToHour = null)
    {
        public double? InterfaceScale { get; init; }

        public NetworkDocument? Network { get; init; }

        public IReadOnlyDictionary<string, bool>? FeatureFlags { get; init; }

        public HorizonsDocument? Horizons { get; init; }

        public SquadSharingDocument? SquadSharing { get; init; }

        public IReadOnlyDictionary<string, string>? Layout { get; init; }

        public IReadOnlyDictionary<string, string>? MapDefaults { get; init; }
    }

    private sealed record NetworkDocument(bool? LocalOnly, bool? SquadSharing, bool? UpdateChecks, bool? ProblemReports, bool? TarkovTracker);

    private sealed record HorizonsDocument(RecommendationHorizon? Quest, RecommendationHorizon? Hideout);

    /// <remarks>The three switches only: the relay address, display name and group key never leave group.json.</remarks>
    private sealed record SquadSharingDocument(bool? IsEnabled, bool? SharesLoadout, bool? SharesQuests);
}
