namespace TarkovCompanion.App.Localization;

/// <summary>[#881] The words of the shell's "Update ready · Restart" notice above the gear.</summary>
public static partial class ShellText
{
    public static string UpdateReady => UiText.Get("Shell.Nav.UpdateWaiting");
    public static string UpdateReadyRestart => UiText.Get("Shell.UpdateReadyRestart");
    public static string UpdateReadyDismiss => UiText.Get("Shell.UpdateReadyDismiss");
    public static string UpdateReadyVersion(string version) => UiText.Format("Shell.UpdateReadyVersion", version);
}
