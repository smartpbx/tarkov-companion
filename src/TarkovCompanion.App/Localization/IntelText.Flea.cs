namespace TarkovCompanion.App.Localization;

// Intel > Flea (#314): the lookup, the photographed flea screen and its row reasons (#842), and the
// V1 Flea page's words that this tab shows. Numbers arrive already formatted in the caller's culture.
public static partial class IntelText
{
    public static string FleaLookUpPlaceholder => UiText.Get("Intel.Flea.LookUpPlaceholder");
    public static string FleaLookUpValue => UiText.Get("Intel.Flea.LookUpValue");
    public static string FleaTitle => UiText.Get("Intel.Flea.Title");
    public static string FleaEmptyHint => UiText.Get("Intel.Flea.EmptyHint");
    public static string FleaOpenInIntel => UiText.Get("Intel.Flea.OpenInIntel");
    public static string FleaPricesAndNeeds => UiText.Get("Intel.Flea.PricesAndNeeds");
    public static string FleaPickForHistory => UiText.Get("Intel.Flea.PickForHistory");
    public static string FleaObservedPrices => UiText.Get("Intel.Flea.ObservedPrices");
    public static string FleaSoldWhilePlaying => UiText.Get("Intel.Flea.SoldWhilePlaying");

    public static string FleaReasonNoComparison => UiText.Get("Intel.Flea.Reason.NoComparison");
    public static string FleaReasonItemNotRead => UiText.Get("Intel.Flea.Reason.ItemNotRead");
    public static string FleaReasonItemUncertain => UiText.Get("Intel.Flea.Reason.ItemUncertain");
    public static string FleaReasonPriceUncertain => UiText.Get("Intel.Flea.Reason.PriceUncertain");
    public static string FleaReasonNoPrice => UiText.Get("Intel.Flea.Reason.NoPrice");
    public static string FleaReasonNoFleaPrice => UiText.Get("Intel.Flea.Reason.NoFleaPrice");
    public static string FleaReasonFeeUnknown => UiText.Get("Intel.Flea.Reason.FeeUnknown");
    public static string FleaReasonTraderUnknown => UiText.Get("Intel.Flea.Reason.TraderUnknown");
    public static string FleaReasonSizeUnknown => UiText.Get("Intel.Flea.Reason.SizeUnknown");
    public static string FleaReasonNotEnough => UiText.Get("Intel.Flea.Reason.NotEnough");
    public static string FleaReasonATrader => UiText.Get("Intel.Flea.Reason.ATrader");
    public static string FleaReasonOnFleaAfterFee => UiText.Get("Intel.Flea.Reason.OnFleaAfterFee");
    public static string FleaReasonEven => UiText.Get("Intel.Flea.Reason.Even");
    public static string FleaReasonComparedTrader => UiText.Get("Intel.Flea.Reason.ComparedTrader");
    public static string FleaReasonComparedFlea => UiText.Get("Intel.Flea.Reason.ComparedFlea");

    public static string FleaReasonPricesOld(string age) => UiText.Format("Intel.Flea.Reason.PricesOld", age);
    public static string FleaReasonToTrader(string trader) => UiText.Format("Intel.Flea.Reason.ToTrader", trader);
    public static string FleaReasonProfit(string profit, string channel) => UiText.Format("Intel.Flea.Reason.Profit", profit, channel);
    public static string FleaReasonLoss(string loss) => UiText.Format("Intel.Flea.Reason.Loss", loss);

    /// <summary>"1 day", "10 days"; <paramref name="days"/> picks the form, <paramref name="daysText"/> is shown.</summary>
    public static string FleaAgeDays(long days, string daysText) => UiText.Plural("Intel.Flea.Age.Days", days, daysText);
    public static string FleaAgeHours(string hours) => UiText.Format("Intel.Flea.Age.Hours", hours);
    public static string FleaAgeMinutes(string minutes) => UiText.Format("Intel.Flea.Age.Minutes", minutes);

    public static string FleaSeenAt(string time) => UiText.Format("Intel.Flea.SeenAt", time);
    public static string FleaMinutesAgo(string minutes) => UiText.Format("Intel.Flea.MinutesAgo", minutes);
    public static string FleaHoursAgo(string hours) => UiText.Format("Intel.Flea.HoursAgo", hours);
    public static string FleaOnDate(string date) => UiText.Format("Intel.Flea.OnDate", date);
    public static string FleaMayBeGone(string seen, string ago) => UiText.Format("Intel.Flea.MayBeGone", seen, ago);

    public static string FleaRowBestBuy => UiText.Get("Intel.Flea.Row.BestBuy");
    public static string FleaRowCountNotRead => UiText.Get("Intel.Flea.Row.CountNotRead");
    public static string FleaRowConditionNotRead => UiText.Get("Intel.Flea.Row.ConditionNotRead");
    public static string FleaRowActionGoodBuy => UiText.Get("Intel.Flea.Row.ActionGoodBuy");
    public static string FleaRowActionSkip => UiText.Get("Intel.Flea.Row.ActionSkip");
    public static string FleaRowActionReview => UiText.Get("Intel.Flea.Row.ActionReview");
    public static string FleaRowRank(string rank) => UiText.Format("Intel.Flea.Row.Rank", rank);
    public static string FleaRowEach(string price) => UiText.Format("Intel.Flea.Row.Each", price);
    public static string FleaRowEachConverted(string original, string roubles) => UiText.Format("Intel.Flea.Row.EachConverted", original, roubles);

    /// <summary>"1 unit", or "4 units · ₽1,000 for the lot"; <paramref name="count"/> picks the form.</summary>
    public static string FleaRowUnits(long count, string countText, string lot) => UiText.Plural("Intel.Flea.Row.Units", count, countText, lot);
    public static string FleaRowConfidence(string percent) => UiText.Format("Intel.Flea.Row.Confidence", percent);
    public static string FleaRowCondition(string kind, string current, string maximum) => UiText.Format("Intel.Flea.Row.Condition", kind, current, maximum);
    public static string FleaRowOtherReads(string reads) => UiText.Format("Intel.Flea.Row.OtherReads", reads);
    public static string FleaRowOtherRead(string item, string action) => UiText.Format("Intel.Flea.Row.OtherRead", item, action);
    public static string FleaRowRead(string source) => UiText.Format("Intel.Flea.Row.Read", source);
    public static string FleaRowRules(string version) => UiText.Format("Intel.Flea.Row.Rules", version);

    public static string FleaConditionUses => UiText.Get("Intel.Flea.Condition.Uses");
    public static string FleaConditionCharges => UiText.Get("Intel.Flea.Condition.Charges");
    public static string FleaConditionResource => UiText.Get("Intel.Flea.Condition.Resource");
    public static string FleaConditionDurability => UiText.Get("Intel.Flea.Condition.Durability");

    public static string FleaVerdictGoodBuy => UiText.Get("Intel.Flea.Verdict.GoodBuy");
    public static string FleaVerdictUnderAverage => UiText.Get("Intel.Flea.Verdict.UnderAverage");
    public static string FleaVerdictOverAverage => UiText.Get("Intel.Flea.Verdict.OverAverage");
    public static string FleaVerdictNoComparison => UiText.Get("Intel.Flea.Verdict.NoComparison");

    public static string FleaScanOffersPhotographed => UiText.Get("Intel.Flea.Scan.OffersPhotographed");
    public static string FleaScanNameIllegible => UiText.Get("Intel.Flea.Scan.NameIllegible");
    public static string FleaScanReadFromScreenshot => UiText.Get("Intel.Flea.Scan.ReadFromScreenshot");
    public static string FleaScanBestTrader => UiText.Get("Intel.Flea.Scan.BestTrader");
    public static string FleaScanNoTrader => UiText.Get("Intel.Flea.Scan.NoTrader");
    public static string FleaScanNoAverage => UiText.Get("Intel.Flea.Scan.NoAverage");
    public static string FleaScanOfflineCached => UiText.Get("Intel.Flea.Scan.OfflineCached");
    public static string FleaScanOffersFor(string item) => UiText.Format("Intel.Flea.Scan.OffersFor", item);
    public static string FleaScanReadCouldBe(string alternates) => UiText.Format("Intel.Flea.Scan.ReadCouldBe", alternates);
    public static string FleaScanTraderPays(string trader, string roubles) => UiText.Format("Intel.Flea.Scan.TraderPays", trader, roubles);
    public static string FleaScanAverageAfterFee(string average, string net, string fee) => UiText.Format("Intel.Flea.Scan.AverageAfterFee", average, net, fee);
    public static string FleaScanAverageNoRates(string average) => UiText.Format("Intel.Flea.Scan.AverageNoRates", average);
    public static string FleaScanAverageNoBase(string average) => UiText.Format("Intel.Flea.Scan.AverageNoBase", average);
    public static string FleaScanOfflineFrom(string moment) => UiText.Format("Intel.Flea.Scan.OfflineFrom", moment);
    public static string FleaScanOverADay(string moment) => UiText.Format("Intel.Flea.Scan.OverADay", moment);
    public static string FleaScanSummaryNone(string rows) => UiText.Format("Intel.Flea.Scan.SummaryNone", rows);
    public static string FleaScanSummary(string rows, string good) => UiText.Format("Intel.Flea.Scan.Summary", rows, good);

    public static string FleaPageTitle => UiText.Get("Intel.Flea.Page.Title");
    public static string FleaPageSubtitle => UiText.Get("Intel.Flea.Page.Subtitle");
    public static string FleaPageNotLoaded => UiText.Get("Intel.Flea.Page.NotLoaded");
    public static string FleaPageSalesNotObserved => UiText.Get("Intel.Flea.Page.SalesNotObserved");
    public static string FleaPageReadyToSearch => UiText.Get("Intel.Flea.Page.ReadyToSearch");
    public static string FleaPagePickAnItem => UiText.Get("Intel.Flea.Page.PickAnItem");
    public static string FleaPageItemNotInCatalog => UiText.Get("Intel.Flea.Page.ItemNotInCatalog");
    public static string FleaPageStateNotLoaded => UiText.Get("Intel.Flea.Page.StateNotLoaded");
    public static string FleaPageEnterQuery => UiText.Get("Intel.Flea.Page.EnterQuery");
    public static string FleaPageSearching => UiText.Get("Intel.Flea.Page.Searching");
    public static string FleaPageNotOnFlea => UiText.Get("Intel.Flea.Page.NotOnFlea");
    public static string FleaPageNoMatch => UiText.Get("Intel.Flea.Page.NoMatch");
    public static string FleaPageReadingHistory => UiText.Get("Intel.Flea.Page.ReadingHistory");
    public static string FleaPagePerSlotUnavailable => UiText.Get("Intel.Flea.Page.PerSlotUnavailable");
    public static string FleaPageHistoryNotBuilt => UiText.Get("Intel.Flea.Page.HistoryNotBuilt");
    public static string FleaPageOnePrice => UiText.Get("Intel.Flea.Page.OnePrice");
    public static string FleaPageNoRange => UiText.Get("Intel.Flea.Page.NoRange");
    public static string FleaPageNoPriceCached => UiText.Get("Intel.Flea.Page.NoPriceCached");
    public static string FleaPageNoSaleValue => UiText.Get("Intel.Flea.Page.NoSaleValue");
    public static string FleaPageNoTimestamp => UiText.Get("Intel.Flea.Page.NoTimestamp");

    public static string FleaPageEvidence(object availability, int items) => UiText.Format("Intel.Flea.Page.Evidence", availability, items);
    public static string FleaPageOffersSold(long count) => UiText.Plural("Intel.Flea.Page.OffersSold", count);
    public static string FleaPageSold(long count) => UiText.Plural("Intel.Flea.Page.Sold", count);
    public static string FleaPageDimensions(int width, int height, int slots) => UiText.Format("Intel.Flea.Page.Dimensions", width, height, slots);
    public static string FleaPageSource(string moment) => UiText.Format("Intel.Flea.Page.Source", moment);
    public static string FleaPageResults(int count) => UiText.Plural("Intel.Flea.Page.Results", count);
    public static string FleaPageSearchFailed => UiText.Get("Intel.Flea.Page.SearchFailed");
    public static string FleaPageSearchFailedTitle => UiText.Get("Intel.Flea.Page.SearchFailedTitle");
    public static string FleaPageSearchFailedDetail => UiText.Get("Intel.Flea.Page.SearchFailedDetail");
    public static string FleaPageNoObservations(string item) => UiText.Format("Intel.Flea.Page.NoObservations", item);
    public static string FleaPageOneObservation(string item) => UiText.Format("Intel.Flea.Page.OneObservation", item);
    public static string FleaPageObservations(int count, string item, double days) => UiText.Format("Intel.Flea.Page.Observations", count, item, days);
    public static string FleaPageUnreadable => UiText.Get("Intel.Flea.Page.Unreadable");
    public static string FleaPagePerSlot(string roubles) => UiText.Format("Intel.Flea.Page.PerSlot", roubles);
    public static string FleaPageHistory(string low, string average, string high) => UiText.Format("Intel.Flea.Page.History", low, average, high);
    public static string FleaPageBand(string low, string high) => UiText.Format("Intel.Flea.Page.Band", low, high);
    public static string FleaPageBandAverage(string low, string high, string average) => UiText.Format("Intel.Flea.Page.BandAverage", low, high, average);
    public static string FleaPageBestOnFlea(string roubles) => UiText.Format("Intel.Flea.Page.BestOnFlea", roubles);
    public static string FleaPageBestAtTrader(string roubles, string trader) => UiText.Format("Intel.Flea.Page.BestAtTrader", roubles, trader);
    public static string FleaPageATrader => UiText.Get("Intel.Flea.Page.ATrader");
}
