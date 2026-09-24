using TarkovCompanion.Core.Domain.Ammo;

namespace TarkovCompanion.App.Localization;

/// <summary>Every word the Intel workspace shows, typed, from Localization/Strings (#314).</summary>
/// <remarks>
/// Moved off literals (and off V2ShellText) the way <see cref="PlanText"/> was. The Intel tabs share
/// this one class across IntelText*.cs partial files: this file is the Items tab, its item detail,
/// the compare table and the cheapest-way chain, and every other tab's members start with the tab's
/// name (Ammo…, Keys…, Flea…, Crafts…, Stash…, LootScan…) so two tabs cannot both claim "Refresh".
/// Views bind these with x:Static, so a label that is not here fails the XAML compile. A counted
/// phrase is a method taking the count. Item, trader, map, caliber and quest names are data and
/// pass through as arguments untranslated.
/// </remarks>
public static partial class IntelText
{
    public static string Heading => UiText.Get("Intel.Heading");
    public static string UnknownItem => UiText.Get("Intel.UnknownItem");
    public static string Close => UiText.Get("Intel.Close");
    public static string Loading => UiText.Get("Intel.Loading");
    public static string NotFound => UiText.Get("Intel.NotFound");
    public static string KindItem => UiText.Get("Intel.Kind.Item");
    public static string KindKey => UiText.Get("Intel.Kind.Key");
    public static string KindAmmo => UiText.Get("Intel.Kind.Ammo");
    public static string Wiki => UiText.Get("Intel.Wiki");
    public static string DetailShortName => UiText.Get("Intel.Detail.ShortName");
    public static string DetailCategory => UiText.Get("Intel.Detail.Category");
    public static string DetailSize => UiText.Get("Intel.Detail.Size");
    public static string DetailSizeValue(int width, int height, int slots) => UiText.Format("Intel.Detail.SizeValue", width, height, slots);
    public static string DetailPrice => UiText.Get("Intel.Detail.Price");
    public static string DetailPriceValue(long roubles, string channel) => UiText.Format("Intel.Detail.PriceValue", roubles, channel);
    public static string DetailPriceUnknown => UiText.Get("Intel.Detail.PriceUnknown");
    public static string DetailFlea => UiText.Get("Intel.Detail.Flea");
    public static string DetailFleaAllowed => UiText.Get("Intel.Detail.FleaAllowed");
    public static string DetailFleaNotAllowed => UiText.Get("Intel.Detail.FleaNotAllowed");
    public static string DetailNeed => UiText.Get("Intel.Detail.Need");
    public static string DetailNeedValue(int active, int later, int hideout) => UiText.Format("Intel.Detail.NeedValue", active, later, hideout);
    public static string DetailNeedNone => UiText.Get("Intel.Detail.NeedNone");
    public static string DetailOpens => UiText.Get("Intel.Detail.Opens");
    public static string DetailOpensMapAndLocks(string map, string locks) => UiText.Format("Intel.Detail.OpensMapAndLocks", map, locks);
    public static string DetailOpensUnknown => UiText.Get("Intel.Detail.OpensUnknown");
    public static string DetailDamage => UiText.Get("Intel.Detail.Damage");
    public static string DetailPenetration => UiText.Get("Intel.Detail.Penetration");
    public static string DetailTier => UiText.Get("Intel.Detail.Tier");
    public static string DetailAdvice => UiText.Get("Intel.Detail.Advice");
    public static string DetailAmmoUnknown => UiText.Get("Intel.Detail.AmmoUnknown");
    public static string DetailUses => UiText.Get("Intel.Detail.Uses");
    public static string DetailKeyCost => UiText.Get("Intel.Detail.KeyCost");
    public static string DetailArmorDamage => UiText.Get("Intel.Detail.ArmorDamage");
    public static string DetailArmorDamageValue(int percent) => UiText.Format("Intel.Detail.ArmorDamageValue", percent);
    public static string DetailFragmentation => UiText.Get("Intel.Detail.Fragmentation");
    public static string SearchPlaceholder => UiText.Get("Intel.SearchPlaceholder");
    public static string ClearSearch => UiText.Get("Intel.ClearSearch");
    public static string FilterAll => UiText.Get("Intel.Filter.All");
    public static string FilterItems => UiText.Get("Intel.Filter.Items");
    public static string FilterAmmo => UiText.Get("Intel.Filter.Ammo");
    public static string FilterKeys => UiText.Get("Intel.Filter.Keys");
    public static string NoKindMatch => UiText.Get("Intel.NoKindMatch");
    public static string NoSelection => UiText.Get("Intel.NoSelection");
    public static string NoPrice => UiText.Get("Intel.NoPrice");
    public static string Roubles(long roubles) => UiText.Format("Intel.Roubles", roubles);
    public static string Slots(int width, int height) => UiText.Format("Intel.Slots", width, height);
    public static string KeyInfo => UiText.Get("Intel.KeyInfo");
    public static string VerdictKeep(int count) => UiText.Format("Intel.Verdict.Keep", count);
    public static string VerdictKeepSome => UiText.Get("Intel.Verdict.KeepSome");
    public static string VerdictNoNeed => UiText.Get("Intel.Verdict.NoNeed");
    public static string NeedTracked(int count) => UiText.Format("Intel.Need.Tracked", count);
    public static string NeedHideout(int count) => UiText.Format("Intel.Need.Hideout", count);
    public static string NeedFoundInRaid(int count) => UiText.Format("Intel.Need.FoundInRaid", count);
    public static string BestSale(string channel) => UiText.Format("Intel.BestSale", channel);
    public static string Prices => UiText.Get("Intel.Prices");
    public static string NoPrices => UiText.Get("Intel.NoPrices");
    public static string FleaMarket => UiText.Get("Intel.FleaMarket");
    public static string NotOnFlea => UiText.Get("Intel.NotOnFlea");
    public static string Unavailable => UiText.Get("Intel.Unavailable");
    public static string PriceUpdated(string age) => UiText.Format("Intel.PriceUpdated", age);
    public static string JustNow => UiText.Get("Intel.JustNow");
    public static string Ago(string age) => UiText.Format("Intel.Ago", age);
    public static string DaysAgo(int days) => UiText.Format("Intel.DaysAgo", days);
    public static string BestTrader => UiText.Get("Intel.BestTrader");
    public static string Last24Hours => UiText.Get("Intel.Last24Hours");
    public static string Last7Days => UiText.Get("Intel.Last7Days");
    public static string OnePriceSoFar => UiText.Get("Intel.OnePriceSoFar");
    public static string HistoryRange(string low, string average, string high) => UiText.Format("Intel.HistoryRange", low, average, high);
    public static string Range(string low, string high) => UiText.Format("Intel.Range", low, high);
    public static string RangeWithAverage(string low, string high, string average) => UiText.Format("Intel.RangeWithAverage", low, high, average);
    public static string Sources => UiText.Get("Intel.Sources");
    public static string WhereToBuy => UiText.Get("Intel.WhereToBuy");
    public static string NoAcquisitionSources => UiText.Get("Intel.NoAcquisitionSources");
    public static string AcquisitionBarter => UiText.Get("Intel.Acquisition.Barter");
    public static string AcquisitionPriceUnknown => UiText.Get("Intel.Acquisition.PriceUnknown");
    public static string AcquisitionBarterInput(int count, string item) => UiText.Format("Intel.Acquisition.BarterInput", count, item);
    public static string Ballistics => UiText.Get("Intel.Ballistics");
    public static string Caliber => UiText.Get("Intel.Caliber");
    public static string ArmorClasses => UiText.Get("Intel.ArmorClasses");
    public static string ArmorClassFormat => UiText.Get("Intel.ArmorClassFormat");
    public static string ArmorPoor => UiText.Get("Intel.Armor.Poor");
    public static string ArmorLimited => UiText.Get("Intel.Armor.Limited");
    public static string ArmorFair => UiText.Get("Intel.Armor.Fair");
    public static string ArmorGood => UiText.Get("Intel.Armor.Good");
    public static string ArmorExcellent => UiText.Get("Intel.Armor.Excellent");
    public static string PerSlot(long roubles) => UiText.Format("Intel.PerSlot", roubles);
    public static string PerSlotHeading => UiText.Get("Intel.PerSlotHeading");
    public static string SortRelevance => UiText.Get("Intel.Sort.Relevance");
    public static string SortPrice => UiText.Get("Intel.Sort.Price");
    public static string SortPerSlot => UiText.Get("Intel.Sort.PerSlot");
    public static string SortName => UiText.Get("Intel.Sort.Name");
    public static string SortHeading => UiText.Get("Intel.SortHeading");
    public static string MatchedAs(string text) => UiText.Format("Intel.MatchedAs", text);
    public static string CategoryAmmunitionPack => UiText.Get("Intel.Category.AmmunitionPack");
    public static string CategoryUnknown => UiText.Get("Intel.Category.Unknown");
    public static string HomeNeededNow => UiText.Get("Intel.Home.NeededNow");
    public static string HomePinned => UiText.Get("Intel.Home.Pinned");
    public static string HomeRecent => UiText.Get("Intel.Home.Recent");
    public static string HomeHighestValue => UiText.Get("Intel.Home.HighestValue");
    public static string HomeVia(string channel) => UiText.Format("Intel.Home.Via", channel);
    public static string HomeNeedCount(int count) => UiText.Format("Intel.Home.NeedCount", count);
    public static string Fee => UiText.Get("Intel.Fee");
    public static string FeeUnknown => UiText.Get("Intel.FeeUnknown");
    public static string SellingWithTrader(int held, string asking, string net, string trader, string traderPrice) => UiText.Format("Intel.SellingWithTrader", held, asking, net, trader, traderPrice);
    public static string SellingWithoutTrader(int held, string asking, string net) => UiText.Format("Intel.SellingWithoutTrader", held, asking, net);
    public static string KeepHeading => UiText.Get("Intel.Keep.Heading");
    public static string KeepNone => UiText.Get("Intel.Keep.None");
    public static string KeepQuest(string quest, int count) => UiText.Format("Intel.Keep.Quest", quest, count);
    public static string KeepQuestFoundInRaid(string quest, int count) => UiText.Format("Intel.Keep.QuestFoundInRaid", quest, count);
    public static string KeepHideout(string station, int level, int count) => UiText.Format("Intel.Keep.Hideout", station, level, count);
    public static string EventAllergic => UiText.Get("Intel.Event.Allergic");
    public static string EventSafe => UiText.Get("Intel.Event.Safe");
    public static string EventUntested => UiText.Get("Intel.Event.Untested");
    public static string MadeBy => UiText.Get("Intel.MadeBy");
    public static string UsedIn => UiText.Get("Intel.UsedIn");
    public static string NoneMadeBy => UiText.Get("Intel.NoneMadeBy");
    public static string NoneUsedIn => UiText.Get("Intel.NoneUsedIn");
    public static string ChainHeading => UiText.Get("Intel.Chain.Heading");
    public static string ChainLoading => UiText.Get("Intel.Chain.Loading");
    public static string ChainEmpty => UiText.Get("Intel.Chain.Empty");
    public static string ChainTotal(string roubles) => UiText.Format("Intel.Chain.Total", roubles);
    public static string ChainBuy => UiText.Get("Intel.Chain.Buy");
    public static string ChainCraft => UiText.Get("Intel.Chain.Craft");
    public static string ChainBarter => UiText.Get("Intel.Chain.Barter");
    public static string ChainFuel(string roubles) => UiText.Format("Intel.Chain.Fuel", roubles);
    public static string ChainTime(string roubles) => UiText.Format("Intel.Chain.Time", roubles);
    public static string ChainOpportunity(string roubles) => UiText.Format("Intel.Chain.Opportunity", roubles);
    public static string ChainCycle => UiText.Get("Intel.Chain.Cycle");
    public static string ChainDepth => UiText.Get("Intel.Chain.Depth");
    public static string ChainSearchLimit => UiText.Get("Intel.Chain.SearchLimit");
    public static string ChainUpdated(string moment) => UiText.Format("Intel.Chain.Updated", moment);
    public static string ChainClose => UiText.Get("Intel.Chain.Close");
    public static string ChainStep(string verb, int quantity, string item) => UiText.Format("Intel.Chain.Step", verb, quantity, item);
    public static string ChainSourceDuration(string source, string duration) => UiText.Format("Intel.Chain.SourceDuration", source, duration);
    public static string ChainHoursMinutes(int hours, int minutes) => UiText.Format("Intel.Chain.HoursMinutes", hours, minutes);
    public static string ChainMinutes(int minutes) => UiText.Format("Intel.Chain.Minutes", minutes);
    public static string CompareRemove(string item) => UiText.Format("Intel.Compare.Remove", item);
    public static string CompareAdd(string item) => UiText.Format("Intel.Compare.Add", item);
    public static string CompareBest(string value) => UiText.Format("Intel.Compare.Best", value);
    public static string CompareOpen(int count) => UiText.Format("Intel.Compare.Open", count);
    public static string Compare => UiText.Get("Intel.Compare.Compare");
    public static string CompareAddOneMore => UiText.Get("Intel.Compare.AddOneMore");
    public static string CompareClear => UiText.Get("Intel.Compare.Clear");
    public static string CompareBackToItem => UiText.Get("Intel.Compare.BackToItem");
    public static string CompareComparing => UiText.Get("Intel.Compare.Comparing");
    public static string CompareFull => UiText.Get("Intel.Compare.Full");
    public static string CompareHolds(int count) => UiText.Format("Intel.Compare.Holds", count);
    public static string CompareLoading => UiText.Get("Intel.Compare.Loading");
    public static string CompareAmmoHeading => UiText.Get("Intel.Compare.AmmoHeading");
    public static string CompareArmorHeading => UiText.Get("Intel.Compare.ArmorHeading");
    public static string CompareKeysHeading => UiText.Get("Intel.Compare.KeysHeading");
    public static string CompareItemsHeading => UiText.Get("Intel.Compare.ItemsHeading");
    public static string CompareMixedKinds => UiText.Get("Intel.Compare.MixedKinds");
    public static string ArmorRating(ArmorEffectiveness rating) => rating switch
    {
        ArmorEffectiveness.Poor => ArmorPoor,
        ArmorEffectiveness.Limited => ArmorLimited,
        ArmorEffectiveness.Fair => ArmorFair,
        ArmorEffectiveness.Good => ArmorGood,
        ArmorEffectiveness.Excellent => ArmorExcellent,
        _ => rating.ToString(),
    };

    public static string ResultCount(long count) => UiText.Plural("Intel.ResultCount", count);
}
