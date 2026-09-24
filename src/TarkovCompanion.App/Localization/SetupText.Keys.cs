namespace TarkovCompanion.App.Localization;

public static partial class SetupText
{
    /// <summary>
    /// The words for a Setup or Home key that moved here from V2ShellText, or null for one that has not.
    /// </summary>
    /// <remarks>
    /// Section tabs, theme and density choices, profile outcomes and the Home step and health words
    /// are chosen by key at run time, so those keys keep their old "V2.Setup." and "V2.Home." names in
    /// code and are read from the table as "Setup." and "Setup.Home.".
    /// </remarks>
    internal static string? Moved(string key)
    {
        var moved = key.StartsWith("V2.Setup.", StringComparison.Ordinal) ? string.Concat("Setup.", key.AsSpan("V2.Setup.".Length))
            : key.StartsWith("V2.Home.", StringComparison.Ordinal) ? string.Concat("Setup.Home.", key.AsSpan("V2.Home.".Length))
            : null;
        return moved is not null && UiText.English.ContainsKey(moved) ? UiText.Get(moved) : null;
    }
}
