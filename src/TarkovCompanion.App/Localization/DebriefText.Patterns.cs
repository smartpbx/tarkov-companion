namespace TarkovCompanion.App.Localization;

/// <summary>Debrief's one line of the player's own patterns, exits and pace (#712 T8, 2-4).</summary>
public static partial class DebriefText
{
    public static string PatternRaids(int raids, string map) => UiText.Plural("Debrief.PatternRaids", raids, map);
    public static string PatternDeaths(int deaths) => UiText.Plural("Debrief.PatternDeaths", deaths);
    public static string PatternDeathsNear(int deaths, string place, int near) => UiText.Plural("Debrief.PatternDeathsNear", deaths, place, near);
    public static string PatternOut(int extracted) => UiText.Plural("Debrief.PatternOut", extracted);
    public static string PatternExit(string exit, int uses) => UiText.Plural("Debrief.PatternExit", uses, exit);
    public static string PatternPace(double metresPerSecond, int legs) => UiText.Plural("Debrief.PatternPace", legs, metresPerSecond);
    public static string PatternPaceFixed => UiText.Get("Debrief.PatternPaceFixed");
}
