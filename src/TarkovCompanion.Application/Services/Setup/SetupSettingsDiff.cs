namespace TarkovCompanion.Application.Services.Setup;

/// <summary>A setting a diff row names; the App says it in the interface language (#314).</summary>
public enum SetupSettingsField
{
    Theme,
    StatusColours,
    TextSize,
    Spacing,
    ReducedMotion,
    FocusIndicator,
    SquadmateMarks,
    DebriefReady,
    DataRefreshFailed,
    UpdateReady,
    RelayUnreachable,
    FleaOfferSold,
    DesktopPopup,
    QuietHours,
    QuietFrom,
    QuietUntil,
    ScreenshotCleanup,
    ScreenshotRetention,
}

/// <summary>How a diff value is written: as itself (a switch, a choice), a percentage, an hour of the day, or a number of hours.</summary>
public enum SetupSettingsValueKind
{
    Plain,
    Percent,
    HourOfDay,
    Hours,
}

/// <summary>One field that differs between two snapshots.</summary>
/// <param name="Field">Which setting.</param>
/// <param name="CurrentValue">What it is now: a bool, an enum value or a number.</param>
/// <param name="NewValue">What it would become.</param>
/// <param name="Kind">How the number is written.</param>
public sealed record SetupSettingsDiffEntry(
    SetupSettingsField Field,
    object CurrentValue,
    object NewValue,
    SetupSettingsValueKind Kind = SetupSettingsValueKind.Plain);

/// <summary>
/// What would actually change between two <see cref="SetupSettingsSnapshot"/>s, field by field.
/// </summary>
/// <remarks>
/// This is the logic #292 task 2 asks to be unit-tested directly: "Reset everything" and an
/// import both need to show a preview before they touch anything, and the preview is only honest
/// if it is computed the same way for both rather than eyeballed per call site.
/// </remarks>
public static class SetupSettingsDiff
{
    /// <summary>Every field that differs between <paramref name="current"/> and <paramref name="incoming"/>.</summary>
    /// <remarks>Both snapshots are normalized first, so a diff never proposes a value this build
    /// would immediately clamp away.</remarks>
    public static IReadOnlyList<SetupSettingsDiffEntry> Compare(
        SetupSettingsSnapshot current,
        SetupSettingsSnapshot incoming)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        current = current.Normalized();
        incoming = incoming.Normalized();

        var entries = new List<SetupSettingsDiffEntry>();
        void Add(SetupSettingsField field, object currentValue, object newValue, SetupSettingsValueKind kind = SetupSettingsValueKind.Plain)
        {
            if (!Equals(currentValue, newValue))
            {
                entries.Add(new SetupSettingsDiffEntry(field, currentValue, newValue, kind));
            }
        }

        Add(SetupSettingsField.Theme, current.Appearance.Theme, incoming.Appearance.Theme);
        Add(SetupSettingsField.StatusColours, current.Appearance.ColorVision, incoming.Appearance.ColorVision);
        Add(SetupSettingsField.TextSize, current.Appearance.TextScalePercent, incoming.Appearance.TextScalePercent, SetupSettingsValueKind.Percent);
        Add(SetupSettingsField.Spacing, current.Appearance.Density, incoming.Appearance.Density);
        Add(SetupSettingsField.ReducedMotion, current.Appearance.ReduceMotion, incoming.Appearance.ReduceMotion);
        Add(SetupSettingsField.FocusIndicator, current.Appearance.FocusAlwaysVisible, incoming.Appearance.FocusAlwaysVisible);

        Add(SetupSettingsField.SquadmateMarks, current.Notifications.SquadMark, incoming.Notifications.SquadMark);
        Add(SetupSettingsField.DebriefReady, current.Notifications.DebriefReady, incoming.Notifications.DebriefReady);
        Add(SetupSettingsField.DataRefreshFailed, current.Notifications.DataRefreshFailed, incoming.Notifications.DataRefreshFailed);
        Add(SetupSettingsField.UpdateReady, current.Notifications.UpdateReady, incoming.Notifications.UpdateReady);
        Add(SetupSettingsField.RelayUnreachable, current.Notifications.RelayUnreachable, incoming.Notifications.RelayUnreachable);
        Add(SetupSettingsField.FleaOfferSold, current.Notifications.FleaSold, incoming.Notifications.FleaSold);
        Add(SetupSettingsField.DesktopPopup, current.Notifications.ShowsDesktopPopup, incoming.Notifications.ShowsDesktopPopup);
        Add(SetupSettingsField.QuietHours, current.Notifications.QuietHours, incoming.Notifications.QuietHours);
        Add(SetupSettingsField.QuietFrom, current.Notifications.QuietFromHour, incoming.Notifications.QuietFromHour, SetupSettingsValueKind.HourOfDay);
        Add(SetupSettingsField.QuietUntil, current.Notifications.QuietToHour, incoming.Notifications.QuietToHour, SetupSettingsValueKind.HourOfDay);

        Add(SetupSettingsField.ScreenshotCleanup, current.ScreenshotRetention.IsEnabled, incoming.ScreenshotRetention.IsEnabled);
        Add(SetupSettingsField.ScreenshotRetention, current.ScreenshotRetention.RetentionHours, incoming.ScreenshotRetention.RetentionHours, SetupSettingsValueKind.Hours);

        return entries;
    }
}
