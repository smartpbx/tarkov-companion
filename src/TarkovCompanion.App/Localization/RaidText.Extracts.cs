namespace TarkovCompanion.App.Localization;

/// <summary>Extract options, the lower-contact route and the objective route's origin (#314).</summary>
public static partial class RaidText
{
    public static string ExtractOptions => UiText.Get("Raid.ExtractOptions");
    public static string NoExtractData => UiText.Get("Raid.NoExtractData");
    public static string NoBag => UiText.Get("Raid.NoBag");
    public static string NoVest => UiText.Get("Raid.NoVest");
    public static string Offered => UiText.Get("Raid.Offered");
    public static string NotOffered => UiText.Get("Raid.NotOffered");
    public static string Transit => UiText.Get("Raid.Transit");
    public static string Pmc => UiText.Get("Raid.Pmc");
    public static string Scav => UiText.Get("Raid.Scav");
    public static string PmcAndScav => UiText.Get("Raid.PmcAndScav");
    public static string NeedsPower(string chain) => UiText.Format("Raid.NeedsPower", chain);
    public static string PowerChainJoiner => UiText.Get("Raid.PowerChainJoiner");
    public static string CostsCurrency(long count, string symbol) => UiText.Format("Raid.CostsCurrency", count, symbol);
    public static string NeedsKey(string key) => UiText.Format("Raid.NeedsKey", key);
    public static string NeedsCountOf(long count, string item) => UiText.Format("Raid.NeedsCountOf", count, item);
    public static string NeedsAnItem => UiText.Get("Raid.NeedsAnItem");
    public static string NeedsCountOfAnItem(long count) => UiText.Format("Raid.NeedsCountOfAnItem", count);
    public static string NoBackpack => UiText.Get("Raid.NoBackpack");
    public static string NoArmoredVest => UiText.Get("Raid.NoArmoredVest");
    public static string Bring(string items) => UiText.Format("Raid.Bring", items);
    public static string NeedsCoOpPartner => UiText.Get("Raid.NeedsCoOpPartner");
    public static string OneUse => UiText.Get("Raid.OneUse");
    public static string Train => UiText.Get("Raid.Train");
    public static string TrainArrivesWith(string subject, double earliest, double latest, double stays) => UiText.Format("Raid.TrainArrivesWith", subject, earliest, latest, stays);
    public static string TrainIn(string subject, int minutes, double stays) => UiText.Format("Raid.TrainIn", subject, minutes, stays);
    public static string TrainArrivingNow(string subject, int minutes, double stays) => UiText.Format("Raid.TrainArrivingNow", subject, minutes, stays);
    public static string TrainHere(string subject, int minutes) => UiText.Format("Raid.TrainHere", subject, minutes);
    public static string TrainMayStillBeHere(string subject, int minutes) => UiText.Format("Raid.TrainMayStillBeHere", subject, minutes);
    public static string TrainHasLeft(string subject) => UiText.Format("Raid.TrainHasLeft", subject);
    public static string LowerContactRoute => UiText.Get("Raid.LowerContactRoute");
    public static string SuggestedLowerContact => UiText.Get("Raid.SuggestedLowerContact");
    public static string DirectHigherContact => UiText.Get("Raid.DirectHigherContact");
    public static string WhyThisRoute => UiText.Get("Raid.WhyThisRoute");
    public static string RouteTo(string extract) => UiText.Format("Raid.RouteTo", extract);
    public static string RouteCaveat => UiText.Get("Raid.RouteCaveat");
    public static string RouteHint => UiText.Get("Raid.RouteHint");
    public static string DirectLineTo(string extract, string minutes) => UiText.Format("Raid.DirectLineTo", extract, minutes);
    public static string HigherContactDetail(string caveat) => UiText.Format("Raid.HigherContactDetail", caveat);
    public static string LowerContactRouteTo(string extract, string minutes) => UiText.Format("Raid.LowerContactRouteTo", extract, minutes);
    public static string LayerSuggestedRoutes => UiText.Get("Raid.LayerSuggestedRoutes");
    public static string FromYourLastScreenshot => UiText.Get("Raid.FromYourLastScreenshot");
    public static string FromTheSelectedSpawn => UiText.Get("Raid.FromTheSelectedSpawn");
    public static string YourLastScreenshot => UiText.Get("Raid.YourLastScreenshot");
    public static string TheSelectedSpawn => UiText.Get("Raid.TheSelectedSpawn");
}
