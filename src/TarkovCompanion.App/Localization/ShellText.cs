namespace TarkovCompanion.App.Localization;

/// <summary>Every word the V2 shell's own chrome shows: top bar, rail, sub-tabs, banners (#314).</summary>
/// <remarks>
/// The shell names most of its words by key, from registries (a route's label key, a surface's
/// wording key), so those keys still go through <see cref="TarkovCompanion.App.Services.V2.Shell.V2ShellText.Get"/>,
/// which hands a moved "V2.Shell.X" to this table's "Shell.X". The members below are the words the
/// view binds with x:Static. The rail's "Update ready" is said as well as drawn as a dot (#294).
/// </remarks>
public static partial class ShellText
{
    public static string SwitchMap => UiText.Get("Shell.SwitchMap");
    public static string ReviewSync => UiText.Get("Shell.ReviewSync");
    public static string NotNow => UiText.Get("Shell.NotNow");
    public static string UpdateInstalled => UiText.Get("Shell.UpdateInstalled");
    public static string DismissWhatsNew => UiText.Get("Shell.DismissWhatsNew");
    public static string Dismiss => UiText.Get("Shell.Dismiss");
    public static string Close => UiText.Get("Shell.Close");
    public static string GotIt => UiText.Get("Shell.GotIt");
    public static string WhatsNew => UiText.Get("Shell.WhatsNew");
    public static string UpdatedTo(string version) => UiText.Format("Shell.UpdatedTo", version);
    public static string WhatsNewIn(string version) => UiText.Format("Shell.WhatsNewIn", version);
}
