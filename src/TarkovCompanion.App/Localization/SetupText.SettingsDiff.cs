using System.Globalization;
using System.Text;
using TarkovCompanion.App.Services.Settings;
using TarkovCompanion.Application.Services.Setup;
using TarkovCompanion.Core.Features;

namespace TarkovCompanion.App.Localization;

/// <summary>One row of the settings preview, in words.</summary>
public sealed record SetupSettingsDiffRow(string Field, string CurrentValue, string NewValue)
{
    public static SetupSettingsDiffRow From(SetupSettingsDiffEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new(
            SetupText.SettingsFieldWithDetail(entry.Field, entry.Detail),
            SetupText.SettingsValue(entry.CurrentValue, entry.Kind),
            SetupText.SettingsValue(entry.NewValue, entry.Kind));
    }
}

/// <summary>The settings preview's field names and values (#314); SetupSettingsDiff says which changed.</summary>
public static partial class SetupText
{
    /// <summary>What a setting is called; every <see cref="SetupSettingsField"/> has a name.</summary>
    public static string SettingsField(SetupSettingsField field) =>
        UiText.Get(string.Concat("Setup.SettingsDiff.Field.", field.ToString()));

    /// <summary>[#902] A row that stands for one of many: the flag's title, the layout key's
    /// registered name, or the map's own id.</summary>
    public static string SettingsFieldWithDetail(SetupSettingsField field, string? detail)
    {
        if (detail is null)
        {
            return SettingsField(field);
        }

        return field switch
        {
            SetupSettingsField.FeatureFlag => Flag.All.FirstOrDefault(flag => flag.Key == detail) is { } flag
                ? FlagTitle(flag)
                : detail,
            SetupSettingsField.Layout => SettingsRegistry.FindLayoutKey(detail) is { } registered
                ? registered.IsPrefix
                    ? UiText.Format(registered.LabelKey, detail[registered.Key.Length..])
                    : UiText.Get(registered.LabelKey)
                : detail,
            _ => UiText.Format("Setup.SettingsDiff.WithDetail", SettingsField(field), detail),
        };
    }

    /// <summary>"On", "Off", "High Contrast", "150", "07:00", "48h".</summary>
    public static string SettingsValue(object? value, SetupSettingsValueKind kind) => (value, kind) switch
    {
        (null, _) => UiText.Get("Setup.SettingsDiff.Default"),
        (string stored, SetupSettingsValueKind.Stored) => stored.Length <= 24 ? stored : UiText.Get("Setup.SettingsDiff.Custom"),
        (int number, SetupSettingsValueKind.Percent) => number.ToString(CultureInfo.CurrentCulture),
        (int hour, SetupSettingsValueKind.HourOfDay) => UiText.Format("Setup.SettingsDiff.HourOfDay", hour.ToString("00", CultureInfo.CurrentCulture)),
        (int hours, SetupSettingsValueKind.Hours) => UiText.Format("Setup.SettingsDiff.Hours", hours),
        (bool on, _) => on ? UiText.Get("Setup.SettingsDiff.On") : UiText.Get("Setup.SettingsDiff.Off"),
        (Enum choice, _) => SplitPascalCase(choice.ToString()),
        _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty,
    };

    /// <summary>"HighContrast" as "High Contrast", for a diff row a player reads rather than parses.</summary>
    private static string SplitPascalCase(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var result = new StringBuilder(value.Length + 4);
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
