namespace TarkovCompanion.App.Localization;

/// <summary>The Marks and Group waypoints cards, the mark menus and a mark's lifetime (#314).</summary>
/// <remarks>
/// Scope and lifetime names are here rather than read from Application's RaidMarkLifetimes, whose
/// English stays for the stored and relayed marks it also describes.
/// </remarks>
public static partial class RaidText
{
    public static string Marks => UiText.Get("Raid.Marks");
    public static string NewMarks => UiText.Get("Raid.NewMarks");
    public static string JustMe => UiText.Get("Raid.JustMe");
    public static string Colour => UiText.Get("Raid.Colour");
    public static string AutoColour => UiText.Get("Raid.AutoColour");
    public static string AutoColourLetter => UiText.Get("Raid.AutoColourLetter");
    public static string MarksHint => UiText.Get("Raid.MarksHint");
    public static string Remove => UiText.Get("Raid.Remove");
    public static string Options => UiText.Get("Raid.Options");
    public static string MarkNamePlaceholder => UiText.Get("Raid.MarkNamePlaceholder");
    public static string Rename => UiText.Get("Raid.Rename");
    public static string GroupWaypoints => UiText.Get("Raid.GroupWaypoints");
    public static string ClearReached => UiText.Get("Raid.ClearReached");
    public static string ClearAll => UiText.Get("Raid.ClearAll");
    public static string Ping => UiText.Get("Raid.Ping");
    public static string Waypoint => UiText.Get("Raid.Waypoint");
    public static string KindGroup(string kind) => UiText.Format("Raid.KindGroup", kind);
    public static string RouteStop(int step) => UiText.Format("Raid.RouteStop", step);
    public static string RouteStops(int stops) => UiText.Format("Raid.RouteStops", stops);
    public static string WaypointRemovedBySquad(string name) => UiText.Format("Raid.WaypointRemovedBySquad", name);
    public static string WaypointsRemovedBySquad(int count) => UiText.Format("Raid.WaypointsRemovedBySquad", count);
    public static string LifetimePing => UiText.Get("Raid.LifetimePing");
    public static string LifetimeUntilRemoved => UiText.Get("Raid.LifetimeUntilRemoved");
    public static string LifetimeFiveMinutes => UiText.Get("Raid.LifetimeFiveMinutes");
    public static string LifetimeFifteenMinutes => UiText.Get("Raid.LifetimeFifteenMinutes");
    public static string LifetimeThisRaid => UiText.Get("Raid.LifetimeThisRaid");
    public static string MarkExpiring => UiText.Get("Raid.MarkExpiring");
    public static string MarkMinutesSecondsLeft(int minutes, int seconds) => UiText.Format("Raid.MarkMinutesSecondsLeft", minutes, seconds);
    public static string MarkSecondsLeft(int seconds) => UiText.Format("Raid.MarkSecondsLeft", seconds);
    public static string MarkThisRaid => UiText.Get("Raid.MarkThisRaid");
    public static string MarkUntilRemoved => UiText.Get("Raid.MarkUntilRemoved");
}
