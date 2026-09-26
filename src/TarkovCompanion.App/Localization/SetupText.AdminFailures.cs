using TarkovCompanion.App.Services.Settings;
using TarkovCompanion.Application.Services.Setup;

namespace TarkovCompanion.App.Localization;

/// <summary>
/// [#935] Why Backup &amp; reset did not do what was asked, in the interface language. The .NET text
/// behind each goes to the crash log; the status line used to show it, in English, whatever the language.
/// </summary>
public static partial class SetupText
{
    public static string AdminImportError(SetupSettingsImportError? error) => error switch
    {
        SetupSettingsImportError.Empty => UiText.Get("Setup.Admin.ImportError.Empty"),
        SetupSettingsImportError.Newer => UiText.Get("Setup.Admin.ImportError.Newer"),
        _ => UiText.Get("Setup.Admin.ImportError.Malformed"),
    };

    public static string AdminFileProblem(Exception exception, bool writing) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException => UiText.Get("Setup.Admin.File.NotFound"),
        UnauthorizedAccessException or System.Security.SecurityException => UiText.Get("Setup.Admin.File.Denied"),
        ArgumentException or NotSupportedException or PathTooLongException => UiText.Get("Setup.Admin.File.BadPath"),
        _ => UiText.Get(writing ? "Setup.Admin.File.NotWritten" : "Setup.Admin.File.NotRead"),
    };

    public static string AdminNotApplied => UiText.Get("Setup.Admin.NotApplied");

    public static string AdminPartlyApplied(IEnumerable<string> groups) =>
        UiText.Format("Setup.Admin.PartlyApplied", string.Join(", ", groups));

    public static string AdminWithRestart(string done) => UiText.Format("Setup.Admin.WithRestart", done);

    /// <summary>A registered settings group's name; every <see cref="SettingsDomain"/> has one.</summary>
    public static string SettingsDomainName(SettingsDomain domain) =>
        UiText.Get(string.Concat("Setup.Settings.Domain.", domain.ToString()));
}
