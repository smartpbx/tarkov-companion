namespace TarkovCompanion.App.Localization;

/// <summary>The Raid plan's Corrections card (#286, #314).</summary>
public static partial class RaidText
{
    public static string Corrections => UiText.Get("Raid.Corrections");
    public static string NothingToCorrect => UiText.Get("Raid.NothingToCorrect");
    public static string Side => UiText.Get("Raid.Side");
    public static string ReturnToAutomatic => UiText.Get("Raid.ReturnToAutomatic");
    public static string Clock => UiText.Get("Raid.Clock");
    public static string TimeLeftName => UiText.Get("Raid.TimeLeftName");
    public static string TimeLeftTip => UiText.Get("Raid.TimeLeftTip");
    public static string SetTimeLeft => UiText.Get("Raid.SetTimeLeft");
    public static string StartedNow => UiText.Get("Raid.StartedNow");
    public static string StartedNowTip => UiText.Get("Raid.StartedNowTip");
    public static string Extracts => UiText.Get("Raid.Extracts");
    public static string PressOffered => UiText.Get("Raid.PressOffered");
    public static string Automatic => UiText.Get("Raid.Automatic");
    public static string Manual => UiText.Get("Raid.Manual");
    public static string DuringARaid => UiText.Get("Raid.DuringARaid");
    public static string AllAutomatic => UiText.Get("Raid.AllAutomatic");
    public static string SetByYou(int count) => UiText.Format("Raid.SetByYou", count);
    public static string Unknown => UiText.Get("Raid.Unknown");
    public static string NoneMarkedOffered => UiText.Get("Raid.NoneMarkedOffered");
    public static string OfferedCount(int count) => UiText.Format("Raid.OfferedCount", count);
    public static string TimeLeftFormatHint => UiText.Get("Raid.TimeLeftFormatHint");
    public static string NoRaidInProgress => UiText.Get("Raid.NoRaidInProgress");
}
