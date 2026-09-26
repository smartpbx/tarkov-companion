using TarkovCompanion.App.Localization;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>
/// [#935] The interface language as a Backup &amp; reset source: the culture the file names (never the
/// developer's environment override), and null for "follow Windows", which removes the file.
/// </summary>
public static class SetupInterfaceLanguage
{
    public static (Func<string?> Get, Action<string?> Set) Source(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        return (
            () => UiCulturePreference.ReadFile(configDirectory),
            culture =>
            {
                if (string.IsNullOrWhiteSpace(culture))
                {
                    UiCulturePreference.Clear(configDirectory);
                }
                else
                {
                    UiCulturePreference.Write(configDirectory, culture.Trim());
                }
            });
    }
}
