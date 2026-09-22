using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Personalization;

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
            normalized.Notifications.QuietToHour);
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    /// <summary>Reads and validates an exported file's text, without applying anything.</summary>
    public static SetupSettingsValidationResult Validate(string json)
    {
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
        int? QuietToHour = null);
}
