namespace TarkovCompanion.App.Localization;

public static partial class SetupText
{
    public static string QuestSyncIncluded => UiText.Get("Setup.QuestSync.Included");
    public static string QuestSyncUseMatch => UiText.Get("Setup.QuestSync.UseMatch");
    public static string QuestSyncChooseScreenshots => UiText.Get("Setup.QuestSync.ChooseScreenshots");
    public static string QuestSyncPassiveOffer(long count) => UiText.Plural("Setup.QuestSync.PassiveOffer", count);
    public static string QuestSyncMatchedHeading(int count) => UiText.Format("Setup.QuestSync.MatchedHeading", count);
    public static string QuestSyncAmbiguousHeading(int count) => UiText.Format("Setup.QuestSync.AmbiguousHeading", count);
    public static string QuestSyncUnmatchedHeading(int count) => UiText.Format("Setup.QuestSync.UnmatchedHeading", count);
    public static string QuestSyncNoChanges => UiText.Get("Setup.QuestSync.NoChanges");
    public static string QuestSyncChanges(object? active, object? earlier) => UiText.Format("Setup.QuestSync.Changes", active, earlier);
    public static string QuestSyncNotReady(object? reason) => UiText.Format("Setup.QuestSync.NotReady", reason);
    public static string QuestSyncNoneSelected => UiText.Get("Setup.QuestSync.NoneSelected");
    public static string QuestSyncReading(long count) => UiText.Plural("Setup.QuestSync.Reading", count);
    public static string QuestSyncLooking => UiText.Get("Setup.QuestSync.Looking");
    public static string QuestSyncNoReadable => UiText.Get("Setup.QuestSync.NoReadable");
    public static string QuestSyncNoRecent => UiText.Get("Setup.QuestSync.NoRecent");
    public static string QuestSyncSkipped(object? count) => UiText.Format("Setup.QuestSync.Skipped", count);
    public static string QuestSyncReadFailed(object? reason) => UiText.Format("Setup.QuestSync.ReadFailed", reason);
    public static string QuestSyncRead(long count, object? engine) => UiText.Plural("Setup.QuestSync.Read", count, engine);
    public static string QuestSyncConfirmFirst => UiText.Get("Setup.QuestSync.ConfirmFirst");
    public static string QuestSyncSynced(object? changes) => UiText.Format("Setup.QuestSync.Synced", changes);
    public static string QuestSyncNotSynced(object? reason) => UiText.Format("Setup.QuestSync.NotSynced", reason);
    public static string QuestSyncCancelled => UiText.Get("Setup.QuestSync.Cancelled");
    public static string CoverageNotMeasured => UiText.Get("Setup.Coverage.NotMeasured");
    public static string CoverageQuestTitle => UiText.Get("Setup.Coverage.QuestTitle");
    public static string CoverageQuestNoMode => UiText.Get("Setup.Coverage.QuestNoMode");
    public static string CoverageQuestNoData => UiText.Get("Setup.Coverage.QuestNoData");
    public static string CoverageQuestOtherMaps => UiText.Get("Setup.Coverage.QuestOtherMaps");
    public static string CoverageQuestNoObjectives => UiText.Get("Setup.Coverage.QuestNoObjectives");
    public static string CoverageQuestSummary(int drawable, int total, double share) => UiText.Format("Setup.Coverage.QuestSummary", drawable, total, share);
    public static string CoverageQuestFailed(object? reason) => UiText.Format("Setup.Coverage.QuestFailed", reason);
    public static string CoverageQuestPlaced(int placed, int objectives) => UiText.Format("Setup.Coverage.QuestPlaced", placed, objectives);
    public static string CoverageQuestNoPlace(int count) => UiText.Format("Setup.Coverage.QuestNoPlace", count);
    public static string CoverageQuestByPlayer(int count) => UiText.Format("Setup.Coverage.QuestByPlayer", count);
}
