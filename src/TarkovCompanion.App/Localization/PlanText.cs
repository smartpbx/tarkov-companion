namespace TarkovCompanion.App.Localization;

/// <summary>Every word the Plan workspace shows, typed, from Localization/Strings (#314).</summary>
/// <remarks>
/// Moved off literals the way <see cref="DebriefText"/> was. The Plan tabs share this one class
/// across PlanText*.cs partial files: this file is the Quests tab, and every other tab's members
/// start with the tab's name (Hideout…, Keep…, Loadout…, Events…) so two tabs cannot both claim
/// "Refresh". The views bind these with x:Static, so a label that is not here fails the XAML
/// compile. A counted phrase is a method taking the count, never a noun with an "s" glued on.
/// Map, quest, trader, item and event names are data and pass through as arguments untranslated.
/// </remarks>
public static partial class PlanText
{
    public static string NextRaid => UiText.Get("Plan.NextRaid");
    public static string Learn => UiText.Get("Plan.Learn");
    public static string LearnMode => UiText.Get("Plan.LearnMode");
    public static string Export => UiText.Get("Plan.Export");
    public static string ExportName => UiText.Get("Plan.ExportName");
    public static string ExportTip => UiText.Get("Plan.ExportTip");
    public static string Refresh => UiText.Get("Plan.Refresh");
    public static string SearchPlaceholder => UiText.Get("Plan.SearchPlaceholder");
    public static string SearchQuests => UiText.Get("Plan.SearchQuests");
    public static string ClearSearch => UiText.Get("Plan.ClearSearch");
    public static string Trader => UiText.Get("Plan.Trader");
    public static string LevelAndLoyalty => UiText.Get("Plan.LevelAndLoyalty");
    public static string YourLevel => UiText.Get("Plan.YourLevel");
    public static string ByMap => UiText.Get("Plan.ByMap");
    public static string SuggestedNextRaid => UiText.Get("Plan.SuggestedNextRaid");
    public static string CardHideout => UiText.Get("Plan.CardHideout");
    public static string CardHideoutDetail => UiText.Get("Plan.CardHideoutDetail");
    public static string NothingPlanned => UiText.Get("Plan.NothingPlanned");
    public static string PickAMap => UiText.Get("Plan.PickAMap");
    public static string QuestState => UiText.Get("Plan.QuestState");
    public static string Wiki => UiText.Get("Plan.Wiki");
    public static string WikiName => UiText.Get("Plan.WikiName");
    public static string OnIt => UiText.Get("Plan.OnIt");
    public static string OnItTip => UiText.Get("Plan.OnItTip");
    public static string OnItName => UiText.Get("Plan.OnItName");
    public static string ShowInRaid => UiText.Get("Plan.ShowInRaid");
    public static string ShowInRaidTip => UiText.Get("Plan.ShowInRaidTip");
    public static string ShowInRaidName => UiText.Get("Plan.ShowInRaidName");
    public static string MarkObjectiveDone => UiText.Get("Plan.MarkObjectiveDone");
    public static string MoreActions => UiText.Get("Plan.MoreActions");
    public static string Requirements => UiText.Get("Plan.Requirements");
    public static string OpenInRaid => UiText.Get("Plan.OpenInRaid");

    // The objective row's "more actions" menu.
    public static string StartQuest => UiText.Get("Plan.StartQuest");
    public static string MarkQuestDone => UiText.Get("Plan.MarkQuestDone");
    public static string MarkQuestFailed => UiText.Get("Plan.MarkQuestFailed");
    public static string ResetQuest => UiText.Get("Plan.ResetQuest");
    public static string OneMore => UiText.Get("Plan.OneMore");
    public static string OneFewer => UiText.Get("Plan.OneFewer");
    public static string ResetObjective => UiText.Get("Plan.ResetObjective");
    public static string ShowOnMap => UiText.Get("Plan.ShowOnMap");
    public static string OpenWiki => UiText.Get("Plan.OpenWiki");
    public static string PinQuest => UiText.Get("Plan.PinQuest");
    public static string UnpinQuest => UiText.Get("Plan.UnpinQuest");
    public static string PinObjective => UiText.Get("Plan.PinObjective");
    public static string UnpinObjective => UiText.Get("Plan.UnpinObjective");

    // Objective rows and map groups.
    public static string NoTrader => UiText.Get("Plan.NoTrader");
    public static string FromTheGame => UiText.Get("Plan.FromTheGame");
    public static string Optional => UiText.Get("Plan.Optional");
    public static string NoMapPosition => UiText.Get("Plan.NoMapPosition");
    public static string UnsupportedObjective => UiText.Get("Plan.UnsupportedObjective");
    public static string RouteCaveat => UiText.Get("Plan.RouteCaveat");
    public static string AllReady => UiText.Get("Plan.AllReady");
    public static string FindInRaid => UiText.Get("Plan.FindInRaid");
    public static string HandIn => UiText.Get("Plan.HandIn");
    public static string Bring => UiText.Get("Plan.Bring");
    public static string AnyMap => UiText.Get("Plan.AnyMap");
    public static string OtherMap => UiText.Get("Plan.OtherMap");
    public static string ItemNotInCatalog => UiText.Get("Plan.ItemNotInCatalog");

    // Route and map preview notes.
    public static string RouteNeedsOrigin => UiText.Get("Plan.RouteNeedsOrigin");
    public static string RouteNeedsMap => UiText.Get("Plan.RouteNeedsMap");
    public static string RouteNoExactPosition => UiText.Get("Plan.RouteNoExactPosition");
    public static string LoadingMap => UiText.Get("Plan.LoadingMap");
    public static string AnyMapObjectives => UiText.Get("Plan.AnyMapObjectives");
    public static string MapFollowsRaid => UiText.Get("Plan.MapFollowsRaid");
    public static string NoMapPlan => UiText.Get("Plan.NoMapPlan");

    // Status lines.
    public static string LoadingBoard => UiText.Get("Plan.LoadingBoard");
    public static string NoProfileLoaded => UiText.Get("Plan.NoProfileLoaded");
    public static string AllTraders => UiText.Get("Plan.AllTraders");
    public static string QuestDataUnavailable => UiText.Get("Plan.QuestDataUnavailable");
    public static string QuestsDidNotLoad => UiText.Get("Plan.QuestsDidNotLoad");
    public static string NothingLostRetry => UiText.Get("Plan.NothingLostRetry");
    public static string NoProfileNothingChanged => UiText.Get("Plan.NoProfileNothingChanged");
    public static string CopiedToClipboard => UiText.Get("Plan.CopiedToClipboard");
    public static string NothingToExportTo => UiText.Get("Plan.NothingToExportTo");

    // What the game log said about quests, in three parts joined without spaces.
    public static string GameNotReported => UiText.Get("Plan.GameNotReported");
    public static string GameReported => UiText.Get("Plan.GameReported");
    public static string NoneChangedBoard => UiText.Get("Plan.NoneChangedBoard");
    public static string OneChangedBoard => UiText.Get("Plan.OneChangedBoard");
    public static string SentenceEnd => UiText.Get("Plan.SentenceEnd");

    // Filters and quest states.
    public static string FilterActive => UiText.Get("Plan.FilterActive");
    public static string FilterCurrent => UiText.Get("Plan.FilterCurrent");
    public static string FilterNext => UiText.Get("Plan.FilterNext");
    public static string FilterBlocked => UiText.Get("Plan.FilterBlocked");
    public static string FilterFuture => UiText.Get("Plan.FilterFuture");
    public static string FilterCompleted => UiText.Get("Plan.FilterCompleted");
    public static string FilterUnknown => UiText.Get("Plan.FilterUnknown");
    public static string FilterKappa => UiText.Get("Plan.FilterKappa");
    public static string FilterAll => UiText.Get("Plan.FilterAll");
    public static string StatusCompleted => UiText.Get("Plan.StatusCompleted");
    public static string StatusFailed => UiText.Get("Plan.StatusFailed");
    public static string StatusAvailableNow => UiText.Get("Plan.StatusAvailableNow");
    public static string StatusLocked => UiText.Get("Plan.StatusLocked");
    public static string StatusWaitingOnTimer => UiText.Get("Plan.StatusWaitingOnTimer");
    public static string EmptyActive => UiText.Get("Plan.EmptyActive");
    public static string EmptyCurrent => UiText.Get("Plan.EmptyCurrent");
    public static string EmptyNext => UiText.Get("Plan.EmptyNext");
    public static string EmptyBlocked => UiText.Get("Plan.EmptyBlocked");
    public static string EmptyFuture => UiText.Get("Plan.EmptyFuture");
    public static string EmptyCompleted => UiText.Get("Plan.EmptyCompleted");
    public static string EmptyUnknown => UiText.Get("Plan.EmptyUnknown");
    public static string EmptyKappa => UiText.Get("Plan.EmptyKappa");
    public static string EmptyAll => UiText.Get("Plan.EmptyAll");

    // The exported Markdown document. Patterns are formatted by PlanExport in the culture it was
    // given, so the file reads the same whatever the interface culture formats numbers as.
    public static string ExportTitle => UiText.Get("Plan.ExportTitle");
    public static string ExportGeneratedPattern => UiText.Get("Plan.ExportGeneratedPattern");
    public static string ExportScopePattern => UiText.Get("Plan.ExportScopePattern");
    public static string ExportSearchPattern => UiText.Get("Plan.ExportSearchPattern");
    public static string ExportNothingPlanned => UiText.Get("Plan.ExportNothingPlanned");
    public static string ExportStillNeeded => UiText.Get("Plan.ExportStillNeeded");
    public static string ExportShoppingList => UiText.Get("Plan.ExportShoppingList");
    public static string ExportHeldUnknown => UiText.Get("Plan.ExportHeldUnknown");
    public static string ExportAlreadyHeldPattern => UiText.Get("Plan.ExportAlreadyHeldPattern");
    public static string ExportInTheWay => UiText.Get("Plan.ExportInTheWay");
    public static string ExportNothingInTheWay => UiText.Get("Plan.ExportNothingInTheWay");
    public static string ExportWaitingOnPattern => UiText.Get("Plan.ExportWaitingOnPattern");
    public static string ExportQuestsDeepPattern => UiText.Get("Plan.ExportQuestsDeepPattern");

    public static string LearnNeededFor(string handling, string quest) => UiText.Format("Plan.LearnNeededFor", handling, quest);
    public static string LearnAdvances(string quest) => UiText.Format("Plan.LearnAdvances", quest);
    public static string LearnNeededByMap(string handling) => UiText.Format("Plan.LearnNeededByMap", handling);
    public static string ObjectivesHeading(int count) => UiText.Format("Plan.ObjectivesHeading", count);
    public static string RouteFrom(double metres, string start) => UiText.Format("Plan.RouteFrom", metres, start);
    public static string Metres(double metres) => UiText.Format("Plan.Metres", metres);
    public static string Closed(string events) => UiText.Format("Plan.Closed", events);
    public static string ShowMore(int count) => UiText.Format("Plan.ShowMore", count);
    public static string ShowMoreMaps(int count) => UiText.Format("Plan.ShowMoreMaps", count);
    public static string CopiedAndSaved(string path) => UiText.Format("Plan.CopiedAndSaved", path);
    public static string SavedTo(string path) => UiText.Format("Plan.SavedTo", path);
    public static string EventRuleFilesNeedAttention(int count) => UiText.Format("Plan.EventRuleFilesNeedAttention", count);
    public static string GameLastReported(string time) => UiText.Format("Plan.GameLastReported", time);
    public static string ManyChangedBoard(int count) => UiText.Format("Plan.ManyChangedBoard", count);
    public static string NotInLoadedCatalog(string quests) => UiText.Format("Plan.NotInLoadedCatalog", quests);
    public static string CouldNotBeSaved(string quests) => UiText.Format("Plan.CouldNotBeSaved", quests);
    public static string NotInCatalogOrSaved(string unmatched, string failed) => UiText.Format("Plan.NotInCatalogOrSaved", unmatched, failed);
    public static string Suggested(string map, string quests) => UiText.Format("Plan.Suggested", map, quests);
    public static string AcrossMaps(string objectives, string maps) => UiText.Format("Plan.AcrossMaps", objectives, maps);
    public static string LevelNotSaved(string reason) => UiText.Format("Plan.LevelNotSaved", reason);
    public static string LoyaltyNotSaved(string reason) => UiText.Format("Plan.LoyaltyNotSaved", reason);
    public static string RemainingOf(decimal remaining, decimal target) => UiText.Format("Plan.RemainingOf", remaining, target);
    public static string CouldNotSwitchMap(string reason) => UiText.Format("Plan.CouldNotSwitchMap", reason);
    public static string NotChanged(string reason) => UiText.Format("Plan.NotChanged", reason);
    public static string SquadHasIt(string names) => UiText.Format("Plan.SquadHasIt", names);
    public static string LockedBy(string steps) => UiText.Format("Plan.LockedBy", steps);
    public static string NoQuestMatches(string query, string filter) => UiText.Format("Plan.NoQuestMatches", query, filter);
    public static string NoQuestForTrader(string filter) => UiText.Format("Plan.NoQuestForTrader", filter);
    public static string OrMore(string item, int count) => UiText.Format("Plan.OrMore", item, count);
    public static string StillNeeded(string count) => UiText.Format("Plan.StillNeeded", count);
    public static string ToCheck(string count) => UiText.Format("Plan.ToCheck", count);
    public static string StillNeededAndToCheck(string needed, string unknown) => UiText.Format("Plan.StillNeededAndToCheck", needed, unknown);

    public static string ObjectiveCount(long count) => UiText.Plural("Plan.ObjectiveCount", count);
    public static string QuestCount(long count) => UiText.Plural("Plan.QuestCount", count);
    public static string MapCount(long count) => UiText.Plural("Plan.MapCount", count);
    public static string ItemCount(long count) => UiText.Plural("Plan.ItemCount", count);
    public static string Stops(long count) => UiText.Plural("Plan.Stops", count);
    public static string WithoutExactPosition(long count) => UiText.Plural("Plan.WithoutExactPosition", count);
}
