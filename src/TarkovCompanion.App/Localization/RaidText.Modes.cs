using System.Globalization;

namespace TarkovCompanion.App.Localization;

/// <summary>[#286] The Raid map's modes: Navigate, Inspect, Route (Draw's words are in RaidText).</summary>
public static partial class RaidText
{
    public static string ModeNavigate => UiText.Get("Raid.Mode.Navigate");
    public static string ModeInspect => UiText.Get("Raid.Mode.Inspect");
    public static string ModeRoute => UiText.Get("Raid.Mode.Route");
    public static string ModeGroup => UiText.Get("Raid.Mode.Group");
    public static string InspectHere => UiText.Get("Raid.Inspect.Here");
    public static string InspectNear(string place) => UiText.Format("Raid.Inspect.Near", place);
    public static string InspectExtracts => UiText.Get("Raid.Inspect.Extracts");
    public static string InspectExtractsCaveat => UiText.Get("Raid.Inspect.ExtractsCaveat");
    public static string InspectSpawns => UiText.Get("Raid.Inspect.Spawns");
    public static string InspectObjectives => UiText.Get("Raid.Inspect.Objectives");
    public static string InspectLoot => UiText.Get("Raid.Inspect.Loot");
    public static string InspectNothing => UiText.Get("Raid.Inspect.Nothing");
    public static string InspectClose => UiText.Get("Raid.Inspect.Close");
    public static string InspectHint => UiText.Get("Raid.Inspect.Hint");

    /// <summary>"Dorms · 140 m", or the name alone when the map cannot measure.</summary>
    public static string InspectDistance(string label, double? metres) => metres is { } value
        ? UiText.Format("Raid.Inspect.Metres", label, Math.Round(value).ToString("0", CultureInfo.CurrentCulture))
        : label;

    /// <summary>"ZB-1011 · 140 m · ~1–2 min".</summary>
    public static string InspectExtract(string label, double? metres, (int Low, int High)? minutes) =>
        metres is { } value && minutes is { } range
            ? UiText.Format("Raid.Inspect.MetresMinutes", label, Math.Round(value).ToString("0", CultureInfo.CurrentCulture), RouteMinutes(range.Low, range.High))
            : InspectDistance(label, metres);

    public static string PlannedRoute => UiText.Get("Raid.PlannedRoute");
    public static string PlannedRouteHint => UiText.Get("Raid.PlannedRoute.Hint");
    public static string PlannedRouteStops(int stops) =>
        UiText.Plural("Raid.PlannedRoute.Stops", stops, stops.ToString(CultureInfo.CurrentCulture));
    public static string PlannedRouteFull(string stops) => UiText.Format("Raid.PlannedRoute.Full", stops);
    public static string PlannedRouteUndo => UiText.Get("Raid.PlannedRoute.Undo");
    public static string PlannedRouteUndoTip => UiText.Get("Raid.PlannedRoute.UndoTip");
    public static string PlannedRouteClear => UiText.Get("Raid.PlannedRoute.Clear");
    public static string PlannedRouteClearTip => UiText.Get("Raid.PlannedRoute.ClearTip");
    public static string PlannedRouteSettingsTip => UiText.Get("Raid.PlannedRoute.SettingsTip");
    public static string PlannedRouteDoneTip => UiText.Get("Raid.PlannedRoute.DoneTip");
}
