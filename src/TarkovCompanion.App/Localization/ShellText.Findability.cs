namespace TarkovCompanion.App.Localization;

/// <summary>[#902 P9] The words for finding things: settings in the palette, What's new, links to Intel.</summary>
public static partial class ShellText
{
    public static string CaptureShortcutSwitch => UiText.Get("Shell.CaptureShortcutSwitch");
    public static string WhatsNewInThisBuild => UiText.Get("Shell.WhatsNewInThisBuild");
    public static string OpenInIntel => UiText.Get("Shell.OpenInIntel");
    public static string SettingWhere(string page, string section) => UiText.Format("Shell.SettingWhere", page, section);
    public static string TrayNotifications(int count) => count > 0
        ? UiText.Format("Shell.TrayNotificationsCount", count)
        : UiText.Get("Shell.TrayNotifications");
}
