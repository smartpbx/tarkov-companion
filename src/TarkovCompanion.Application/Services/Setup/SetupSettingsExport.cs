using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Personalization;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Core.Network;

namespace TarkovCompanion.Application.Services.Setup;

/// <summary>Why an import file could not be read. The App says it in the interface language.</summary>
public enum SetupSettingsImportError
{
    /// <summary>Not JSON, or JSON of the wrong shape (a typo in an enum name, a string where a number goes).</summary>
    Malformed = 1,

    /// <summary>The file holds nothing (<c>null</c>).</summary>
    Empty,

    /// <summary>Written by a newer build, with a schema version this one does not know.</summary>
    Newer,
}

/// <summary>What came back from reading an import file: either a snapshot ready to preview, or why not.</summary>
/// <remarks>
/// [#935] <see cref="Detail"/> is the serializer's own English text, for the crash log only. It used
/// to be the status line itself, so a player in any language read "System.Nullable`1[...] Path: $.theme".
/// </remarks>
public sealed record SetupSettingsValidationResult(bool IsValid, SetupSettingsSnapshot? Snapshot, SetupSettingsImportError? Error, string? Detail = null)
{
    public static SetupSettingsValidationResult Success(SetupSettingsSnapshot snapshot) => new(true, snapshot, null);

    public static SetupSettingsValidationResult Failure(SetupSettingsImportError error, string? detail = null) => new(false, null, error, detail);
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
                normalized.SquadSharing.SharesQuests,
                normalized.SquadSharing.SharesReadyCheck),
            Layout = new SortedDictionary<string, string>(normalized.Layout.ToDictionary(), StringComparer.Ordinal),
            MapDefaults = new SortedDictionary<string, string>(normalized.MapDefaults.ToDictionary(), StringComparer.OrdinalIgnoreCase),
            Language = new LanguageDocument(normalized.InterfaceLanguage),
            CaptureShortcut = normalized.CaptureShortcut,
            CloseToTray = normalized.Appearance.CloseToTray,
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
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            return SetupSettingsValidationResult.Failure(SetupSettingsImportError.Malformed, exception.Message);
        }

        if (document is null)
        {
            return SetupSettingsValidationResult.Failure(SetupSettingsImportError.Empty);
        }

        if (document.SchemaVersion > SchemaVersion)
        {
            return SetupSettingsValidationResult.Failure(
                SetupSettingsImportError.Newer,
                $"Schema {document.SchemaVersion}; this build reads up to {SchemaVersion}.");
        }

        try
        {
            return SetupSettingsValidationResult.Success(Read(document, current));
        }
        catch (ArgumentException exception)
        {
            // A safety net: every dictionary below is built so a duplicate cannot throw.
            return SetupSettingsValidationResult.Failure(SetupSettingsImportError.Malformed, exception.Message);
        }
    }

    private static SetupSettingsSnapshot Read(Document document, SetupSettingsSnapshot current)
    {

        var snapshot = new SetupSettingsSnapshot(
            new WorkspacePreferences(
                document.Theme ?? WorkspacePreferences.Default.Theme,
                document.ColorVision ?? WorkspacePreferences.Default.ColorVision,
                document.TextScalePercent ?? WorkspacePreferences.Default.TextScalePercent,
                document.Density ?? WorkspacePreferences.Default.Density,
                document.ReduceMotion ?? WorkspacePreferences.Default.ReduceMotion,
                document.FocusAlwaysVisible ?? WorkspacePreferences.Default.FocusAlwaysVisible,
                document.CloseToTray ?? WorkspacePreferences.Default.CloseToTray),
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
                    squad.SharesQuests ?? SquadSharingChoices.Default.SharesQuests,
                    // A file from before #961 has no ready-check switch: the default, as for a new group.json.
                    squad.SharesReadyCheck ?? SquadSharingChoices.Default.SharesReadyCheck)
                : current.SquadSharing,
            Layout = document.Layout is { } layout
                ? new SortedDictionary<string, string>(
                    layout.Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value is not null).ToDictionary(),
                    StringComparer.Ordinal)
                : current.Layout,
            MapDefaults = document.MapDefaults is { } maps ? MapDefaultsOf(maps) : current.MapDefaults,
            InterfaceLanguage = document.Language is { } language ? language.Culture : current.InterfaceLanguage,
            CaptureShortcut = document.CaptureShortcut ?? current.CaptureShortcut,
        }
        .Normalized();

        return snapshot;
    }

    /// <summary>
    /// [#935] The file's keys are exactly as written, so a hand edit can leave "customs" and "Customs"
    /// side by side. Keyed case-insensitively, the later one wins, where ToDictionary threw and the
    /// preview did nothing at all.
    /// </summary>
    private static SortedDictionary<string, string> MapDefaultsOf(IReadOnlyDictionary<string, string> maps)
    {
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in maps)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                result[key] = value;
            }
        }

        return result;
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

        /// <summary>[#935] The interface language; a null culture follows Windows. Absent: left as it is.</summary>
        public LanguageDocument? Language { get; init; }

        /// <summary>[#935] Whether Alt+Shift+C captures. Absent: left as it is.</summary>
        public bool? CaptureShortcut { get; init; }

        /// <summary>[#917] Whether an ordinary close keeps the app in the tray. Absent: on.</summary>
        public bool? CloseToTray { get; init; }
    }

    private sealed record LanguageDocument(string? Culture);

    private sealed record NetworkDocument(bool? LocalOnly, bool? SquadSharing, bool? UpdateChecks, bool? ProblemReports, bool? TarkovTracker);

    private sealed record HorizonsDocument(RecommendationHorizon? Quest, RecommendationHorizon? Hideout);

    /// <remarks>The three switches only: the relay address, display name and group key never leave group.json.</remarks>
    private sealed record SquadSharingDocument(bool? IsEnabled, bool? SharesLoadout, bool? SharesQuests, bool? SharesReadyCheck = null);
}
