using System.Globalization;

namespace TarkovCompanion.Application.Services.Setup;

/// <summary>One field that differs between two snapshots, in words a confirm dialog can show.</summary>
/// <param name="Field">What the field is called, e.g. "Theme".</param>
/// <param name="CurrentValue">What it is now.</param>
/// <param name="NewValue">What it would become.</param>
public sealed record SetupSettingsDiffEntry(string Field, string CurrentValue, string NewValue);

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
        void Add(string field, object currentValue, object newValue, Func<object, string>? describe = null)
        {
            if (!Equals(currentValue, newValue))
            {
                describe ??= Describe;
                entries.Add(new SetupSettingsDiffEntry(field, describe(currentValue), describe(newValue)));
            }
        }

        Add("Theme", current.Appearance.Theme, incoming.Appearance.Theme);
        Add("Status colours", current.Appearance.ColorVision, incoming.Appearance.ColorVision);
        Add("Text size", current.Appearance.TextScalePercent, incoming.Appearance.TextScalePercent, DescribePercent);
        Add("Spacing", current.Appearance.Density, incoming.Appearance.Density);
        Add("Reduced motion", current.Appearance.ReduceMotion, incoming.Appearance.ReduceMotion);
        Add("Focus indicator", current.Appearance.FocusAlwaysVisible, incoming.Appearance.FocusAlwaysVisible);

        Add("Squadmate marks", current.Notifications.SquadMark, incoming.Notifications.SquadMark);
        Add("Debrief ready", current.Notifications.DebriefReady, incoming.Notifications.DebriefReady);
        Add("Data refresh failed", current.Notifications.DataRefreshFailed, incoming.Notifications.DataRefreshFailed);
        Add("Update ready", current.Notifications.UpdateReady, incoming.Notifications.UpdateReady);
        Add("Relay unreachable", current.Notifications.RelayUnreachable, incoming.Notifications.RelayUnreachable);
        Add("Flea offer sold", current.Notifications.FleaSold, incoming.Notifications.FleaSold);
        Add("Desktop pop-up", current.Notifications.ShowsDesktopPopup, incoming.Notifications.ShowsDesktopPopup);
        Add("Quiet hours", current.Notifications.QuietHours, incoming.Notifications.QuietHours);
        Add("Quiet from", current.Notifications.QuietFromHour, incoming.Notifications.QuietFromHour, DescribeHourOfDay);
        Add("Quiet until", current.Notifications.QuietToHour, incoming.Notifications.QuietToHour, DescribeHourOfDay);

        Add("Screenshot cleanup", current.ScreenshotRetention.IsEnabled, incoming.ScreenshotRetention.IsEnabled);
        Add("Screenshot retention", current.ScreenshotRetention.RetentionHours, incoming.ScreenshotRetention.RetentionHours, DescribeHours);

        return entries;
    }

    private static string Describe(object value) => value switch
    {
        bool flag => flag ? "On" : "Off",
        Enum e => SplitPascalCase(e.ToString()),
        _ => value.ToString() ?? string.Empty,
    };

    private static string DescribePercent(object value) => ((int)value).ToString(CultureInfo.InvariantCulture);

    private static string DescribeHourOfDay(object value) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)value:00}:00");

    private static string DescribeHours(object value) =>
        string.Format(CultureInfo.InvariantCulture, "{0}h", (int)value);

    /// <summary>"HighContrast" as "High Contrast", for a diff row a player reads rather than parses.</summary>
    private static string SplitPascalCase(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var result = new System.Text.StringBuilder(value.Length + 4);
        result.Append(value[0]);
        for (var index = 1; index < value.Length; index++)
        {
            if (char.IsUpper(value[index]) && !char.IsUpper(value[index - 1]))
            {
                result.Append(' ');
            }

            result.Append(value[index]);
        }

        return result.ToString();
    }
}
