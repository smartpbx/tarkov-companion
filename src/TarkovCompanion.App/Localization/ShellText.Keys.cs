namespace TarkovCompanion.App.Localization;

public static partial class ShellText
{
    private const string OldPrefix = "V2.Shell.";

    /// <summary>The words for a shell key that moved here from V2ShellText, or null for one that has not.</summary>
    internal static string? Moved(string key) =>
        key.StartsWith(OldPrefix, StringComparison.Ordinal) &&
        UiText.English.ContainsKey(string.Concat("Shell.", key.AsSpan(OldPrefix.Length)))
            ? UiText.Get(string.Concat("Shell.", key.AsSpan(OldPrefix.Length)))
            : null;
}
