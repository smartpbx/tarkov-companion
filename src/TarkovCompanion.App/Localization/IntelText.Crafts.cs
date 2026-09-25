namespace TarkovCompanion.App.Localization;

// Intel > Crafts & barters (#314), moved off V2ShellText's "V2.Shell.Intel.Trade.*" entries.
public static partial class IntelText
{
    public static string CraftsSearchPlaceholder => UiText.Get("Intel.Crafts.SearchPlaceholder");
    public static string CraftsReadyNow => UiText.Get("Intel.Crafts.ReadyNow");
    public static string CraftsSortProfit => UiText.Get("Intel.Crafts.Sort.Profit");
    public static string CraftsSortDuration => UiText.Get("Intel.Crafts.Sort.Duration");
    public static string CraftsSortName => UiText.Get("Intel.Crafts.Sort.Name");
    public static string CraftsSortHeading => UiText.Get("Intel.Crafts.SortHeading");
    public static string CraftsCraft => UiText.Get("Intel.Crafts.Craft");
    public static string CraftsBarter => UiText.Get("Intel.Crafts.Barter");
    public static string CraftsProfitUnknown => UiText.Get("Intel.Crafts.ProfitUnknown");
    public static string CraftsReady => UiText.Get("Intel.Crafts.Ready");
    public static string CraftsLocked => UiText.Get("Intel.Crafts.Locked");
    public static string CraftsLevelUnknown => UiText.Get("Intel.Crafts.LevelUnknown");
    public static string CraftsEmpty => UiText.Get("Intel.Crafts.Empty");
    public static string CraftsNoneReadyNow => UiText.Get("Intel.Crafts.NoneReadyNow");
    public static string CraftsNeedLevels(int count) => UiText.Plural("Intel.Crafts.NeedLevels", count);
    public static string CraftsSetTraderLevels => UiText.Get("Intel.Crafts.SetTraderLevels");
    public static string CraftsSetHideoutLevels => UiText.Get("Intel.Crafts.SetHideoutLevels");
    public static string CraftsLoading => UiText.Get("Intel.Crafts.Loading");
    public static string CraftsChainHeading => UiText.Get("Intel.Crafts.ChainHeading");
    public static string CraftsCloseChain => UiText.Get("Intel.Crafts.CloseChain");
    public static string CraftsShowChain => UiText.Get("Intel.Crafts.ShowChain");

    public static string CraftsProfit(string roubles) => UiText.Format("Intel.Crafts.Profit", roubles);
    public static string CraftsYourLevel(int level) => UiText.Format("Intel.Crafts.YourLevel", level);
    public static string CraftsYourLoyalty(int level) => UiText.Format("Intel.Crafts.YourLoyalty", level);
    public static string CraftsUnknownCount(int count) => UiText.Format("Intel.Crafts.UnknownCount", count);
    public static string CraftsResults(long count) => UiText.Plural("Intel.Crafts.Results", count);
    public static string CraftsRoubles(long roubles) => UiText.Format("Intel.Crafts.Roubles", roubles);
    public static string CraftsCount(int count, string item) => UiText.Format("Intel.Crafts.Count", count, item);
    public static string CraftsLearnReady(string profit) => UiText.Format("Intel.Crafts.LearnReady", profit);
    public static string CraftsLearnLocked(string source, string level) => UiText.Format("Intel.Crafts.LearnLocked", source, level);
    public static string CraftsLearnCheck(string source, string level) => UiText.Format("Intel.Crafts.LearnCheck", source, level);
    public static string CraftsHoursMinutes(int hours, int minutes) => UiText.Format("Intel.Crafts.HoursMinutes", hours, minutes);
    public static string CraftsMinutes(int minutes) => UiText.Format("Intel.Crafts.Minutes", minutes);
}
